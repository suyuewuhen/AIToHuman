# 产品与开发路线图

路线图以里程碑和退出条件为主，不先承诺未经团队评估的具体日期。

> **现状标注**（对齐 2026-09-13 代码；已包含订单取消与任务过期、报名撤回与报名截止时间、争议处理与运营处置、确定性风险规则与人工复核、误拦申诉、草稿字段编辑与版本历史、草稿字段级 diff 与回滚、写接口幂等键、真实 PostgreSQL 回归测试基建、外部依赖（Redis 与 S3 兼容存储）的自动化回归、主机级端到端测试（真进程 + 真 HTTP + 临时真库）、风险规则目录的运营编辑（只追加版本快照 + 依据必填 + 运营审计 + 一键恢复内置目录）、发布后风险复检（规则收紧后重新判定仍在线任务：禁止类别自动下架、有订单的冻结订单、只命中转人工的保持在线并要求复检）与申诉节流与留档（单任务 3 次、单人 24 小时 5 次，每次申诉都留档）、资金托管与只追加账本（下单冻结、验收放款、取消退款、争议分账；走模拟网关，不接真实资金通道））
>
> - ✅ 已完成，并有代码或配置可验证。
> - ⚠️ 部分完成：已交付简化版，仍缺设计中的关键部分。
> - ⛔ 未开始。
>
> M0 与 M1 的大部分交付已实现，M2 核心闭环完成，M3 的订单消息与凭证上传已提前落地；详细实现现状见 [交接文档](./handoff.md)，领域模型差异见 [领域模型与状态机](../architecture/domain-model.md)。

## M0：产品与工程基线（基本完成）

交付：

- ✅ 产品愿景、MVP PRD、用户故事和风险边界。
- ✅ 系统架构、领域状态机和 API 约定。
- ✅ Vue 与 .NET 项目骨架。
- ✅ PostgreSQL、Redis、MinIO 的本地开发环境定义（`compose.yaml`）。
- ⚠️ CI、格式和测试基线：`.github/workflows/ci.yml` 已执行后端 `restore/build/test` 和前端 `npm ci`/`npm run typecheck`/`npm run build`，并有 `.editorconfig` 与 327 个领域单元测试、367 个集成测试（合计 694 个；集成测试本轮从 361 增至 367，新增的 6 条是**主机级端到端用例**——把已构建的 API 起成子进程、连随机命名的临时真库、用真 HTTP 打接口，因此“路由注册 / DI 装配 / 中间件顺序 / JSON 契约 / 启动期迁移”这一层也在覆盖内；另有真实 PostgreSQL 回归用例专门断言网关冻结失败时任务/订单/流水一起回滚与托管字段和账本在真库上的往返，以及 9 个覆盖 Redis 与 S3 兼容存储、含发布后复检、申诉留档与精确地址访问留痕的真库用例）；后端 job 已在 CI 里同时起 `postgres:16`、`redis:7` 与 `minio/minio` 三个服务并注入连接串（测试前用 `minio/mc` 建好测试桶），因此这些真实依赖的回归用例在 CI 会真正运行（主机级用例同样会真跑：测试前的 `dotnet build` 已经把 API 构建好，`dotnet test` 就会跑到这批用例，CI 没有为此新增步骤），本机缺少对应依赖时同一批用例自动跳过；尚无 Lint、Markdown 检查、OpenAPI 兼容性检查和技术文档生成。
- ✅ 配置分层与运营可配置项：部署级配置（连接串、Redis、日志、密钥环路径）只走环境变量；三方集成参数（模型服务、对象存储、内容扫描）与凭证上传上限登记在设置目录里，可通过管理员接口与运营后台页面（独立页面 `/ops.html`，`frontend/src/ops/OpsConsole.vue`）修改，机密加密落库并留审计，改完立即生效（见 [ADR-0003](../architecture/decisions/0003-operator-configurable-settings.md)）。运营后台的其余能力（任务/用户检索、人工下架、争议订单检索与处置、风险复核队列、精确地址留痕查询与规则目录的自述/明细/版本历史/编辑）已实现（发布后复检的运营入口与申诉累计次数也在其中：风险页签有「立即复检在线任务」按钮，申诉队列每条显示这条任务累计申诉过几次、可展开申诉轨迹）；仍未实现的是风险看板与规则命中统计、模型辅助分类、规则目录编辑的双人复核/审批流，以及通知的站外渠道（邮件/短信）；申诉的两条上限目前还是代码常量，没有进运营配置目录；冻结订单后现在有资金处置了——争议处置时可以填金额（强制完成即放款给服务者的金额、终止订单即退回需求方的金额，留空即全额），部分放款与部分退款分别入账，双方与运营都能查资金流水（见 M3、M5）。本轮新增运营配置键 `payment.provider`（`simulated` 默认 / `disabled` 关闭托管），设置目录从 26 个键增至 **27** 个、分组从五个增至**六个**（新分组「资金托管」）。
- ⛔ 秘密扫描与依赖更新自动化。

