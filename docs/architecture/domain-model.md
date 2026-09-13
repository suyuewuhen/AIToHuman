# 领域模型与状态机

> **实现现状标注**（对齐 2026-09-13 代码，含当轮的风险规则改动）
>
> 本文描述设计目标，不表示当前代码已具备全部能力。正文使用以下标记区分：
>
> - ✅ **已实现**：领域层有实体、值对象或规则，并已通过 API 接入。
> - ⚠️ **部分实现**：类型存在，但缺少字段、状态或规则，或以简化实现承载。
> - ⛔ **未实现**：仅存在于设计，领域层没有对应代码。
>
> 逐项对照见 [交接文档](../development/handoff.md) 和 `backend/AIToHuman.Domain`。

## 0. 实现现状总览

| 设计项 | 状态 | 代码现状 |
| --- | --- | --- |
| `User` | ⚠️ | 账户、JWT 和角色切换已实现；`WorkerProfile` 未实现，服务者没有资料与服务区域 |
| `Conversation` | ✅ | `Conversation` 聚合已实现，消息与草稿状态落库，支持刷新恢复与历史截断；仍缺模型运行元数据 |
| `TaskDraft` | ⚠️ | 无独立草稿实体；**版本号与编辑历史已实现**（`task_draft_revisions` 只追加：创建草稿写第 1 版、每次编辑追加一版，每版记录原文、变更摘要与该版的风险结论，`GET /api/v1/tasks/{id}/revisions` 仅所有者可读）；AI 返回的 `plan` 经确认后直接创建 `ReadyToPublish` 任务，但该任务的字段可编辑（`PUT /api/v1/tasks/{id}`，仅 `ReadyToPublish`），编辑复用创建时的校验并重跑风险判定；仍缺独立聚合与回滚、字段级 diff |
| `Task` | ✅ | `TaskItem` 已实现，含所有者、截止时间、公开区域和验收标准校验；可选的报名截止时间 `ApplicationDeadline` 到点后只关闭新报名；`Expired`（后台扫描超期未分配任务）与 `Cancelled`（所有者撤销、运营下架）都有落库时间戳与原因 |
| `Application` | ⚠️ | `TaskApplication` 已实现；服务者可撤回自己仍处于 `Pending` 的报名（`Withdrawn`，之后可重新报名），订单取消把选中的报名置为 `Rejected`、任务过期把 `Pending` 置为 `Expired`；报名列表内联该服务者的公开评价摘要（`WorkerAverageRating`/`WorkerReviewCount`，只统计已公开评价）；仍无预计到达时间，也没有选人时固化的报名快照 |
| `Order` | ✅ | `Order` 已实现，含参与者校验、状态机、取消（`Cancelled` + 取消人、取消时间、取消原因）与争议（`Disputed` + 发起人、原因、发起时间、处置结果、处置依据与处置时间） |
| 风险模型（`RiskRule`/`RiskAssessment`） | ⚠️ | 代码内版本化规则目录（`RiskRuleCatalog`，版本 1、12 个原因代码）已实现，并接入创建草稿与发布两个门禁，`RiskReviewStatus` 记录人工复核结论；缺模型辅助分类、误拦申诉与追加式决策历史，见第 10 节 |
| `Evidence` | ⚠️ | `OrderEvidence` + `evidence` 表已实现：类型白名单与文件签名校验、扫描状态机与退避重扫、上传时元数据剥离、按人小时配额，存储走 `IFileStorage`（本机目录或 S3 兼容对象存储，支持短时直连下载地址）；订单上的 `EvidenceNote` 仍作为“完成说明”文本框与凭证文件并存。仍缺图片像素级重编码与“凭证关联到具体验收项” |
| `Review` | ⚠️ | `Review` 已实现；无状态字段（盲期按时间动态判定），单一评分维度 |
| `Dispute` | ✅ | 已接入但不单独建表：争议状态与处置信息记在 `orders` 上（`DisputeReason`/`DisputeOpenedBy`/`DisputeOpenedAt`/`DisputeResolution`/`DisputeResolutionNote`/`DisputeResolvedAt`）；参与者按阶段发起，运营三种处置，见第 4 节 |
| `AuditEvent` | ⚠️ | 没有通用业务审计表；运营侧有 `admin_audit_entries`（人工下架与争议处置的三种动作）与配置审计 `system_setting_audits`，其它关键操作仍只体现为实体上的时间戳 |
| 领域事件 | ⚠️ | 领域层仍无事件类型；通知事件由应用层显式入队到 `notifications`（兼作 Outbox），由后台任务派发 |
| 并发控制 | ⚠️ | `tasks`、`orders` 已有 `Version` 乐观并发令牌（冲突返回 `409`），选人与验收使用显式事务；报名行、会话与评价仍无令牌 |
| 幂等 | ⛔ | 未处理 `Idempotency-Key` 或等价防重机制 |

## 1. 聚合与核心实体

### User

账户身份和平台状态。服务者能力通过 `WorkerProfile` 扩展，不把用户复制成两个账户。

> 实现现状（⚠️）：注册、登录、`GET /api/v1/auth/me` 和 `owner`/`worker` 角色切换已实现，一个账户一个用户 ID。`WorkerProfile` 不存在，服务者没有资质、服务区域或分类配置，报名也没有账户状态前置校验。

