using System.Security.Claims;
using AIToHuman.Application.Operations;

namespace AIToHuman.Api.Operations;

/// <summary>
/// 写接口限流中间件：按用户（未登录按来源 IP）限制一分钟内的写请求数，超限返回 <c>429</c> 与 <c>Retry-After</c>。
///
/// 四条刻意的取舍：
/// - **只压写请求**（POST/PUT/PATCH/DELETE）与 <c>/api/v1</c> 之外的路径一律放行：读接口、<c>/health</c>、
///   SignalR 协商都不该被限流挡住——限流是为了挡脚本刷写，不是为了让人打不开页面。
/// - **配置写入永不限流**（<c>/api/v1/admin/settings</c>）：上限本身是配置项，如果配小了连改回来的请求都被挡，
///   运营就会把自己锁在门外整整一个窗口（真机联调时确实撞到过这件事）。这条豁免是"自救通道"，
///   其余运营写接口（处置争议、复检、下架等）照常计数。
/// - **放在幂等中间件之前**：幂等中间件会缓存非 5xx 的 JSON 响应并在重试时回放，如果限流在它之后，
///   一个 <c>429</c> 会被当成"这个键的正常结果"缓存下来，客户端之后的重试会一直拿到 429。
/// - **未登录也要限**：注册与登录是匿名写请求，按来源 IP 计数；否则撞库没有成本。
///   代价是同一出口 IP 后面的所有人共用一份额度（反向代理部署时尤其明显），因此默认上限给得很宽。
/// </summary>
public sealed class WriteRateLimitMiddleware(RequestDelegate next, ILogger<WriteRateLimitMiddleware> logger)
{
    private static readonly string[] WriteMethods = ["POST", "PUT", "PATCH", "DELETE"];

    /// <summary>配置写入的路径前缀：这条通道永远不限流（见类注释里的自救通道说明）。</summary>
    public const string ExemptPathPrefix = "/api/v1/admin/settings";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsLimited(context))
        {
            await next(context);
            return;
        }

        var limiter = context.RequestServices.GetRequiredService<WriteRateLimiter>();
        var partition = ResolvePartition(context);
        var decision = limiter.Evaluate(partition);

        // 把"这一份额度算在谁头上"回给调用方：排查限流时最常见的问题是"我跟别人共用一个 IP"，
        // 只给剩余次数是看不出来的。
        context.Response.Headers["X-RateLimit-Limit"] = decision.Limit.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString();
        context.Response.Headers["X-RateLimit-Partition"] = partition;

        if (decision.Allowed)
        {
            await next(context);
            return;
        }

        logger.LogWarning(
            "写请求被限流：{Method} {Path} 分区 {Partition}，一分钟上限 {Limit} 次。",
            context.Request.Method,
            context.Request.Path,
            partition,
            decision.Limit);

        context.Response.Headers["Retry-After"] = decision.RetryAfterSeconds.ToString();
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            title = "请求过于频繁",
            status = StatusCodes.Status429TooManyRequests,
            detail = $"一分钟内的写请求次数已达上限（{decision.Limit} 次），请在 {decision.RetryAfterSeconds} 秒后重试。",
            instance = context.Request.Path.Value
        });
    }

    private static bool IsLimited(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api/v1") &&
        !context.Request.Path.StartsWithSegments(ExemptPathPrefix) &&
        WriteMethods.Contains(context.Request.Method, StringComparer.OrdinalIgnoreCase);

    /// <summary>分区键：已登录用用户 id（换 IP 也绕不过），未登录用来源 IP（没法按用户区分）。</summary>
    private static string ResolvePartition(HttpContext context)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(userId)) return $"user:{userId}";

        // 反向代理后面 RemoteIpAddress 是代理地址，部署时需由代理转发真实来源（见交接文档的部署说明）。
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"ip:{address}";
    }
}
