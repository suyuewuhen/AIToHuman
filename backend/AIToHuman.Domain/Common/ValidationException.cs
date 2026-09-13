namespace AIToHuman.Domain.Common;

/// <summary>
/// 用户输入不合法的异常（缺字段、字段超长、取值不在允许范围内……）。
///
/// 与 <see cref="DomainException"/> 的区别只在语义：那个表示"业务规则不允许"，这个表示"请求本身写错了"。
/// 两者都会把消息原样返回给调用方（HTTP 422 + 可读原因）——这一点很重要：
/// 之前这类校验抛的是 <see cref="InvalidOperationException"/>，而它的消息出于安全考虑不会外泄，
/// 于是客户端只拿到"请求暂时无法处理。"，连"少了 role 字段"都要靠猜。
/// </summary>
public sealed class ValidationException(string message) : Exception(message);