### Conversation

包含用户消息、AI 消息、模型运行元数据以及关联的任务草稿。对话不能直接代表用户授权。

> 实现现状（✅）：`Conversation` 聚合已实现，包含会话所有者、消息序列（`user`/`assistant`、序号、时间、`ReadyToDraft`、不透明的 `PlanJson`）以及 `UpdatedAt`。不变量：开场白必须最先且唯一、上一条用户消息未回复前不能再追加用户消息、AI 回合必须紧跟用户消息、标记需求已明确时必须同时保存草稿、单条消息 1 至 4000 字符、序号在截断后仍单调递增。历史策略：最多保存 200 条，送给模型最近 30 条（含本轮用户消息）。
>
> 已接入：`POST /api/v1/conversations` 创建、`GET /api/v1/conversations/{id}` 读取、`GET /api/v1/conversations` 列表，以及 AI 流端点在回合成功后落库。会话仅所有者可读可写，越权返回 `403`。
>
> 缺失：模型运行元数据（模型名、提示版本、令牌用量、延迟）、消息编辑与删除、跨会话检索与归档、以及对话内容的保留期限策略。

### TaskDraft

AI 与用户共同编辑的临时结构。保存字段完整性、风险检查结果和版本号。确认后转换为 `Task`，后续编辑必须重新检查。

> 实现现状（⚠️）：没有独立的草稿聚合（也没有独立的风险检查结果实体）。AI 在信息足够时于同一轮返回完整 `plan`，前端提交 `POST /api/v1/tasks` 直接创建 `ReadyToPublish` 任务；`readyToDraft=false` 时后端不返回 `plan`，前端草稿区保持锁定，不使用演示数据兜底。**字段编辑已实现**：`TaskItem.UpdateDraft(title, description, district, deadline, reward, criteria, executionAddress, applicationDeadline, now)` 只允许 `ReadyToPublish` 状态，并且与构造函数**共用同一套字段校验**（抽出的 `NormalizeCriteria` + `EnsureDraftFields`，因此不存在“创建拦得住、编辑能绕过”的字段缺口）；编辑后会立即重跑确定性风险规则并作废原有的人工复核结论（见第 10 节）。**版本号与编辑历史也已实现**：`task_draft_revisions` 只追加，创建草稿写第 1 版（`changeSummary = "创建草稿"`），之后每次编辑追加一版，`Revision` 在同一任务内单调递增；变更摘要由领域层比对前后字段得出（例如“标题、悬赏”；超过 6 个字段时只列前 6 个并追加“等 N 项”，N 是本次变更的字段总数；一个字段都没变时写“无字段变化”）；每版都记下该版文本对应的 `RiskVerdict`/`RiskRuleCode`/`RiskRuleVersion`，因此事后能回答“第几版被判成禁止、用的是哪一版规则”；任务写入与版本快照在同一个工作单元里，并发落败的编辑不会留下多余版本（先撞 `tasks.Version` 乐观并发令牌，再由 `(TaskId, Revision)` 唯一索引兜底）。仍缺：没有独立草稿聚合（历史是任务的附属表）、历史只读（无回滚、无字段级 diff、无导出）、已发布任务的字段仍不可改（历史到发布那一刻为止）。

### Task

用户公开发布的需求，包含公开信息、私密执行信息、步骤、验收条件、单一固定悬赏和截止时间。建议价格区间只属于草稿辅助信息，不进入已发布任务的交易条件。

> 实现现状（✅）：`TaskItem` 已实现标题（≤80 字）、描述（≤4000 字）、`District`（≤120 字）、截止时间、`Money` 悬赏和验收标准集合（≤12 条、每条 ≤200 字），并校验所有者非空、截止时间晚于创建时间、至少一项验收标准。精确执行地址也已实现：`tasks.ExecutionAddress` 由 `ExecutionAddressFor(viewer)` 决定是否披露，只给所有者与**被选中**的服务者，订单取消把选中报名置为 `Rejected` 后立即收回；接口是 `GET /api/v1/tasks/{id}/execution-address`，大厅与公开详情只暴露 `hasExecutionAddress`。风险判定也已落地：`tasks` 上保存 `RiskVerdict`/`RiskRuleCode`/`RiskCategory`/`RiskSummary`/`RiskRuleVersion`/`RiskAssessedAt`/`RiskReviewStatus`/`RiskReviewedBy`/`RiskReviewedAt`/`RiskReviewNote` 十个字段，创建草稿、编辑草稿与发布前各判定一次（见第 10 节）。草稿字段编辑也已接入：`PUT /api/v1/tasks/{id}` 只对 `ReadyToPublish` 开放（其它状态 `422`），响应里的 `draftEditable` 由服务端判定，执行地址仍只走独立接口、不随任务响应返回（`TaskResponse` 也会返回给申请报名的服务者）。每次创建与编辑还会向 `task_draft_revisions` 追加一版只读快照（该版原文 + 该版风险结论，仅所有者可读，见 `TaskDraft`）。尚无：联系方式、任务步骤、分类、隐私等级、取消条件。

### Application

