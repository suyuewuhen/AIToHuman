# API 设计约定

## 1. 基础约定

- 协议：HTTPS
- 风格：REST/JSON；实时事件使用 SignalR
- 前缀：`/api/v1`
- JSON 字段：`camelCase`
- 时间：ISO 8601，包含时区；后端保存 UTC
- 标识符：推荐 UUID/ULID，作为字符串传输
- 金额：JSON 数字加币种字段，不使用浮点运算

API 契约通过 ASP.NET Core OpenAPI 输出，并生成 TypeScript 客户端。破坏性变更通过新版本或兼容迁移发布。

## 2. 资源示例

```text
POST   /api/v1/auth/register
POST   /api/v1/auth/login
POST   /api/v1/auth/refresh

POST   /api/v1/conversations
POST   /api/v1/conversations/{conversationId}/messages
POST   /api/v1/conversations/{conversationId}/task-drafts
PUT    /api/v1/task-drafts/{draftId}
POST   /api/v1/task-drafts/{draftId}/reward-suggestion
POST   /api/v1/task-drafts/{draftId}/confirm

GET    /api/v1/tasks
GET    /api/v1/tasks/mine
GET    /api/v1/tasks/{taskId}
POST   /api/v1/tasks/{taskId}/publish
POST   /api/v1/tasks/{taskId}/cancel

POST   /api/v1/tasks/{taskId}/increase-reward
POST   /api/v1/tasks/{taskId}/applications
GET    /api/v1/tasks/{taskId}/applications
POST   /api/v1/applications/{applicationId}/select
POST   /api/v1/applications/{applicationId}/withdraw

GET    /api/v1/orders/{orderId}
POST   /api/v1/orders/{orderId}/start-travel
POST   /api/v1/orders/{orderId}/start-work
POST   /api/v1/orders/{orderId}/submit
POST   /api/v1/orders/{orderId}/approve
POST   /api/v1/orders/{orderId}/reject
POST   /api/v1/orders/{orderId}/resume
POST   /api/v1/orders/{orderId}/cancel
POST   /api/v1/orders/{orderId}/disputes
POST   /api/v1/orders/{orderId}/reviews
GET    /api/v1/users/{userId}/reviews
GET    /api/v1/workers/{workerId}/reviews
```

命令型子资源表达业务动作，避免允许客户端通过通用 PATCH 任意设置状态。

> 上面这份清单表达的是**设计意图**，实际路径与实现进度以下文与 [交接文档](../development/handoff.md) 第 9 节为准。其中仍**尚未实现**的有：`POST /api/v1/auth/refresh`、`GET /api/v1/orders/{orderId}`、`/api/v1/conversations/{id}/messages`、`/api/v1/task-drafts/*`、`/api/v1/orders/{orderId}/start-travel`、`/api/v1/users/{userId}/reviews`、`/api/v1/workers/{workerId}/reviews`。已经实现但路径与清单写法不同的三处：撤回报名是 `POST /api/v1/tasks/{taskId}/applications/{applicationId}/withdraw`（清单里写成了 `/api/v1/applications/{applicationId}/withdraw`），发起争议是 `POST /api/v1/orders/{orderId}/dispute`（清单里写成了复数 `/disputes`，且不需要额外资源 id），**草稿字段编辑是 `PUT /api/v1/tasks/{id}`**（清单里的 `/api/v1/task-drafts/{draftId}` 仍不存在：没有独立草稿资源，编辑直接改 `ReadyToPublish` 的任务，见下文）。本节下文只描述已实现的部分。

认证最小闭环当前提供：`POST /api/v1/auth/register` 注册并返回短时 Access Token，`POST /api/v1/auth/login` 登录，`POST /api/v1/auth/switch-role` 在 `owner`/`worker` 间切换当前操作角色，`GET /api/v1/auth/me` 需要 `Authorization: Bearer <token>`。一个账户只有一个用户 ID，角色切换只重新签发带不同 role claim 的 JWT，不复制账户。任务写操作从 JWT 的用户 ID 与角色读取身份；Development 环境为兼容旧演示数据保留显式 ID 回退，生产环境必须配置 `Authentication__SigningKey` 并使用 JWT。

任务大厅的 `GET /tasks` 与公开任务详情只返回区域、悬赏、验收标准和报名人数等公开摘要，不返回服务者 `workerId`、报名备注或联系方式。报名详情仅在后续完成认证和资源授权后，向任务所有者或对应服务者返回。

已实现的报名列表（`GET /api/v1/tasks/{id}/applications?ownerId=...`，仅任务所有者，其他身份 `403`）在报名本身之外带上了该服务者的**公开**评价摘要：`workerAverageRating` 与 `workerReviewCount`，以及显示名 `workerDisplayName`（取不到时为 `null`，客户端退化成显示 id 前缀，而不是整列取不到数据）。只统计已达到公开条件的评价（同一订单双方都提交，或订单完成满 7 天），盲期内的评价不计入，因此与 `GET /api/v1/users/{id}/review-summary` 的口径完全一致（服务端共用同一处公开信用计算，前端对没有公开评价的服务者显示“暂无公开评价”，而不是 0 分）。响应仍然不含执行地址等参与者层信息。

