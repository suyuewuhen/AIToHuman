# 领域模型与状态机

## 1. 聚合与核心实体

### User

账户身份和平台状态。服务者能力通过 `WorkerProfile` 扩展，不把用户复制成两个账户。

### Conversation

包含用户消息、AI 消息、模型运行元数据以及关联的任务草稿。对话不能直接代表用户授权。

### TaskDraft

AI 与用户共同编辑的临时结构。保存字段完整性、风险检查结果和版本号。确认后转换为 `Task`，后续编辑必须重新检查。

### Task

用户公开发布的需求，包含公开信息、私密执行信息、步骤、验收条件、预算方式和截止时间。

### Offer

服务者对任务提交的交易条件。选中时生成不可变报价快照。

### Order

用户选择报价后形成的执行关系，是执行状态、验收、争议以及未来支付的核心聚合。

### Evidence

订单凭证的元数据，文件内容位于私有对象存储。包括类型、上传者、时间、扫描状态、哈希和关联验收项。

### Review / Dispute / AuditEvent

分别表示评价、争议流程和不可抵赖的关键操作记录。

## 2. 任务状态

```text
Draft
  ├─ user confirms + risk allowed → Published
  └─ user abandons               → Archived

Published
  ├─ deadline reached            → Expired
  ├─ user cancels                → Cancelled
  └─ offer selected              → Matched

Matched
  └─ order created               → Closed
```

`Draft` 可建模为独立实体。正式 `Task` 建议从 `Published` 开始，避免草稿与大厅查询耦合。

## 3. 报价状态

```text
Pending
  ├─ worker withdraws → Withdrawn
  ├─ user selects     → Accepted
  ├─ another selected → Rejected
  └─ task unavailable → Expired
```

报价选择必须在数据库事务中检查任务状态，并使用并发令牌保证最多一份报价成功。

## 4. 订单状态

```text
PendingAcceptance
  ├─ worker accepts → Accepted
  └─ timeout/reject → Cancelled

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

是否需要 `PendingAcceptance` 取决于报价本身是否构成服务者承诺；在编码前应明确。首版建议保留，以处理服务者离线和超时。

## 5. 不变量

- Task 必须有所有者、截止时间、地点范围和至少一项验收标准。
- Published Task 必须具有通过的风险决策版本。
- 同一 Task 最多一个非终态 Order。
- Offer 的服务者不能是 Task 所有者。
- Order 的用户、服务者、任务快照和报价快照创建后不可替换。
- 状态转换必须同时验证操作者、当前状态和必要材料。
- AwaitingReview 必须至少有一份通过安全检查的 Evidence。
- Completed、Cancelled 是普通流程下的终态；纠错由受控运营命令产生补偿事件。

## 6. 领域事件

- `TaskPublished`
- `OfferSubmitted`
- `OfferAccepted`
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

支付上线后区分任务预算、报价金额、用户应付、平台服务费、服务者应收和退款，不能只用一个 `Amount` 字段承载所有含义。