遗留项：

- ✅ 数据库结构已收敛为 EF Core Migration 单一来源：28 个迁移覆盖 users、orders、order_evidence、reviews、tasks、task_applications、conversations、conversation_messages、notifications、order_messages、evidence（含扫描记账与容器规范化记录）、system_settings、system_setting_audits、admin_audit_entries、task_draft_revisions、idempotency_entries、risk_rule_catalog_revisions、task_risk_appeals、address_access_entries、ledger_entries 等表；最新的是第 28 个迁移 `AddOrderEscrowAndLedger`（建资金流水表 `ledger_entries`：`Id`/`OrderId`/`TaskId`/`DebitAccount`/`CreditAccount`/`Amount`/`Currency`/`Kind`/`Note`/`OccurredAt`，只追加，并给 `(OrderId, OccurredAt)` 与 `OccurredAt` 各建一条索引；给 `orders` 补托管七列 `EscrowStatus`/`EscrowAmount`/`ReleasedAmount`/`RefundedAmount`/`PaymentReference`/`EscrowHeldAt`/`EscrowSettledAt`，`EscrowStatus` 用数据库默认值 `None` 回填存量订单，并加 `(EscrowStatus, CreatedAt)` 索引）。此前几次迁移是 `AddOrderCancellationAndTaskExpiry`（给 `orders` 补取消三列 `CancelledAt`/`CancelledBy`/`CancellationReason`，给 `tasks` 补 `ExpiredAt`/`CancelledAt`/`CancellationReason`）、`AddApplicationDeadline`（给 `tasks` 补可选的报名截止时间 `ApplicationDeadline`）、`AddOrderDispute`（给 `orders` 补争议六列 `DisputeReason`/`DisputeOpenedBy`/`DisputeOpenedAt`/`DisputeResolution`/`DisputeResolutionNote`/`DisputeResolvedAt`，并加 `(Status, CreatedAt)` 索引供运营按状态检索）、`AddTaskRiskAssessment`（给 `tasks` 补风险判定与复核十列 `RiskVerdict`/`RiskRuleCode`/`RiskCategory`/`RiskSummary`/`RiskRuleVersion`/`RiskAssessedAt`/`RiskReviewStatus`/`RiskReviewedBy`/`RiskReviewedAt`/`RiskReviewNote`，并加 `(RiskReviewStatus, CreatedAt)` 索引供复核队列先来先处理；存量行按数据库默认值 `Allowed`/`NotRequired` 回填）、`AddTaskDraftRevisions`（建草稿历史表 `task_draft_revisions`，一行一个完整快照，`(TaskId, Revision)` 唯一索引）、`AddIdempotencyEntries`（建幂等记录表 `idempotency_entries`，主键 `(UserId, Key)` 加 `StartedAt` 索引）、`AddRiskAppeal`（给 `tasks` 补误拦申诉六列 `RiskAppealStatus`/`RiskAppealReason`/`RiskAppealedAt`/`RiskAppealDecidedBy`/`RiskAppealDecidedAt`/`RiskAppealDecisionNote`，并加 `(RiskAppealStatus, RiskAppealedAt)` 索引供申诉队列扫描）与第 24 个迁移 `AddRiskRuleCatalogRevisions`（建风险规则目录的版本快照表 `risk_rule_catalog_revisions`：`Id`/`Version`/`CatalogJson`/`ChangeSummary`/`ChangeReason`/`UpdatedBy`/`CreatedAt`，`Version` 唯一索引加 `CreatedAt` 索引；只追加，读取取版本号最大的一版），加上第 25 个迁移 `AddTaskRiskEnforcement`（给 `tasks` 补风控处置三列 `RiskEnforcementStatus`/`RiskEnforcementReason`/`RiskEnforcedAt`，`RiskEnforcementStatus` 用数据库默认值 `None` 回填存量行，并加 `(Status, RiskRuleVersion)` 索引供发布后复检挑候选）与第 26 个迁移 `AddTaskRiskAppeals`（建申诉留档表 `task_risk_appeals`：一行一次申诉、只追加，记下提交时的规则代码/版本/结论与理由，运营结论写回同一行且只能处置一次；`(TaskId, SubmittedAt)` 与 `(OwnerId, SubmittedAt)` 两条索引分别服务“看轨迹”和“按人算次数”），加上第 27 个迁移 `AddAddressAccessEntries`（建精确执行地址的访问留痕表 `address_access_entries`：`Id`/`TaskId`/`ViewerId`（可空，空表示匿名请求）/`ViewerRole`/`Outcome`/`OccurredAt`，只追加、一次读取一行，并给 `(TaskId, OccurredAt)` 与 `(ViewerId, OccurredAt)` 各建一条索引，分别服务“看某条任务被谁读过”和“看某个人读过哪些任务”）。Development 启动执行 `Database.Migrate()`，早期 `EnsureCreated()` 建出的旧库会自动基线化（见 `handoff.md` 第 10 节）。空库迁移、并发选人、草稿编辑落库、争议冻结与风险门禁已经有真实 PostgreSQL 自动化回归（`backend/tests/AIToHuman.IntegrationTests/Postgres/`，见 [开发指南](./development-guide.md) 第 4 节）；Redis 与对象存储也已有自动化回归（`backend/tests/AIToHuman.IntegrationTests/External/`，覆盖扇出广播与订阅、预签名地址的签名校验与过期，见同一节）。
- ⚠️ Redis 已真正接入：用于通知的多实例扇出（派发方原子认领后发布到固定频道，各实例推给自己进程内的在线客户端，运营可开关，见 [ADR-0004](../architecture/decisions/0004-notification-fanout.md)），已用本机 Redis + 双 API 实例端到端验证，并已有自动化回归覆盖"广播能到达订阅方、异常不中断订阅、开关关闭时的行为"（但**“两个真实实例 + 客户端”之间的整链路投递**仍是手工端到端——主机级端到端用例只起一个 API 进程，覆盖不到多实例扇出）；缓存、分布式锁与频率限制仍未接入。MinIO 仍只是 `compose.yaml` 里的本地依赖（S3 兼容对象存储实现已用本机 MinIO 端到端验证，并有自动化回归覆盖对象往返、签名校验与过期，默认仍写本机私有目录，切到 `storage.provider=s3` 即可）。

