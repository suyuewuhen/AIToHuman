using System.Text.Json;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Risk;

namespace AIToHuman.Domain.Tasks;

/// <summary>
/// 草稿的一版快照，只追加、不修改也不删除。
///
/// 为什么需要它：草稿字段可以编辑之后，"这条任务为什么变成现在这样"就没有答案了——
/// 谁在什么时候把截止时间从几点改到几点、改完之后风险结论是什么，都只留在最后一行数据里。
/// 风险审核又恰好绑定"当时那份文本"，所以复核记录必须能对上是第几版被判成禁止或转人工。
/// </summary>
public sealed class TaskDraftRevision
{
    /// <summary>描述与区域在草稿阶段也要有上限，否则历史快照的列宽没有依据。</summary>
    public const int MaxDescriptionLength = 4000;
    public const int MaxDistrictLength = 120;

    /// <summary>验收标准条数与单条长度上限：与 AI 输出校验的口径保持一致。</summary>
    public const int MaxCriteriaCount = 12;
    public const int MaxCriterionLength = 200;

    /// <summary>变更摘要里一句最多列出几个字段，其余的收成"等 N 项"。</summary>
    public const int MaxChangedFieldNames = 6;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private TaskDraftRevision()
    {
        Title = string.Empty;
        Description = string.Empty;
        District = string.Empty;
        AcceptanceCriteriaJson = "[]";
        ChangeSummary = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }

    /// <summary>同一任务内单调递增的版本号，从 1 开始（1 = 创建草稿）。</summary>
    public int Revision { get; private set; }

    /// <summary>这一版的字段快照。</summary>
    public string Title { get; private set; }
    public string Description { get; private set; }
    public string District { get; private set; }
    public DateTimeOffset Deadline { get; private set; }
    public decimal RewardAmount { get; private set; }
    public string RewardCurrency { get; private set; } = "CNY";
    public string AcceptanceCriteriaJson { get; private set; }
    public string? ExecutionAddress { get; private set; }
    public DateTimeOffset? ApplicationDeadline { get; private set; }

    /// <summary>这一版文本对应的风险结论（原因代码与规则版本），方便事后对账。</summary>
    public string RiskVerdict { get; private set; } = nameof(Domain.Risk.RiskVerdict.Allowed);
    public string? RiskRuleCode { get; private set; }
    public int RiskRuleVersion { get; private set; }

    /// <summary>谁改的（所有者本人编辑，或创建者本人）。</summary>
    public Guid EditedBy { get; private set; }

    /// <summary>这次改了哪些字段，例如"标题、截止时间"；创建那一版写"创建草稿"。</summary>
    public string ChangeSummary { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<string> AcceptanceCriteria =>
        JsonSerializer.Deserialize<string[]>(AcceptanceCriteriaJson) ?? [];

    /// <summary>创建草稿时的第一版。</summary>
    public static TaskDraftRevision Initial(TaskItem task, Guid editedBy, DateTimeOffset now) =>
        Build(task, revision: 1, editedBy, "创建草稿", now);

    /// <summary>编辑之后追加的一版：<paramref name="changedFields"/> 由领域层比对前后字段得出。</summary>
    public static TaskDraftRevision FromEdit(TaskItem task, int revision, Guid editedBy, IReadOnlyCollection<string> changedFields, DateTimeOffset now) =>
        Build(task, revision, editedBy, Summarize(changedFields), now);

    private static TaskDraftRevision Build(TaskItem task, int revision, Guid editedBy, string changeSummary, DateTimeOffset now)
    {
        if (task.Id == Guid.Empty) throw new DomainException("草稿版本必须关联任务。");
        if (revision < 1) throw new DomainException("草稿版本号必须从 1 开始。");
        if (editedBy == Guid.Empty) throw new DomainException("草稿版本必须记录修改人。");

        return new TaskDraftRevision
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Revision = revision,
            Title = task.Title,
            Description = task.Description,
            District = task.District,
            Deadline = UtcTimestamp.Normalize(task.Deadline),
            RewardAmount = task.Reward.Amount,
            RewardCurrency = task.Reward.Currency,
            AcceptanceCriteriaJson = JsonSerializer.Serialize(task.AcceptanceCriteria, JsonOptions),
            ExecutionAddress = task.ExecutionAddress,
            ApplicationDeadline = task.ApplicationDeadline,
            RiskVerdict = task.RiskVerdict.ToString(),
            RiskRuleCode = task.RiskRuleCode,
            RiskRuleVersion = task.RiskRuleVersion,
            EditedBy = editedBy,
            ChangeSummary = changeSummary,
            CreatedAt = UtcTimestamp.Normalize(now)
        };
    }

    public static TaskDraftRevision Rehydrate(
        Guid id,
        Guid taskId,
        int revision,
        string title,
        string description,
        string district,
        DateTimeOffset deadline,
        decimal rewardAmount,
        string rewardCurrency,
        string acceptanceCriteriaJson,
        string? executionAddress,
        DateTimeOffset? applicationDeadline,
        string riskVerdict,
        string? riskRuleCode,
        int riskRuleVersion,
        Guid editedBy,
        string changeSummary,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = id,
            TaskId = taskId,
            Revision = revision,
            Title = title,
            Description = description,
            District = district,
            Deadline = deadline,
            RewardAmount = rewardAmount,
            RewardCurrency = rewardCurrency,
            AcceptanceCriteriaJson = acceptanceCriteriaJson,
            ExecutionAddress = executionAddress,
            ApplicationDeadline = applicationDeadline,
            RiskVerdict = riskVerdict,
            RiskRuleCode = riskRuleCode,
            RiskRuleVersion = riskRuleVersion,
            EditedBy = editedBy,
            ChangeSummary = changeSummary,
            CreatedAt = createdAt
        };

    /// <summary>把字段名列表拼成一句可读的摘要，过长的部分收成"等 N 项"。</summary>
    private static string Summarize(IReadOnlyCollection<string> changedFields)
    {
        if (changedFields.Count == 0) return "无字段变化";

        var names = changedFields.Take(MaxChangedFieldNames).ToArray();
        var extra = changedFields.Count - names.Length;
        var summary = string.Join("、", names);
        return extra > 0 ? $"{summary} 等 {changedFields.Count} 项" : summary;
    }
}
