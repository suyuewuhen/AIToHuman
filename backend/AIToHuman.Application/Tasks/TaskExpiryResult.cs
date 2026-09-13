namespace AIToHuman.Application.Tasks;

/// <summary>
/// 一轮过期扫描的结果：<paramref name="Expired"/> 是这一轮成功置为过期的任务数，
/// <paramref name="Skipped"/> 是被并发改动（已被选中、被撤销，或另一个实例先处理了）而跳过、留给下一轮的数量。
/// </summary>
public sealed record TaskExpiryResult(int Expired, int Skipped);