公开详情 `GET /tasks/{id}` 不返回他人的草稿：`ReadyToPublish` 状态只有所有者能读到，其他人（含匿名）得到 `404`。精确执行地址走独立接口 `GET /tasks/{id}/execution-address`，只有所有者与被选中的服务者可读，其余返回 `403`；大厅与公开详情只暴露 `hasExecutionAddress` 布尔值。

`GET /tasks/mine` 是所有者视角的任务列表：返回当前用户作为所有者的全部任务及其状态（`ReadyToPublish`、`Published`、`Assigned`、`Closed` 等）和报名人数，避免草稿、已分配和已结束的任务只在公开大厅里消失。身份优先取 JWT，Development 环境可用 `?ownerId=...` 回退；已认证时请求里的 `ownerId` 会被忽略。状态里包含 `Expired`（超过截止时间被后台置为过期）与 `Cancelled`（所有者撤销或运营下架）。

已实现的任务撤销：`POST /api/v1/tasks/{id}/cancel`，body `{ ownerId, reason }`，返回 `TaskResponse`。这是**所有者自己撤销**尚未被选中的任务，`reason` 必填且 ≤200 字；被选中之后（`Assigned`）由订单流程接管，撤销会被状态机拦下并返回 `422`。运营下架仍走 `POST /api/v1/admin/tasks/{id}/cancel`：原因照旧写入 `admin_audit_entries`，现在也同时记在任务上（`cancelledAt` / `cancellationReason`）。已过截止时间的任务由后台每 60 秒扫描并置为 `Expired`：`Published` 且 `Deadline` 已过的会被处理，同时作废 `Pending` 报名并通知所有者与报名者；**已分配的（`Assigned`）任务不参与扫描**，它归订单流程管。过期是幂等的，同一任务不会重复处理。

已实现的订单取消：`POST /api/v1/orders/{id}/cancel`，body `{ actorId, note }`（`note` 即取消原因，必填、≤200 字），返回 `OrderResponse`。阶段与角色规则由领域层强制，违反一律 `422` 加可读中文原因：服务者只能在 `Accepted`（还没开始执行）时取消，开工后要终止必须由需求方发起；需求方在 `Submitted` 之前都可取消；服务者提交验收之后双方都不能取消（先验收或驳回）；`Approved`/`Cancelled` 不能再取消，非参与者返回“只有订单参与者可以取消订单。”。同一事务内连带改变任务：未过截止时间则任务退回 `Published` 重新招募、本次选中的报名置为 `Rejected`（服务者可重新报名；顺带收回执行地址的披露资格），已过截止时间则直接把任务置为 `Expired`（记 `expiredAt`，取消者不是需求方时需求方还会收到 `task.expired`）；双方参与者收到 `order.cancelled`。响应里新增 `cancelledAt` / `cancelledBy` / `cancellationReason`；`TaskResponse` 新增 `expiredAt` / `cancelledAt` / `cancellationReason`，而大厅列表的 `TaskSummaryResponse` 不含这些字段，仍按状态展示。

已实现的报名撤回与报名截止时间：`POST /api/v1/tasks/{taskId}/applications/{applicationId}/withdraw`，body `{ workerId }`（role `worker`），返回 `TaskResponse`。只允许撤回**自己的**、状态为 `Pending` 的报名：撤回后状态变 `Withdrawn`、记录保留、**服务者可以重新报名**（同一服务者的重复报名仍被 `422` 拦下）；已经被选中（`Selected`）之后要退出只能走订单取消，撤回会被状态机拒绝。撤回后任务所有者收到 `task.applicationWithdrawn`（载荷含 `taskId` / `applicationId` / `workerId` / `title`）。任务新增可选字段 `applicationDeadline`（创建时由所有者给出）：必须晚于创建时间、且不晚于任务截止时间；到点后不再接受**新**报名（`422`“该任务的报名已经截止，不能再报名。”），但**已有报名仍可被选中**；若报名截止时间已过还去发布，`POST /tasks/{id}/publish` 会被拒绝（“报名截止时间已过，任务不能发布：请撤销后重新创建，或先调整报名截止时间。”）。`TaskResponse` 与 `TaskSummaryResponse` 都带 `applicationDeadline` 与 `acceptingApplications`（服务端按当前时间算：已发布且报名窗口未过）。

服务者视角的报名列表：`GET /api/v1/tasks/applications/mine?workerId=&limit=`（role `worker`）返回 `MyApplicationListResponse`（`{ items: MyApplicationResponse[] }`），每条含 `applicationId`、`taskId`、`title`、`district`、`reward`、`currency`、`deadline`、`applicationDeadline`、`taskStatus`、`applicationStatus`、`submittedAt` 与 `canWithdraw`（由服务端判定：只有仍处于 `Pending` 的报名为 `true`，客户端不需要自己推算状态）。

已实现的争议处理：`POST /api/v1/orders/{orderId}/dispute`，body `{ actorId, note }`（复用 `OrderActionRequest`，`note` 即争议原因，必填、≤500 字），返回 `OrderResponse`。谁能发起、从哪个阶段发起由领域层强制，违反一律 `422` 加可读中文原因：需求方只能在 `Submitted`（服务者已提交验收）发起，服务者只能在 `Rejected`（验收被驳回）发起，非参与者返回“只有订单参与者可以发起争议。”。争议期间订单冻结：提交、验收、驳回、返工与取消全部 `422`，任务保持 `Assigned`、不会回到大厅。`OrderResponse` 新增 `disputeReason` / `disputeOpenedBy` / `disputeOpenedAt` / `disputeResult` / `disputeResolutionNote` / `disputeResolvedAt`；发起时对方参与者收到 `order.disputed`。