服务者查看任务悬赏和用户公开评价后，表示愿意按当前悬赏执行任务的报名。报名可包含预计到达时间和说明，但不包含服务者自定义价格。选中时生成不可变报名快照。

> 实现现状（⚠️）：`TaskApplication` 已实现 `WorkerId`、备注、提交时间和状态，`Apply` 已校验“不能报名自己的任务”和“不能重复报名（同一服务者仅一条 `Pending`）”。撤回已实现：`TaskItem.WithdrawApplication(applicationId, workerId, now)` 只允许撤回自己的、仍处于 `Pending` 的报名，撤回后记录保留为 `Withdrawn`、服务者可以重新报名（重复报名仍被拦），被选中之后要退出只能走订单取消；端点是 `POST /api/v1/tasks/{id}/applications/{applicationId}/withdraw`，任务所有者会收到 `task.applicationWithdrawn`。**服务者声明的报名有效期**仍没有字段，取而代之的是任务级的报名截止时间（可选的 `ApplicationDeadline` 到点后只关闭新报名，已有报名仍可被选中）；`GET /api/v1/tasks/applications/mine` 返回服务者自己的报名与 `canWithdraw`。缺失：预计到达时间、选中时固化的报名快照；报名的失效只在任务过期时统一置为 `Expired`，也只在订单取消时由 `Selected` 退回 `Rejected`（见第 3 节）。
>
> 双向选择已经双向可见：大厅返回需求方的公开评价摘要（服务者报名前能看到需求方信用），报名列表里也带上了该服务者的公开评价摘要——`TaskApplicationResponse` 在报名本身之外返回 `WorkerAverageRating` 与 `WorkerReviewCount`，供需求方在选人前看信用。摘要只统计**已公开**的评价（双方都提交，或订单完成满 7 天），盲期内的评价不计入，口径与 `GET /api/v1/users/{id}/review-summary` 完全一致（服务层共用同一个公开信用计算）；没有公开评价的服务者返回 `0` 分 / `0` 条，不伪造分数。列表仍只有任务所有者可读，也不含执行地址等参与者层信息。

### Order

用户查看服务者公开评价并选择报名者后形成的执行关系，是执行状态、验收、争议以及未来支付的核心聚合。

> 实现现状（✅）：`Order` 已实现任务、双方参与者、标题与 `Money` 快照、状态机和参与者校验（`EnsureWorker`/`EnsureOwner` + `EnsureStatus`）。`RejectionNote` 保存最近一次驳回原因并在验收通过时清空，`ReworkCount` 累计返工次数。取消已落地：`CancelledAt`、`CancelledBy`、`CancellationReason` 三个字段记录取消人、时间与必填原因。快照仅覆盖标题与悬赏，未保存验收条件、报名快照和执行说明；争议已经落地（`DisputeReason`/`DisputeOpenedBy`/`DisputeOpenedAt`/`DisputeResolution`/`DisputeResolutionNote`/`DisputeResolvedAt` 六个字段，参与者发起与运营处置两条路径见第 4 节），支付相关字段仍未出现。

### Evidence

订单凭证的元数据，文件内容位于私有对象存储。包括类型、上传者、时间、扫描状态、哈希和关联验收项。

> 实现现状（⚠️）：`OrderEvidence` 实体已实现，包含订单、上传者、展示用文件名、MIME、系统生成的存储键、字节数、SHA-256 摘要、创建时间、扫描状态（`Pending`/`Clean`/`Rejected`，终态）以及扫描记账（尝试次数、最近一次说明、最近尝试时间）。类型白名单（JPEG/PNG/WebP/PDF）与文件签名双重校验；单份大小与每单份数默认 5 MB / 10 份，可由运营在硬上限（25 MB / 50 份）内收紧。只有订单服务者能上传，只有订单双方能在扫描通过后通过鉴权下载。
>
> 文件内容走可插拔的 `IFileStorage`：`local` 写本机私有目录，`s3` 走 S3 兼容对象存储（自研 SigV4、路径风格、无 SDK 依赖），并能签发短时直连下载地址（有效期可配 5 至 900 秒）。上传限额（单份大小、每单份数）与按人小时配额都是运营可配置的，但大小与份数只能在领域硬上限（25 MB / 50 份）内收紧；类型白名单刻意留在代码里。`EvidenceContentSanitizer` 会在上传时剥掉 JPEG/PNG/WebP 的元数据段（EXIF/GPS、文本块），像素数据不动，结果记在 `MetadataRemoved` 上。`IEvidenceScanner` 由 `HttpEvidenceScanner` 实现：`provider=none` 时显式放行并打警告，`provider=http` 时调用外部扫描服务，扫描没有结论时凭证保持“待扫描、不可下载”并由后台任务退避重扫（30 秒退避、最多 5 次，文件缺失直接判拒绝）。真实的病毒/内容扫描服务、图片像素级重编码、凭证与验收项关联、以及访问审计都还未实现。

### OrderMessage（订单会话消息）

订单执行期间的双方沟通记录，是未来争议举证与“订单内文字消息”需求（P0 功能之一）的载体。

> 实现现状（⚠️）：`OrderMessage` 已实现订单、发送者、内容（1 至 2000 字）、创建时间和单个 `ReadAt`。只有订单双方可读写，未读按“发送者不是查看者且 `ReadAt` 为空”判定；新消息通过与业务同事务的 Outbox 通知对方。缺失：分页与历史截断、消息撤回或编辑、附件、运营在争议中查看消息的独立审计入口，以及消息内容的保留期限策略。

