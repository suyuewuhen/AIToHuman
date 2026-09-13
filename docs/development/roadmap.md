# 产品与开发路线图

路线图以里程碑和退出条件为主，不先承诺未经团队评估的具体日期。

> **现状标注**（对齐 2026-09-13 代码；已包含订单取消与任务过期、报名撤回与报名截止时间、争议处理与运营处置、确定性风险规则与人工复核、误拦申诉、草稿字段编辑与版本历史、草稿字段级 diff 与回滚、写接口幂等键、真实 PostgreSQL 回归测试基建、外部依赖（Redis 与 S3 兼容存储）的自动化回归）
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
- ⚠️ CI、格式和测试基线：`.github/workflows/ci.yml` 已执行后端 `restore/build/test` 和前端 `npm ci`/`npm run typecheck`/`npm run build`，并有 `.editorconfig` 与 255 个领域单元测试、313 个集成测试（其中 12 个是真实 PostgreSQL 回归用例，10 个覆盖 Redis 与 S3 兼容存储）；后端 job 已在 CI 里同时起 `postgres:16`、`redis:7` 与 `minio/minio` 三个服务并注入连接串（测试前用 `minio/mc` 建好测试桶），因此这些真实依赖的回归用例在 CI 会真正运行，本机缺少对应依赖时同一批用例自动跳过；尚无 Lint、Markdown 检查、OpenAPI 兼容性检查和技术文档生成。
- ✅ 配置分层与运营可配置项：部署级配置（连接串、Redis、日志、密钥环路径）只走环境变量；三方集成参数（模型服务、对象存储、内容扫描）与凭证上传上限登记在设置目录里，可通过管理员接口与顶栏“运营配置”页面修改，机密加密落库并留审计，改完立即生效（见 [ADR-0003](../architecture/decisions/0003-operator-configurable-settings.md)）。运营后台的其余能力（任务/用户检索、人工下架、争议订单检索与处置、风险复核队列与规则目录自述）已实现；仍未实现的是风险看板、申诉的次数/频率限制与模型辅助分类。
- ⛔ 秘密扫描与依赖更新自动化。

遗留项：

- ✅ 数据库结构已收敛为 EF Core Migration 单一来源：20 个迁移覆盖 users、orders、order_evidence、reviews、tasks、task_applications、conversations、conversation_messages、notifications、order_messages、evidence（含扫描记账与元数据剥离记录）、system_settings、system_setting_audits、admin_audit_entries 等表；最近四条是 `AddOrderCancellationAndTaskExpiry`（给 `orders` 补取消三列 `CancelledAt`/`CancelledBy`/`CancellationReason`，给 `tasks` 补 `ExpiredAt`/`CancelledAt`/`CancellationReason`）、`AddApplicationDeadline`（给 `tasks` 补可选的报名截止时间 `ApplicationDeadline`）、`AddOrderDispute`（给 `orders` 补争议六列 `DisputeReason`/`DisputeOpenedBy`/`DisputeOpenedAt`/`DisputeResolution`/`DisputeResolutionNote`/`DisputeResolvedAt`，并加 `(Status, CreatedAt)` 索引供运营按状态检索）与 `AddTaskRiskAssessment`（给 `tasks` 补风险判定与复核十列 `RiskVerdict`/`RiskRuleCode`/`RiskCategory`/`RiskSummary`/`RiskRuleVersion`/`RiskAssessedAt`/`RiskReviewStatus`/`RiskReviewedBy`/`RiskReviewedAt`/`RiskReviewNote`，并加 `(RiskReviewStatus, CreatedAt)` 索引供复核队列先来先处理；存量行按数据库默认值 `Allowed`/`NotRequired` 回填）。Development 启动执行 `Database.Migrate()`，早期 `EnsureCreated()` 建出的旧库会自动基线化（见 `handoff.md` 第 10 节）。空库迁移、并发选人、草稿编辑落库、争议冻结与风险门禁已经有真实 PostgreSQL 自动化回归（`backend/tests/AIToHuman.IntegrationTests/Postgres/`，见 [开发指南](./development-guide.md) 第 4 节）；Redis 与对象存储也已有自动化回归（`backend/tests/AIToHuman.IntegrationTests/External/`，覆盖扇出广播与订阅、预签名地址的签名校验与过期，见同一节）。
- ⚠️ Redis 已真正接入：用于通知的多实例扇出（派发方原子认领后发布到固定频道，各实例推给自己进程内的在线客户端，运营可开关，见 [ADR-0004](../architecture/decisions/0004-notification-fanout.md)），已用本机 Redis + 双 API 实例端到端验证，并已有自动化回归覆盖"广播能到达订阅方、异常不中断订阅、开关关闭时的行为"（但**两个真实实例之间的整链路投递**仍是手工端到端）；缓存、分布式锁与频率限制仍未接入。MinIO 仍只是 `compose.yaml` 里的本地依赖（S3 兼容对象存储实现已用本机 MinIO 端到端验证，并有自动化回归覆盖对象往返、签名校验与过期，默认仍写本机私有目录，切到 `storage.provider=s3` 即可）。