退出条件：团队确认首发城市、任务分类、AI 建议价冷启动方式和身份/支付策略；开发环境可复现。开发环境可复现已基本达成，产品决策项仍未确认。

## M1：AI 建单闭环（核心已实现）

交付：

- ✅ 注册、登录和基础账户，含密码哈希、JWT 和 `owner`/`worker` 角色切换。
- ⚠️ AI 对话、缺失字段澄清和结构化草稿：多轮澄清、SSE 流式输出、结构化 `plan` 与严格输出校验（校验失败最多重试一次并发 `restart` 事件）已实现；对话已服务端持久化（`Conversation` 聚合 + 消息表），刷新或换设备可恢复；草稿字段也能改（见下一条），草稿的**版本号与编辑历史也已实现**（`task_draft_revisions` 只追加：创建写第 1 版、每次编辑追加一版并写变更摘要与该版风险结论，同事务写入、仅所有者可读；每版还带与上一版逐字段比较的差异，并可从历史版本回滚，见下一条）；风险规则已落地（确定性词表与阈值规则在创建草稿、编辑草稿、回滚草稿、发布前与已发布任务加价后各判定一次，用的是当下生效的那一版目录；仍在线的任务还会被周期复检，见后两条），见下一条。
- ⚠️ 草稿编辑、风险规则、确认和任务发布：草稿编辑**已实现**——所有者可以对 `ReadyToPublish` 的任务改标题、描述、区域、截止时间、悬赏、验收标准、执行地址与报名截止时间（`PUT /api/v1/tasks/{id}`），字段校验与创建任务共用同一套（不存在“创建拦得住、编辑能绕过”的缺口），编辑后立即重跑风险规则并作废原有的人工复核结论；草稿区在信息完整前保持锁定，预览后确认发布。仍缺独立 `TaskDraft` 聚合；草稿的版本号与编辑历史已实现（`task_draft_revisions` 只追加，创建写第 1 版、每次编辑追加一版并记录变更摘要与该版风险结论，任务写入与快照同事务，仅所有者可读），**字段级 diff 与回滚也已实现**（历史接口每项带与上一版逐字段比较的 `changes`；`POST /api/v1/tasks/{id}/revisions/{revision}/restore` 让所有者恢复到某一版，走与编辑同一条 `ApplyDraft` 路径——同样重新校验、重跑风险判定、作废人工复核结论与申诉状态，并且**回滚本身也追加一版**、不覆盖中间版本）。已发布任务的字段仍不可改（只能加价或撤销后重建）。风险规则已落地：代码内置目录（`RiskRuleCatalog.BuiltIn`，`Version = 1`）仍是硬底线，当前 10 条词表规则（6 条禁止 + 4 条转人工）加 2 条阈值规则（悬赏 > 5000 元、截止时间落在北京时间 00:00–06:00），共 12 个原因代码；**运营现在可以在后台编辑出覆盖版本**——整份替换规则、阈值与时段，版本号自动 +1、变更依据必填、只追加不覆盖、可一键恢复代码内置目录，提交带 `expectedVersion`（不一致返回 `409`）。`TaskItem` 在创建草稿、编辑草稿、回滚草稿与 `Publish` 前都会用**当下生效的那一版目录**重新判定，因此运营收紧规则后已存在的草稿在发布时同样会被拦；**已经在线（甚至已被接单）的任务也会被重新判定**，见下面「发布后复检」这一段。任务上记的 `RiskRuleVersion` 是判定当时的版本号，历史判定不会被后续改规则改写。禁止类别一律不能发布（人工也无权放行），需人工复核的进入运营队列、放行后才能发布（人工驳回是终态）；被拦的草稿仍会创建，用户能看到原因并自行撤销。**误拦申诉已实现**：被拦下的所有者可以申诉（理由必填 ≤500 字），运营在「误拦申诉」队列里给出结论（依据必填并写审计）；处置分两档——转人工被驳回的任务申诉成立即放行，禁止类别命中的任务申诉成立也不放行、只记为规则误伤，且同一版内容只能申诉一次。**申诉现在还有两道节流**：同一条任务累计最多 3 次、同一个人 24 小时内最多 5 次（都在库上 COUNT，超限返回 `422` 并给出可读文案），而且**每次申诉都留档**——一次申诉一行写进 `task_risk_appeals`，记下提交时的规则代码/版本/结论与理由，运营结论写回同一行；改文案后再申诉不会再把上一轮的理由与结论覆盖掉，运营在「申诉轨迹」里能看到这条任务被误拦过几次、每次结论是什么。仍缺模型辅助分类、规则命中统计/看板、规则目录编辑的双人复核/审批流、规则目录的灰度或 A/B、按规则维度的报表，以及通知的站外渠道（邮件/短信）。**加价绕过路径已修掉**：已发布任务的「提高悬赏」（`IncreaseReward`）现在会接着按当下生效的那一版规则重判，悬赏改到高金额（> 5000 元）会当场变成 `NeedsReview` 并进人工复核队列，不再是“先发一条普通任务、再改成高标准悬赏”的现成后门。
- ✅ 管理员查看风险决策和下架任务：人工下架已实现；风险复核队列走 `GET /api/v1/admin/risk/reviews`（按创建时间升序，先来先处理），运营用 `POST /api/v1/admin/risk/reviews/{taskId}/decide` 放行或驳回、依据必填（≤200 字，与动作 `task.risk.approve`/`task.risk.reject` 一起写进 `admin_audit_entries`），规则目录的查看与编辑走 `GET /api/v1/admin/risk/rules`（概述：版本、阈值与规则清单，只给匹配词数量不给词本身）、`GET /api/v1/admin/risk/rules/detail`（明细，含匹配词与最近一次改动的摘要/依据/操作人）、`GET /api/v1/admin/risk/rules/versions`（版本历史）、`POST /api/v1/admin/risk/rules`（整份替换，版本号自动 +1、依据必填、带 `expectedVersion` 挡并发）与 `POST /api/v1/admin/risk/rules/reset`（恢复内置目录，同样是追加一版）；**误拦申诉**也已实现：所有者 `POST /api/v1/tasks/{id}/risk-appeals` 提交（理由必填 ≤500 字），运营用 `GET /api/v1/admin/risk/appeals` 取队列（每项带 `appealCount`，即这条任务累计申诉过几次）、`GET /api/v1/admin/risk/appeals/{taskId}/history` 看完整申诉轨迹（每次提交的规则代码/版本与理由、运营结论与依据，另附当前生效的两条上限）、`POST /api/v1/admin/risk/appeals/{taskId}/decide` 处置（`Accept`/`Deny`，依据必填，动作 `task.risk.appeal.accept`/`task.risk.appeal.deny` 写审计，结果通知所有者；`canBeReleasedByAppeal` 标明本次结论能否真的放行——禁止类别恒为 false）。**发布后复检**也已实现：运营用 `POST /api/v1/admin/risk/recheck?limit=` 手动跑一轮（返回扫描/仅刷新/要求复检/自动下架/冻结订单/跳过 的条数），后台另有 `RiskRecheckService` 每 5 分钟跑一轮；平台自己的动作以固定系统身份写运营审计（`task.risk.recheck.unpublish`/`task.risk.recheck.freezeOrder`）。仍缺风险看板与规则命中统计、追加式决策历史，也没有规则目录的编辑审批流、灰度或按规则维度的报表，以及通知的站外渠道。
- ✅ 发布后风险复检（规则收紧会作用于已在线任务）：`TaskItem.ReassessRisk(catalog, now)` 只对**仍在线**的 `Published`/`Assigned` 任务生效（草稿与已撤销、已结束的任务调用会报错），处置口径三条——命中禁止类别且**没有订单**的当场 `Cancel` 自动下架（原因写明命中的规则代码与规则版本）；命中禁止类别但**已经有订单**的不硬撤任务，改由应用层冻结订单（复用既有的争议路径，避免“任务没了、订单还挂在服务者名下”）；只命中“需人工复核”的**保持在线**，只把它送回运营队列要求复检。复检刻意收敛，避免通知风暴：如果运营已经就**同一条规则**放行过（命中代码与上一次相同），或这条任务已经在队列里等人工，就只刷新规则版本号，**不重新排队、不重复通知**。`RiskEnforcementService.Recheck(limit)` 跑单轮并返回 扫描/仅刷新/要求复检/自动下架/冻结订单/跳过 的条数，后台 `RiskRecheckService` 启动 30 秒后跑第一轮、之后每 5 分钟一轮；它只扫“仍在线且规则版本不是最新”的任务，判完就把版本号刷新，因此天然幂等。平台动作以固定系统身份（`AdminAuditEntry.SystemActorId`）写运营审计（`task.risk.recheck.unpublish`/`task.risk.recheck.freezeOrder`），任务响应里带 `riskEnforcementStatus`/`riskEnforcementReason`/`riskEnforcedAt`，所有者收到通知 `task.riskEnforced`（自动下架时所有者与每个报名中的服务者还会各收到一条 `task.cancelled`；冻结订单时双方收到的是复用的 `order.disputed`）。**已知缺口**：禁止类别命中的**已分配**任务仍然只有“冻结订单 + 运营按争议处置”——赔付与退款现在能做了（运营处置时可填金额，部分放款与部分退款分别入账并写账本），但仍要人工判断，没有自动退款或赔付规则。

