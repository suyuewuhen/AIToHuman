using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 主机级端到端用例：**真的把 API 起成子进程**，用真 HTTP 打它。
///
/// 这一层专门覆盖"单元测试与真库用例都碰不到"的东西：路由是否注册、DI 是否装得上、
/// 中间件顺序对不对（幂等键、异常映射、鉴权策略）、启动期迁移能不能把空库建起来、
/// JSON 契约有没有在序列化这一层变味。跑不起来（缺库或缺已构建的程序集）时整组跳过。
/// </summary>
[Collection(HostE2ECollection.Name)]
public sealed class HostE2ETests(HostE2EFixture host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [HostFact]
    public async Task The_real_process_starts_healthy_and_exposes_the_dev_session()
    {
        using var client = NewClient();

        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var payload = await health.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", payload.GetProperty("status").GetString());

        // 开发环境下才有这个端点：顺带证明 ASPNETCORE_ENVIRONMENT 真的生效了。
        var session = await client.GetAsync("/api/v1/session/dev");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [HostFact]
    public async Task The_whole_task_to_escrow_flow_works_over_real_http()
    {
        using var client = NewClient();
        var owner = await Register(client, "host-owner", "owner");
        var worker = await Register(client, "host-worker", "worker");

        var task = await Post(client, "/api/v1/tasks", new
        {
            ownerId = owner.UserId,
            title = "帮我把一份文件送到前台",
            description = "到前台交给行政即可",
            district = "朝阳区",
            deadline = DateTimeOffset.UtcNow.AddHours(14),
            reward = 60,
            acceptanceCriteria = new[] { "当面交付" },
            executionAddress = "世纪大道 100 号 A 座 1801 室"
        }, owner.Token);
        var taskId = task.GetProperty("id").GetGuid();
        Assert.Equal("Allowed", task.GetProperty("riskVerdict").GetString());

        await Post(client, $"/api/v1/tasks/{taskId}/publish", new { ownerId = owner.UserId }, owner.Token);

        var applied = await Post(client, $"/api/v1/tasks/{taskId}/applications", new { workerId = worker.UserId, note = "半小时可到" }, worker.Token);
        var applicationId = applied.GetProperty("applications")[0].GetProperty("id").GetGuid();

        var selected = await Post(client, $"/api/v1/tasks/{taskId}/applications/{applicationId}/select", new { ownerId = owner.UserId }, owner.Token);
        var order = selected.GetProperty("order");
        var orderId = order.GetProperty("id").GetGuid();

        // 选人这一步在真 HTTP 链路上完成了资金冻结。
        Assert.Equal("Held", order.GetProperty("escrowStatus").GetString());
        Assert.Equal(60m, order.GetProperty("escrowAmount").GetDecimal());

        var held = await Get(client, $"/api/v1/orders/{orderId}/ledger", owner.Token);
        Assert.Equal(1, held.GetArrayLength());
        Assert.Equal("Hold", held[0].GetProperty("kind").GetString());

        await Post(client, $"/api/v1/orders/{orderId}/start", new { actorId = worker.UserId }, worker.Token);
        await Post(client, $"/api/v1/orders/{orderId}/submit", new { actorId = worker.UserId, note = "已完成" }, worker.Token);
        var approved = await Post(client, $"/api/v1/orders/{orderId}/approve", new { actorId = owner.UserId, note = "干得不错" }, owner.Token);

        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.Equal("Released", approved.GetProperty("escrowStatus").GetString());
        Assert.Equal(60m, approved.GetProperty("releasedAmount").GetDecimal());

        var settled = await Get(client, $"/api/v1/orders/{orderId}/ledger", owner.Token);
        Assert.Equal(2, settled.GetArrayLength());
        Assert.Equal("Release", settled[1].GetProperty("kind").GetString());
    }

    [HostFact]
    public async Task Readable_errors_and_authorization_come_from_the_real_pipeline()
    {
        using var client = NewClient();
        var owner = await Register(client, "host-error-owner", "owner");

        // 邮箱重复 → 409 + 原样消息（不是通用的 500 文案）。
        var conflict = await Send(client, HttpMethod.Post, "/api/v1/auth/register",
            new { email = $"{owner.EmailPrefix}@aitohuman.local", password = "Host!2026e2e", displayName = "重复", role = "owner" }, null);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("该邮箱已注册", await conflict.Content.ReadAsStringAsync());

        // 请求本身写错 → 422 + 可读原因。
        var invalid = await Send(client, HttpMethod.Post, "/api/v1/auth/register",
            new { email = $"host-bad-{Guid.NewGuid():N}@aitohuman.local", password = "Host!2026e2e", displayName = "缺角色" }, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Contains("角色必须是 owner 或 worker", await invalid.Content.ReadAsStringAsync());

        // 非管理员访问运营接口 → 403（策略在真实管线上生效）。
        var forbidden = await Send(client, HttpMethod.Get, "/api/v1/admin/settings", null, owner.Token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // 不存在的任务 → 404。
        var missing = await Send(client, HttpMethod.Get, $"/api/v1/tasks/{Guid.NewGuid()}/ledger", null, owner.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [HostFact]
    public async Task The_admin_policy_accepts_the_configured_admin_email()
    {
        using var client = NewClient();
        // fixture 用 Admin__Emails 指定了这一个邮箱：注册它就应该拿到运营权限（所以这里不能用带随机后缀的邮箱）。
        var admin = await Register(client, "host-e2e-admin", "owner", uniqueEmail: false);

        var settings = await Get(client, "/api/v1/admin/settings", admin.Token);
        Assert.True(settings.GetArrayLength() >= 27, $"运营配置项应该有 27 个以上，实际 {settings.GetArrayLength()} 个");

        var me = await Get(client, "/api/v1/auth/me", admin.Token);
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
    }

    [HostFact]
    public async Task An_idempotency_key_replays_the_response_and_a_changed_body_conflicts()
    {
        using var client = NewClient();
        var owner = await Register(client, "host-idem-owner", "owner");
        var task = await Post(client, "/api/v1/tasks", new
        {
            ownerId = owner.UserId,
            title = "帮我把一份文件送到前台",
            description = "到前台交给行政即可",
            district = "朝阳区",
            deadline = DateTimeOffset.UtcNow.AddHours(14),
            reward = 50,
            acceptanceCriteria = new[] { "当面交付" }
        }, owner.Token);
        var taskId = task.GetProperty("id").GetGuid();

        var key = $"host-e2e-{Guid.NewGuid():N}";
        var first = await Send(client, HttpMethod.Post, $"/api/v1/tasks/{taskId}/publish", new { ownerId = owner.UserId }, owner.Token, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));

        // 同一个键重放：状态码与响应体都一样，并带上回放标记——这条只有真管线能验证。
        var replay = await Send(client, HttpMethod.Post, $"/api/v1/tasks/{taskId}/publish", new { ownerId = owner.UserId }, owner.Token, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync());
        Assert.True(replay.Headers.TryGetValues("Idempotency-Replayed", out var values) && values.Single() == "true");

        // 同一个键配不同请求体：客户端用错了键，必须 409 而不是拿旧响应糊过去。
        var second = await Post(client, "/api/v1/tasks", new
        {
            ownerId = owner.UserId,
            title = "帮我把另一份文件送到前台",
            description = "到前台交给行政即可",
            district = "朝阳区",
            deadline = DateTimeOffset.UtcNow.AddHours(14),
            reward = 50,
            acceptanceCriteria = new[] { "当面交付" }
        }, owner.Token);
        var secondTaskId = second.GetProperty("id").GetGuid();
        var mismatched = await Send(client, HttpMethod.Post, $"/api/v1/tasks/{secondTaskId}/publish", new { ownerId = owner.UserId }, owner.Token, key);

        Assert.Equal(HttpStatusCode.Conflict, mismatched.StatusCode);
    }

    [HostFact]
    public async Task Raising_the_reward_over_the_threshold_queues_the_task_for_review_over_real_http()
    {
        using var client = NewClient();
        var owner = await Register(client, "host-reward-owner", "owner");
        var task = await Post(client, "/api/v1/tasks", new
        {
            ownerId = owner.UserId,
            title = "帮我去前台取一份文件",
            description = "到前台取件",
            district = "朝阳区",
            deadline = DateTimeOffset.UtcNow.AddHours(14),
            reward = 50,
            acceptanceCriteria = new[] { "按时送达" }
        }, owner.Token);
        var taskId = task.GetProperty("id").GetGuid();
        await Post(client, $"/api/v1/tasks/{taskId}/publish", new { ownerId = owner.UserId }, owner.Token);

        // 加价即重判：这条路径曾经能绕过高金额转人工。
        var raised = await Post(client, $"/api/v1/tasks/{taskId}/increase-reward", new { reward = 9000 }, owner.Token);

        Assert.Equal("NeedsReview", raised.GetProperty("riskVerdict").GetString());
        Assert.Equal("review.high_reward", raised.GetProperty("riskRuleCode").GetString());
        Assert.Equal("RecheckRequired", raised.GetProperty("riskEnforcementStatus").GetString());
    }

    [HostFact]
    public async Task The_rule_statistics_endpoint_is_admin_only_and_returns_the_dashboard_shape()
    {
        using var client = NewClient();
        var owner = await Register(client, "host-stats-owner", "owner");

        // 非管理员：运营看板不给看（策略挂在真管线上）。
        var forbidden = await Send(client, HttpMethod.Get, "/api/v1/admin/risk/stats?days=7", null, owner.Token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // 管理员：拿得到看板结构（窗口、总数、按规则的明细）。
        var admin = await Register(client, "host-e2e-admin", "owner", uniqueEmail: false);
        var stats = await Get(client, "/api/v1/admin/risk/stats?days=7", admin.Token);

        Assert.Equal(7, stats.GetProperty("windowDays").GetInt32());
        Assert.True(stats.GetProperty("totalDecisions").GetInt32() >= 0);
        Assert.Equal(JsonValueKind.Array, stats.GetProperty("rules").ValueKind);
    }

    [HostFact]
    public async Task The_readiness_endpoint_probes_real_dependencies()
    {
        using var client = NewClient();

        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var payload = await ready.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", payload.GetProperty("status").GetString());

        var checks = payload.GetProperty("checks").EnumerateArray().ToArray();
        // 真库这一项必须真的跑过（临时库是应用自己 Migrate() 建出来的，所以迁移也应该是全的）。
        var postgres = Assert.Single(checks, check => check.GetProperty("name").GetString() == "postgres");
        Assert.Equal("ok", postgres.GetProperty("status").GetString());
        Assert.Contains("没有待应用的迁移", postgres.GetProperty("detail").GetString());

        // 文件存储这一项也真的探测了：本机目录模式下会写一条探针对象再删掉，因此事后不该留下它。
        var storage = Assert.Single(checks, check => check.GetProperty("name").GetString() == "storage");
        Assert.Equal("ok", storage.GetProperty("status").GetString());

        // 这一批用例没有开多实例扇出，所以 Redis 是"跳过"而不是"失败"——
        // 否则单实例部署会因为没配 Redis 而永远未就绪。
        var redis = Assert.Single(checks, check => check.GetProperty("name").GetString() == "redis");
        Assert.Equal("skipped", redis.GetProperty("status").GetString());

        // 存活探针保持"只看进程"，不因为依赖探测而变慢或变红。
        var liveness = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
    }

    [HostFact]
    public async Task Write_rate_limiting_returns_429_with_a_readable_reason_after_the_configured_limit()
    {
        using var client = NewClient();
        var admin = await Register(client, "host-e2e-admin", "owner", uniqueEmail: false);
        var owner = await Register(client, "host-limit-owner", "owner");

        // 运营在后台把上限降到 2：限流口径每次请求现读设置，因此下一个请求就该按新值判定。
        // 上限是按用户分区的，所以管理员自己改配置用的是管理员那一份额度。
        await Put(client, "/api/v1/admin/settings/ratelimit.writesPerMinute", new { value = "2" }, admin.Token);

        try
        {
            // 这位需求方的第 1、2 个写请求放行，第 3 个被挡。
            await Post(client, "/api/v1/tasks", NewTaskBody(owner.UserId, "限流用例一"), owner.Token);
            await Post(client, "/api/v1/tasks", NewTaskBody(owner.UserId, "限流用例二"), owner.Token);

            var limited = await Send(client, HttpMethod.Post, "/api/v1/tasks", NewTaskBody(owner.UserId, "限流用例三"), owner.Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.True(limited.Headers.TryGetValues("Retry-After", out var retryAfter));
            Assert.True(int.Parse(retryAfter.Single()) >= 1, "Retry-After 应该是还要等多少秒");

            var body = await limited.Content.ReadAsStringAsync();
            Assert.Contains("请求过于频繁", body);
            Assert.Contains("一分钟内的写请求次数已达上限（2 次）", body);

            // 读接口不受影响：限流的目标是挡脚本刷写，不是让人打不开大厅。
            var hall = await Get(client, "/api/v1/tasks?limit=5", owner.Token);
            Assert.Equal(JsonValueKind.Array, hall.GetProperty("items").ValueKind);

            // 配额算在谁头上要能看出来（同一出口 IP 后面的人共用匿名额度，这是最常见的困惑）。
            var limitedPartition = limited.Headers.GetValues("X-RateLimit-Partition").Single();
            Assert.Equal($"user:{owner.UserId}", limitedPartition);

            // 配置写入是"自救通道"：上限配小了也必须能改回来，否则运营会被锁在门外整整一个窗口。
            // 把上限压到 1 之后连做两次配置写入，两次都必须成功（不豁免的话第二次就是 429）。
            await Put(client, "/api/v1/admin/settings/ratelimit.writesPerMinute", new { value = "1" }, admin.Token);
            var stillAllowed = await Send(client, HttpMethod.Put, "/api/v1/admin/settings/ratelimit.writesPerMinute", new { value = "240" }, admin.Token);
            Assert.Equal(HttpStatusCode.OK, stillAllowed.StatusCode);
        }
        finally
        {
            // 兜底恢复：这一批用例共用一个进程，配置留在 2 会把后面的用例连带限流。
            await Send(client, HttpMethod.Put, "/api/v1/admin/settings/ratelimit.writesPerMinute", new { value = "240" }, admin.Token);
        }
    }

    private static object NewTaskBody(Guid ownerId, string title) => new
    {
        ownerId,
        title,
        description = "到前台交给行政即可",
        district = "朝阳区",
        deadline = DateTimeOffset.UtcNow.AddHours(14),
        reward = 30,
        acceptanceCriteria = new[] { "当面交付" }
    };

    private HttpClient NewClient() => new() { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromSeconds(30) };

    private sealed record TestUser(string EmailPrefix, Guid UserId, string Token);

    /// <summary>
    /// 注册一个账号（默认邮箱带随机后缀；需要命中管理员名单时必须用固定邮箱）。
    /// 固定邮箱在同一批用例里可能被注册两次：第二次改为登录，让用例彼此独立。
    /// </summary>
    private static async Task<TestUser> Register(HttpClient client, string prefix, string role, bool uniqueEmail = true)
    {
        var emailPrefix = uniqueEmail ? $"{prefix}-{Guid.NewGuid():N}" : prefix;
        var email = $"{emailPrefix}@aitohuman.local";
        var response = await Send(client, HttpMethod.Post, "/api/v1/auth/register",
            new { email, password = "Host!2026e2e", displayName = prefix, role }, null);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            response = await Send(client, HttpMethod.Post, "/api/v1/auth/login", new { email, password = "Host!2026e2e" }, null);
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new TestUser(emailPrefix, payload.GetProperty("userId").GetGuid(), payload.GetProperty("accessToken").GetString()!);
    }

    private static Task<JsonElement> Post(HttpClient client, string path, object body, string token) =>
        SendJson(client, HttpMethod.Post, path, body, token);

    private static Task<JsonElement> Put(HttpClient client, string path, object body, string token) =>
        SendJson(client, HttpMethod.Put, path, body, token);

    private static Task<JsonElement> Get(HttpClient client, string path, string token) =>
        SendJson(client, HttpMethod.Get, path, null, token);

    private static async Task<JsonElement> SendJson(HttpClient client, HttpMethod method, string path, object? body, string? token, string? idempotencyKey = null)
    {
        var response = await Send(client, method, path, body, token, idempotencyKey);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"{method} {path} 返回 {(int)response.StatusCode}：{text}");
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? body, string? token, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }
}
