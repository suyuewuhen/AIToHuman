using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AIToHuman.Api.Idempotency;
using AIToHuman.Application.Idempotency;
using AIToHuman.Domain.Idempotency;
using AIToHuman.Infrastructure.Idempotency;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 幂等中间件的用例层行为：同一个键重试只执行一次、原样回放响应；
/// 同一个键配不同请求体要报冲突；没有键时行为完全不变。
/// </summary>
public sealed class IdempotencyMiddlewareTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Replaying_the_same_key_executes_the_handler_only_once()
    {
        var world = new World();
        var first = await world.Post("/api/v1/tasks", """{"title":"代取文件"}""", "key-1");
        var second = await world.Post("/api/v1/tasks", """{"title":"代取文件"}""", "key-1");

        Assert.Equal(1, world.Executions);
        Assert.Equal(201, first.StatusCode);
        Assert.Equal(201, second.StatusCode);
        Assert.Equal(first.Body, second.Body);
        // 回放要能被客户端识别出来，方便排查"为什么没有真的执行"。
        Assert.True(second.Replayed);
    }

    [Fact]
    public async Task Different_keys_execute_separately()
    {
        var world = new World();
        await world.Post("/api/v1/tasks", """{"title":"代取文件"}""", "key-1");
        await world.Post("/api/v1/tasks", """{"title":"代取文件"}""", "key-2");

        Assert.Equal(2, world.Executions);
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_request_is_a_conflict()
    {
        var world = new World();
        await world.Post("/api/v1/tasks", """{"title":"代取文件"}""", "key-1");
        var conflict = await world.Post("/api/v1/tasks", """{"title":"另一件事"}""", "key-1");

        Assert.Equal(409, conflict.StatusCode);
        Assert.Contains("幂等键", conflict.Body);
        // 不能拿旧响应糊过去，也不能真的执行第二次。
        Assert.Equal(1, world.Executions);
    }

    [Fact]
    public async Task A_request_without_the_header_is_untouched()
    {
        var world = new World();
        await world.Post("/api/v1/tasks", """{"title":"代取文件"}""");
        await world.Post("/api/v1/tasks", """{"title":"代取文件"}""");

        Assert.Equal(2, world.Executions);
        Assert.Null(world.Store.Find(User, "key-1"));
    }

    [Fact]
    public async Task Only_writes_are_covered()
    {
        var world = new World();
        await world.Get("/api/v1/tasks", "key-1");
        await world.Get("/api/v1/tasks", "key-1");

        Assert.Equal(2, world.Executions);
    }

    [Fact]
    public async Task Anonymous_requests_and_streams_are_not_covered()
    {
        var anonymous = new World(authenticated: false);
        await anonymous.Post("/api/v1/auth/login", """{"email":"a@b.c"}""", "key-1");
        await anonymous.Post("/api/v1/auth/login", """{"email":"a@b.c"}""", "key-1");
        Assert.Equal(2, anonymous.Executions);

        var stream = new World();
        await stream.Post("/api/v1/ai/plan/stream", """{"message":"帮我取件"}""", "key-1");
        await stream.Post("/api/v1/ai/plan/stream", """{"message":"帮我取件"}""", "key-1");
        Assert.Equal(2, stream.Executions);
    }

    [Fact]
    public async Task A_failure_is_not_cached_so_a_retry_can_really_run_again()
    {
        var world = new World();
        world.FailWith = StatusCodes.Status500InternalServerError;
        var failed = await world.Post("/api/v1/tasks", """{"title":"代取文件"}""", "key-1");

        world.FailWith = null;
        var retried = await world.Post("/api/v1/tasks", """{"title":"代取文件"}""", "key-1");

        Assert.Equal(500, failed.StatusCode);
        Assert.Equal(201, retried.StatusCode);
        Assert.Equal(2, world.Executions);
    }

    [Fact]
    public async Task A_stale_in_flight_record_can_be_retried()
    {
        var world = new World();
        // 模拟"上次执行中途挂了"：只占位、没有完成，而且已经过了过期时间。
        var stale = IdempotencyEntry.Start(User, "key-1", "not-a-real-hash", Now - IdempotencyEntry.InFlightTimeout - TimeSpan.FromMinutes(1));
        world.Store.TryStart(stale, out _);

        var response = await world.Post("/api/v1/tasks", """{"title":"代取文件"}""", "key-1");

        Assert.Equal(201, response.StatusCode);
        Assert.Equal(1, world.Executions);
    }

    [Fact]
    public async Task An_in_flight_record_blocks_a_second_request()
    {
        var world = new World();
        var payload = """{"title":"代取文件"}""";
        var hash = world.HashFor("POST", "/api/v1/tasks", payload);
        world.Store.TryStart(IdempotencyEntry.Start(User, "key-1", hash, Now), out _);

        var response = await world.Post("/api/v1/tasks", payload, "key-1");

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("正在处理中", response.Body);
        Assert.Equal(0, world.Executions);
    }

    private sealed class World
    {
        private readonly ServiceProvider provider;

        public World(bool authenticated = true)
        {
            Authenticated = authenticated;
            var services = new ServiceCollection();
            services.AddSingleton<IIdempotencyStore>(Store);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            provider = services.BuildServiceProvider();
        }

        public bool Authenticated { get; }
        public InMemoryIdempotencyStore Store { get; } = new();
        public int Executions { get; private set; }
        public int? FailWith { get; set; }

        public Task<Response> Get(string path, string? key) => Send("GET", path, null, key);

        public Task<Response> Post(string path, string body, string? key = null) => Send("POST", path, body, key);

        /// <summary>中间件算指纹的方式与实现保持一致，测试里用它来构造"占位中"的记录。</summary>
        public string HashFor(string method, string path, string body)
        {
            var payload = $"{method}\n{path}\n\n{body}";
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        }

        private async Task<Response> Send(string method, string path, string? body, string? key)
        {
            var context = new DefaultHttpContext { RequestServices = provider };
            context.Request.Method = method;
            context.Request.Path = path;
            if (body is not null) context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            if (key is not null) context.Request.Headers[IdempotencyMiddleware.HeaderName] = key;
            if (Authenticated)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, User.ToString())], "test"));
            }

            context.Response.Body = new MemoryStream();
            var middleware = new IdempotencyMiddleware(_ =>
            {
                Executions++;
                if (FailWith is { } status)
                {
                    _.Response.StatusCode = status;
                    _.Response.ContentType = "application/json; charset=utf-8";
                    return _.Response.WriteAsync("""{"title":"服务器内部错误"}""");
                }

                _.Response.StatusCode = StatusCodes.Status201Created;
                _.Response.ContentType = "application/json; charset=utf-8";
                return _.Response.WriteAsync($$"""{"id":"{{Guid.NewGuid()}}"}""");
            }, NullLogger<IdempotencyMiddleware>.Instance);

            await middleware.InvokeAsync(context);

            context.Response.Body.Position = 0;
            var text = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
            return new Response(context.Response.StatusCode, text, context.Response.Headers.ContainsKey("Idempotency-Replayed"));
        }
    }

    private sealed record Response(int StatusCode, string Body, bool Replayed);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