退出条件：测试用户能稳定完成对话到发布；禁止任务测试集达到上线要求；AI 失败有明确降级路径。多轮对话和发布已可用，超时（120 秒无数据）和流内 `error` 事件提供基本降级；禁止任务测试集（`RiskRuleCatalogTests`：17 条禁止、6 条转人工、8 条正常跑腿用例，并锁死“匹配词不得为单字”与“原因代码唯一”）与发布前拦截、人工复核都已建立，**误拦申诉也已实现**（含两档处置、审计与通知，禁止类别不因申诉放行）；仍缺模型辅助分类、规则命中统计/看板与规则目录编辑的双人复核。

## M2：任务大厅与订单（核心已实现）

交付：

- ⛔ 服务者资料和服务区域：没有 `WorkerProfile`，报名不校验账户状态、类别或区域。**服务者信用已经双向可见**：报名列表内联该服务者的公开评价摘要（`workerAverageRating` / `workerReviewCount`，只统计已公开评价、口径与 `GET /users/{id}/review-summary` 一致），需求方选人前能看到服务者信用；仍未实现的是服务者资料（显示名、服务区域、类别）与邀请制人工核验。
- ⚠️ 大厅列表、筛选和任务详情：列表（游标分页 + 区域/悬赏区间筛选）、公开详情与草稿隔离已实现，只返回区域、悬赏、验收标准和报名数；分类、时间、距离筛选与排序选项尚未实现。
- ⚠️ AI 建议价、用户设定/提高悬赏、报名、撤回、选择报名者和订单创建：建议价（本地规则）、悬赏设定与分配前加价、报名、选择报名者和订单创建已实现；订单取消已实现（服务者只能在未开工前取消、需求方在服务者提交验收前可取消，原因必填；取消后任务若未过截止时间就回到大厅重新招募并把选中的报名置为 `Rejected`，已过截止时间则直接把任务置为 `Expired`）；报名撤回与报名截止时间也已实现——`POST /api/v1/tasks/{id}/applications/{applicationId}/withdraw` 只允许撤回自己仍处于 `Pending` 的报名（撤回后状态为 `Withdrawn`、记录保留、**可以重新报名**，重复报名仍被拦；被选中之后要退出只能走订单取消，`422`），任务可选的 `ApplicationDeadline` 到点后只关闭**新**报名（**已有报名仍可被选中**），已过报名截止时间的任务不能发布；`GET /api/v1/tasks/applications/mine` 返回服务者自己的报名与 `canWithdraw`（仅 `Pending` 为 true）。仍未实现：预计到达时间、服务者声明的报名有效期（现在只有任务级的报名截止时间）、选人时固化的不可变报名快照。
- ⚠️ 后端状态机、审计和基础通知：任务与订单状态机、乐观并发令牌与显式事务已实现，写接口也已支持可选的 `Idempotency-Key`（已认证用户在 `/api/v1` 下的写请求按「用户 + 键」回放原响应，避免超时重试把加价、报名、选人、状态转换做两遍；`5xx` 与非 JSON 响应不缓存，仍缺 ETag/版本字段返回（幂等旧记录的清理已有后台任务））；审计日志缺失；通知已持久化（`notifications` 表兼作 Outbox + 后台派发与重试 + 收件箱未读数），派发改为「先用条件 UPDATE 原子认领（`WHERE DispatchedAt IS NULL`）→ 再发布/推送 → 失败则撤回认领退回 Outbox 等下一轮」，因此多实例不会重复推送；启用 `notifications.fanout.enabled` 时由认领方广播到 Redis，各实例推给连在自己身上的在线客户端，未启用或 Redis 不可用时降级为本实例推送。

