using AIToHuman.Application.Tasks;
using AIToHuman.Application.Conversations;
using AIToHuman.Application.Common;
using AIToHuman.Contracts.Notifications;
using AIToHuman.Contracts.Orders;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Storage;
using AIToHuman.Contracts.Conversations;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Contracts;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Conversations;
using AIToHuman.Domain.Orders;
using AIToHuman.Infrastructure.Tasks;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Conversations;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Notifications;
using AIToHuman.Api;
using AIToHuman.Api.Notifications;
using AIToHuman.Api.Orders;
using AIToHuman.Api.Settings;
using AIToHuman.Application.Settings;
using AIToHuman.Contracts.Settings;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
// 运营可配置项：生效值 = 数据库覆盖 → 环境变量/配置文件 → 代码默认值（设置目录见 SettingCatalog）。
// 机密用 Data Protection 加密落库，密钥环来自部署环境（DataProtection__KeysPath 可指到持久卷）。
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("AIToHuman");
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

builder.Services.AddSingleton<SettingsCache>();
builder.Services.AddSingleton<ISettingsProvider, DatabaseSettingsProvider>();
builder.Services.AddSingleton<SettingsSnapshotReloader>();
builder.Services.AddSingleton<ISettingsReloader>(provider => provider.GetRequiredService<SettingsSnapshotReloader>());
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddScoped<SettingsSnapshotBuilder>();
builder.Services.AddScoped<ISettingProbe, SettingProbe>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddHostedService<SettingsRefreshService>();
builder.Services.AddHttpClient("settings-probe", client => client.Timeout = Timeout.InfiniteTimeSpan);

builder.Services.AddHttpClient<AiPlanningService>(client => client.Timeout = Timeout.InfiniteTimeSpan);