运营侧的争议处置：`GET /api/v1/admin/orders?status=&limit=`（管理员身份）返回 `AdminOrderListResponse`——省略 `status` 时默认**只返回 `Disputed`**，传 `all` 看全部；条目另含双方邮箱、提交说明、审批/驳回说明、返工次数与取消信息。`POST /api/v1/admin/orders/{id}/resolve`，body `{ decision, note }`，`decision ∈ { Approve, Rework, Cancel }`，`note` 必填、≤500 字，返回 `AdminOrderItemResponse`：`Approve` 把订单置为 `Approved`（写审批时间与说明、清空驳回原因）并把任务从 `Assigned` 推进到 `Closed`；`Rework` 退回 `InProgress`、累加返工次数、把处置依据记进驳回原因、任务保持 `Assigned`；`Cancel` 终止订单（`cancelledAt` 落库，`cancelledBy` 为空表示平台处置而不是某个参与者取消，处置依据写进取消原因），任务按“取消订单”的规则回到大厅或直接过期。三种处置都在同一事务里写运营审计（`order.dispute.approve` / `order.dispute.rework` / `order.dispute.cancel`，`targetType=order`，依据写在 `reason`）并通知双方（`order.disputeResolved`，事件键按接收者派生）。

已实现的风险门禁与人工复核：任务在创建草稿、编辑草稿、回滚草稿与 `POST /tasks/{id}/publish` 前各判定一次确定性风险规则（用的是当下生效的那一版规则目录），结果随任务返回——`TaskResponse` 与 `TaskSummaryResponse` 都带 `riskVerdict`（`Allowed`/`NeedsReview`/`Blocked`）、`riskRuleCode`、`riskCategory`、`riskSummary`、`riskRuleVersion`、`riskReviewStatus`（`NotRequired`/`Pending`/`Approved`/`Rejected`）、`riskReviewNote` 与 `riskPublishBlocked`，`TaskResponse` 另有 `riskAssessedAt` 与 `riskReviewedAt`。**`riskPublishBlocked` 是服务端算好的结论，客户端不要自己按 `riskVerdict`/`riskReviewStatus` 推算**（禁止类别、以及需人工复核但尚未放行或已被驳回都为 `true`）；被拦的任务发布返回 `422` 与可读原因（例如“该任务属于平台禁止的类别（prohibited.exam_impersonation · 代考与冒名顶替），不能发布：…”），禁止类别人工也无权放行，草稿仍然保留，用户可以查看原因或自行撤销。运营侧的人工复核接口都要求管理员身份（**匿名 `401`、非管理员 `403`**）：`GET /api/v1/admin/risk/reviews?limit=50` 返回 `AdminRiskReviewListResponse`（条目 `AdminRiskReviewItemResponse`，只列判成 `NeedsReview` 且仍为 `Pending` 的任务，按创建时间升序、先来先处理）；`POST /api/v1/admin/risk/reviews/{taskId}/decide`，body `{ decision: "Approve" | "Reject", note }`，`note` 即依据、必填且 ≤200 字，`Approve` 之后任务可以发布，`Reject` 是终态（规则之后不再命中也不会变回可发布），依据与动作名 `task.risk.approve`/`task.risk.reject` 一起写进 `admin_audit_entries`，任务所有者收到 `task.riskReviewed`（载荷含 `canPublish`）；`GET /api/v1/admin/risk/rules` 返回 `RiskRuleCatalogResponse`（`version`、`highRewardThreshold` 与规则清单，每条给 `code`/`category`/`verdict`/`description`/`keywordCount`，**不返回匹配词本身**）。非法取值返回 `422`（例如“风险复核结论 Maybe 不存在：可选值为 Approve（放行）或 Reject（驳回）。”），对没有待处置复核的任务调用返回 `422`“该任务没有待处置的风险复核。”，重复处置同样是 `422`。

已实现的风险规则目录运营维护：运营可以在后台看当前生效的那一版目录、翻版本历史，并编辑出覆盖版本（整份替换），改坏了还能一键恢复代码内置目录。这 5 个接口全部要求管理员身份（**匿名 `401`、非管理员 `403`**）；数据库里没有任何覆盖版本时生效的是代码内置目录（版本 `1`），因此运营第一次编辑得到的是版本 `2`。

