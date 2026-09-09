using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Contracts;
using AIToHuman.Domain.Common;
using AIToHuman.Infrastructure.Tasks;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var postgresConnection = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgresConnection))
    builder.Services.AddSingleton<ITaskRepository, InMemoryTaskRepository>();
else
{
    builder.Services.AddDbContext<TaskDbContext>(options => options.UseNpgsql(postgresConnection));
    builder.Services.AddScoped<ITaskRepository, EfTaskRepository>();
}
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<TaskService>();
builder.Services.AddProblemDetails();
builder.Services.AddCors(options => options.AddPolicy("development", policy => policy
    .WithOrigins("http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

if (app.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(postgresConnection))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TaskDbContext>();
    db.Database.EnsureCreated();
}

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = exception switch
    {
        DomainException => (StatusCodes.Status422UnprocessableEntity, "业务规则不允许该操作"),
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

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "AIToHuman.Api", utc = DateTimeOffset.UtcNow }));

app.MapGet("/api/v1/session/dev", (IHostEnvironment environment) =>
{
    if (!environment.IsDevelopment()) return Results.NotFound();
    return Results.Ok(new DevSessionResponse(Guid.Parse("20000000-0000-0000-0000-000000000001"), "开发服务者", "worker", ["owner", "worker"], true));
});

var tasks = app.MapGroup("/api/v1/tasks");
tasks.MapGet("/", (TaskService service) => Results.Ok(service.ListPublished()));
tasks.MapGet("/{id:guid}", (Guid id, TaskService service) => service.GetPublic(id) is { } task ? Results.Ok(task) : Results.NotFound());
tasks.MapGet("/{id:guid}/applications", (Guid id, Guid ownerId, TaskService service) => Results.Ok(service.ListApplications(id, ownerId)));
tasks.MapPost("/", (CreateTaskRequest request, TaskService service) =>
{
    var task = service.Create(request);
    return Results.Created($"/api/v1/tasks/{task.Id}", task);
});
tasks.MapPost("/{id:guid}/publish", (Guid id, Guid ownerId, TaskService service) => Results.Ok(service.Publish(id, ownerId)));
tasks.MapPost("/{id:guid}/increase-reward", (Guid id, Guid ownerId, IncreaseRewardRequest request, TaskService service) => Results.Ok(service.IncreaseReward(id, ownerId, request)));
tasks.MapPost("/{id:guid}/applications", (Guid id, ApplyForTaskRequest request, TaskService service) => Results.Ok(service.Apply(id, request)));
tasks.MapPost("/{id:guid}/applications/{applicationId:guid}/select", (Guid id, Guid applicationId, SelectApplicationRequest request, TaskService service) => Results.Ok(service.Select(id, applicationId, request)));

app.MapPost("/api/v1/reward-suggestions", (RewardSuggestionRequest request, TaskService service) => Results.Ok(service.SuggestReward(request)));

app.Run();

public partial class Program;