退出条件：并发选单无重复订单；跨用户和跨角色越权测试通过；公开数据不泄露精确位置。并发选单已由乐观并发令牌 + 显式事务保证（12 路并行选人、并发报名、并发验收都有真实 PostgreSQL 验证，其中并发选人与并发令牌已有自动化回归用例）；精确执行地址只在订单成立后向参与者披露（**披露口径不变**：所有者与被选中的服务者，其余 `403`、任务不存在 `404`），大厅与公开详情只暴露 `hasExecutionAddress`；**每次读取都留痕**——谁、以什么身份、什么时候、有没有真的披露，**被拒绝的尝试同样落库**（任务本来没登记地址记 `NotSet`，不算越权尝试），运营可按任务或按查看者查 `GET /api/v1/admin/address-access?taskId=&viewerId=&limit=`（默认 50、上限 200，并按同一过滤条件给出拒绝次数）；越权由领域单元测试与手工端到端覆盖，**主机级端到端用例本轮已补上**——不用 `WebApplicationFactory`（本机离线取不到 `Microsoft.AspNetCore.Mvc.Testing`），改为把已构建的 `AIToHuman.Api.dll` 起成子进程、连一个临时真库、用真 HTTP 打接口，因此覆盖的是真管线：`/health` 与开发环境路由、鉴权与错误映射（邮箱重复 `409`、缺角色 `422`、非管理员访问运营接口 `403`、不存在的订单/任务 `404`）、整条任务到资金链路的真 HTTP 走通（建任务 → 发布 → 报名 → 选人 → 开工 → 提交 → 验收，含托管 `Held` → `Released` 与账本 `Hold`/`Release` 各一条流水）以及幂等键在真管线上的回放（同键同体回放原状态码与响应体并带 `Idempotency-Replayed: true`，同键不同体 `409`）；仍未自动化的是**两个真实实例 + 客户端**的通知扇出整链路（见 `handoff.md` 第 11 节）。