退出条件：团队确认首发城市、任务分类、AI 建议价冷启动方式和身份/支付策略；开发环境可复现。开发环境可复现已基本达成，产品决策项仍未确认。

## M1：AI 建单闭环（核心已实现）

交付：

- ✅ 注册、登录和基础账户，含密码哈希、JWT 和 `owner`/`worker` 角色切换。
- ⚠️ AI 对话、缺失字段澄清和结构化草稿：多轮澄清、SSE 流式输出、结构化 `plan` 与严格输出校验（校验失败最多重试一次并发 `restart` 事件）已实现；对话已服务端持久化（`Conversation` 聚合 + 消息表），刷新或换设备可恢复；草稿字段也能改（见下一条），草稿的**版本号与编辑历史也已实现**（`task_draft_revisions` 只追加：创建写第 1 版、每次编辑追加一版并写变更摘要与该版风险结论，同事务写入、仅所有者可读；每版还带与上一版逐字段比较的差异，并可从历史版本回滚，见下一条）；风险规则已落地（确定性词表与阈值规则在创建草稿时就判定一次），见下一条。
- ⚠️ 草稿编辑、风险规则、确认和任务发布：草稿编辑**已实现**——所有者可以对 `ReadyToPublish` 的任务改标题、描述、区域、截止时间、悬赏、验收标准、执行地址与报名截止时间（`PUT /api/v1/tasks/{id}`），字段校验与创建任务共用同一套（不存在“创建拦得住、编辑能绕过”的缺口），编辑后立即重跑风险规则并作废原有的人工复核结论；草稿区在信息完整前保持锁定，预览后确认发布。仍缺独立 `TaskDraft` 聚合；草稿的版本号与编辑历史已实现（`task_draft_revisions` 只追加，创建写第 1 版、每次编辑追加一版并记录变更摘要与该版风险结论，任务写入与快照同事务，仅所有者可读），**字段级 diff 与回滚也已实现**（历史接口每项带与上一版逐字段比较的 `changes`；`POST /api/v1/tasks/{id}/revisions/{revision}/restore` 让所有者恢复到某一版，走与编辑同一条 `ApplyDraft` 路径——同样重新校验、重跑风险判定、作废人工复核结论与申诉状态，并且**回滚本身也追加一版**、不覆盖中间版本）。已发布任务的字段仍不可改（只能加价或撤销后重建）。风险规则已落地：规则目录固定在代码里并带 `Version = 1`（改规则必须提升版本号），当前 10 条词表规则（6 条禁止 + 4 条转人工）加 2 条阈值规则（悬赏 > 5000 元、截止时间落在北京时间 00:00–06:00），共 12 个原因代码；`TaskItem` 在创建草稿、编辑草稿与 `Publish` 前各判定一次，禁止类别一律不能发布（人工也无权放行），需人工复核的进入运营队列、放行后才能发布（人工驳回是终态）；被拦的草稿仍会创建，用户能看到原因并自行撤销。**误拦申诉已实现**：被拦下的所有者可以申诉（理由必填 ≤500 字），运营在「误拦申诉」队列里给出结论（依据必填并写审计）；处置分两档——转人工被驳回的任务申诉成立即放行，禁止类别命中的任务申诉成立也不放行、只记为规则误伤，且同一版内容只能申诉一次。仍缺模型辅助分类、申诉的次数/频率限制与申诉历史留档。
- ✅ 管理员查看风险决策和下架任务：人工下架已实现；风险复核队列走 `GET /api/v1/admin/risk/reviews`（按创建时间升序，先来先处理），运营用 `POST /api/v1/admin/risk/reviews/{taskId}/decide` 放行或驳回、依据必填（≤200 字，与动作 `task.risk.approve`/`task.risk.reject` 一起写进 `admin_audit_entries`），规则目录自述走 `GET /api/v1/admin/risk/rules`（返回版本、阈值与规则清单，只给匹配词数量不给词本身）；**误拦申诉**也已实现：所有者 `POST /api/v1/tasks/{id}/risk-appeals` 提交（理由必填 ≤500 字），运营用 `GET /api/v1/admin/risk/appeals` 取队列、`POST /api/v1/admin/risk/appeals/{taskId}/decide` 处置（`Accept`/`Deny`，依据必填，动作 `task.risk.appeal.accept`/`task.risk.appeal.deny` 写审计，结果通知所有者；`canBeReleasedByAppeal` 标明本次结论能否真的放行——禁止类别恒为 false）。仍缺风险看板、追加式决策历史、申诉次数限制与申诉历史留档。

