using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Contracts;
using AIToHuman.Domain.Common;
using AIToHuman.Infrastructure.Tasks;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

var signingKey = builder.Configuration["Authentication:SigningKey"];
if (string.IsNullOrWhiteSpace(signingKey) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("生产环境必须配置 Authentication__SigningKey。");

var postgresConnection = builder.Configuration.GetConnectionString("Postgres");
var usePostgres = !string.IsNullOrWhiteSpace(postgresConnection);
if (usePostgres)
{
    builder.Services.AddDbContext<TaskDbContext>(options => options.UseNpgsql(postgresConnection));
    builder.Services.AddScoped<ITaskRepository, EfTaskRepository>();
    builder.Services.AddScoped<AuthService>();
}
else builder.Services.AddSingleton<ITaskRepository, InMemoryTaskRepository>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<TaskService>();
builder.Services.AddProblemDetails();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
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
builder.Services.AddAuthorization();
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
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS users (
            "Id" uuid NOT NULL,
            "Email" character varying(320) NOT NULL,
            "DisplayName" character varying(80) NOT NULL,
            "PasswordHash" character varying(512) NOT NULL,
            "Role" character varying(16) NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_users" PRIMARY KEY ("Id")
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_Email" ON users ("Email");
        """);
}
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "PostgreSQL 连接失败。请检查 ConnectionStrings__Postgres；本次进程将继续启动，但数据库接口不可用。 ");
    }
}

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = exception switch
    {
        DomainException => (StatusCodes.Status422UnprocessableEntity, "业务规则不允许该操作"),
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
        Detail = exception is DomainException or KeyNotFoundException or UnauthorizedAccessException ? exception.Message : "请求暂时无法处理。",
        Instance = context.Request.Path
    });
}));

if (app.Environment.IsDevelopment()) app.UseCors("development");
app.UseAuthentication();
app.UseAuthorization();

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
auth.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new CurrentUserResponse(
    Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
    user.FindFirstValue(ClaimTypes.Email)!,
    user.FindFirstValue(ClaimTypes.Name)!,
    user.FindFirstValue(ClaimTypes.Role)!))).RequireAuthorization();

var tasks = app.MapGroup("/api/v1/tasks");
tasks.MapGet("/", (TaskService service) => Results.Ok(service.ListPublished()));
tasks.MapGet("/{id:guid}", (Guid id, TaskService service) => service.GetPublic(id) is { } task ? Results.Ok(task) : Results.NotFound());
tasks.MapGet("/{id:guid}/applications", (Guid id, Guid ownerId, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) =>
{
    EnsureRole(user, "owner", environment);
    return Results.Ok(service.ListApplications(id, ResolveUserId(user, ownerId, environment)));
});
tasks.MapPost("/", (CreateTaskRequest request, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) =>
{
    EnsureRole(user, "owner", environment);
    var task = service.Create(request with { OwnerId = ResolveUserId(user, request.OwnerId, environment) });
    return Results.Created($"/api/v1/tasks/{task.Id}", task);
});
tasks.MapPost("/{id:guid}/publish", (Guid id, Guid ownerId, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) =>
{
    EnsureRole(user, "owner", environment);
    return Results.Ok(service.Publish(id, ResolveUserId(user, ownerId, environment)));
});
tasks.MapPost("/{id:guid}/increase-reward", (Guid id, Guid ownerId, IncreaseRewardRequest request, ClaimsPrincipal user, IHostEnvironment environment, TaskService service) =>
{
    EnsureRole(user, "owner", environment);
    return Results.Ok(service.IncreaseReward(id, ResolveUserId(user, ownerId, environment), request));
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

app.MapPost("/api/v1/reward-suggestions", (RewardSuggestionRequest request, TaskService service) => Results.Ok(service.SuggestReward(request)));

app.Run();

static Guid ResolveUserId(ClaimsPrincipal user, Guid developmentFallback, IHostEnvironment environment)
{
    var claim = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (Guid.TryParse(claim, out var userId)) return userId;
    if (environment.IsDevelopment() && developmentFallback != Guid.Empty) return developmentFallback;
    throw new UnauthorizedAccessException("请先登录后再执行该操作。");
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

public partial class Program;
