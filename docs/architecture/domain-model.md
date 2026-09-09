# 领域模型与状态机

## 1. 聚合与核心实体

### User

账户身份和平台状态。服务者能力通过 `WorkerProfile` 扩展，不把用户复制成两个账户。

### Conversation

包含用户消息、AI 消息、模型运行元数据以及关联的任务草稿。对话不能直接代表用户授权。

### TaskDraft

AI 与用户共同编辑的临时结构。保存字段完整性、风险检查结果和版本号。确认后转换为 `Task`，后续编辑必须重新检查。

### Task

用户公开发布的需求，包含公开信息、私密执行信息、步骤、验收条件、单一固定悬赏和截止时间。建议价格区间只属于草稿辅助信息，不进入已发布任务的交易条件。

### Application

服务者查看任务悬赏和用户公开评价后，表示愿意按当前悬赏执行任务的报名。报名可包含预计到达时间和说明，但不包含服务者自定义价格。选中时生成不可变报名快照。

### Order

用户查看服务者公开评价并选择报名者后形成的执行关系，是执行状态、验收、争议以及未来支付的核心聚合。

### Evidence

订单凭证的元数据，文件内容位于私有对象存储。包括类型、上传者、时间、扫描状态、哈希和关联验收项。

### Review / Dispute / AuditEvent

分别表示评价、争议流程和不可抵赖的关键操作记录。

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

## 3. 报名状态

```text
Pending
  ├─ worker withdraws → Withdrawn
  ├─ user selects     → Selected
  ├─ another selected → Rejected
  └─ task unavailable → Expired
```

报名在服务者声明的有效期内构成按任务当前悬赏接单的承诺。服务者不能提交不同价格。用户选择必须在数据库事务中检查任务状态和报名有效期，并使用并发令牌保证最多一份报名成功；成功后订单直接进入 `Accepted`。

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

## 5. 不变量

- Task 必须有所有者、截止时间、地点范围和至少一项验收标准。
- Published Task 必须具有通过的风险决策版本。`ReadyToPublish` Task 只有所有者可以发布，发布时必须重新验证截止时间和风险决策是否仍有效。
- 同一 Task 最多一个非终态 Order。
- Application 的服务者不能是 Task 所有者，报名中不得包含价格。
- 已发布 Task 的悬赏只能在分配前提高；加价使用并发令牌并形成 `TaskRewardIncreased` 事件。
- Order 的用户、服务者、任务快照和报名快照创建后不可替换。
- 状态转换必须同时验证操作者、当前状态和必要材料。
- AwaitingReview 必须至少有一份通过安全检查的 Evidence。
- Completed、Cancelled 是普通流程下的终态；纠错由受控运营命令产生补偿事件。终态任务和订单不重新开启；再次履约通过复制必要字段创建拥有新标识和新审计链的任务。

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

## 7. 位置与隐私建模

建议分开保存：

- 大厅位置：行政区、商圈或模糊坐标。
- 执行位置：加密的精确地址和坐标。
- 披露策略：何种订单状态、何种角色可以访问。

所有访问精确位置的行为进入安全审计，接口不得因序列化实体而意外泄露字段。

## 8. 金额建模

金额使用 `decimal`，并同时保存币种。推荐值对象：

```text
Money(amount, currency)
```

支付上线后区分任务悬赏、用户应付、平台服务费、服务者应收和退款，不能只用一个 `Amount` 字段承载所有含义。AI 建议价仅是带版本和依据摘要的建议，不是订单金额来源；用户确认的悬赏才进入任务与订单快照。

## 9. 双向评价建模

`Review` 必须关联已完成订单、评价人、被评价人及被评价角色。用户对服务者和服务者对用户使用各自的评分维度及汇总，不能混合成一个分数。

评价具有 `PendingDisclosure`、`Published`、`HiddenByModeration` 状态。双方均提交或订单完成满 7×24 小时后公开；期限使用 UTC 计算并允许平台配置。盲期内查询接口不能返回对方本单评分与正文。公开信用资料同时返回平均分和有效评价数量，防止小样本误导。