退出条件：测试用户能稳定完成对话到发布；禁止任务测试集达到上线要求；AI 失败有明确降级路径。多轮对话和发布已可用，超时（120 秒无数据）和流内 `error` 事件提供基本降级；禁止任务测试集（`RiskRuleCatalogTests`：17 条禁止、6 条转人工、8 条正常跑腿用例，并锁死“匹配词不得为单字”与“原因代码唯一”）与发布前拦截、人工复核都已建立，**误拦申诉也已实现**（含两档处置、审计与通知，禁止类别不因申诉放行）；仍缺模型辅助分类与申诉的次数/频率限制。

## M2：任务大厅与订单（核心已实现）

交付：

- ⛔ 服务者资料和服务区域：没有 `WorkerProfile`，报名不校验账户状态、类别或区域。**服务者信用已经双向可见**：报名列表内联该服务者的公开评价摘要（`workerAverageRating` / `workerReviewCount`，只统计已公开评价、口径与 `GET /users/{id}/review-summary` 一致），需求方选人前能看到服务者信用；仍未实现的是服务者资料（显示名、服务区域、类别）与邀请制人工核验。
- ⚠️ 大厅列表、筛选和任务详情：列表（游标分页 + 区域/悬赏区间筛选）、公开详情与草稿隔离已实现，只返回区域、悬赏、验收标准和报名数；分类、时间、距离筛选与排序选项尚未实现。
- ⚠️ AI 建议价、用户设定/提高悬赏、报名、撤回、选择报名者和订单创建：建议价（本地规则）、悬赏设定与分配前加价、报名、选择报名者和订单创建已实现；订单取消已实现（服务者只能在未开工前取消、需求方在服务者提交验收前可取消，原因必填；取消后任务若未过截止时间就回到大厅重新招募并把选中的报名置为 `Rejected`，已过截止时间则直接把任务置为 `Expired`）；报名撤回与报名截止时间也已实现——`POST /api/v1/tasks/{id}/applications/{applicationId}/withdraw` 只允许撤回自己仍处于 `Pending` 的报名（撤回后状态为 `Withdrawn`、记录保留、**可以重新报名**，重复报名仍被拦；被选中之后要退出只能走订单取消，`422`），任务可选的 `ApplicationDeadline` 到点后只关闭**新**报名（**已有报名仍可被选中**），已过报名截止时间的任务不能发布；`GET /api/v1/tasks/applications/mine` 返回服务者自己的报名与 `canWithdraw`（仅 `Pending` 为 true）。仍未实现：预计到达时间、服务者声明的报名有效期（现在只有任务级的报名截止时间）、选人时固化的不可变报名快照。
- ⚠️ 后端状态机、审计和基础通知：任务与订单状态机、乐观并发令牌与显式事务已实现，写接口也已支持可选的 `Idempotency-Key`（已认证用户在 `/api/v1` 下的写请求按「用户 + 键」回放原响应，避免超时重试把加价、报名、选人、状态转换做两遍；`5xx` 与非 JSON 响应不缓存，仍缺 ETag/版本字段与旧记录清理）；审计日志缺失；通知已持久化（`notifications` 表兼作 Outbox + 后台派发与重试 + 收件箱未读数），派发改为「先用条件 UPDATE 原子认领（`WHERE DispatchedAt IS NULL`）→ 再发布/推送 → 失败则撤回认领退回 Outbox 等下一轮」，因此多实例不会重复推送；启用 `notifications.fanout.enabled` 时由认领方广播到 Redis，各实例推给连在自己身上的在线客户端，未启用或 Redis 不可用时降级为本实例推送。

