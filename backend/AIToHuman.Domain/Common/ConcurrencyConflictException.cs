namespace AIToHuman.Domain.Common;

/// <summary>
/// 乐观并发冲突：调用方基于已经过期的版本提交修改。
/// 领域层不引用 EF，因此用这个类型表达“版本对不上”，由 API 层统一映射为 409。
/// </summary>
public sealed class ConcurrencyConflictException(string message) : Exception(message);
