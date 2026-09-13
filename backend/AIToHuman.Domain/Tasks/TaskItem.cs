using AIToHuman.Domain.Common;
using AIToHuman.Domain.Risk;

namespace AIToHuman.Domain.Tasks;

public sealed class TaskItem
{
    private readonly List<TaskApplication> _applications = [];

    private TaskItem()
    {
        Title = string.Empty;
        Description = string.Empty;
        District = string.Empty;
        AcceptanceCriteria = [];
    }

    public TaskItem(
        Guid ownerId,
        string title,
        string description,
        string district,
        DateTimeOffset deadline,
        Money reward,
        IEnumerable<string> acceptanceCriteria,
        DateTimeOffset createdAt,
        string? executionAddress = null,
        DateTimeOffset? applicationDeadline = null)
    {
        var criteria = NormalizeCriteria(acceptanceCriteria);

        if (ownerId == Guid.Empty) throw new DomainException("任务必须有所有者。");
        EnsureDraftFields(title, description, district, criteria, executionAddress);
        if (UtcTimestamp.Normalize(deadline) <= UtcTimestamp.Normalize(createdAt)) throw new DomainException("截止时间必须晚于创建时间。");

        Id = Guid.NewGuid();
        OwnerId = ownerId;
        Title = title.Trim();
        Description = description.Trim();
        District = district.Trim();
        Deadline = UtcTimestamp.Normalize(deadline);
        Reward = reward;
        AcceptanceCriteria = criteria;
        CreatedAt = UtcTimestamp.Normalize(createdAt);
        ExecutionAddress = NormalizeExecutionAddress(executionAddress);
        ApplicationDeadline = NormalizeApplicationDeadline(applicationDeadline, Deadline, CreatedAt);

        // 风险判定发生在创建草稿时：被禁止的类别从一开始就锁死，转人工的类别直接进复核队列。
        // 草稿仍然创建出来（用户能看到自己写的内容和原因），但发布这一关过不去。
        AssessRisk(CreatedAt);
    }

    /// <summary>执行地址属于订单参与者层信息，最长 200 字。</summary>
    public const int MaxExecutionAddressLength = 200;

    /// <summary>验收标准：去掉空白项、逐个 trim 并去重。</summary>
    private static string[] NormalizeCriteria(IEnumerable<string> acceptanceCriteria) =>
        acceptanceCriteria
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct()
            .ToArray();