### Review / Dispute / AuditEvent

分别表示评价、争议流程和不可抵赖的关键操作记录。

> 实现现状：`Review` 已实现（⚠️）；`Dispute`（✅）没有独立聚合，而是 `Order` 上的争议状态与处置字段（见第 4 节）；`AuditEvent`（⚠️）仍没有通用审计表，运营动作写在 `admin_audit_entries` 里。`Review` 包含订单、评价人、被评价人、1–5 星评分、≤1000 字评论和创建时间，并校验不能自评；没有状态字段、被评价角色字段和评分维度区分，也没有举报与运营隐藏能力。

## 2. 任务状态

```text
Draft
  ├─ user confirms + risk allowed → ReadyToPublish Task
  └─ user abandons               → Archived

ReadyToPublish
  ├─ owner publishes             → Published
  └─ owner cancels               → Cancelled

Published
  ├─ deadline reached            → Expired
  ├─ user cancels                → Cancelled
  └─ application selected + order made → Assigned

Assigned
  └─ order reaches terminal state → Closed
```

`TaskDraft` 是独立实体。确认操作固化字段并创建 `ReadyToPublish` Task，发布是随后由所有者执行的独立命令。这样用户可以在最终公开前预览；Task 一旦进入 `Published`，其交易快照不再被草稿编辑影响。

> 实现现状（⚠️）：`TaskStatus` 枚举为 `ReadyToPublish`、`Published`、`Assigned`、`Closed`、`Expired`、`Cancelled`，其中 `ReadyToPublish → Published`（`Publish`，并校验截止时间未过、报名截止时间未过）、`Published → Assigned`（`SelectApplication`）已接入，`Published` 期间可 `IncreaseReward` 和 `Apply`（`Apply` 在报名截止时间已过时返回 `422`“该任务的报名已经截止，不能再报名。”）。
>
> - `Draft`、`Archived` 不在枚举中：没有草稿聚合，也没有放弃草稿的归档路径。
> - `Closed` 已接入：订单验收通过后由 `TaskService.ApproveOrder` 调用 `TaskItem.Close()`，把任务从 `Assigned` 推进到 `Closed`，关闭后不再出现在任务大厅。
> - `Expired` 已接入：后台 `TaskExpiryService` 每 60 秒扫一批（单轮上限 100 条）仍处于 `Published` 且 `Deadline` 已过的任务，置为 `Expired` 并记 `ExpiredAt`，同时把 `Pending` 报名置为 `Expired`、通知任务所有者与每个被作废的报名者（事件键按接收者派生，否则只会入队第一条）。**`Assigned` 的任务不参与扫描**：它归订单流程管，不能因为过了截止时间就从服务者名下消失。扫描是幂等的，同一任务不会重复处理；单条任务被并发改动（已被选中、已被撤销、另一个实例先处理）只跳过那一条，不影响同批其它任务，一轮结果是 `TaskExpiryResult(Expired, Skipped)`。
> - `Cancelled` 已接入：所有者在被选中前可用 `POST /api/v1/tasks/{id}/cancel`（`ownerId` + 必填 `reason`，≤200 字）撤销，记 `CancelledAt`/`CancellationReason` 并通知报名者；运营下架走 `POST /api/v1/admin/tasks/{id}/cancel`，原因除写 `admin_audit_entries` 外同样记在任务上。
> - `ReadyToPublish` 草稿不进入大厅，公开详情也只对所有者可见，其他人拿到 `404`。
> - 报名截止时间（可选）：`ApplicationDeadline` 必须晚于创建时间且不晚于任务截止时间；到点后 `AcceptingApplications(now)` 为 false，但任务仍留在 `Published`——否则已有的报名就没人能选中了——只是不再接受新报名。若报名截止时间已过还去发布，`Publish` 直接拒绝（“报名截止时间已过，任务不能发布：请撤销后重新创建，或先调整报名截止时间。”）。
> - 发布前会重新判定风险（悬赏与截止时间在草稿阶段都可能被改），命中禁止类别、或判成需人工复核但尚未被放行的任务一律 `422` 不能发布；被拦的草稿仍然保留，用户能看到原因并自行撤销（见第 10 节）。

## 3. 报名状态

```text
Pending
  ├─ worker withdraws → Withdrawn
  ├─ user selects     → Selected
  ├─ another selected → Rejected
  └─ task unavailable → Expired
```

报名在服务者声明的有效期内构成按任务当前悬赏接单的承诺。服务者不能提交不同价格。用户选择必须在数据库事务中检查任务状态和报名有效期，并使用并发令牌保证最多一份报名成功；成功后订单直接进入 `Accepted`。