var signingKey = builder.Configuration["Authentication:SigningKey"];
if (string.IsNullOrWhiteSpace(signingKey) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("生产环境必须配置 Authentication__SigningKey。");

var postgresConnection = builder.Configuration.GetConnectionString("Postgres");
var usePostgres = !string.IsNullOrWhiteSpace(postgresConnection);
if (usePostgres)
{
    builder.Services.AddDbContext<TaskDbContext>(options => options.UseNpgsql(postgresConnection));
    builder.Services.AddScoped<ITaskRepository, EfTaskRepository>();
    builder.Services.AddScoped<IOrderRepository, EfOrderRepository>();
    builder.Services.AddScoped<IReviewRepository, EfReviewRepository>();
    builder.Services.AddScoped<IConversationRepository, EfConversationRepository>();
    builder.Services.AddScoped<INotificationRepository, EfNotificationRepository>();
    builder.Services.AddScoped<IOrderMessageRepository, EfOrderMessageRepository>();
    builder.Services.AddScoped<IEvidenceRepository, EfEvidenceRepository>();
    builder.Services.AddScoped<ISystemSettingsRepository, EfSystemSettingRepository>();
    builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
    builder.Services.AddScoped<AuthService>();
}
else
{
    builder.Services.AddSingleton<ITaskRepository, InMemoryTaskRepository>();
    builder.Services.AddSingleton<IOrderRepository, InMemoryOrderRepository>();
    builder.Services.AddSingleton<IReviewRepository, InMemoryReviewRepository>();
    builder.Services.AddSingleton<IConversationRepository, InMemoryConversationRepository>();
    builder.Services.AddSingleton<INotificationRepository, InMemoryNotificationRepository>();
    builder.Services.AddSingleton<IOrderMessageRepository, InMemoryOrderMessageRepository>();
    builder.Services.AddSingleton<IEvidenceRepository, InMemoryEvidenceRepository>();
    builder.Services.AddSingleton<ISystemSettingsRepository, InMemorySystemSettingRepository>();
    builder.Services.AddSingleton<IUnitOfWork, InMemoryUnitOfWork>();
}
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSignalR();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<OrderChatService>();
// 凭证文件存储与内容扫描都由运营配置决定（storage.provider / evidence.scanner.provider）：
// local 写本机目录，s3 走 S3 兼容对象存储（自研 SigV4，不依赖厂商 SDK）。
builder.Services.AddSingleton<LocalFileStorage>();
builder.Services.AddHttpClient<S3FileStorage>(client => client.Timeout = Timeout.InfiniteTimeSpan);
// IFileStorage 是 scoped：SettingsFileStorage 注入的是 typed client（transient），
// 放进单例会把 HttpClient 的处理器永久钉死，scoped 则与 EvidenceService 的生命周期一致。
builder.Services.AddScoped<IFileStorage, SettingsFileStorage>();
builder.Services.AddHttpClient<HttpEvidenceScanner>(client => client.Timeout = Timeout.InfiniteTimeSpan);
// 扫描实现按 evidence.scanner.provider 分派：none 显式放行、http 调外部服务、clamav 走 clamd INSTREAM。
// 三者都依赖 scoped 的 IFileStorage，因此注册成 scoped：放进单例会被 DI 校验直接拒绝启动。
builder.Services.AddScoped<ClamAvEvidenceScanner>();
builder.Services.AddScoped<IEvidenceScanner, SettingsEvidenceScanner>();
builder.Services.AddScoped<EvidenceService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<ConversationService>();
// Outbox 派发：把已落库但还没推送的通知发给在线客户端。
builder.Services.AddHostedService<NotificationDispatcher>();
// 待扫描凭证的自动重扫：扫描服务不可用时不让凭证永远卡在“不可下载”。
builder.Services.AddHostedService<EvidenceRescanService>();
builder.Services.AddProblemDetails();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"].ToString();
            if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                context.Token = accessToken;
            return Task.CompletedTask;
        }
    };
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(signingKey ?? "QUlUb0h1bWFuLWxvY2FsLWRldi1rZXktMzItYnl0ZXMhIQ==")),
        ClockSkew = TimeSpan.FromSeconds(30)
    };
});
// 运营后台：管理员名单来自部署配置（Admin__UserIds / Admin__Emails），刻意不放配置表，避免权限自举。
var adminAccess = new AdminAccess(builder.Configuration);
builder.Services.AddSingleton(adminAccess);
builder.Services.AddAuthorization(options => options.AddPolicy(
    AdminAccess.PolicyName,
    policy => policy.RequireAssertion(context => adminAccess.IsAdmin(context.User))));
builder.Services.AddCors(options => options.AddPolicy("development", policy => policy
    .WithOrigins("http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

if (app.Environment.IsDevelopment() && usePostgres)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TaskDbContext>();
    try
    {
        ApplyDatabaseMigrations(app.Logger, db);
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "数据库迁移失败。请检查 ConnectionStrings__Postgres；本次进程将继续启动，但数据库接口不可用。");
    }
}

if (adminAccess.IsEmpty)
    app.Logger.LogWarning("未配置运营管理员（Admin__UserIds 或 Admin__Emails），/api/v1/admin/settings 会对所有请求返回 403。");

// 配置快照必须在开始处理请求前就绪；数据库暂时不可用时先退回环境变量与默认值，后台任务会继续重试。
SettingsStartup.Initialize(app.Services, app.Logger);

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = exception switch
    {
        AiPlanningTimeoutException => (StatusCodes.Status504GatewayTimeout, "AI 规划服务响应超时"),
        AiPlanningUnavailableException => (StatusCodes.Status502BadGateway, "AI 规划服务暂时不可用"),
        AiPlanningUpstreamException => (StatusCodes.Status502BadGateway, "AI 规划服务请求失败"),
        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "资源已被其他操作更新"),
        ConcurrencyConflictException => (StatusCodes.Status409Conflict, "配置已被其他操作更新"),
        ArgumentException => (StatusCodes.Status400BadRequest, "请求参数不正确"),
        DomainException => (StatusCodes.Status422UnprocessableEntity, "业务规则不允许该操作"),
        BadHttpRequestException => (StatusCodes.Status400BadRequest, "请求格式不正确"),
        InvalidOperationException invalid when invalid.Message.Contains("已注册", StringComparison.Ordinal) => (StatusCodes.Status409Conflict, "资源冲突"),
        InvalidOperationException => (StatusCodes.Status422UnprocessableEntity, "请求参数不符合要求"),
        KeyNotFoundException => (StatusCodes.Status404NotFound, "资源不存在"),
        UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "无权执行该操作"),
        _ => (StatusCodes.Status500InternalServerError, "服务器内部错误")
    };

    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = status,
        Title = title,
        Detail = exception switch
        {
            // EF 的并发异常文案是技术细节，这里换成用户能理解并据以重试的说明。
            DbUpdateConcurrencyException => "该任务或订单刚刚被其他人更新，请刷新后重试。",
            ArgumentException or DomainException or KeyNotFoundException or UnauthorizedAccessException or AiPlanningTimeoutException or AiPlanningUnavailableException or AiPlanningUpstreamException or ConcurrencyConflictException => exception.Message,
            _ => "请求暂时无法处理。"
        },
        Instance = context.Request.Path
    });
}));

