# 领域模型与状态机

> **实现现状标注**（对齐 2026-09-12 已提交代码，提交 `e45da76`）
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
| `TaskDraft` | ⛔ | 无草稿实体与版本号；AI 返回的 `plan` 经确认后直接创建 `ReadyToPublish` 任务 |
| `Task` | ✅ | `TaskItem` 已实现，含所有者、截止时间、公开区域和验收标准校验 |
| `Application` | ⚠️ | `TaskApplication` 已实现；无有效期字段，无撤回与过期流程 |
| `Order` | ✅ | `Order` 已实现，含参与者校验和状态机 |
| `Evidence` | ⚠️ | 无独立实体和对象存储；凭证以订单上的 `EvidenceNote` 文本承载（≤4000 字符） |
| `Review` | ⚠️ | `Review` 已实现；无状态字段（盲期按时间动态判定），单一评分维度 |
| `Dispute` | ⛔ | 无实体、无接口；`OrderStatus.Disputed` 仅枚举占位 |
| `AuditEvent` | ⛔ | 无实体、无审计日志；操作痕迹只体现为部分实体上的时间戳 |
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

> 实现现状（⛔）：没有独立草稿实体、版本号和风险检查结果。AI 在信息足够时于同一轮返回完整 `plan`，前端提交 `POST /api/v1/tasks` 直接创建 `ReadyToPublish` 任务；`readyToDraft=false` 时后端不返回 `plan`，前端草稿区保持锁定，不使用演示数据兜底。字段编辑本身没有独立的重新校验步骤，校验发生在创建任务时。

### Task

用户公开发布的需求，包含公开信息、私密执行信息、步骤、验收条件、单一固定悬赏和截止时间。建议价格区间只属于草稿辅助信息，不进入已发布任务的交易条件。

> 实现现状（✅）：`TaskItem` 已实现标题（≤80 字）、描述、`District`、截止时间、`Money` 悬赏和验收标准集合，并校验所有者非空、截止时间晚于创建时间、至少一项验收标准。尚无：私密执行信息（精确地址、联系方式）、任务步骤、分类、隐私等级、取消条件、风险决策版本，以及“任务”级别的并发令牌。

### Application

服务者查看任务悬赏和用户公开评价后，表示愿意按当前悬赏执行任务的报名。报名可包含预计到达时间和说明，但不包含服务者自定义价格。选中时生成不可变报名快照。

> 实现现状（⚠️）：`TaskApplication` 已实现 `WorkerId`、备注、提交时间和状态，`Apply` 已校验“不能报名自己的任务”和“不能重复报名（同一服务者仅一条 `Pending`）”。缺失：预计到达时间、报名有效期字段、撤回接口、过期处理，以及选中时固化的报名快照。
>
> 双向选择目前是单向可见：大厅返回需求方的公开评价摘要，服务者报名前能看到需求方信用；但报名接口只返回 `WorkerId`、备注、状态和提交时间，需求方在选人时看不到服务者的评价摘要。这属于接口缺口，不是有意设计。

### Order

用户查看服务者公开评价并选择报名者后形成的执行关系，是执行状态、验收、争议以及未来支付的核心聚合。

> 实现现状（✅）：`Order` 已实现任务、双方参与者、标题与 `Money` 快照、状态机和参与者校验（`EnsureWorker`/`EnsureOwner` + `EnsureStatus`）。`RejectionNote` 保存最近一次驳回原因并在验收通过时清空，`ReworkCount` 累计返工次数。快照仅覆盖标题与悬赏，未保存验收条件、报名快照和执行说明；也没有争议、取消、支付相关字段。

### Evidence

订单凭证的元数据，文件内容位于私有对象存储。包括类型、上传者、时间、扫描状态、哈希和关联验收项。