> 实现现状（⚠️）：`TaskApplicationStatus` 枚举为 `Pending`、`Selected`、`Withdrawn`、`Rejected`、`Expired`。`SelectApplication` 把选中的一条置为 `Selected`，其余 `Pending` 一并置为 `Rejected`；除此之外还接入了 `Selected → Rejected`（订单被取消，服务者可重新报名）、`Pending → Expired`（任务过了截止时间被后台置为过期）与 `Pending → Withdrawn`（服务者自己撤回报名）。
>
> - 服务者声明的报名有效期仍没有字段；代替它的是任务级的报名截止时间 `ApplicationDeadline`（可选），到点只关闭新报名、**已有报名仍可被选中**，所以它不是“报名失效时间”。报名失效只发生在任务过期时（`Pending → Expired`）。
> - 撤回只允许自己的 `Pending` 报名：`Selected` 之后要退出只能走订单取消（`422`）；撤回是状态迁移而不是删除，记录保留（`Withdrawn`），服务者之后可以重新报名，重复报名仍被拦。撤回后任务所有者收到 `task.applicationWithdrawn`。
> - 订单取消时把选中报名置为 `Rejected` 不只是状态清理：执行地址只对当前被选中的服务者披露，留着 `Selected` 会继续向他泄露地址。
> - 选择报名的并发安全由任务行的 `Version` 乐观并发令牌 + 显式数据库事务保证（选人与建订单同一事务），并有 `orders.TaskId` 唯一索引兜底；并发选单的“最多一份成功”已用 12 路并行请求在真实 PostgreSQL 上验证。
> - 选中后在同一次调用中直接创建 `Accepted` 订单，与设计一致。

## 4. 订单状态

```text
Accepted → EnRoute → InProgress → AwaitingReview
                                      ├─ accepted → Completed
                                      ├─ rejected → InProgress
                                      └─ dispute  → Disputed

Accepted / EnRoute / InProgress
  ├─ allowed cancellation → Cancelled
  └─ dispute              → Disputed

Disputed
  ├─ resume execution → InProgress
  ├─ accept result    → Completed
  └─ terminate        → Cancelled
```

首版不设置 `PendingAcceptance`。服务者无法履行时必须走受审计的取消流程，平台据此计算履约指标；未来若引入非承诺型推荐或抢单模式，再通过独立 ADR 扩展状态机。

> 实现现状（⚠️）：`OrderStatus` 枚举为 `Accepted`、`InProgress`、`Submitted`、`Approved`、`Rejected`、`Disputed`、`Cancelled`；已接入的实际状态机是 `Accepted → InProgress → Submitted → Approved`、`Submitted → Rejected`、返工路径 `Rejected → InProgress`、取消 `Accepted`/`InProgress` → `Cancelled`，以及争议 `Submitted`/`Rejected` → `Disputed`（再由运营处置到 `Approved`/`InProgress`/`Cancelled`）。与上文设计的对应关系：
>
> | 设计状态 | 实现 | 接入方式与差异 |
> | --- | --- | --- |
> | `Accepted` | ✅ `Accepted` | 选人时自动创建，无需服务者二次确认 |
> | `EnRoute` | ⛔ 无 | 没有“出发/在路上”状态与接口 |
> | `InProgress` | ✅ `InProgress` | 服务者 `POST /api/v1/orders/{id}/start` |
> | `AwaitingReview` | ⚠️ `Submitted` | 提交换名为 `Submitted`，要求必填执行凭证说明 |
> | `Completed` | ⚠️ `Approved` | 终态改名为 `Approved`，`Approved` 后才可评价 |
> | `rejected → InProgress` | ✅ `Rejected → InProgress` | 返工闭环已接入：`Order.ResumeRework(actorId)` 校验操作者是服务者且订单处于 `Rejected`，随后 `ReworkCount++` 并退回 `InProgress`；驳回原因保留在 `RejectionNote`（验收通过时清空）以便追溯。端点为 `POST /api/v1/orders/{id}/resume`，前端“继续返工”按钮调用它 |
> | `Disputed` | ✅ `Disputed` | 已接入：`POST /api/v1/orders/{id}/dispute`（复用 `OrderActionRequest`，`note` 即争议原因，必填 ≤500 字）由参与者发起——需求方只能在 `Submitted`（服务者已提交验收）发起，服务者只能在 `Rejected`（验收被驳回）发起，非参与者返回“只有订单参与者可以发起争议。”，其它阶段同样返回可读的 `422` 说明。争议期间订单冻结：提交、验收、驳回、返工、取消一律 `422`，任务保持 `Assigned`、不会回到大厅。运营处置：`GET /api/v1/admin/orders?status=`（省略 `status` 默认只返回 `Disputed`，传 `all` 看全部）检索，`POST /api/v1/admin/orders/{id}/resolve`（`{ decision, note }`，`decision ∈ { Approve, Rework, Cancel }`，`note` 必填 ≤500 字）三种结果：`Approve` 订单 → `Approved`（写 `ReviewedAt`/`ReviewNote`、清空 `RejectionNote`）且任务 `Assigned → Closed`；`Rework` 订单 → `InProgress`、`ReworkCount` 累加、`RejectionNote` 记处置依据、任务保持 `Assigned`；`Cancel` 订单 → `Cancelled`（`CancelledAt` 落库、`CancelledBy` 为 **null**——表示平台处置而不是某个参与者取消、`CancellationReason` 记处置依据），任务按“取消订单”的规则回到大厅或直接过期。三种处置都在同一事务里写运营审计（`order.dispute.approve`/`order.dispute.rework`/`order.dispute.cancel`，`targetType=order`，依据写在 `reason`）并通知双方（发起时对方收到 `order.disputed`，处置后双方收到 `order.disputeResolved`，事件键按接收者派生） |
> | `Cancelled` | ✅ `Cancelled` | 已接入：`POST /api/v1/orders/{id}/cancel`（`actorId` + 必填 `note`，≤200 字）记 `CancelledAt`/`CancelledBy`/`CancellationReason`。服务者只能在 `Accepted`（还没开始执行）时取消，开工后要终止必须由需求方发起；需求方在 `Submitted` 之前都可取消；提交验收之后双方都不能取消（先验收或驳回）；`Approved`/`Cancelled` 不能再取消，非参与者返回「只有订单参与者可以取消订单。」（均为 `422` + 可读中文原因）。同一事务内连带：任务未过截止时间则退回 `Published` 重新招募并把选中报名置为 `Rejected`，已过截止时间则置为 `Expired`（记 `ExpiredAt`，取消者不是需求方时需求方还会收到 `task.expired`）；双方参与者收到 `order.cancelled` |
>
> 另外，订单验收通过时 `TaskService.ApproveOrder` 在同一个事务里调用 `CloseTaskIfAssigned`，把 `Assigned` 任务推进到 `Closed`（返工期间任务保持 `Assigned`，不会回到大厅）；执行凭证已经是独立实体与独立表（`OrderEvidence` + `evidence`），支持上传、鉴权下载、扫描门禁与短时直连地址，`EvidenceNote` 只承担“完成说明”文本的角色。