    /// <summary>
    /// 标题、描述、区域、验收标准与执行地址的校验。创建草稿和编辑草稿共用这一套，
    /// 否则很容易出现“创建时拦得住的字段，编辑时能绕过去”的缺口。
    /// 描述与验收标准同时给了上限：草稿版本快照要按这些上限落库，没有上限就没法给它列宽。
    /// </summary>
    private static void EnsureDraftFields(string title, string description, string district, string[] criteria, string? executionAddress)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 80) throw new DomainException("任务标题必须为 1 到 80 个字符。");
        if (string.IsNullOrWhiteSpace(description)) throw new DomainException("任务描述不能为空。");
        if (description.Trim().Length > TaskDraftRevision.MaxDescriptionLength) throw new DomainException($"任务描述不能超过 {TaskDraftRevision.MaxDescriptionLength} 个字符。");
        if (string.IsNullOrWhiteSpace(district)) throw new DomainException("任务必须包含公开区域。");
        if (district.Trim().Length > TaskDraftRevision.MaxDistrictLength) throw new DomainException($"公开区域不能超过 {TaskDraftRevision.MaxDistrictLength} 个字符。");
        if (criteria.Length == 0) throw new DomainException("任务至少需要一项验收标准。");
        if (criteria.Length > TaskDraftRevision.MaxCriteriaCount) throw new DomainException($"验收标准最多 {TaskDraftRevision.MaxCriteriaCount} 条。");
        if (criteria.Any(item => item.Length > TaskDraftRevision.MaxCriterionLength)) throw new DomainException($"每条验收标准不能超过 {TaskDraftRevision.MaxCriterionLength} 个字符。");
        if (executionAddress?.Trim().Length > MaxExecutionAddressLength) throw new DomainException($"执行地址不能超过 {MaxExecutionAddressLength} 个字符。");
    }

    /// <summary>
    /// 编辑草稿：只有还没发布的草稿可以改，字段校验与创建时完全一致，改完立即重跑风险规则。
    ///
    /// 这里最要紧的一条是**清空原有的人工复核结论**：审核是针对某一版文本做出的，
    /// 如果编辑后保留“已放行”，那么“先提一份干净文案过审、再改成代考”就是一条现成的绕过路径。
    /// 反过来，被运营驳回的草稿只要改掉了敏感内容，也会重新进入新一轮复核，而不是被永久钉死。
    ///
    /// 返回这次改了哪些字段（面向人的名字），调用方据此追加一版草稿快照。
    /// </summary>
    public IReadOnlyCollection<string> UpdateDraft(
        string title,
        string description,
        string district,
        DateTimeOffset deadline,
        Money reward,
        IEnumerable<string> acceptanceCriteria,
        string? executionAddress,
        DateTimeOffset? applicationDeadline,
        DateTimeOffset now)
    {
        EnsureStatus(TaskStatus.ReadyToPublish);

        var criteria = NormalizeCriteria(acceptanceCriteria);
        EnsureDraftFields(title, description, district, criteria, executionAddress);

        var normalizedNow = UtcTimestamp.Normalize(now);
        var normalizedDeadline = UtcTimestamp.Normalize(deadline);
        if (normalizedDeadline <= normalizedNow) throw new DomainException("截止时间必须晚于当前时间。");

        var normalizedAddress = NormalizeExecutionAddress(executionAddress);
        var normalizedApplicationDeadline = NormalizeApplicationDeadline(applicationDeadline, normalizedDeadline, normalizedNow, "当前时间");

        var changed = new List<string>();
        if (Title != title.Trim()) changed.Add("标题");
        if (Description != description.Trim()) changed.Add("描述");
        if (District != district.Trim()) changed.Add("公开区域");
        if (Deadline != normalizedDeadline) changed.Add("截止时间");
        if (Reward.Amount != reward.Amount || Reward.Currency != reward.Currency) changed.Add("悬赏");
        if (!AcceptanceCriteria.SequenceEqual(criteria)) changed.Add("验收标准");
        if (ExecutionAddress != normalizedAddress) changed.Add("执行地址");
        if (ApplicationDeadline != normalizedApplicationDeadline) changed.Add("报名截止时间");

        Title = title.Trim();
        Description = description.Trim();
        District = district.Trim();
        Deadline = normalizedDeadline;
        Reward = reward;
        AcceptanceCriteria = criteria;
        ExecutionAddress = normalizedAddress;
        ApplicationDeadline = normalizedApplicationDeadline;

        AssessRisk(normalizedNow, resetHumanDecision: true);

        // 正文变了，原来的申诉就不再针对同一份材料：连同申诉结论一起作废，需要的话重新提交。
        RiskAppealStatus = RiskAppealStatus.None;
        RiskAppealReason = null;
        RiskAppealedAt = null;
        RiskAppealDecidedBy = null;
        RiskAppealDecidedAt = null;
        RiskAppealDecisionNote = null;

        return changed;
    }

    /// <summary>报名截止时间：可选。到点后不再接受新报名，但已经报名的服务者仍然可以被选中。</summary>
    public DateTimeOffset? ApplicationDeadline { get; private set; }

    /// <summary>
    /// 报名截止时间必须晚于创建时间、且不晚于任务截止时间——否则要么一发布就报不了名，
    /// 要么出现“任务还能干但报不了名”的怪状态。
    /// </summary>
    private static DateTimeOffset? NormalizeApplicationDeadline(DateTimeOffset? applicationDeadline, DateTimeOffset deadline, DateTimeOffset floor, string floorLabel = "创建时间")
    {
        if (applicationDeadline is not { } value) return null;

        var normalized = UtcTimestamp.Normalize(value);
        if (normalized <= floor) throw new DomainException($"报名截止时间必须晚于{floorLabel}。");
        if (normalized > deadline) throw new DomainException("报名截止时间不能晚于任务截止时间。");
        return normalized;
    }

    /// <summary>现在还能不能报名：任务已发布、且（若设了）报名截止时间还没到。</summary>
    public bool AcceptingApplications(DateTimeOffset now) =>
        Status == TaskStatus.Published && (ApplicationDeadline is not { } deadline || deadline > now);

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public string Title { get; private set; }
    public string Description { get; private set; }
    public string District { get; private set; }

    /// <summary>精确执行地址，只在订单成立后向参与者披露；大厅与公开详情永远不含它。</summary>
    public string? ExecutionAddress { get; private set; }

    public DateTimeOffset Deadline { get; private set; }
    public Money Reward { get; private set; }
    public IReadOnlyList<string> AcceptanceCriteria { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public TaskStatus Status { get; private set; } = TaskStatus.ReadyToPublish;
    public IReadOnlyCollection<TaskApplication> Applications => _applications.AsReadOnly();

    /// <summary>系统发现任务超过截止时间仍无人被选中的时刻；过期时间语义上等于 <see cref="Deadline"/>，这里记录的是处理时刻。</summary>
    public DateTimeOffset? ExpiredAt { get; private set; }

    /// <summary>任务被撤销（所有者撤销或运营下架）的时间与原因；原因必填。</summary>
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancellationReason { get; private set; }

    public bool HasExecutionAddress => !string.IsNullOrWhiteSpace(ExecutionAddress);

    /// <summary>确定性风险规则的结论；创建草稿时判定，发布前会按当时的字段重新判定一次。</summary>
    public RiskVerdict RiskVerdict { get; private set; } = RiskVerdict.Allowed;

    /// <summary>命中的原因代码（例如 <c>prohibited.exam_impersonation</c>）；放行时为 null。</summary>
    public string? RiskRuleCode { get; private set; }

    /// <summary>命中规则的类别名（面向人）。</summary>
    public string? RiskCategory { get; private set; }

    /// <summary>可展示给用户与运营的说明。刻意不含命中的具体词，避免被逐字试探绕过。</summary>
    public string? RiskSummary { get; private set; }

    /// <summary>判定时使用的规则目录版本，用于事后回溯“当时是哪一版规则”。</summary>
    public int RiskRuleVersion { get; private set; }

    public DateTimeOffset? RiskAssessedAt { get; private set; }

    /// <summary>人工复核状态：只有转人工的任务会进入 Pending。</summary>
    public RiskReviewStatus RiskReviewStatus { get; private set; } = RiskReviewStatus.NotRequired;

    public Guid? RiskReviewedBy { get; private set; }
    public DateTimeOffset? RiskReviewedAt { get; private set; }

    /// <summary>运营复核的意见（放行或驳回的依据）。</summary>
    public string? RiskReviewNote { get; private set; }

    /// <summary>复核意见的长度上限。</summary>
    public const int MaxRiskReviewNoteLength = 200;

    /// <summary>误拦申诉的理由长度的上限。</summary>
    public const int MaxRiskAppealReasonLength = 500;

    /// <summary>误拦申诉的当前状态；编辑草稿会让它作废（正文变了，原来的申诉就不再针对同一份材料）。</summary>
    public RiskAppealStatus RiskAppealStatus { get; private set; } = RiskAppealStatus.None;

    /// <summary>所有者提交的申诉理由。</summary>
    public string? RiskAppealReason { get; private set; }

    public DateTimeOffset? RiskAppealedAt { get; private set; }

    /// <summary>运营的处置结论留痕。</summary>
    public Guid? RiskAppealDecidedBy { get; private set; }
    public DateTimeOffset? RiskAppealDecidedAt { get; private set; }
    public string? RiskAppealDecisionNote { get; private set; }

    /// <summary>
    /// 能不能申诉：被判定为禁止类别、或转人工后被驳回，且**这一版文本还没有申诉过**。
    /// 处置过（无论成立还是驳回）之后要先改文案——编辑会把申诉状态清成 None——
    /// 否则同一份材料可以反复申诉，把运营队列变成刷屏的地方。
    /// </summary>
    public bool CanAppealRisk => RiskAppealStatus == RiskAppealStatus.None
        && (RiskVerdict == RiskVerdict.Blocked
            || (RiskVerdict == RiskVerdict.NeedsReview && RiskReviewStatus == RiskReviewStatus.Rejected));

    /// <summary>正在等运营处置的申诉。</summary>
    public bool AwaitingRiskAppeal => RiskAppealStatus == RiskAppealStatus.Pending;

    /// <summary>被规则判定为禁止类别：任何人都不能发布，人工只能驳回、不能放行。</summary>
    public bool IsRiskBlocked => RiskVerdict == RiskVerdict.Blocked;

    /// <summary>正在等人工复核，复核通过前不能发布。</summary>
    public bool AwaitingRiskReview => RiskVerdict == RiskVerdict.NeedsReview && RiskReviewStatus == RiskReviewStatus.Pending;

    /// <summary>发布这一关是否已经被风险处置卡住（被禁止、待复核、或复核被驳回）。</summary>
    public bool IsPublishBlockedByRisk =>
        RiskReviewStatus == RiskReviewStatus.Rejected
        || RiskVerdict == RiskVerdict.Blocked
        || (RiskVerdict == RiskVerdict.NeedsReview && RiskReviewStatus != RiskReviewStatus.Approved);

    public static TaskItem Rehydrate(
        Guid id,
        Guid ownerId,
        string title,
        string description,
        string district,
        DateTimeOffset deadline,
        Money reward,
        IReadOnlyList<string> acceptanceCriteria,
        DateTimeOffset createdAt,
        TaskStatus status,
        IEnumerable<TaskApplication> applications,
        string? executionAddress = null,
        DateTimeOffset? expiredAt = null,
        DateTimeOffset? cancelledAt = null,
        string? cancellationReason = null,
        DateTimeOffset? applicationDeadline = null,
        RiskVerdict riskVerdict = RiskVerdict.Allowed,
        string? riskRuleCode = null,
        string? riskCategory = null,
        string? riskSummary = null,
        int riskRuleVersion = 0,
        DateTimeOffset? riskAssessedAt = null,
        RiskReviewStatus riskReviewStatus = RiskReviewStatus.NotRequired,
        Guid? riskReviewedBy = null,
        DateTimeOffset? riskReviewedAt = null,
        string? riskReviewNote = null,
        RiskAppealStatus riskAppealStatus = RiskAppealStatus.None,
        string? riskAppealReason = null,
        DateTimeOffset? riskAppealedAt = null,
        Guid? riskAppealDecidedBy = null,
        DateTimeOffset? riskAppealDecidedAt = null,
        string? riskAppealDecisionNote = null)
    {
        var task = new TaskItem
        {
            Id = id,
            OwnerId = ownerId,
            Title = title,
            Description = description,
            District = district,
            Deadline = deadline,
            Reward = reward,
            AcceptanceCriteria = acceptanceCriteria,
            CreatedAt = createdAt,
            Status = status,
            ExecutionAddress = NormalizeExecutionAddress(executionAddress),
            ExpiredAt = expiredAt,
            CancelledAt = cancelledAt,
            CancellationReason = cancellationReason,
            ApplicationDeadline = applicationDeadline,
            RiskVerdict = riskVerdict,
            RiskRuleCode = riskRuleCode,
            RiskCategory = riskCategory,
            RiskSummary = riskSummary,
            RiskRuleVersion = riskRuleVersion,
            RiskAssessedAt = riskAssessedAt,
            RiskReviewStatus = riskReviewStatus,
            RiskReviewedBy = riskReviewedBy,
            RiskReviewedAt = riskReviewedAt,
            RiskReviewNote = riskReviewNote,
            RiskAppealStatus = riskAppealStatus,
            RiskAppealReason = riskAppealReason,
            RiskAppealedAt = riskAppealedAt,
            RiskAppealDecidedBy = riskAppealDecidedBy,
            RiskAppealDecidedAt = riskAppealDecidedAt,
            RiskAppealDecisionNote = riskAppealDecisionNote
        };
        task._applications.AddRange(applications);
        return task;
    }

    /// <summary>
    /// 执行地址的分阶段披露：所有者始终可见；被选中的服务者在订单成立后可见；
    /// 其他任何人（包括已报名但未被选中的服务者）都拿不到。
    /// </summary>
    public string? ExecutionAddressFor(Guid? viewerId)
    {
        if (viewerId is null || viewerId == Guid.Empty) return null;
        if (viewerId == OwnerId) return ExecutionAddress;

        var selected = _applications.FirstOrDefault(item => item.Status == TaskApplicationStatus.Selected);
        return selected is not null && selected.WorkerId == viewerId ? ExecutionAddress : null;
    }

    private static string? NormalizeExecutionAddress(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public void Publish(DateTimeOffset now)
    {
        EnsureStatus(TaskStatus.ReadyToPublish);

        // 发布前重新判定一次：悬赏与截止时间在草稿阶段会变（加价到高金额、改到深夜），
        // 判定结果与原因代码都要跟当前字段一致，再决定这一关过不过。
        AssessRisk(now);
        EnsurePublishableByRisk();

        if (Deadline <= now) throw new DomainException("已过截止时间的任务不能发布。");
        // 报名截止时间已经过去还发布，等于把一个谁都报不了名的任务放进大厅。
        if (ApplicationDeadline is { } applicationDeadline && applicationDeadline <= now)
        {
            throw new DomainException("报名截止时间已过，任务不能发布：请撤销后重新创建，或先调整报名截止时间。");
        }

        Status = TaskStatus.Published;
    }

    public void IncreaseReward(Money reward)
    {
        EnsureStatus(TaskStatus.Published);
        if (reward.Currency != Reward.Currency || reward.Amount <= Reward.Amount)
        {
            throw new DomainException("已发布任务只能提高同币种悬赏。");
        }

        Reward = reward;
    }

    public TaskApplication Apply(Guid workerId, string? note, DateTimeOffset now)
    {
        EnsureStatus(TaskStatus.Published);
        if (workerId == OwnerId) throw new DomainException("任务发布者不能报名自己的任务。");
        if (ApplicationDeadline is { } applicationDeadline && applicationDeadline <= now)
        {
            throw new DomainException("该任务的报名已经截止，不能再报名。");
        }

        if (_applications.Any(item => item.WorkerId == workerId && item.Status == TaskApplicationStatus.Pending))
        {
            throw new DomainException("服务者已经报名该任务。");
        }

        var application = new TaskApplication(workerId, note ?? string.Empty, now);
        _applications.Add(application);
        return application;
    }

    /// <summary>
    /// 服务者撤回自己还没被处理的报名。撤回只是作废这一条：记录保留可追溯，服务者之后想反悔可以重新报名。
    /// 被选中之后就不能撤回了——那时订单已经成立，要退出只能走订单取消。
    /// </summary>
    public TaskApplication WithdrawApplication(Guid applicationId, Guid workerId, DateTimeOffset now)
    {
        var application = _applications.SingleOrDefault(item => item.Id == applicationId)
            ?? throw new DomainException("报名不存在。");
        if (application.WorkerId != workerId) throw new UnauthorizedAccessException("只能撤回自己的报名。");
        if (application.Status != TaskApplicationStatus.Pending)
        {
            throw new DomainException($"报名当前状态 {application.Status} 不能撤回，只有待处理的报名可以撤回。");
        }

        _ = UtcTimestamp.Normalize(now);
        application.Status = TaskApplicationStatus.Withdrawn;
        return application;
    }

    public TaskApplication SelectApplication(Guid applicationId)
    {
        EnsureStatus(TaskStatus.Published);
        var selected = _applications.SingleOrDefault(item => item.Id == applicationId)
            ?? throw new DomainException("报名不存在。");
        if (selected.Status != TaskApplicationStatus.Pending) throw new DomainException("只能选择有效报名。");

        foreach (var application in _applications.Where(item => item.Status == TaskApplicationStatus.Pending))
        {
            application.Status = application.Id == applicationId
                ? TaskApplicationStatus.Selected
                : TaskApplicationStatus.Rejected;
        }

        Status = TaskStatus.Assigned;
        return selected;
    }

    /// <summary>订单验收通过后关闭任务。只有已分配的任务可以关闭，关闭后不再出现在任务大厅。</summary>
    public void Close()
    {
        EnsureStatus(TaskStatus.Assigned);
        Status = TaskStatus.Closed;
    }

    /// <summary>
    /// 运营人工下架或所有者自己撤销：草稿或已发布但尚未分配的任务可以撤销，撤销后不再出现在任务大厅。
    /// 必须给出原因，原因与时间记在任务上（运营下架同时还会写一条运营审计）。
    /// 已经产生订单的任务不能直接撤销——订单要先按正常流程结束，或者走“取消订单”把任务放回大厅，
    /// 否则会出现“任务消失了但订单还挂在服务者名下”的状态。
    /// </summary>
    public void Cancel(string reason, DateTimeOffset now)
    {
        if (Status is not (TaskStatus.ReadyToPublish or TaskStatus.Published))
        {
            throw Status == TaskStatus.Assigned
                ? new DomainException("任务已经分配并产生订单，不能直接撤销：请先处理订单（验收、驳回或取消订单）。")
                : new DomainException($"任务当前状态 {Status} 不允许撤销。");
        }

        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > MaxCancellationReasonLength)
            throw new DomainException($"撤销任务必须填写原因，长度不超过 {MaxCancellationReasonLength} 个字符。");

        CancelledAt = UtcTimestamp.Normalize(now);
        CancellationReason = trimmed;
        Status = TaskStatus.Cancelled;
    }

    /// <summary>撤销原因的长度上限（运营下架的原因同时记在运营审计表里）。</summary>
    public const int MaxCancellationReasonLength = 200;

    /// <summary>
    /// 超过截止时间仍无人被选中的已发布任务自动过期。
    /// 返回被这次过期作废的报名者（用于通知他们“报名已失效”），报名状态同时置为 <see cref="TaskApplicationStatus.Expired"/>。
    /// </summary>
    public IReadOnlyCollection<Guid> Expire(DateTimeOffset now)
    {
        EnsureStatus(TaskStatus.Published);
        if (Deadline > now) throw new DomainException("截止时间还没到，任务不能标记为过期。");

        var affected = _applications.Where(item => item.Status == TaskApplicationStatus.Pending).Select(item => item.WorkerId).ToArray();
        foreach (var application in _applications.Where(item => item.Status == TaskApplicationStatus.Pending))
        {
            application.Status = TaskApplicationStatus.Expired;
        }

        Status = TaskStatus.Expired;
        ExpiredAt = UtcTimestamp.Normalize(now);
        return affected;
    }

    /// <summary>
    /// 订单被取消后任务的去向：还没到截止时间就回到大厅重新招募（该次选择作废，服务者可以重新报名），
    /// 已经过了截止时间就直接过期，避免留下一个再也没人能接的“已发布”任务。
    /// 作废选中报名还有一个必要作用：执行地址只对“被选中的服务者”披露，留着 Selected 会继续泄露地址。
    /// </summary>
    public TaskReleaseOutcome ReleaseAfterOrderCancelled(DateTimeOffset now)
    {
        EnsureStatus(TaskStatus.Assigned);
        foreach (var application in _applications.Where(item => item.Status == TaskApplicationStatus.Selected))
        {
            application.Status = TaskApplicationStatus.Rejected;
        }

        if (Deadline <= now)
        {
            Status = TaskStatus.Expired;
            ExpiredAt = UtcTimestamp.Normalize(now);
            return TaskReleaseOutcome.Expired;
        }

        Status = TaskStatus.Published;
        return TaskReleaseOutcome.Reopened;
    }

    private void EnsureStatus(TaskStatus expected)
    {
        if (Status != expected)
        {
            throw new DomainException($"任务当前状态 {Status} 不允许该操作，期望状态为 {expected}。");
        }
    }

    /// <summary>
    /// 跑一遍确定性规则并把结论记在任务上。已有人工结论时不让规则覆盖：
    /// 运营驳回过的任务不会因为后续字段变化又变回可发布。
    /// <paramref name="resetHumanDecision"/> 用于“文本被改过”的场景：审核针对的是某一版文本，
    /// 文本一变就作废，重新按新文本判定。
    /// </summary>
    private void AssessRisk(DateTimeOffset now, bool resetHumanDecision = false)
    {
        if (resetHumanDecision)
        {
            RiskReviewedBy = null;
            RiskReviewedAt = null;
            RiskReviewNote = null;
        }

        var assessment = RiskRuleCatalog.Evaluate(Title, Description, AcceptanceCriteria, ExecutionAddress, Reward.Amount, Deadline);
        var humanDecisionExists = !resetHumanDecision && RiskReviewStatus is RiskReviewStatus.Approved or RiskReviewStatus.Rejected;

        RiskVerdict = assessment.Verdict;
        RiskRuleCode = assessment.RuleCode;
        RiskCategory = assessment.Category;
        RiskSummary = assessment.Description;
        RiskRuleVersion = assessment.RuleVersion;
        RiskAssessedAt = UtcTimestamp.Normalize(now);

        RiskReviewStatus = assessment.Verdict switch
        {
            RiskVerdict.NeedsReview when humanDecisionExists => RiskReviewStatus,
            RiskVerdict.NeedsReview => RiskReviewStatus.Pending,
            // 人工驳回是终态：即便规则不再命中，也不让它自己变回可发布（除非文本被改过，那时已清空结论）。
            _ when !resetHumanDecision && RiskReviewStatus == RiskReviewStatus.Rejected => RiskReviewStatus.Rejected,
            _ => RiskReviewStatus.NotRequired
        };
    }

    /// <summary>
    /// 发布前的风险门禁：禁止类别一律不放行（人工也不能放行），转人工类别必须有运营的放行结论，
    /// 已经被运营驳回的任务即使规则不再命中也不能发布。
    /// </summary>
    private void EnsurePublishableByRisk()
    {
        if (RiskReviewStatus == RiskReviewStatus.Rejected)
        {
            throw new DomainException($"该任务未通过人工复核，不能发布：{RiskReviewNote}");
        }

        if (RiskVerdict == RiskVerdict.Blocked)
        {
            throw new DomainException($"该任务属于平台禁止的类别（{RiskRuleCode} · {RiskCategory}），不能发布：{RiskSummary}");
        }

        if (RiskVerdict != RiskVerdict.NeedsReview || RiskReviewStatus == RiskReviewStatus.Approved) return;

        throw new DomainException($"该任务需要人工复核（{RiskRuleCode} · {RiskCategory}），复核通过后才能发布：{RiskSummary}");
    }

    /// <summary>运营复核放行：只对仍在待复核队列里的任务有效，放行后可以发布。</summary>
    public void ApproveRiskReview(Guid reviewerId, string note, DateTimeOffset now) =>
        DecideRiskReview(RiskReviewStatus.Approved, reviewerId, note, now);

    /// <summary>运营复核驳回：任务不能发布，用户仍可以自己撤销草稿。</summary>
    public void RejectRiskReview(Guid reviewerId, string note, DateTimeOffset now) =>
        DecideRiskReview(RiskReviewStatus.Rejected, reviewerId, note, now);

    /// <summary>
    /// 所有者提交误拦申诉：只有被判定为禁止类别、或转人工后被驳回的任务可以申诉，
    /// 且同一时间只能有一条待处置的申诉。理由必填并落库，供运营判断是不是误伤。
    /// </summary>
    public void OpenRiskAppeal(Guid ownerId, string reason, DateTimeOffset now)
    {
        if (ownerId != OwnerId) throw new UnauthorizedAccessException("只有任务所有者可以申诉。");
        if (RiskAppealStatus == RiskAppealStatus.Pending) throw new DomainException("这条任务已经有一条待处置的申诉。");
        if (RiskAppealStatus != RiskAppealStatus.None)
        {
            throw new DomainException("这一版内容已经申诉过：请先修改草稿（改完会重新判定风险），再决定是否重新申诉。");
        }

        if (!CanAppealRisk)
        {
            throw RiskVerdict == RiskVerdict.NeedsReview
                ? new DomainException("这条任务还在等人工复核（或已经放行），不需要申诉：请等复核结果。")
                : new DomainException("这条任务没有被风险规则判定为需要申诉的状态。");
        }

        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > MaxRiskAppealReasonLength)
        {
            throw new DomainException($"申诉必须说明理由，长度不超过 {MaxRiskAppealReasonLength} 个字符。");
        }

        RiskAppealStatus = RiskAppealStatus.Pending;
        RiskAppealReason = trimmed;
        RiskAppealedAt = UtcTimestamp.Normalize(now);
        RiskAppealDecidedBy = null;
        RiskAppealDecidedAt = null;
        RiskAppealDecisionNote = null;
    }

    /// <summary>
    /// 运营处置申诉。**两档能力刻意不同**：
    /// - 转人工被驳回的任务：申诉成立即放行（这是运营本来就有权的动作，申诉只是第二双眼睛）；
    /// - 被禁止类别命中的任务：申诉成立也**不会**获得发布许可，只记录"规则误伤"的结论并提示改文案后重新判定。
    ///   禁止类别是平台红线，人工无权放行——这条不变量不能因为多了一个申诉入口就被绕过去。
    /// </summary>
    public void ResolveRiskAppeal(Guid reviewerId, bool accepted, string note, DateTimeOffset now)
    {
        if (RiskAppealStatus != RiskAppealStatus.Pending)
        {
            throw new DomainException("这条任务没有待处置的申诉。");
        }

        if (reviewerId == Guid.Empty) throw new DomainException("申诉处置必须记录处置人。");

        var trimmed = note?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > MaxRiskReviewNoteLength)
        {
            throw new DomainException($"申诉处置必须填写依据，长度不超过 {MaxRiskReviewNoteLength} 个字符。");
        }

        var normalizedNow = UtcTimestamp.Normalize(now);
        RiskAppealStatus = accepted ? RiskAppealStatus.Accepted : RiskAppealStatus.Denied;
        RiskAppealDecidedBy = reviewerId;
        RiskAppealDecidedAt = normalizedNow;
        RiskAppealDecisionNote = trimmed;

        // 转人工被驳回的任务：申诉成立可以直接放行（沿用同一条人工结论路径，只是结论相反）。
        if (accepted && RiskVerdict == RiskVerdict.NeedsReview && RiskReviewStatus == RiskReviewStatus.Rejected)
        {
            RiskReviewStatus = RiskReviewStatus.Approved;
            RiskReviewedBy = reviewerId;
            RiskReviewedAt = normalizedNow;
            RiskReviewNote = trimmed;
        }
    }

    private void DecideRiskReview(RiskReviewStatus decision, Guid reviewerId, string note, DateTimeOffset now)
    {
        if (RiskVerdict != RiskVerdict.NeedsReview) throw new DomainException("该任务没有待处置的风险复核。");
        if (RiskReviewStatus != RiskReviewStatus.Pending)
        {
            throw new DomainException($"该任务的风险复核已经处置过（{RiskReviewStatus}），不能重复处置。");
        }

        if (reviewerId == Guid.Empty) throw new DomainException("风险复核必须记录处置人。");

        var trimmed = note?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > MaxRiskReviewNoteLength)
        {
            throw new DomainException($"风险复核必须填写依据，长度不超过 {MaxRiskReviewNoteLength} 个字符。");
        }

        RiskReviewStatus = decision;
        RiskReviewedBy = reviewerId;
        RiskReviewedAt = UtcTimestamp.Normalize(now);
        RiskReviewNote = trimmed;
    }
}
