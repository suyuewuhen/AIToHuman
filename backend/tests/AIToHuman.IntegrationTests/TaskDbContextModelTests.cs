using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 锁定并发令牌的配置：只要有人把 IsConcurrencyToken 去掉或删掉 Version 列，这里就会失败。
/// 只构建模型，不连接数据库。
/// </summary>
public sealed class TaskDbContextModelTests
{
    [Theory]
    [InlineData(typeof(TaskRecord))]
    [InlineData(typeof(OrderRecord))]
    public void Version_is_configured_as_a_concurrency_token(Type entityType)
    {
        using var context = CreateContext();

        var property = context.Model.FindEntityType(entityType)?.FindProperty(nameof(TaskRecord.Version));

        Assert.NotNull(property);
        Assert.True(property!.IsConcurrencyToken, $"{entityType.Name}.Version 必须是并发令牌，否则并发写入不会被拦住");
    }

    [Fact]
    public void Order_is_unique_per_task()
    {
        using var context = CreateContext();

        var index = context.Model.FindEntityType(typeof(OrderRecord))!
            .GetIndexes()
            .Single(item => item.Properties.Select(property => property.Name).SequenceEqual([nameof(OrderRecord.TaskId)]));

        Assert.True(index.IsUnique, "一个任务最多只能有一个订单");
    }

    private static TaskDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TaskDbContext>()
            // 只为构建模型，不会真正连接。
            .UseNpgsql("Host=localhost;Database=model_only;Username=model;Password=model")
            .Options);
}