## 5. 不变量

- ✅ Task 必须有所有者、截止时间、地点范围和至少一项验收标准。`TaskItem` 构造函数已校验，草稿编辑（`UpdateDraft`）复用同一套校验，因此创建与编辑不会出现两套规则；但“地点范围”目前只是 `District` 字符串，没有坐标、范围或披露策略模型。
- ✅ 草稿字段创建后可编辑，但只限 `ReadyToPublish`，且编辑会重跑风险判定并作废原有的人工复核结论。人工结论绑定的是**当时那份文本**：否则“先提干净文案过审、再改成禁止内容”就是现成的绕过路径；反向也成立——被运营驳回的草稿只要改掉敏感内容，就会拿到新一轮判定，而不是被永久钉死。
- ✅ 报名截止时间（可选）必须晚于创建时间且不晚于任务截止时间。`TaskItem` 构造函数已校验；到点只关闭新报名（`AcceptingApplications(now)` 为 false），已有报名仍可被选中，因此任务仍停留在 `Published`；报名截止时间已过的任务不能发布。
- ⚠️ Published Task 必须具有通过的风险决策版本。已实现的是：创建草稿与每次发布都用当前规则目录判定一次，任务上保存结论、原因代码与 `RiskRuleVersion`，禁止类别不能发布（人工也无权放行），判成需人工复核的必须先被运营放行；仍没有独立的“风险决策”实体与版本表，任务行上只保留最新一条结论（见第 10 节）。`ReadyToPublish` 任务仍只有所有者可以发布。
- ⚠️ 同一 Task 最多一个非终态 Order。现在由任务行的乐观并发令牌（并发的第二个选人会拿到 `409`）、`orders.TaskId` 唯一索引和“任务进入 `Assigned` 后不再接受报名”共同保证；仍没有按“非终态订单”查询的显式校验。
- ✅ Application 的服务者不能是 Task 所有者，报名中不得包含价格。领域层已校验，`ApplyForTaskRequest` 没有价格字段。
- ⚠️ 已发布 Task 的悬赏只能在分配前提高。`IncreaseReward` 已校验“仅 `Published` 状态、同币种、金额只能提高”，并受任务行并发令牌保护；`TaskRewardIncreased` 事件仍未实现，加价也不通知已报名者。
- ⚠️ Order 的用户、服务者、任务快照和报名快照创建后不可替换。参与者创建后不可替换，但只固化了标题和悬赏，验收条件、执行说明和报名快照未进入订单。
- ✅ 状态转换必须同时验证操作者、当前状态和必要材料。`EnsureWorker`/`EnsureOwner` 区分服务者与需求方动作，`EnsureStatus` 校验当前状态；提交凭证和驳回验收均要求必填说明。
- ⚠️ AwaitingReview 必须至少有一份通过安全检查的 Evidence。当前只要求 `EvidenceNote` 非空文本，没有文件、类型/大小校验或扫描状态。
- ⚠️ Completed、Cancelled 是普通流程下的终态。订单终态目前是 `Approved`（不可重开）与 `Cancelled`（参与者取消或运营在争议中处置），任务终态是 `Closed`、`Cancelled` 与 `Expired`；`Disputed` 不是终态，运营处置会把订单带到 `Approved`/`InProgress`/`Cancelled`。纠错用的受控运营命令和“复制字段创建新任务”的流程未实现。
- ✅ 所有领域时刻以 UTC 保存。`TaskItem`、`Order`、`TaskApplication`、`Review` 的构造函数统一调用 `UtcTimestamp.Normalize`，因为 PostgreSQL 的 `timestamp with time zone` 只接受零偏移量；此前客户端或 AI 提交带 `+08:00` 的 `deadline` 会让创建任务返回 `500`。

