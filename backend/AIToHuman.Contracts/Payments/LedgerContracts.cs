namespace AIToHuman.Contracts.Payments;

/// <summary>
/// 一条资金流水。借/贷账户是复式记账的两端（例如"托管 → 服务者应得"），
/// 金额恒为正数，方向由账户表达；<paramref name="Kind"/> 说明这笔是冻结、放款还是退款。
/// </summary>
public sealed record LedgerEntryResponse(
    Guid Id,
    Guid OrderId,
    Guid TaskId,
    string Kind,
    string DebitAccount,
    string CreditAccount,
    decimal Amount,
    string Currency,
    string? Note,
    DateTimeOffset OccurredAt);
