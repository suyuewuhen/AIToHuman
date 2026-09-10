namespace AIToHuman.Application.Common;

/// <summary>
/// 把一个用例里的多次仓储写入放进同一个数据库事务。
/// 内存仓储实现为空操作；EF 实现用显式事务，嵌套调用复用外层事务。
/// </summary>
public interface IUnitOfWork
{
    void Execute(Action operation);
}
