using AIToHuman.Domain.Tasks;

namespace AIToHuman.Application.Tasks;

/// <summary>
/// 执行地址访问留痕的存储：只追加，不更新也不删除。
/// 两种读法各有用处——按任务看"谁看过这条地址"，按人看"这个人查过多少地址"。
/// </summary>
public interface IAddressAccessRepository
{
    void Add(AddressAccessEntry entry);

    /// <summary>最近的访问留痕，按时间倒序；两个过滤条件都可以为空。</summary>
    IReadOnlyCollection<AddressAccessEntry> List(Guid? taskId, Guid? viewerId, int limit);

    /// <summary>被拒绝的尝试次数（同样支持两个过滤条件），运营据此判断"是不是有人在批量试探"。</summary>
    int CountDenied(Guid? taskId, Guid? viewerId);
}