> 实现现状（⚠️）：`OrderEvidence` 实体已实现，包含订单、上传者、展示用文件名、MIME、系统生成的存储键、字节数、SHA-256 摘要、创建时间、扫描状态（`Pending`/`Clean`/`Rejected`，终态）以及扫描记账（尝试次数、最近一次说明、最近尝试时间）。类型白名单（JPEG/PNG/WebP/PDF）与文件签名双重校验；单份大小与每单份数默认 5 MB / 10 份，可由运营在硬上限（25 MB / 50 份）内收紧。只有订单服务者能上传，只有订单双方能在扫描通过后通过鉴权下载。
>
> 文件内容走可插拔的 `IFileStorage`：`local` 写本机私有目录，`s3` 走 S3 兼容对象存储（自研 SigV4、路径风格、无 SDK 依赖），短时签名 URL 尚未实现。上传限额（单份大小、每单份数）也是运营可配置的，但只能在领域硬上限（25 MB / 50 份）内收紧；类型白名单刻意留在代码里。`IEvidenceScanner` 由 `HttpEvidenceScanner` 实现：`provider=none` 时显式放行并打警告，`provider=http` 时调用外部扫描服务，扫描没有结论时凭证保持“待扫描、不可下载”并由后台任务退避重扫（30 秒退避、最多 5 次，文件缺失直接判拒绝）。真实病毒/内容扫描服务商、图片重新编码与 EXIF 去除、上传限速、凭证与验收项关联、以及访问审计都还未实现。

### OrderMessage（订单会话消息）

订单执行期间的双方沟通记录，是未来争议举证与“订单内文字消息”需求（P0 功能之一）的载体。

> 实现现状（⚠️）：`OrderMessage` 已实现订单、发送者、内容（1 至 2000 字）、创建时间和单个 `ReadAt`。只有订单双方可读写，未读按“发送者不是查看者且 `ReadAt` 为空”判定；新消息通过与业务同事务的 Outbox 通知对方。缺失：分页与历史截断、消息撤回或编辑、附件、运营在争议中查看消息的独立审计入口，以及消息内容的保留期限策略。

### Review / Dispute / AuditEvent

分别表示评价、争议流程和不可抵赖的关键操作记录。

> 实现现状：`Review` 已实现（⚠️）；`Dispute`（⛔）和 `AuditEvent`（⛔）没有对应代码。`Review` 包含订单、评价人、被评价人、1–5 星评分、≤1000 字评论和创建时间，并校验不能自评；没有状态字段、被评价角色字段和评分维度区分，也没有举报与运营隐藏能力。

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

> 实现现状（⚠️）：`TaskStatus` 枚举为 `ReadyToPublish`、`Published`、`Assigned`、`Closed`、`Expired`、`Cancelled`，其中 `ReadyToPublish → Published`（`Publish`，并校验截止时间未过）、`Published → Assigned`（`SelectApplication`）已接入，`Published` 期间可 `IncreaseReward` 和 `Apply`。
>
> - `Draft`、`Archived` 不在枚举中：没有草稿聚合，也没有放弃草稿的归档路径。
> - `Closed` 已接入：订单验收通过后由 `TaskService.ApproveOrder` 调用 `TaskItem.Close()`，把任务从 `Assigned` 推进到 `Closed`，关闭后不再出现在任务大厅。`Expired`、`Cancelled` 仍只有枚举值，没有截止时间过期作业、取消接口和取消原因记录。
> - `ReadyToPublish` 草稿不进入大厅，公开详情也只对所有者可见，其他人拿到 `404`。
> - 发布时不重新校验风险决策，因为风险决策尚未实现。

## 3. 报名状态

```text
Pending
  ├─ worker withdraws → Withdrawn
  ├─ user selects     → Selected
  ├─ another selected → Rejected
  └─ task unavailable → Expired
```

报名在服务者声明的有效期内构成按任务当前悬赏接单的承诺。服务者不能提交不同价格。用户选择必须在数据库事务中检查任务状态和报名有效期，并使用并发令牌保证最多一份报名成功；成功后订单直接进入 `Accepted`。

> 实现现状（⚠️）：`TaskApplicationStatus` 枚举为 `Pending`、`Selected`、`Withdrawn`、`Rejected`、`Expired`，实际只用到 `Pending`、`Selected` 和 `Rejected`——`SelectApplication` 把选中的一条置为 `Selected`，其余 `Pending` 一并置为 `Rejected`（简化实现，未使用 `Withdrawn`/`Expired`）。
>
> - 没有报名有效期字段，因此“按有效期校验”和报名自动过期都不存在。
> - 没有撤回接口。
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