## 6. 领域事件

- `TaskPublished`
- `ApplicationSubmitted`
- `ApplicationSelected`
- `TaskRewardIncreased`
- `OrderCreated`
- `OrderStatusChanged`
- `EvidenceUploaded`
- `OrderSubmittedForReview`
- `OrderCompleted`
- `DisputeOpened`
- `RiskReviewRequested`

事件用于通知、审计和异步工作，不应把关键不变量推迟到异步消费者处理。

> 实现现状（⚠️）：领域层仍然没有事件类型，也没有事件存储；但通知已经具备可靠投递链路：`Notification` 实体 + `notifications` 表（`EventId` 唯一索引做幂等），应用层在业务事务内写入通知（兼作 Outbox），`NotificationDispatcher` 后台任务扫描未派发记录并通过 SignalR 推送 `notification.created` 信封（`eventId`/`type`/`version`/`occurredAt`/`payload`）。派发前先用条件 UPDATE 原子认领（`WHERE DispatchedAt IS NULL`），失败则撤回认领退回 Outbox 等下一轮，因此多实例不会重复推送；多实例经 Redis 扇出转发到各实例的在线客户端，未启用扇出时只推本实例。
>
> 已接入的事件类型：`order.created`（选人后通知服务者）、`order.statusChanged`（开始、提交、验收、驳回、返工时通知对方参与者）、`order.cancelled`（取消订单时通知对方参与者）、`order.disputed`（参与者发起争议时通知对方参与者）、`order.disputeResolved`（运营处置后通知双方，事件键按接收者派生）、`task.expired`（任务过期时通知所有者与每个被作废的报名者）、`task.cancelled`（所有者撤销或运营下架任务时通知报名者）、`task.applicationWithdrawn`（服务者撤回报名时通知任务所有者，载荷含 `taskId`/`applicationId`/`workerId`/`title`）、`task.riskReviewed`（运营放行或驳回风险复核后通知任务所有者，载荷 `{ taskId, title, reviewStatus, verdict, ruleCode, canPublish }`）。订单类载荷是 `OrderNotificationPayload(orderId, status, title)`，任务类载荷是 `TaskNotificationPayload(taskId, status, title)`。`TaskPublished`、`TaskRewardIncreased`、`ReviewPublished` 等仍未实现；`EvidenceUploaded` 依赖尚未实现的功能，`RiskReviewRequested` 作为领域事件也仍未实现（通知侧已有 `task.riskReviewed`）。

## 7. 位置与隐私建模

建议分开保存：

- 大厅位置：行政区、商圈或模糊坐标。
- 执行位置：加密的精确地址和坐标。
- 披露策略：何种订单状态、何种角色可以访问。

所有访问精确位置的行为进入安全审计，接口不得因序列化实体而意外泄露字段。

> 实现现状（⚠️）：只实现了“大厅位置”的最简形式——`TaskItem.District` 字符串，公开大厅和公开详情只返回区域名、悬赏、验收标准和报名数。
>
> - 执行位置已实现为 `TaskItem.ExecutionAddress`（≤200 字、可空），只在订单成立后向所有者与被选中的服务者披露；已报名但未被选中的服务者和匿名访问者返回 `403`，大厅与公开详情只暴露 `hasExecutionAddress` 布尔值。
> - 没有坐标、地理围栏和距离计算，也没有联系人或联系方式代理。
> - 没有位置访问审计。加价、选人、验收等关键操作同样缺少独立审计事件（只有部分实体时间戳）。
> - 由于精确位置只在参与者层披露，目前“未采集”不再是主要风险；但访问审计缺失意味着无法回答“谁在什么时候看过这个地址”。

## 8. 金额建模

金额使用 `decimal`，并同时保存币种。推荐值对象：

```text
Money(amount, currency)
```

支付上线后区分任务悬赏、用户应付、平台服务费、服务者应收和退款，不能只用一个 `Amount` 字段承载所有含义。AI 建议价仅是带版本和依据摘要的建议，不是订单金额来源；用户确认的悬赏才进入任务与订单快照。

> 实现现状（✅ Money / ⚠️ 金额语义）：`Money` 值对象已实现，使用 `decimal` 加三位币种代码（默认 `CNY`），校验金额大于零并四舍五入到两位小数；任务和订单都保存 `Reward` 快照，订单创建后不受任务加价影响。
>
> - 只有“悬赏”一种金额语义，没有用户应付、平台服务费、服务者应收、退款或账本字段。
> - AI 建议价由 `POST /api/v1/reward-suggestions` 的本地规则计算，不调用模型，也不写入任务金额；用户提交的 `reward` 才是任务悬赏。
> - 当前建议价响应不含规则/模型版本，也没有数据充分度指标。

## 9. 双向评价建模

`Review` 必须关联已完成订单、评价人、被评价人及被评价角色。用户对服务者和服务者对用户使用各自的评分维度及汇总，不能混合成一个分数。

评价具有 `PendingDisclosure`、`Published`、`HiddenByModeration` 状态。双方均提交或订单完成满 7×24 小时后公开；期限使用 UTC 计算并允许平台配置。盲期内查询接口不能返回对方本单评分与正文。公开信用资料同时返回平均分和有效评价数量，防止小样本误导。