if (app.Environment.IsDevelopment()) app.UseCors("development");
app.UseAuthentication();
app.UseAuthorization();
app.MapHub<NotificationsHub>("/hubs/notifications");

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "AIToHuman.Api", utc = DateTimeOffset.UtcNow }));

app.MapGet("/api/v1/session/dev", (IHostEnvironment environment) =>
{
    if (!environment.IsDevelopment()) return Results.NotFound();
    return Results.Ok(new DevSessionResponse(Guid.Parse("20000000-0000-0000-0000-000000000001"), "开发服务者", "worker", ["owner", "worker"], true));
});

var auth = app.MapGroup("/api/v1/auth");
auth.MapPost("/register", (RegisterRequest request, IServiceProvider services) => services.GetService<AuthService>() is { } service
    ? Results.Ok(service.Register(request))
    : Results.Problem("未配置 PostgreSQL，认证功能暂不可用。", statusCode: StatusCodes.Status503ServiceUnavailable));
auth.MapPost("/login", (LoginRequest request, IServiceProvider services) => services.GetService<AuthService>() is { } service
    ? Results.Ok(service.Login(request))
    : Results.Problem("未配置 PostgreSQL，认证功能暂不可用。", statusCode: StatusCodes.Status503ServiceUnavailable));
auth.MapPost("/switch-role", (SwitchRoleRequest request, ClaimsPrincipal user, IServiceProvider services) =>
{
    if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Results.Unauthorized();
    return services.GetService<AuthService>() is { } service
        ? Results.Ok(service.SwitchRole(userId, request.Role))
        : Results.Problem("未配置 PostgreSQL，认证功能暂不可用。", statusCode: StatusCodes.Status503ServiceUnavailable);
}).RequireAuthorization();
auth.MapGet("/me", (ClaimsPrincipal user, AdminAccess adminAccess) => Results.Ok(new CurrentUserResponse(
    Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
    user.FindFirstValue(ClaimTypes.Email)!,
    user.FindFirstValue(ClaimTypes.Name)!,
    user.FindFirstValue(ClaimTypes.Role)!,
    adminAccess.IsAdmin(user)))).RequireAuthorization();

var tasks = app.MapGroup("/api/v1/tasks");
// 大厅列表：按区域与悬赏区间筛选，游标分页（按截止时间升序）。
tasks.MapGet("/", (string? district, decimal? minReward, decimal? maxReward, int? limit, string? cursor, TaskService service) =>
    Results.Ok(service.SearchPublished(new TaskSearchRequest(district, minReward, maxReward, limit ?? 12, cursor))));
