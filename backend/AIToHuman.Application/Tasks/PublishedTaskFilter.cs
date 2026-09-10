namespace AIToHuman.Application.Tasks;

/// <summary>
/// 大厅列表的仓储级过滤条件。游标已在应用层解码成上一页最后一条的排序键
/// （按截止时间升序、同一时刻用 Id 兜底），因此仓储只做纯数据筛选。
/// </summary>
public sealed record PublishedTaskFilter(
    string? District,
    decimal? MinReward,
    decimal? MaxReward,
    DateTimeOffset? CursorDeadline,
    Guid? CursorId,
    int Limit);
