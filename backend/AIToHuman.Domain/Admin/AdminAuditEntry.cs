using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Admin;

/// <summary>运营在后台做过什么：只追加，不修改也不删除。</summary>
public sealed class AdminAuditEntry
{
    public const int MaxActionLength = 60;
    public const int MaxTargetTypeLength = 40;
    public const int MaxReasonLength = 200;

    private AdminAuditEntry(Guid id, Guid actorId, string action, string targetType, Guid targetId, string reason, DateTimeOffset occurredAt)
    {
        Id = id;
        ActorId = actorId;
        Action = action;
        TargetType = targetType;
        TargetId = targetId;
        Reason = reason;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }
    public Guid ActorId { get; private set; }
    public string Action { get; private set; }
    public string TargetType { get; private set; }
    public Guid TargetId { get; private set; }
    public string Reason { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    public static AdminAuditEntry Record(Guid actorId, string action, string targetType, Guid targetId, string reason, DateTimeOffset occurredAt)
    {
        if (actorId == Guid.Empty) throw new DomainException("运营操作必须记录操作人。");
        if (targetId == Guid.Empty) throw new DomainException("运营操作必须记录对象。");

        var normalizedAction = Require(action, MaxActionLength, "操作名称");
        var normalizedTarget = Require(targetType, MaxTargetTypeLength, "对象类型");
        var normalizedReason = Require(reason, MaxReasonLength, "操作原因");

        return new AdminAuditEntry(Guid.NewGuid(), actorId, normalizedAction, normalizedTarget, targetId, normalizedReason, UtcTimestamp.Normalize(occurredAt));
    }

    public static AdminAuditEntry Rehydrate(Guid id, Guid actorId, string action, string targetType, Guid targetId, string reason, DateTimeOffset occurredAt) =>
        new(id, actorId, Require(action, MaxActionLength, "操作名称"), Require(targetType, MaxTargetTypeLength, "对象类型"), targetId,
            Require(reason, MaxReasonLength, "操作原因"), UtcTimestamp.Normalize(occurredAt));

    private static string Require(string? value, int maxLength, string label)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) throw new DomainException($"{label}不能为空。");
        if (trimmed.Length > maxLength) throw new DomainException($"{label}不能超过 {maxLength} 个字符。");
        return trimmed;
    }
}