## M3：履约与信任（部分实现）

交付：

- ⚠️ 订单消息和 SignalR 更新：订单内消息、会话未读数与状态变更事件已实现（统一走持久化通知 + SignalR 推送，客户端按 REST 重新拉取事实状态）；消息分页、撤回与编辑未实现。
- ⚠️ 文件上传、扫描、凭证关联和验收：验收与驳回已实现（驳回必须填原因），凭证文件上传与鉴权下载已实现（类型白名单 + 文件签名校验 + 大小与摘要校验 + 扫描状态机 + 待扫描凭证的退避重扫 + 按人小时配额），存储位置可在本机目录与 S3 兼容对象存储之间切换，下载也支持短时直连签名地址，上传时默认做**容器白名单化 + 结构 fail-closed**（JPEG 丢掉全部 APPn 与注释、PNG 只留结构必需的 `IHDR`/`PLTE`/`tRNS`/`IDAT`/`IEND`、WebP 只留图像与动画块，其余含未知私有块一律丢弃；容器结构不合法一律 `422` 拒绝，不再原样放行——**这是容器规范化，不是像素级重编码**），丢弃了什么记在 `metadataRemoved` 上，扫描方式支持 `none`/外部 HTTP 服务/clamd INSTREAM 三选一，这些开关都在运营后台；缺的是**部署**真实病毒库或选定内容扫描服务，以及**图片像素级重编码（需图像编解码库）**与“凭证关联到具体验收项”。
- ⚠️ 取消、超时、争议、双向评价盲期/公开和运营处理：评价盲期与公开规则（双方提交或 7 天后）已实现；取消与截止时间过期已实现（`POST /api/v1/orders/{id}/cancel` 按角色与阶段限制、原因必填并落库；后台 `TaskExpiryService` 每 60 秒把仍处于 `Published` 且已过截止时间的任务置为 `Expired`，作废 `Pending` 报名并通知任务所有者与报名者，已分配的任务不参与过期扫描）；争议处理已实现：需求方在 `Submitted`、服务者在 `Rejected` 可发起 `POST /api/v1/orders/{id}/dispute`（原因必填、≤500 字），争议期间订单冻结（提交、验收、驳回、返工、取消一律 `422`，任务保持 `Assigned` 不回大厅），运营用 `GET /api/v1/admin/orders?status=` 检索（省略 `status` 默认只返回 `Disputed`，传 `all` 看全部）并用 `POST /api/v1/admin/orders/{id}/resolve` 做三种处置：`Approve` 强制完成（订单 `Approved` 且任务 `Closed`）、`Rework` 退回返工（订单 `InProgress`、`ReworkCount` 累加、任务保持 `Assigned`）、`Cancel` 终止订单（订单 `Cancelled`，`CancelledBy` 为空表示平台处置，任务按取消订单的规则回到大厅或直接过期）；三种处置都在同一事务里连同运营审计（`order.dispute.approve`/`order.dispute.rework`/`order.dispute.cancel`）与双方通知（`order.disputeResolved`）完成。**平台按风控冻结订单走的是同一条争议路径**：发布后复检命中禁止类别但任务已有订单时，`Order.SuspendByRisk` 把订单冻结成 `Disputed`（`DisputeOpenedBy` 为空表示这不是任何一方发起的，而是平台动作），于是它天然出现在运营的争议队列里、三种处置照旧。**赔付与退款已实现**：本轮把资金托管接进了订单生命周期——选人时冻结（冻结失败则任务、订单与流水一起回滚）、验收通过全额放款、取消订单全额退回，争议处置可按金额分账（部分放款 + 部分退款分别入账），每一步都写只追加的资金流水，双方参与者与运营都能查“钱去哪了”（见 M5）。仍未实现：责任判定与**自动**赔付规则（现在要靠运营在处置时填金额）、争议申诉与时限、客服工单流程。
- ⛔ 完整可观测性、备份和恢复演练。