- `GET /api/v1/admin/risk/rules` 概述：返回 `RiskRuleCatalogResponse`（`version`、`highRewardThreshold` 与 `rules[]`，每条给 `code`/`category`/`verdict`/`description`/`keywordCount`）。**只有匹配词数量，没有词本身**——概述也可能出现在排查记录里，完整词表只在明细接口按需给出。
- `GET /api/v1/admin/risk/rules/detail` 明细：返回 `RiskRuleCatalogDetailResponse`（`version`、`isBuiltIn`、`highRewardThreshold`、`nightWindowStart`、`nightWindowEnd`、`rules[]`——每条含 `keywords[]` 全量匹配词，以及最近一次改动的 `changeSummary`/`changeReason`/`updatedBy`/`updatedByName`/`updatedAt`）。`isBuiltIn` 为 `true` 表示还没有任何运营覆盖，用的就是代码内置目录。
- `GET /api/v1/admin/risk/rules/versions?limit=` 版本历史：返回 `RiskRuleCatalogVersionListResponse`（`items[]` 按版本倒序，`limit` 默认 20、上限 100）。每条含 `version`、`changeSummary`、`changeReason`、`updatedBy`/`updatedByName`、`createdAt`，以及当版的 `ruleCount`、`blockedRuleCount`、`highRewardThreshold`、`nightWindowStart`/`nightWindowEnd`。空列表表示还没有人改过，此时生效的是内置目录。
- `POST /api/v1/admin/risk/rules` 整份替换：body 为 `UpdateRiskRuleCatalogRequest(expectedVersion, reason, highRewardThreshold, nightWindowStart, nightWindowEnd, rules)`，其中每条规则是 `{ code, category, verdict, description, keywords[] }`，`verdict` 只能取 `Blocked`/`NeedsReview`。版本号由服务端自动 +1（不接受客户端指定），`reason` 即变更依据、必填且 ≤200 字，响应是最新的明细。带上 `expectedVersion` 且与当前版本不一致返回 **`409`「风险规则已被其他人修改（当前版本 vX，你读到的是 vY），请刷新后重试。」**；内容不合法返回 **`422`** 加可读原因（至少保留一条禁止类规则、原因代码唯一、结论不能是放行、匹配词每词 2–20 字、规则 ≤ 50 条、每规则匹配词 ≤ 400 个、阈值 > 0 且 ≤ 1000000、深夜时段必须向前且落在 0–24 小时内、`review.high_reward`/`review.night_window` 是保留代码不能被普通规则顶替）；内容与当前版本一模一样同样是 `422`（「风险规则内容没有变化，无需更新。」），失败的提交不会产生新版本。成功写运营审计动作 `task.risk.rules.update`。
- `POST /api/v1/admin/risk/rules/reset` 恢复到代码内置目录：body 为 `ResetRiskRuleCatalogRequest(expectedVersion, reason)`（依据同样必填），**同样是追加一版**——版本号继续 +1，历史里被恢复掉的那一版仍然保留；写运营审计动作 `task.risk.rules.reset`。当前内容已经是内置目录时返回 `422`（「当前生效的就是内置目录的内容，不需要恢复。」）。

创建草稿、编辑草稿、回滚草稿与发布都会用**当下生效的那一版目录**重新判定，任务上返回的 `riskRuleVersion` 就是判定当时的版本号——所以运营收紧规则后，已经存在的草稿在发布时同样会被拦，而历史判定不会被后续改规则改写。

已实现的误拦申诉：被风险规则拦下的任务，所有者可以申诉，运营给出结论。任务响应（`TaskResponse` 与 `TaskSummaryResponse`）新增 `riskAppealStatus`（`None`/`Pending`/`Accepted`/`Denied`）、`riskAppealReason`、`riskAppealedAt`、`riskAppealDecisionNote` 与 `canAppealRisk`（**由服务端判定**，客户端不要自己按 `riskVerdict`/`riskReviewStatus` 推算）。所有者用 `POST /api/v1/tasks/{id}/risk-appeals` 提交，body 为 `RiskAppealRequest(ownerId, reason)`，理由必填 ≤500 字；只有**被判定为禁止类别**或**转人工后被驳回**的任务有资格，还在等复核或已放行的任务返回 `422`（例如“还在等人工复核，不需要申诉。”），非所有者 `403`，**同一版内容只能申诉一次**（处置过后再申诉返回 `422`“这一版内容已经申诉过：请先修改草稿（改完会重新判定风险），再决定是否重新申诉。”；编辑草稿会把状态清回 `None`，于是可以重新申诉）。运营侧（管理员身份，非管理员 `403`）：`GET /api/v1/admin/risk/appeals?limit=50` 返回 `AdminRiskAppealListResponse`（条目 `AdminRiskAppealItemResponse`，含 `appealStatus`/`appealReason`/`appealedAt` 与 **`canBeReleasedByAppeal`**），按提交时间升序、先来先处理；`POST /api/v1/admin/risk/appeals/{taskId}/decide`，body `{ decision: "Accept" | "Deny", note }`，`note` 即依据、必填 ≤200 字，非法取值返回 `422`，依据与动作名 `task.risk.appeal.accept`/`task.risk.appeal.deny` 一起写进 `admin_audit_entries`，任务所有者收到 `task.riskAppealDecided`（载荷含 `canPublish`）。**处置能力分两档**：`Accept` 对“转人工后被驳回”的任务会真的放行（`riskReviewStatus` 变 `Approved`），对“禁止类别命中”的任务**不会**给发布许可——`canBeReleasedByAppeal` 就是给运营看的这条标注（禁止类别恒为 false），只有前者能靠申诉拿到发布资格。

