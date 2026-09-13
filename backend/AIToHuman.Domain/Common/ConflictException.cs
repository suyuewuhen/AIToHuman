namespace AIToHuman.Domain.Common;

/// <summary>
/// 资源冲突（例如邮箱已经注册）：HTTP 409，并且把原因原样返回给调用方。
///
/// 之前这类情况用的是 <see cref="InvalidOperationException"/>，状态码靠"消息里包含『已注册』"这种字符串匹配
/// 猜出来，而消息本身不会外泄，于是客户端只能看到"请求暂时无法处理。"。现在冲突是一种明确的类型。
/// </summary>
public sealed class ConflictException(string message) : Exception(message);