退出条件：核心端到端场景和异常场景通过；对象权限、安全日志与运营处置流程通过评审。当前正常履约路径，以及取消、过期、争议三类异常路径都已覆盖，并在真实 PostgreSQL 上端到端验证（取消后的任务去向、报名作废与通知，过期扫描与通知，争议期间的冻结与运营三种处置的落库、审计与通知；其中争议冻结与取消订单的连带效果已进入自动化回归）；仍未开始的是责任判定与自动赔付规则（赔付与退款本身已随资金托管落地，见 M5）、争议申诉与时限，以及对象权限与安全日志的评审。

## M4：封闭试点（未开始）

> 现状（⛔）：尚未开始。前置条件是 M1–M3 的异常流程、运营后台和合规边界先落地。

交付：

- 一个城市、有限类别、邀请制用户与服务者。
- 运营看板、反馈入口和人工兜底流程。
- 身份验证及通知渠道的受控接入。

退出条件：获得草稿转化、报名时间、建议价采纳率、用户加价率、完成率、争议率和安全事件的可信基线；决定是否进入真实交易试点。

## M5：交易与规模化准备（未开始）

> 现状（⚠️）：**托管与账本已实现第一批**——订单托管抽象（`EscrowStatus`：未托管 / 已冻结 / 已放款 / 已退款 / 部分放款 + 部分退款）+ 只追加的复式账本 `ledger_entries` + 模拟支付网关（`payment.provider=simulated`，确定性本地实现，只发可对账凭据、不持有金额状态），资金接入下单冻结、验收放款、取消退款与争议分账四条路径。**仍缺真实支付服务商接入、失败重试队列与自动对账、佣金抽成**（本轮不抽佣金，账本里没有平台收入账户）。正式支付接入前，按 [安全、隐私与风控](../security/security-and-risk.md) 的要求不得把普通数据库余额当作真实钱包。