已实现的草稿字段编辑：`PUT /api/v1/tasks/{id}`，body 为 `UpdateTaskDraftRequest(ownerId, title, description, district, deadline, reward, acceptanceCriteria, executionAddress?, applicationDeadline?)`，需要所有者身份（JWT 优先，`ownerId` 只在 Development 合成会话下作为回退）。字段校验与创建任务**共用同一套**：标题 1–80 字、描述与区域非空、至少一项验收标准（去空白/去重）、执行地址 ≤200 字、截止时间必须晚于当前时间、报名截止必须晚于当前时间且不晚于任务截止——所以不存在“创建拦得住、编辑能绕过”的缺口。只有 `ReadyToPublish` 可以编辑：非所有者 `403`，其它状态 `422`（例如“任务当前状态 Published 不允许该操作”），字段不合法同样是 `422`（例如“截止时间必须晚于当前时间。”“任务至少需要一项验收标准。”）。编辑后会立即重跑确定性风险规则并**作废原有的人工复核结论**（复核人、时间与依据一并清空，按新文本重新判定；被判成需复核的任务会重新回到运营队列），因此同一条任务的 `riskReviewStatus` 可能在编辑后从 `Approved` 回到 `Pending`。响应里的 `draftEditable` 由服务端按状态判定，**客户端不要自己推算**；执行地址不随任务响应返回，仍只走 `GET /api/v1/tasks/{id}/execution-address`（`TaskResponse` 也会返回给申请报名的服务者，不能带参与者层信息）。

已实现的草稿历史（只追加、不覆盖）：`GET /api/v1/tasks/{id}/revisions?ownerId=...`，仅任务所有者可读（其他身份 `403`，任务不存在 `404`）。返回 `items[]`，每项是 `TaskDraftRevisionResponse(id, taskId, revision, title, description, district, deadline, reward, currency, acceptanceCriteria[], executionAddress, applicationDeadline, riskVerdict, riskRuleCode, riskRuleVersion, editedBy, changeSummary, changes[], createdAt)`：第 1 版是创建草稿（`changeSummary = "创建草稿"`），之后每次编辑或回滚追加一版，`revision` 在同一任务内单调递增；`changeSummary` 写明本次改了哪些字段（例如“标题、悬赏”；超过 6 个字段时只列前 6 个并追加“等 N 项”，N 是本次变更的字段总数；一个字段都没变时写“无字段变化”）；每项都带该版文本对应的风险结论（`riskVerdict` / `riskRuleCode` / `riskRuleVersion`），因此能对账“第几版被判成禁止或转人工、用的是哪一版规则”。

**`changes[]` 是字段级 diff**：由服务端把该版与**上一版**逐字段比较得出，每项形如 `{ "field": "截止时间", "before": "2026-09-14 09:00 UTC", "after": "2026-09-15 09:00 UTC" }`。字段名与 `changeSummary` 用同一套中文名（标题/描述/公开区域/截止时间/悬赏/验收标准/执行地址/报名截止时间）；值由服务端格式化成可读文本——时间统一 `yyyy-MM-dd HH:mm UTC`、金额写成“数值 币种”、验收标准用 `；` 连接。**第 1 版没有上一版，因此 `changes` 是空数组**。差异算在服务端（领域层的 `TaskDraftRevision.Diff`），客户端只负责渲染，不要自己按快照字段比较（否则每个客户端都会长出一套自己的比较规则）。

已实现的草稿回滚：`POST /api/v1/tasks/{id}/revisions/{revision}/restore?ownerId=...`，把草稿恢复到某一版，返回更新后的 `TaskResponse`。语义与边界：

- **仅任务所有者**可调用（其他身份 `403`），且**只有草稿（`ReadyToPublish`）能回滚**：已发布的返回 `422`（发布后字段本来就不可改，要改只能撤销后重建）。`{revision}` 不存在返回 `404`；跨任务的版本快照会被领域层拒绝（“这一版不属于当前任务。”）。
- 回滚走的是与编辑**完全同一套**写入路径：同样的字段校验、同样重跑确定性风险规则、**同样作废原有的人工复核结论与误拦申诉状态**。它不是绕过校验的后门——如果目标版的截止时间已经过去，回滚会像编辑一样返回 `422`「截止时间必须晚于当前时间。」，而不是悄悄造出一个永远发布不了的草稿。
- **回滚本身也追加一版**，`changeSummary` 形如「回滚自第 1 版：标题、公开区域、悬赏」（内容与目标版完全一致时写「回滚自第 N 版（内容与该版一致）」），并且这一版带的反向差异就是“回到该版”时改了哪些字段。**中间版本不会被覆盖或删除**，历史只会往前长。

建议价响应至少包含建议金额、建议区间、币种、主要估价因素、数据充分度和规则/模型版本。它不修改草稿金额；用户另行编辑并确认的 `reward` 才是任务悬赏。

对话最少闭环当前提供：`POST /api/v1/conversations` 创建会话（服务端写入开场白），`GET /api/v1/conversations/{id}?userId=...` 读取历史与当前草稿用于刷新恢复，`GET /api/v1/conversations?userId=...&limit=...` 列出该用户的会话。会话历史以服务端为准：`POST /api/v1/ai/plan/stream` 的请求体是 `{ conversationId, userId, message }`，服务端取出最近 29 条历史并接上本轮消息发给模型，回合完整成功后才写入用户消息与 AI 回复。会话仅所有者可读可写，越权返回 `403`；会话不存在返回 `404`；消息为空或超长返回 `422`。这三类问题在开始写 SSE 之前判定，因此仍用 HTTP 状态码表达。