> 实现现状（⚠️）：公开条件已按设计实现，但没有独立状态字段。
>
> - `Review` 不含状态列，公开性在查询时动态判定：本单评价数 ≥ 2，或当前 UTC 时间 ≥ 该订单最早评价时间 + 7 天。
> - 7 天期限是代码常量，不是可配置的平台设置；使用 `TimeProvider` 获取 UTC 时间。
> - 评价用单一 1–5 星评分和一段文字，没有区分“用户评价服务者”和“服务者评价用户”的评分维度，但通过 `RevieweeId` 分别汇总到各自信用资料。
> - 每方每个订单只能评价一次：应用层先查 `GetByReviewer` 并返回 `422`，数据库 `(OrderId, ReviewerId)` 唯一索引仅作兜底。
> - 公开摘要返回平均分与评价数量，已满足“显示样本量”要求。同一份公开信用同时用于两处：`GET /api/v1/users/{id}/review-summary`（公开资料）与任务报名列表（需求方选人前看服务者信用），因此两处口径不会漂移；服务者侧看需求方信用仍走大厅与公开详情里的公开摘要。
> - `HiddenByModeration`、举报处理、公开后不可修改/删除的约束都未实现（当前也没有修改或删除接口）。

## 10. 风险规则与人工复核

平台禁止的类别必须由确定性规则判定，不能只依赖模型自由文本：规则目录固定在代码里，任何一次拦截都要能回答“用的是哪条规则、哪一版”，因此原因代码一旦上线就保持稳定，规则改动必须提升目录版本号。规则只有两种结论——`Blocked`（一律不能发布）与 `NeedsReview`（进人工队列，等运营放行）。

> 实现现状（⚠️）：领域层已实现 `RiskVerdict`（`Allowed`/`NeedsReview`/`Blocked`）、`RiskReviewStatus`（`NotRequired`/`Pending`/`Approved`/`Rejected`）、`RiskRule`、`RiskAssessment` 与 `RiskRuleCatalog`（`Version = 1`）。
>
> - 规则目录：10 条词表规则（6 条 `prohibited.*` + 4 条 `review.*`）加 2 条阈值规则（`review.high_reward`：悬赏 > 5000 元；`review.night_window`：截止时间落在北京时间 00:00–06:00），共 12 个原因代码；匹配词表里刻意不用单字，避免“代取”“代送”这类正常跑腿任务被误伤。
> - 判定输入只有用户填写的字段（标题、描述、验收标准、执行地址）；顺序是先禁止类、再转人工类、最后两条阈值规则，都不命中才是 `Allowed`。说明文本（`RiskSummary`）刻意不含命中的具体词，避免被逐字试探绕过。
> - 门禁位置：`TaskItem` 构造（创建草稿）时判定一次，`Publish` 前再判定一次（悬赏与截止时间在草稿阶段会变），草稿编辑（`UpdateDraft`）后再判定一次。禁止类别一律 `422` 且人工无权放行；判成 `NeedsReview` 的任务进入队列，运营 `Approve` 后才能发布，`Reject` 是终态（规则之后不再命中也不会变回可发布）。
> - 编辑草稿会让判定重跑并清空人工结论：`UpdateDraft` 以 `resetHumanDecision: true` 调用判定，`RiskReviewedBy`/`RiskReviewedAt`/`RiskReviewNote` 一并置空，状态按新文本重新判定（需要复核的回到 `Pending`，会重新出现在运营队列里）。审核针对的是某一版文本，文本一变结论即作废；`Reject` 只是当时那份文本的终态，不是对任务的永久封禁。同一次编辑还会向 `task_draft_revisions` 追加一版快照并记下该版的判定结论（`RiskVerdict`/`RiskRuleCode`/`RiskRuleVersion`），所以事后能对上是哪一版文本被判成什么、用的是哪一版规则。
> - 落库字段：`RiskVerdict`、`RiskRuleCode`、`RiskCategory`、`RiskSummary`、`RiskRuleVersion`、`RiskAssessedAt`、`RiskReviewStatus`、`RiskReviewedBy`、`RiskReviewedAt`、`RiskReviewNote`（复核依据必填、≤200 字）。
> - 人工复核走管理员接口：队列 `GET /api/v1/admin/risk/reviews` 按创建时间升序（先来先处理），`POST /api/v1/admin/risk/reviews/{taskId}/decide` 放行或驳回并把依据写进 `admin_audit_entries`（动作 `task.risk.approve`/`task.risk.reject`），`GET /api/v1/admin/risk/rules` 是规则目录自述（版本、阈值、规则清单与匹配词数量，不返回词本身）；非管理员 `403`。
> - 通知：复核结论通过 `task.riskReviewed` 走既有 Outbox + SignalR 链路；任务响应里的 `riskPublishBlocked` 由服务端判定，客户端不要自己按 `riskVerdict`/`riskReviewStatus` 推算。
> - 缺失：模型辅助分类与语义判断（现在只有字面词表匹配，改写过的表述可能漏过）；误拦申诉与客服工单——被拦用户没有申诉入口；规则目录不能后台编辑（改规则要发版并提升 `Version`）；决策只保留最新一条（记在任务行上），没有追加式的决策历史表，也没有独立的风险事件流。