tasks.MapGet("/mine", (Guid? ownerId, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) =>
{
    EnsureRole(user, "owner", environment);
    return Results.Ok(service.ListMine(ResolveUserId(user, ownerId ?? Guid.Empty, environment)));
});
// 公开详情：草稿只对所有者可见；这里用可空身份，匿名访问草稿一律 404。
tasks.MapGet("/{id:guid}", (Guid id, Guid? userId, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) =>
    service.GetPublic(id, TryResolveUserId(user, environment, userId)) is { } task ? Results.Ok(task) : Results.NotFound());
// 精确执行地址：只有所有者与被选中的服务者可读，其他人 403。
tasks.MapGet("/{id:guid}/execution-address", (Guid id, Guid? userId, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) =>
    Results.Ok(new TaskExecutionAddressResponse(id, service.GetExecutionAddress(id, ResolveUserId(user, userId ?? Guid.Empty, environment)))));
tasks.MapGet("/{id:guid}/applications", (Guid id, Guid? ownerId, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) =>
{
    EnsureRole(user, "owner", environment);
    return Results.Ok(service.ListApplications(id, ResolveUserId(user, ownerId ?? Guid.Empty, environment)));
});
tasks.MapPost("/", (CreateTaskRequest request, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) =>
{
    EnsureRole(user, "owner", environment);
    var task = service.Create(request with { OwnerId = ResolveUserId(user, request.OwnerId, environment) });
    return Results.Created($"/api/v1/tasks/{task.Id}", task);
});
// ownerId 只作为 Development 合成会话的回退；携带 JWT 时以令牌身份为准，因此这里不必强制传查询参数。
tasks.MapPost("/{id:guid}/publish", (Guid id, Guid? ownerId, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) =>
{
    EnsureRole(user, "owner", environment);
    return Results.Ok(service.Publish(id, ResolveUserId(user, ownerId ?? Guid.Empty, environment)));
});
tasks.MapPost("/{id:guid}/increase-reward", (Guid id, Guid? ownerId, IncreaseRewardRequest request, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) =>
{
    EnsureRole(user, "owner", environment);
    return Results.Ok(service.IncreaseReward(id, ResolveUserId(user, ownerId ?? Guid.Empty, environment), request));
});
tasks.MapPost("/{id:guid}/applications", (Guid id, ApplyForTaskRequest request, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) =>
{
    EnsureRole(user, "worker", environment);
    var workerId = ResolveUserId(user, request.WorkerId, environment);
    return Results.Ok(service.Apply(id, request with { WorkerId = workerId }));
});
tasks.MapPost("/{id:guid}/applications/{applicationId:guid}/select", (Guid id, Guid applicationId, SelectApplicationRequest request, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) =>
{
    EnsureRole(user, "owner", environment);
    var ownerId = ResolveUserId(user, request.OwnerId, environment);
    return Results.Ok(service.Select(id, applicationId, request with { OwnerId = ownerId }));
});
tasks.MapGet("/{id:guid}/order", (Guid id, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) =>
{
    var claim = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (!Guid.TryParse(claim, out var userId))
    {
        if (!environment.IsDevelopment()) throw new UnauthorizedAccessException("请先登录后再查看订单。");
        userId = Guid.Empty;
    }
    return service.GetOrderByTask(id, userId) is { } order ? Results.Ok(order) : Results.NotFound();
});

app.MapPost("/api/v1/reward-suggestions", (RewardSuggestionRequest request, TaskService service) => Results.Ok(service.SuggestReward(request)));

var conversations = app.MapGroup("/api/v1/conversations");
conversations.MapPost("/", (CreateConversationRequest request, ClaimsPrincipal user, IHostEnvironment environment, ConversationService service) =>
    Results.Ok(service.Create(ResolveUserId(user, request.UserId ?? Guid.Empty, environment))));
conversations.MapGet("/{id:guid}", (Guid id, Guid? userId, ClaimsPrincipal user, IHostEnvironment environment, ConversationService service) =>
    Results.Ok(service.Get(id, ResolveUserId(user, userId ?? Guid.Empty, environment))));