## 3. 成功响应

单资源直接返回资源或命令结果，HTTP 状态码表达结果，不额外固定包装一层 `code = 0`：

```json
{
  "id": "01J...",
  "status": "published",
  "createdAt": "2026-09-09T08:00:00Z"
}
```

创建返回 `201 Created` 和 `Location`；无响应体的成功操作返回 `204 No Content`。

## 4. 错误响应

采用 RFC 9457 Problem Details：

```json
{
  "type": "https://docs.example.com/problems/invalid-order-transition",
  "title": "Order state transition is not allowed",
  "status": 409,
  "detail": "An awaiting-review order cannot start work.",
  "instance": "/api/v1/orders/01J.../start-work",
  "traceId": "00-abcd...",
  "errors": {
    "status": ["Expected accepted or enRoute."]
  }
}
```

建议状态码：

- `400` 请求格式或字段错误。
- `401` 未认证。
- `403` 已认证但无权限。
- `404` 资源不存在，必要时也用于避免泄露资源存在性。
- `409` 状态冲突、并发冲突或幂等键冲突。资源冲突（例如邮箱已注册）用 `ConflictException`，`detail` 是原样原因「该邮箱已注册。」。
- `422` 请求语法正确但不符合业务规则；状态机不允许的操作走这里并返回可读中文原因，例如“只有订单参与者可以取消订单。”“服务者只能在开始执行前取消订单，当前状态 InProgress 请与需求方协商后由需求方处理。”“服务者已经提交验收，请先验收或驳回，不要直接取消。”“任务已经分配并产生订单，不能直接撤销：请先处理订单（验收、驳回或取消订单）。”“取消订单必须填写原因，长度不超过 200 个字符。”“该任务的报名已经截止，不能再报名。”“报名截止时间已过，任务不能发布：请撤销后重新创建，或先调整报名截止时间。”“只有订单参与者可以发起争议。”
- `429` 超过速率限制。

## 5. 分页和过滤

列表优先使用游标分页：

```text
GET /api/v1/tasks?category=pickup&district=chaoyang&limit=20&cursor=...
```

```json
{
  "items": [],
  "nextCursor": "...",
  "hasMore": false
}
```

排序字段采用白名单，不能将客户端字段直接拼接为 SQL。

已实现：`GET /api/v1/tasks` 按截止时间升序游标分页，返回 `items`/`nextCursor`/`hasMore`；支持 `district`、`minReward`、`maxReward` 筛选，`limit` 上限 50、默认 12。游标是不透明字符串（编码上一页最后一条的截止时间与 Id），排序第二关键字用 Id 保证同一截止时间下不丢条不重复。非法区间或非法游标返回 `400` 与可读原因，而不是静默忽略。

## 6. 认证与授权

- Access Token 短时有效，Refresh Token 轮换并可撤销。
- 每个端点同时检查角色和资源关系，如任务所有者、订单参与者。
- SignalR 连接使用相同认证体系，并在每个 Hub 方法中授权。
- 管理接口使用独立策略和更严格审计。

具体 Token 存储方式在身份方案实现前通过安全评审确定。

## 7. 幂等与并发

- 创建任务、提高悬赏、提交报名、选择报名者、提交评价、状态转换和未来支付接口支持 `Idempotency-Key`（已实现，细则见本节末尾）。
- 更新资源返回 ETag 或版本字段；冲突返回 `409`。
- 客户端超时后可以使用相同幂等键安全重试。

已实现的并发控制：`tasks` 与 `orders` 各有一个 `Version` 乐观并发令牌，并发加价、并发选人、并发提交或验收时后写入者返回 `409` 与“该任务或订单刚刚被其他人更新，请刷新后重试。”；选择报名者和验收在同一数据库事务内完成；`orders.TaskId` 唯一索引保证一个任务最多一个订单。ETag 与版本字段仍未实现，响应里也不返回版本号。

已实现的幂等键：已认证用户在 `/api/v1` 下的写请求（`POST`/`PUT`/`PATCH`/`DELETE`）可以带请求头 `Idempotency-Key`（1 至 120 字符）。服务端按「用户 + 键」记录并**回放上一次的响应**，因此客户端超时重试不会把加价、报名、选人、提交或状态转换做两遍；回放时返回原来的状态码与响应体，并加响应头 `Idempotency-Replayed: true`。

