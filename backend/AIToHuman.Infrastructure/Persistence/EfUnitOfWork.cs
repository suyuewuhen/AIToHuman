using AIToHuman.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Persistence;

public sealed class EfUnitOfWork(TaskDbContext db) : IUnitOfWork
{
    public void Execute(Action operation)
    {
        // 嵌套调用复用外层事务，由最外层负责提交。
        if (db.Database.CurrentTransaction is not null)
        {
            operation();
            return;
        }

        using var transaction = db.Database.BeginTransaction();
        operation();
        transaction.Commit();
    }
}

/// <summary>内存仓储没有事务语义，直接执行。</summary>
public sealed class InMemoryUnitOfWork : IUnitOfWork
{
    public void Execute(Action operation) => operation();
}
