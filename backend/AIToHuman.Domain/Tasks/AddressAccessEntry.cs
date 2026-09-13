using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Tasks;

/// <summary>请求者在这次地址读取里的身份：决定了"该不该给他看"。</summary>
public enum AddressAccessRole
{
    /// <summary>任务所有者：始终可见。</summary>
    Owner,

    /// <summary>被选中的服务者：订单成立后可见。</summary>
    SelectedWorker,

    /// <summary>其他人（含匿名探测）：不可见。</summary>
    Other
}

/// <summary>这次读取的结果：留痕要能回答"有没有真的把地址给出去"。</summary>
public enum AddressAccessOutcome
{
    /// <summary>地址已披露。</summary>
    Granted,

    /// <summary>请求者不是参与者，地址没有披露。</summary>
    Denied,

    /// <summary>请求者本来有权看，但这条任务没有登记精确地址（无从披露）。</summary>
    NotSet
}

/// <summary>
/// 精确执行地址的访问留痕：**只追加**，一次读取一行。
///
/// 为什么需要它：执行地址是整个系统里最敏感的用户数据（住址级别的信息），
/// 现在只有"披露规则"（所有者与被选中的服务者可见）而没有"谁在什么时候看过"的记录——
/// 出问题时既无法复盘泄露路径，也无法发现有人在批量试探。
/// 因此这里把**被拒绝的读取也记下来**：探测行为本身才是最有价值的信号。
///
/// 身份与结论都由领域层从任务本身推出（<see cref="Record"/>），
/// 不依赖调用方传进来的角色字符串——那是很容易被上游写错、也最容易被绕过的部分。
/// </summary>
public sealed class AddressAccessEntry
{
    private AddressAccessEntry(
        Guid id,
        Guid taskId,
        Guid? viewerId,
        AddressAccessRole role,
        AddressAccessOutcome outcome,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TaskId = taskId;
        ViewerId = viewerId;
        Role = role;
        Outcome = outcome;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; }

    public Guid TaskId { get; }

    /// <summary>请求者；<c>null</c> 表示匿名（未登录）请求。</summary>
    public Guid? ViewerId { get; }

    public AddressAccessRole Role { get; }

    public AddressAccessOutcome Outcome { get; }

    public DateTimeOffset OccurredAt { get; }

    /// <summary>这次读取有没有真的把地址给出去。</summary>
    public bool Disclosed => Outcome == AddressAccessOutcome.Granted;

    /// <summary>
    /// 按任务当前的参与者关系与地址登记情况记一条留痕。
    /// 结论口径与 <see cref="TaskItem.ExecutionAddressFor"/> 完全一致：
    /// 有地址且请求者是所有者或被选中的服务者 → 披露；有地址但请求者不是参与者 → 拒绝；
    /// 没有登记地址 → 记成"无从披露"（这不是越权尝试）。
    /// </summary>
    public static AddressAccessEntry Record(TaskItem task, Guid? viewerId, DateTimeOffset occurredAt)
    {
        var viewer = viewerId is { } id && id != Guid.Empty ? id : (Guid?)null;
        var role = ResolveRole(task, viewer);
        var outcome = !task.HasExecutionAddress
            ? AddressAccessOutcome.NotSet
            : role == AddressAccessRole.Other ? AddressAccessOutcome.Denied : AddressAccessOutcome.Granted;

        return new AddressAccessEntry(Guid.NewGuid(), task.Id, viewer, role, outcome, UtcTimestamp.Normalize(occurredAt));
    }

    public static AddressAccessEntry Rehydrate(
        Guid id,
        Guid taskId,
        Guid? viewerId,
        AddressAccessRole role,
        AddressAccessOutcome outcome,
        DateTimeOffset occurredAt) =>
        new(id, taskId, viewerId, role, outcome, UtcTimestamp.Normalize(occurredAt));

    private static AddressAccessRole ResolveRole(TaskItem task, Guid? viewer)
    {
        if (viewer is not { } id) return AddressAccessRole.Other;
        if (id == task.OwnerId) return AddressAccessRole.Owner;

        // 被选中的服务者与服务者本人是不是同一个人：只认"这条报名被选中"这一个事实，
        // 不认报名状态里的其他取值（撤回、被拒、过期都不算）。
        return task.Applications.Any(item => item.WorkerId == id && item.Status == TaskApplicationStatus.Selected)
            ? AddressAccessRole.SelectedWorker
            : AddressAccessRole.Other;
    }
}
