using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AIToHuman.Application.Idempotency;
using AIToHuman.Domain.Idempotency;

namespace AIToHuman.Api.Idempotency;

/// <summary>
/// 写接口的幂等键支持：客户端带 <c>Idempotency-Key</c> 重试时，服务端回放上一次的响应，
/// 而不是把加价、报名、选人、提交这类动作再做一遍（PRD 非功能要求里的"写接口支持幂等键"）。
///
/// 规则：
/// - 只对已登录用户的 <c>/api/v1</c> 写请求生效（POST/PUT/PATCH/DELETE）；匿名请求与 SSE 流（<c>/ai/plan/stream</c>）不参与。
/// - 不带该请求头时行为完全不变，因此是可选能力，老前端不受影响。
/// - 同一个键配不同请求体 → <c>409</c>，这是客户端复用键的错误，不能拿旧响应糊过去。
/// - 占位后 2 分钟内没写完（上次执行中途挂了）视为过期，允许重新执行。
/// - 业务抛异常或响应体过大时不记录结果：宁可让重试重新执行，也不回放半截响应。
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
{
    public const string HeaderName = "Idempotency-Key";

    /// <summary>响应体缓存上限之内的类型才缓存：文件与流不参与回放。</summary>
    private static readonly string[] CacheableMethods = ["POST", "PUT", "PATCH", "DELETE"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!TryResolveKey(context, out var key, out var userId))
        {
            await next(context);
            return;
        }

        var store = context.RequestServices.GetRequiredService<IIdempotencyStore>();
        var timeProvider = context.RequestServices.GetRequiredService<TimeProvider>();
        var now = timeProvider.GetUtcNow();
        var requestHash = await ComputeRequestHashAsync(context);

        if (store.Find(userId, key) is { } found)
        {
            if (found.IsStale(now))
            {
                // 上次执行中途挂了：清掉占位，让这次请求真正执行。
                store.Remove(found);
            }
            else
            {
                await RespondFromRecordAsync(context, found, requestHash);
                return;
            }
        }

        var entry = IdempotencyEntry.Start(userId, key, requestHash, now);
        if (!store.TryStart(entry, out var existing))
        {
            // 并发：另一个请求刚占位。可能是同一请求的重复提交（回放或 409），也可能正在处理中。
            if (existing is not null)
            {
                await RespondFromRecordAsync(context, existing, requestHash);
                return;
            }

            await WriteProblemAsync(context, StatusCodes.Status409Conflict, "同一个幂等键的请求正在处理中，请稍后重试。");
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
        }
        catch
        {
            // 异常会由外层的异常处理中间件变成响应；这里只负责把占位清掉，让重试能重新执行。
            context.Response.Body = originalBody;
            store.Remove(entry);
            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        var bodyText = context.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
            ? await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync()
            : null;

        // 5xx 不缓存：服务端出错时重试应当真的重跑一次。
        if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            store.Remove(entry);
        }
        else if (bodyText is null)
        {
            // 非 JSON 响应（例如文件下载）不缓存响应体，直接放行。
            store.Remove(entry);
        }
        else
        {
            store.Complete(entry, context.Response.StatusCode, bodyText, context.Response.ContentType, now);

            // 响应体太大时领域层不会记住它，占位也会被删掉（见 Complete 的返回值）——那种情况下重试会重新执行。
            if (store.Find(userId, key) is null)
            {
                logger.LogWarning("幂等键 {Key} 的响应体过大，未缓存；同一键的重试会重新执行请求。", key);
            }
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody);
    }

    /// <summary>取出幂等键与用户；不满足条件就说明这个请求不参与幂等（直接放行）。</summary>
    private static bool TryResolveKey(HttpContext context, out string key, out Guid userId)
    {
        key = string.Empty;
        userId = Guid.Empty;

        var header = context.Request.Headers[HeaderName].ToString().Trim();
        if (header.Length is < 1 or > IdempotencyEntry.MaxKeyLength) return false;
        key = header;
        if (!CacheableMethods.Contains(context.Request.Method, StringComparer.OrdinalIgnoreCase)) return false;
        if (!context.Request.Path.StartsWithSegments("/api/v1")) return false;

        // SSE 流是长连接、且不能回放；认证接口在登录前调用，没有用户可绑定。
        if (context.Request.Path.StartsWithSegments("/api/v1/ai")) return false;
        if (context.Request.Path.StartsWithSegments("/api/v1/auth")) return false;
        if (context.Request.Path.StartsWithSegments("/api/v1/session")) return false;

        return Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out userId) && userId != Guid.Empty;
    }

    /// <summary>把已记录的响应原样回放；请求体不一致时说明客户端复用了键，报 409 而不是糊过去。</summary>
    private static async Task RespondFromRecordAsync(HttpContext context, IdempotencyEntry entry, string requestHash)
    {
        if (!entry.Matches(requestHash))
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, "这个幂等键已经用于另一个请求：请为不同的请求使用不同的 Idempotency-Key。");
            return;
        }

        if (!entry.IsCompleted)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, "同一个幂等键的请求正在处理中，请稍后重试。");
            return;
        }

        context.Response.StatusCode = entry.StatusCode ?? StatusCodes.Status200OK;
        context.Response.ContentType = entry.ContentType ?? "application/json; charset=utf-8";
        context.Response.Headers["Idempotency-Replayed"] = "true";
        if (!string.IsNullOrEmpty(entry.ResponseBody)) await context.Response.WriteAsync(entry.ResponseBody);
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string detail)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            title = statusCode == StatusCodes.Status409Conflict ? "幂等键冲突" : "请求无法处理",
            status = statusCode,
            detail,
            instance = context.Request.Path.Value
        });
    }

    /// <summary>请求指纹：方法 + 路径 + 查询串 + 请求体。允许请求体被重复读取。</summary>
    private static async Task<string> ComputeRequestHashAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        context.Request.Body.Position = 0;
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;

        var payload = $"{context.Request.Method}\n{context.Request.Path}\n{context.Request.QueryString}\n{body}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