> 实现现状（⚠️）：`OrderStatus` 枚举为 `Accepted`、`InProgress`、`Submitted`、`Approved`、`Rejected`、`Disputed`、`Cancelled`；已接入的实际状态机是 `Accepted → InProgress → Submitted → Approved`、`Submitted → Rejected`，以及返工路径 `Rejected → InProgress`。与上文设计的对应关系：
>
> | 设计状态 | 实现 | 接入方式与差异 |
> | --- | --- | --- |
> | `Accepted` | ✅ `Accepted` | 选人时自动创建，无需服务者二次确认 |
> | `EnRoute` | ⛔ 无 | 没有“出发/在路上”状态与接口 |
> | `InProgress` | ✅ `InProgress` | 服务者 `POST /api/v1/orders/{id}/start` |
> | `AwaitingReview` | ⚠️ `Submitted` | 提交换名为 `Submitted`，要求必填执行凭证说明 |
> | `Completed` | ⚠️ `Approved` | 终态改名为 `Approved`，`Approved` 后才可评价 |
> | `rejected → InProgress` | ⛔ 不支持 | `Rejected` 是终态：驳回后没有返工或重新提交路径（P0 待办） |
> | `Disputed` | ⛔ 仅枚举 | 除订单状态流转接口外没有争议接口，也没有冻结结算语义 |
> | `Cancelled` | ⛔ 仅枚举 | 没有取消接口、取消人和取消原因记录，任务也不会随之取消 |
>
> 另外，订单批准后不会自动把任务推进到 `Closed`；服务者提交凭证仅有文本说明，没有 `Evidence` 材料校验。

## 5. 不变量

- ✅ Task 必须有所有者、截止时间、地点范围和至少一项验收标准。`TaskItem` 构造函数已校验；但“地点范围”目前只是 `District` 字符串，没有坐标、范围或披露策略模型。
- ⚠️ Published Task 必须具有通过的风险决策版本。风险决策实体与版本尚未实现，发布时只校验截止时间是否已过，`ReadyToPublish` 任务也只有所有者可以发布。
- ⚠️ 同一 Task 最多一个非终态 Order。现在由任务行的乐观并发令牌（并发的第二个选人会拿到 `409`）、`orders.TaskId` 唯一索引和“任务进入 `Assigned` 后不再接受报名”共同保证；仍没有按“非终态订单”查询的显式校验。
- ✅ Application 的服务者不能是 Task 所有者，报名中不得包含价格。领域层已校验，`ApplyForTaskRequest` 没有价格字段。
- ⚠️ 已发布 Task 的悬赏只能在分配前提高。`IncreaseReward` 已校验“仅 `Published` 状态、同币种、金额只能提高”，并受任务行并发令牌保护；`TaskRewardIncreased` 事件仍未实现，加价也不通知已报名者。
- ⚠️ Order 的用户、服务者、任务快照和报名快照创建后不可替换。参与者创建后不可替换，但只固化了标题和悬赏，验收条件、执行说明和报名快照未进入订单。
- ✅ 状态转换必须同时验证操作者、当前状态和必要材料。`EnsureWorker`/`EnsureOwner` 区分服务者与需求方动作，`EnsureStatus` 校验当前状态；提交凭证和驳回验收均要求必填说明。
- ⚠️ AwaitingReview 必须至少有一份通过安全检查的 Evidence。当前只要求 `EvidenceNote` 非空文本，没有文件、类型/大小校验或扫描状态。
- ⚠️ Completed、Cancelled 是普通流程下的终态。当前终态是 `Approved` 且不可重开，与设计一致；但纠错用的受控运营命令和“复制字段创建新任务”的流程未实现。
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

> 实现现状（⚠️）：领域层仍然没有事件类型，也没有事件存储；但通知已经具备可靠投递链路：`Notification` 实体 + `notifications` 表（`EventId` 唯一索引做幂等），应用层在业务事务内写入通知（兼作 Outbox），`NotificationDispatcher` 后台任务扫描未派发记录并通过 SignalR 推送 `notification.created` 信封（`eventId`/`type`/`version`/`occurredAt`/`payload`），成功后标记派发时间，失败自动重试。
>
> 已接入的事件类型：`order.created`（选人后通知服务者）、`order.statusChanged`（开始、提交、验收、驳回、返工时通知对方参与者）。`TaskPublished`、`TaskRewardIncreased`、`ReviewPublished` 等仍未实现；`EvidenceUploaded`、`DisputeOpened`、`RiskReviewRequested` 依赖尚未实现的功能。

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
> - 公开摘要返回平均分与评价数量，已满足“显示样本量”要求。
> - `HiddenByModeration`、举报处理、公开后不可修改/删除的约束都未实现（当前也没有修改或删除接口）。