conversations.MapGet("/", (Guid? userId, int? limit, ClaimsPrincipal user, IHostEnvironment environment, ConversationService service) =>
    Results.Ok(service.List(ResolveUserId(user, userId ?? Guid.Empty, environment), limit ?? 20)));

var notifications = app.MapGroup("/api/v1/notifications");
notifications.MapGet("/", (Guid? userId, int? limit, ClaimsPrincipal user, IHostEnvironment environment, NotificationService service) =>
    Results.Ok(service.List(ResolveUserId(user, userId ?? Guid.Empty, environment), limit ?? 20)));
notifications.MapPost("/read", (MarkNotificationsReadRequest request, ClaimsPrincipal user, IHostEnvironment environment, NotificationService service) =>
    Results.Ok(new { unreadCount = service.MarkRead(ResolveUserId(user, request.UserId ?? Guid.Empty, environment), request.Ids) }));

app.MapPost("/api/v1/ai/plan/stream", async (AiTaskPlanRequest request, AiPlanningService service, ConversationService conversationService, HttpContext context, IHostEnvironment environment, ClaimsPrincipal user, CancellationToken cancellationToken) =>
{
    // 会话归属、消息长度这类可判定的问题在开始写流之前抛出，仍然返回正常 HTTP 状态码；
    // 一旦开始写 SSE，就只有模型侧的问题才走流内 error 事件。
    var userId = ResolveUserId(user, request.UserId ?? Guid.Empty, environment);
    var userMessage = request.Message?.Trim() ?? string.Empty;
    if (userMessage.Length is < 1 or > ConversationLimits.MaxMessageLength)
        throw new InvalidOperationException($"消息内容应为 1 至 {ConversationLimits.MaxMessageLength} 个字符。");

    var history = conversationService.BuildHistory(request.ConversationId, userId, ConversationLimits.MaxHistoryMessages - 1);
    var messages = history.Append(new AiConversationMessage("user", userMessage)).ToArray();

    AiPlanStreamWriter.PrepareResponse(context.Response);

    try
    {
        await foreach (var streamEvent in service.PlanStreamAsync(messages, cancellationToken))
        {
            switch (streamEvent)
            {
                case AiPlanningDeltaEvent delta:
                    await AiPlanStreamWriter.WriteDeltaAsync(context.Response, delta.Text, cancellationToken);
                    break;
                case AiPlanningRestartEvent restart:
                    await AiPlanStreamWriter.WriteRestartAsync(context.Response, restart.Reason, cancellationToken);
                    break;
                case AiPlanningCompletedEvent completed:
                    // 先落库再通知前端完成：保存失败时用户应该看到错误，而不是以为这轮已经保存。
                    conversationService.AppendTurn(request.ConversationId, userId, userMessage, completed.Turn);
                    await AiPlanStreamWriter.WriteCompleteAsync(context.Response, completed.Turn, cancellationToken);
                    break;
            }
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // The browser closed the request; there is no client left to notify.
    }
    catch (Exception exception) when (exception is AiPlanningTimeoutException or AiPlanningUnavailableException or AiPlanningUpstreamException or InvalidOperationException or DomainException or JsonException or DbUpdateException)
    {
        if (!cancellationToken.IsCancellationRequested)
            await AiPlanStreamWriter.WriteErrorAsync(context.Response, exception, cancellationToken);
    }
});
app.MapGet("/api/v1/orders", (Guid? userId, ClaimsPrincipal user, IHostEnvironment environment, TaskService service, OrderChatService chat) =>
{
    var resolved = ResolveUserId(user, userId ?? Guid.Empty, environment);
    var orders = service.ListOrders(resolved);
    // 订单列表顺带返回各自会话的未读数，避免前端逐单查询。
    var unread = chat.UnreadByOrder(resolved, orders.Select(item => item.Id).ToArray());
    return Results.Ok(orders.Select(item => item with { UnreadMessageCount = unread.GetValueOrDefault(item.Id) }).ToArray());
});

var orderMessages = app.MapGroup("/api/v1/orders/{orderId:guid}/messages");
orderMessages.MapGet("/", (Guid orderId, int? limit, Guid? userId, ClaimsPrincipal user, IHostEnvironment environment, OrderChatService service) =>
    Results.Ok(service.List(orderId, ResolveUserId(user, userId ?? Guid.Empty, environment), limit ?? 50)));
orderMessages.MapPost("/", (Guid orderId, SendOrderMessageRequest request, ClaimsPrincipal user, IHostEnvironment environment, OrderChatService service) =>
    Results.Ok(service.Send(orderId, ResolveUserId(user, request.SenderId ?? Guid.Empty, environment), request.Content)));
orderMessages.MapPost("/read", (Guid orderId, Guid? userId, ClaimsPrincipal user, IHostEnvironment environment, OrderChatService service) =>
    Results.Ok(new { unreadCount = service.MarkRead(orderId, ResolveUserId(user, userId ?? Guid.Empty, environment)) }));

// 执行凭证：只有订单服务者能上传，只有订单双方能列出与下载。
var orderEvidence = app.MapGroup("/api/v1/orders/{orderId:guid}/evidence");
orderEvidence.MapGet("/", (Guid orderId, Guid? userId, ClaimsPrincipal user, IHostEnvironment environment, EvidenceService service) =>
    Results.Ok(service.List(orderId, ResolveUserId(user, userId ?? Guid.Empty, environment))));
orderEvidence.MapPost("/", async (Guid orderId, IFormFile file, Guid? userId, ClaimsPrincipal user, IHostEnvironment environment, EvidenceService service, CancellationToken cancellationToken) =>
{
    var uploaderId = ResolveUserId(user, userId ?? Guid.Empty, environment);
    // 先用声明的长度拦掉超大上传，避免把大文件读进内存；上限来自运营配置，硬上限在领域层。
    var limits = service.Limits();
    if (file.Length > limits.MaxSizeBytes)
        return Results.Problem(title: "凭证过大", detail: $"凭证大小不能超过 {limits.MaxSizeDisplay}。", statusCode: StatusCodes.Status413PayloadTooLarge);

    await using var stream = file.OpenReadStream();
    return Results.Ok(await service.UploadAsync(orderId, uploaderId, file.FileName, file.ContentType, file.Length, stream, cancellationToken));
}).DisableAntiforgery();

app.MapGet("/api/v1/evidence/{id:guid}/content", async (Guid id, Guid? userId, ClaimsPrincipal user, IHostEnvironment environment, EvidenceService service, HttpContext context, CancellationToken cancellationToken) =>
{
    var (evidence, content) = await service.DownloadAsync(id, ResolveUserId(user, userId ?? Guid.Empty, environment), cancellationToken);
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    // 下载名由系统生成，不使用用户原始文件名，避免响应头注入。
    return Results.File(content, evidence.ContentType, $"evidence-{evidence.Id:N}.{OrderEvidence.ExtensionFor(evidence.ContentType)}");
});
// 直连下载地址：只有对象存储支持；权限与扫描状态判定和 /content 完全一致，只是把取字节的活交给浏览器。
app.MapGet("/api/v1/evidence/{id:guid}/download-url", (Guid id, Guid? userId, ClaimsPrincipal user, IHostEnvironment environment, EvidenceService service) =>
    Results.Ok(service.CreateDownloadUrl(id, ResolveUserId(user, userId ?? Guid.Empty, environment))));
app.MapPost("/api/v1/orders/{id:guid}/start", (Guid id, OrderActionRequest request, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) => Results.Ok(service.StartOrder(id, ResolveUserId(user, request.ActorId, environment))));
app.MapPost("/api/v1/orders/{id:guid}/submit", (Guid id, OrderActionRequest request, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) => Results.Ok(service.SubmitOrder(id, ResolveUserId(user, request.ActorId, environment), request.Note)));
app.MapPost("/api/v1/orders/{id:guid}/approve", (Guid id, OrderActionRequest request, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) => Results.Ok(service.ApproveOrder(id, ResolveUserId(user, request.ActorId, environment), request.Note)));
app.MapPost("/api/v1/orders/{id:guid}/reject", (Guid id, OrderActionRequest request, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) => Results.Ok(service.RejectOrder(id, ResolveUserId(user, request.ActorId, environment), request.Note)));
app.MapPost("/api/v1/orders/{id:guid}/resume", (Guid id, OrderActionRequest request, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) => Results.Ok(service.ResumeOrder(id, ResolveUserId(user, request.ActorId, environment))));
app.MapGet("/api/v1/orders/{id:guid}/reviews", (Guid id, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) => Results.Ok(service.ListReviews(id, ResolveUserId(user, Guid.Empty, environment))));
app.MapPost("/api/v1/orders/{id:guid}/reviews", (Guid id, CreateReviewRequest request, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) => Results.Ok(service.CreateReview(id, request with { ReviewerId = ResolveUserId(user, request.ReviewerId, environment) }, ResolveUserId(user, request.ReviewerId, environment))));
app.MapGet("/api/v1/users/{id:guid}/review-summary", (Guid id, TaskService service) => Results.Ok(service.GetReviewSummary(id)));

// 运营配置：只有管理员名单里的人可以读写。机密值只返回掩码与指纹，明文永远不出服务端。
var adminSettings = app.MapGroup("/api/v1/admin/settings").RequireAuthorization(AdminAccess.PolicyName);
adminSettings.MapGet("/", (SettingsService service) => Results.Ok(service.List()));
adminSettings.MapGet("/audits", (int? limit, SettingsService service) => Results.Ok(service.Audits(limit ?? 50)));
adminSettings.MapGet("/{key}", (string key, SettingsService service) => Results.Ok(service.Get(key)));
adminSettings.MapPut("/{key}", async (string key, UpdateSettingRequest request, ClaimsPrincipal user, SettingsService service, CancellationToken cancellationToken) =>
{
    var actorId = ResolveAdminId(user);
    var updated = await service.UpdateAsync(key, request, actorId, cancellationToken);
    app.Logger.LogInformation("运营配置 {Key} 已由 {ActorId} 更新，当前来源 {Source}。", updated.Key, actorId, updated.Source);
    return Results.Ok(updated);
});
adminSettings.MapDelete("/{key}", async (string key, ClaimsPrincipal user, SettingsService service, CancellationToken cancellationToken) =>
{
    var actorId = ResolveAdminId(user);
    var reset = await service.ResetAsync(key, actorId, cancellationToken);
    app.Logger.LogInformation("运营配置 {Key} 已由 {ActorId} 恢复默认，当前来源 {Source}。", reset.Key, actorId, reset.Source);
    return Results.Ok(reset);
});
adminSettings.MapPost("/{key}/test", async (string key, SettingsService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.TestAsync(key, cancellationToken)));

app.Run();

/// <summary>运营接口的操作人：管理员策略已经放行，这里取出用于审计的用户标识。</summary>
static Guid ResolveAdminId(ClaimsPrincipal user) =>
    Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
        ? userId
        : throw new UnauthorizedAccessException("运营接口必须携带可识别的管理员身份。");

static Guid ResolveUserId(ClaimsPrincipal user, Guid developmentFallback, IHostEnvironment environment)
{
    var claim = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (Guid.TryParse(claim, out var userId)) return userId;
    if (environment.IsDevelopment() && developmentFallback != Guid.Empty) return developmentFallback;
    throw new UnauthorizedAccessException("请先登录后再执行该操作。");
}

/// <summary>可空版本：匿名访问时返回 null，用于「草稿只有所有者可见」这类按身份分流的查询。</summary>
static Guid? TryResolveUserId(ClaimsPrincipal user, IHostEnvironment environment, Guid? developmentFallback = null)
{
    if (Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return userId;
    if (environment.IsDevelopment() && developmentFallback is { } fallback && fallback != Guid.Empty) return fallback;
    return null;
}

static void EnsureRole(ClaimsPrincipal user, string requiredRole, IHostEnvironment environment)
{
    if (!user.Identity?.IsAuthenticated ?? true)
    {
        if (environment.IsDevelopment()) return;
        throw new UnauthorizedAccessException("请先登录后再执行该操作。");
    }

    if (!user.IsInRole(requiredRole)) throw new UnauthorizedAccessException($"当前角色不能执行此操作，需要 {requiredRole} 角色。");
}

/// <summary>
/// 应用 EF Core 迁移。这里只在 Development 自动迁移，生产环境由部署流程执行。
/// 早期版本用 EnsureCreated() + 幂等 SQL 建表，这类数据库没有迁移历史：
/// 表已与当前模型一致时把已有迁移记为已应用；表不齐时明确报错，避免在错误的 schema 上继续运行。
/// </summary>
static void ApplyDatabaseMigrations(ILogger logger, TaskDbContext db)
{
    if (!db.Database.GetAppliedMigrations().Any() && db.Database.GetPendingMigrations().Any() && LegacySchemaExists(db))
    {
        var missing = MissingSchema(db);
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"本地数据库不是由迁移创建的，并且缺少 {missing.Length} 处结构（例如 {string.Join("、", missing.Take(5))}）。请删除并重建本地数据库，或按迁移内容手动补齐后重启。");
        }

        BaselineMigrations(db, logger);
    }

    // Migrate() 会创建尚不存在的数据库，并应用所有待执行迁移。
    db.Database.Migrate();
    logger.LogInformation("数据库迁移已应用，共 {Count} 个迁移。", db.Database.GetAppliedMigrations().Count());
}