- **不带这个头时行为完全不变**，老客户端不受影响；`GET` 等读请求一律不走这套逻辑。
- **不参与**：匿名请求（含 `/api/v1/auth/*` 的注册登录、`/api/v1/session/*`）与 SSE 长连接 `/api/v1/ai/plan/stream`（回放没有意义）。
- **同一个键换了请求体**返回 `409`「这个幂等键已经用于另一个请求：请为不同的请求使用不同的 Idempotency-Key。」——请求指纹是方法 + 路径 + 查询串 + 请求体的 SHA-256，复用键是客户端错误，不能拿旧响应糊过去。
- **并发**：主键是 `(UserId, Key)`，两个并发请求里只有第一个能占位；第二个若发现记录还没完成返回 `409`「同一个幂等键的请求正在处理中，请稍后重试。」占位超过 2 分钟仍未完成（上次执行中途挂了）视为过期，允许重新真正执行。
- **不缓存的情形**：`5xx` 不缓存（服务端出错时重试应当真的重跑）；响应体超过 32000 字符或响应不是 JSON（例如文件下载）不缓存，占位会被清掉；业务抛异常时同样清掉占位。
- **记录会自动清理**：`idempotency_entries` 不会一直涨——后台任务每小时扫一轮，删除已完成超过 24 小时的记录，以及占位超过 10 分钟仍未完成的记录，单次最多 500 条（见 `IdempotencyCleanupService` 的启动日志）。客户端的重试窗口只有几秒到几分钟，24 小时的保留期足够覆盖真实重试；**不要把幂等键当作长期业务单据使用**。
- **键按用户隔离**：不同用户可以使用相同的键。存储是 `idempotency_entries` 表（迁移 22），中间件在认证与授权之后执行。
- 仍未实现：ETag 与版本字段返回；前端也没有做“自动生成并复用键”的改造。

## 8. 文件上传

推荐三步流程：

1. 客户端请求上传授权并声明文件类型、大小和用途。
2. 客户端使用短时效签名 URL 上传至私有对象存储。
3. 客户端提交文件 ID，服务端确认存在且安全扫描通过后关联订单。

服务端不信任扩展名，应检查 MIME、文件签名、大小和图片元数据；必要时重新编码图片并移除 EXIF。

已实现的凭证流程（单次 multipart 上传 + 鉴权流式下载，而不是两步签名 URL）：

- `POST /api/v1/orders/{orderId}/evidence` 使用 `multipart/form-data`（字段名 `file`），仅订单服务者可调用；服务端按白名单校验 MIME、按文件签名校验内容、核对声明大小与实际字节数，超出当前生效上限（默认 5 MB）返回 `413`、类型或内容不符返回 `422`。
- `GET /api/v1/orders/{orderId}/evidence` 返回元数据（含扫描状态与是否可下载），仅订单参与者。
- `GET /api/v1/evidence/{id}/content` 在鉴权后流式返回文件，响应带 `X-Content-Type-Options: nosniff`，下载文件名由系统生成（不使用用户原始文件名，避免响应头注入）；未通过扫描的凭证返回 `403`。
- 上传上限来自运营配置（`evidence.maxPerOrder` / `evidence.maxSizeBytes`，默认 10 份 / 5 MB，硬上限 50 份 / 25 MB）：超限返回 `413`（请求体过大）或 `422`（份数已满），错误文案里带上当前生效的数值。浏览器侧只挡超过硬上限的文件，真正判断以服务端为准。
- 凭证状态里带 `scanAttempts` / `lastScanNote` / `scanExhausted`：扫描服务没给出结论时凭证保持 `Pending`、不可下载，由后台任务退避重扫（30 秒退避、最多 5 次）；用尽次数后 `scanExhausted=true` 并保留原因，说明“为什么不能下载”对参与者始终可见。
- 上传时默认剥离图片元数据（`evidence.stripMetadata`）：JPEG 的 EXIF/XMP 与注释、PNG 的文本/时间/EXIF 块、WebP 的 EXIF/XMP 块会被丢掉，像素数据不变；`metadataRemoved` 字段说明剥掉了什么，为空表示没剥或不需要剥。关闭该开关会保留原始文件。
- 每个上传者每小时有提交次数上限（`evidence.uploadsPerUserPerHour`，默认 60），超限返回 `422` 并给出当前上限；计数来自数据库，多实例部署同样生效。
- 对象存储模式下还可以走两步流程：`GET /api/v1/evidence/{id}/download-url` 返回短时签名地址（有效期由 `evidence.downloadUrlLifetimeSeconds` 决定，默认 120 秒），客户端直接向私有 Bucket 取字节，省掉一次转发。地址里签了对象路径、有效期与 `response-content-disposition`，改动任何一项都会被对象存储拒绝（`403`）；权限与扫描门禁的判定和 `/content` 完全一致。列表响应里的 `presignedDownloadAvailable` 表明当前存储是否支持这条路径，本机目录存储申请地址会返回 `422` 并提示改用 `/content`。
- 切换存储 provider 不会迁移已有对象：切回本机目录后，之前写在对象存储里的凭证下载会返回 `404`。

## 9. SignalR 事件

事件名称使用过去式和版本化载荷，例如：

- `task.rewardIncreased`
- `task.applicationSubmitted`
- `order.statusChanged`
- `order.messageCreated`
- `review.published`
- `notification.created`

实时事件只是刷新提示，客户端断线重连后必须通过 REST 获取事实状态，不能只依赖事件恢复数据。

已实现的推送是统一的 `notification.created`，载荷为版本化信封：

```json
{
  "eventId": "347dd6a9-6c1d-4007-9424-a45c192636bd",
  "type": "order.created",
  "version": 1,
  "occurredAt": "2026-09-10T13:29:04.496024+00:00",
  "payload": { "orderId": "347dd6a9-...", "status": "Accepted", "title": "通知骨干验证" }
}
```

