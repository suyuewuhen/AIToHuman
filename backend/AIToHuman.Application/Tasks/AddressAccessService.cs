using AIToHuman.Application.Admin;
using AIToHuman.Application.Common;
using AIToHuman.Contracts.Admin;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Application.Tasks;

/// <summary>读取执行地址的结果：地址本身（可能为空）与这次留痕的结论。</summary>
public sealed record AddressAccessResult(Guid TaskId, string? Address, AddressAccessOutcome Outcome);

/// <summary>
/// 精确执行地址的读取与留痕。
///
/// 一条不可让步的规则：**先留痕，再判断**（或者说是"无论给不给都把这次读取记下来"）。
/// 被拒绝的读取才是审计里最该看的信号——如果只记成功的那次，批量试探在日志里就是一片空白。
/// 因此这里的顺序是：解析任务 → 由领域层算出结论并写一条留痕 → 再决定抛 403 还是返回地址。
/// </summary>
public sealed class AddressAccessService(
    ITaskRepository taskRepository,
    IAddressAccessRepository accessRepository,
    IUserDirectory userDirectory,
    TimeProvider timeProvider,
    IUnitOfWork unitOfWork)
{
    /// <summary>运营一次最多看多少条留痕。</summary>
    public const int MaxLimit = 200;
    public const int DefaultLimit = 50;

    /// <summary>
    /// owner/worker 读取执行地址：权限口径完全由领域层（<see cref="TaskItem.ExecutionAddressFor"/>）决定，
    /// 这里只负责留痕与把结果翻译成用例层的返回/异常。
    /// </summary>
    public AddressAccessResult Read(Guid taskId, Guid viewerId)
    {
        var task = taskRepository.Get(taskId) ?? throw new KeyNotFoundException("任务不存在。");

        var entry = AddressAccessEntry.Record(task, viewerId, timeProvider.GetUtcNow());
        // 无论结论是什么，这次读取都先落库：拒绝也要留痕。
        unitOfWork.Execute(() => accessRepository.Add(entry));

        var address = task.ExecutionAddressFor(viewerId);
        if (address is null && task.HasExecutionAddress)
        {
            throw new UnauthorizedAccessException("执行地址仅在订单成立后向参与者披露。");
        }

        return new AddressAccessResult(task.Id, address, entry.Outcome);
    }

    /// <summary>运营查询访问留痕（可按任务或按人过滤），并给出同一个过滤条件下的拒绝次数。</summary>
    public AdminAddressAccessListResponse ListAudits(Guid? taskId, Guid? viewerId, int? limit)
    {
        var normalizedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var entries = accessRepository.List(taskId, viewerId, normalizedLimit);
        var viewers = userDirectory.FindMany(entries.Where(item => item.ViewerId is not null).Select(item => item.ViewerId!.Value).Distinct().ToArray());

        return new(
            entries.Select(entry => new AdminAddressAccessItemResponse(
                entry.Id,
                entry.TaskId,
                entry.ViewerId,
                entry.ViewerId is { } id && viewers.TryGetValue(id, out var view) ? view.Email : null,
                entry.Role.ToString(),
                entry.Outcome.ToString(),
                entry.Disclosed,
                entry.OccurredAt)).ToArray(),
            normalizedLimit,
            accessRepository.CountDenied(taskId, viewerId));
    }
}