/// <summary>目标库还不存在、或连接不可用时返回 false，由 Migrate() 负责建库。</summary>
static bool LegacySchemaExists(TaskDbContext db)
{
    try
    {
        return TableExists(db, "users");
    }
    catch (Exception exception) when (exception is System.Data.Common.DbException or InvalidOperationException)
    {
        return false;
    }
}

static void BaselineMigrations(TaskDbContext db, ILogger logger)
{
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
            "MigrationId" character varying(150) NOT NULL,
            "ProductVersion" character varying(32) NOT NULL,
            CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
        );
        """);

    var productVersion = typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "10.0.0";
    var migrations = db.Database.GetMigrations().ToArray();
    foreach (var migration in migrations)
    {
        db.Database.ExecuteSqlRaw(
            "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({0}, {1}) ON CONFLICT (\"MigrationId\") DO NOTHING",
            migration,
            productVersion);
    }

    logger.LogWarning("检测到由早期 EnsureCreated 建出的数据库，已把 {Count} 个迁移记为已应用。若之后出现列缺失错误，请删除并重建本地数据库。", migrations.Length);
}

/// <summary>对比模型期望的「表名.列名」与物理库实际结构，返回缺失项。</summary>
static string[] MissingSchema(TaskDbContext db)
{
    var expected = db.Model.GetEntityTypes()
        .SelectMany(entity => entity.GetProperties().Select(property => (Table: entity.GetTableName(), Column: property.GetColumnName())))
        .Where(item => !string.IsNullOrEmpty(item.Table))
        .Select(item => $"{item.Table}.{item.Column}")
        .Distinct()
        .ToArray();

    var existing = db.Database
        .SqlQueryRaw<string>("SELECT table_name || '.' || column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public'")
        .AsEnumerable()
        .ToHashSet(StringComparer.Ordinal);

    return expected.Where(name => !existing.Contains(name)).ToArray();
}

static bool TableExists(TaskDbContext db, string table) =>
    db.Database
        .SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_name = {0}", table)
        .AsEnumerable()
        .Single() > 0;

public partial class Program;