前置条件：法律和支付方案已评审。

交付候选：

- ⚠️ 合规支付、托管/分账、退款、账本和对账：**托管与账本已实现**（下单冻结、验收放款、取消退款、争议分账 + 只追加账本 + 模拟网关；争议处置可带赔付金额，双方可查流水），仍缺真实支付服务商接入、失败重试队列与自动对账、佣金抽成。
- 欺诈检测、信用体系、地图与地理围栏。
- 更丰富的通知和移动端体验。
- 依据真实容量数据进行性能优化或模块拆分。

## 当前待办

1. ⛔ 确认 GitHub 仓库可见性、许可证和协作成员（`README.md` 尚未确定开源许可）。
2. ⛔ 选择首发城市与 2～3 个低风险任务类别。
3. ⚠️ 确定 AI 建议价的冷启动数据、估价因素与合理区间：目前只有 `POST /api/v1/reward-suggestions` 的本地规则，未使用历史成交数据。
4. ⚠️ 确定首版 AI 服务商及数据保留政策：服务商已定为火山引擎 Ark（默认模型 `glm-4-7-251222`），数据保留政策仍未确定。
5. ⛔ 明确封闭测试阶段是否完全不处理真实支付。
6. ✅ 生成 Vue/.NET 工程骨架与 Docker Compose（已完成，见 M0）。