- `eventId` 是幂等键，客户端可据此去重；订单创建用订单 ID，状态变化由订单 ID 与状态推导，重放同一状态不会产生第二条。任务过期与撤销的通知事件键按接收者派生，因此同一轮里多个接收者各自收到一条。
- 已接入的事件类型：`order.created`、`order.statusChanged`、`order.messageCreated`、`order.cancelled`、`order.disputed`、`order.disputeResolved`、`task.expired`、`task.cancelled`、`task.applicationWithdrawn`；订单类载荷为 `{ orderId, status, title }`，任务类载荷为 `{ taskId, status, title }`，撤回报名的载荷另含 `{ applicationId, workerId }`。争议发起只通知对方参与者，处置结果通知双方（事件键按接收者派生）。
- `version` 是信封版本，客户端应忽略高于自身支持版本的推送，并改用 REST 刷新。
- 推送前通知已经落库，`GET /api/v1/notifications` 能拿到同一批数据；`POST /api/v1/notifications/read` 标记已读并返回最新未读数。
- 服务端不等待推送结果：写库成功即视为该业务事件成立，派发失败由后台任务重试。
- 投递保证是 at-most-once：实时推送只是提示，可能丢失，客户端必须以 `notifications` 表与 `GET /api/v1/notifications` 的事实为准，重连后重新拉取补齐。派发方先用条件 UPDATE 原子认领待派发记录（`WHERE DispatchedAt IS NULL`），因此多实例不会重复推送；运营开关 `notifications.fanout.enabled` 打开时，认领方把消息发布到 Redis 扇出频道，各实例推给连在自己身上的在线客户端（未启用或 Redis 不可用时只推本实例）。若发布成功但没有任何实例在订阅，服务端打警告日志。

已实现的订单会话接口：`GET /api/v1/orders/{orderId}/messages`（参与者读取消息与未读数）、`POST /api/v1/orders/{orderId}/messages`（发送消息，同时在事务内写入通知）、`POST /api/v1/orders/{orderId}/messages/read`（标记已读并返回最新未读数）。非参与者一律 `403`，与订单相关的其它端点保持同一套参与者校验。订单列表的 `GET /api/v1/orders` 会附带每个订单的 `unreadMessageCount`，便于前端直接渲染未读徽标。

## 10. 运营配置接口

运营接口挂在 `/api/v1/admin` 下（配置类在 `/admin/settings`，人工兜底类在 `/admin/tasks`、`/admin/orders`、`/admin/users`、`/admin/risk`，审计在 `/admin/audits`），都要求管理员身份（部署配置里的 `Admin__UserIds` / `Admin__Emails`，或 `admin` 角色声明）；未配置管理员时一律 `403`。

```text
GET    /api/v1/admin/settings                  列出全部可配置项（含生效值、来源、默认值、可选值）
GET    /api/v1/admin/settings/{key}            单条配置
PUT    /api/v1/admin/settings/{key}            写入覆盖值
DELETE /api/v1/admin/settings/{key}            删除覆盖，恢复环境变量或默认值
POST   /api/v1/admin/settings/{key}/test       只读自检（目录可写、服务可达）
GET    /api/v1/admin/settings/audits?limit=50  变更审计，按时间倒序
```

约定：

- **白名单**：只有登记在 `SettingCatalog` 里的键能被读写；未注册的键返回 `400`，不是静默忽略，也不允许用它改写部署级配置。
- **错误码**：非法取值（类型、范围、枚举、URL 格式）返回 `422`；带 `expectedVersion` 且版本不一致返回 `409`；未注册键返回 `400`；非管理员 `403`。
- **机密**：`isSecret` 为真的键，响应里只有 `****末四位` 与 `fingerprint`，明文永不出服务端；审计里同样只写掩码与指纹。
- **来源**：每条配置的 `source` 取值是 `database`（后台覆盖过）、`configuration`（来自环境变量/配置文件）或 `default`（代码默认值），便于判断“改了到底有没有生效”。
- **写入请求**：`{ "value": "...", "expectedVersion": 1 }`；`expectedVersion` 省略表示不检查版本，新增覆盖时传 `0`。
- **立即生效**：写入成功后服务端刷新内存快照，消费方下一次调用就用新值，不需要重启进程；绕过 API 直接改库的改动会在 15 秒内被后台轮询同步。

示例响应（机密已脱敏）：
```json
{
  "key": "ai.apiKey",
  "category": "AI 服务商",
  "displayName": "API Key",
  "kind": "String",
  "isSecret": true,
  "value": "****3456",
  "source": "database",
  "hasOverride": true,
  "overrideVersion": 1,
  "updatedBy": "264fa867-bd3e-464b-966e-50b8d3b982f8",
  "fingerprint": "7ab5f1bce26a"
}
```

前端：运营相关的接口都由**独立页面 `/ops.html`**（`frontend/src/ops/OpsConsole.vue`）调用；主应用顶栏只对 `GET /api/v1/auth/me` 返回 `isAdmin=true` 的账户展示一个跳转链接（服务端仍会独立校验，前端隐藏不构成安全边界）。运营页面自己要求登录并向 `/auth/me` 确认管理员身份，非管理员只显示一句说明、不会去请求运营接口。客户端封装在 `frontend/src/api/settings.ts` 与 `frontend/src/api/admin.ts`；机密项在界面上只显示掩码，必须输入新值才能保存，清空需要显式操作。