退出条件：并发选单无重复订单；跨用户和跨角色越权测试通过；公开数据不泄露精确位置。并发选单已由乐观并发令牌 + 显式事务保证（12 路并行选人、并发报名、并发验收都有真实 PostgreSQL 验证，其中并发选人与并发令牌已有自动化回归用例）；精确执行地址只在订单成立后向参与者披露，大厅与公开详情只暴露 `hasExecutionAddress`；越权由领域单元测试与手工端到端覆盖，主机级端到端（`WebApplicationFactory`）仍缺（见 `handoff.md` 第 11 节）。

## M3：履约与信任（部分实现）

交付：

- ⚠️ 订单消息和 SignalR 更新：订单内消息、会话未读数与状态变更事件已实现（统一走持久化通知 + SignalR 推送，客户端按 REST 重新拉取事实状态）；消息分页、撤回与编辑未实现。
- ⚠️ 文件上传、扫描、凭证关联和验收：验收与驳回已实现（驳回必须填原因），凭证文件上传与鉴权下载已实现（类型白名单 + 文件签名校验 + 大小与摘要校验 + 扫描状态机 + 待扫描凭证的退避重扫 + 按人小时配额），存储位置可在本机目录与 S3 兼容对象存储之间切换，下载也支持短时直连签名地址，上传时默认剥离图片元数据（EXIF/GPS 等），扫描方式支持 `none`/外部 HTTP 服务/clamd INSTREAM 三选一，这些开关都在运营后台；缺的是**部署**真实病毒库或选定内容扫描服务，以及图片像素级重编码与“凭证关联到具体验收项”。
- ⚠️ 取消、超时、争议、双向评价盲期/公开和运营处理：评价盲期与公开规则（双方提交或 7 天后）已实现；取消与截止时间过期已实现（`POST /api/v1/orders/{id}/cancel` 按角色与阶段限制、原因必填并落库；后台 `TaskExpiryService` 每 60 秒把仍处于 `Published` 且已过截止时间的任务置为 `Expired`，作废 `Pending` 报名并通知任务所有者与报名者，已分配的任务不参与过期扫描）；争议处理已实现：需求方在 `Submitted`、服务者在 `Rejected` 可发起 `POST /api/v1/orders/{id}/dispute`（原因必填、≤500 字），争议期间订单冻结（提交、验收、驳回、返工、取消一律 `422`，任务保持 `Assigned` 不回大厅），运营用 `GET /api/v1/admin/orders?status=` 检索（省略 `status` 默认只返回 `Disputed`，传 `all` 看全部）并用 `POST /api/v1/admin/orders/{id}/resolve` 做三种处置：`Approve` 强制完成（订单 `Approved` 且任务 `Closed`）、`Rework` 退回返工（订单 `InProgress`、`ReworkCount` 累加、任务保持 `Assigned`）、`Cancel` 终止订单（订单 `Cancelled`，`CancelledBy` 为空表示平台处置，任务按取消订单的规则回到大厅或直接过期）；三种处置都在同一事务里连同运营审计（`order.dispute.approve`/`order.dispute.rework`/`order.dispute.cancel`）与双方通知（`order.disputeResolved`）完成。仍未实现：责任判定、赔付与退款、争议申诉与时限、客服工单流程。
- ⛔ 完整可观测性、备份和恢复演练。

退出条件：核心端到端场景和异常场景通过；对象权限、安全日志与运营处置流程通过评审。当前正常履约路径，以及取消、过期、争议三类异常路径都已覆盖，并在真实 PostgreSQL 上端到端验证（取消后的任务去向、报名作废与通知，过期扫描与通知，争议期间的冻结与运营三种处置的落库、审计与通知；其中争议冻结与取消订单的连带效果已进入自动化回归）；仍未开始的是责任判定与赔付/退款、争议申诉与时限，以及对象权限与安全日志的评审。

## M4：封闭试点（未开始）

> 现状（⛔）：尚未开始。前置条件是 M1–M3 的异常流程、运营后台和合规边界先落地。

交付：

- 一个城市、有限类别、邀请制用户与服务者。
- 运营看板、反馈入口和人工兜底流程。
- 身份验证及通知渠道的受控接入。

退出条件：获得草稿转化、报名时间、建议价采纳率、用户加价率、完成率、争议率和安全事件的可信基线；决定是否进入真实交易试点。

## M5：交易与规模化准备（未开始）

> 现状（⛔）：尚未开始。正式支付接入前，按 [安全、隐私与风控](../security/security-and-risk.md) 的要求不得把普通数据库余额当作真实钱包。

前置条件：法律和支付方案已评审。

交付候选：

- 合规支付、托管/分账、退款、账本和对账。
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
