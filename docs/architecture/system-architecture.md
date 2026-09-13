# 系统架构

## 1. 架构目标

- 用较低运维成本快速交付 MVP。
- 让业务规则集中、可测试，并防止前端或 AI 绕过。
- 为未来拆分高负载模块保留清晰边界。
- 默认保护位置、身份、对话和任务凭证等敏感数据。

## 2. 总体方案

采用前后端分离的模块化单体。Vue 应用只通过公开 API 和 SignalR Hub 访问后端；ASP.NET Core 应用承担认证、授权、领域状态、AI 编排、文件授权和审计职责。

```text
Vue 3 Web App
  ├─ REST/JSON ───────────────┐
  └─ SignalR ───────────────┐ │
                            ▼ ▼
                    ASP.NET Core API
  ┌────────────────────────────────────────────┐
  │ Identity │ AI │ Tasks │ Applications │ Orders │
  │ Messages │ Evidence │ Reviews │ Risk/Admin │
  └────────────────────────────────────────────┘
        │             │             │
   PostgreSQL       Redis       Object Storage
        │             │
        └──── Hangfire Worker
                      │
                AI Provider Adapter
```

## 3. 前端架构

建议目录：

```text
frontend/src/
├── api/             # OpenAPI 生成客户端与手写适配
├── components/      # 可复用表现组件
├── features/        # 按业务功能组织页面、store 和 composable
├── layouts/
├── router/
├── stores/          # 会话和全局状态
├── types/
└── utils/
```

原则：

- TypeScript 开启严格模式。
- 服务端数据以 OpenAPI 生成类型为主，避免重复手写契约。
- Pinia 仅保存确有跨页面生命周期的状态。
- 权限路由用于改善体验，真正授权由后端完成。
- Token 优先使用安全 Cookie 方案；如果选择浏览器存储，需单独评估 XSS 风险。

## 4. 后端解决方案结构

```text
backend/
├── AIToHuman.Api/             # HTTP、认证、异常映射、SignalR
├── AIToHuman.Application/     # 用例、命令查询、接口和事务边界
├── AIToHuman.Domain/          # 实体、值对象、状态机和领域规则
├── AIToHuman.Infrastructure/  # EF Core、Redis、文件、AI 和外部服务
├── AIToHuman.Contracts/       # API 请求响应契约
├── AIToHuman.Worker/          # Hangfire 作业宿主（按需独立部署）
└── tests/
    ├── AIToHuman.UnitTests/
    ├── AIToHuman.IntegrationTests/
    └── AIToHuman.ArchitectureTests/
```

依赖方向：

```text
Api → Application → Domain
Infrastructure → Application + Domain
Worker → Application + Infrastructure
```

Domain 不引用 EF Core、HTTP、AI SDK 或对象存储 SDK。

## 5. 业务模块

- Identity：账户、角色、登录、刷新令牌和服务者资料。
- Conversations：对话、消息和 AI 运行记录。
- Tasks：草稿、任务、步骤、位置摘要、发布和取消。（现状：精确执行地址只向所有者与被选中的服务者披露，**每次读取都留痕**——含被拒绝的尝试，运营可用 `GET /api/v1/admin/address-access?taskId=&viewerId=&limit=` 按任务或按人审计，见 [领域模型](./domain-model.md) 第 7 节。）
- Risk：规则检查、AI 辅助分类、审核队列和决策记录。（现状：确定性规则检查、运营可编辑的规则目录（版本历史/明细/编辑/恢复内置）、人工审核队列、**发布后复检**（规则收紧后重新判定仍在线任务：禁止类别自动下架、有订单的冻结订单、转人工的保持在线并要求复检，平台动作以固定系统身份写审计）与**申诉节流与留档**（单任务 3 次、单人 24 小时 5 次，每次申诉留档、运营可看轨迹）都已实现；AI 辅助分类仍未实现；每次判定都追加留痕、可按原因代码看窗口内的命中/复检命中/被判误伤次数、可按任务查完整判定轨迹，这些都已实现。）
- Applications：按固定悬赏报名、撤回、选择和并发控制，不承载服务者价格。
- Orders：交易快照、状态机、执行事件和验收。
- Payments / Escrow：订单资金托管与账本。（现状：✅ 第一批已实现——应用层的 `IPaymentGateway` 端口（`Hold`/`Capture`/`Refund`，返回 `PaymentGatewayResult`）与 `ILedgerRepository`（只追加，按订单或按最近查询），用例层 `PaymentService` 负责下单冻结 / 验收放款 / 取消退款 / 争议分账；基础设施层是本地 `SimulatedPaymentGateway`（确定性实现、**不持有金额状态**，只发可对账凭据 `sim-hold-<orderId:N>`/`sim-capture-…`/`sim-refund-…`，测试可用 `FailureMode` 让指定动作失败）与 `EfLedgerRepository`/`InMemoryLedgerRepository`。资金动作与订单保存在同一个工作单元里，网关失败即整体回滚；运营配置 `payment.provider` 取 `disabled` 可把托管整体关掉。仍缺真实支付服务商接入、失败重试队列与自动对账、佣金抽成，见 [领域模型](./domain-model.md) 第 8 节。）
- Messaging：订单会话、消息和实时推送。
- Evidence：上传授权、元数据、病毒扫描状态和访问控制。（现状：上传时先按声明类型校验签名，再做**容器白名单化 + 结构 fail-closed**——非白名单的扩展段/块一律丢弃，容器结构不合法一律 `422` 而不是原样放行；随后落库并进入扫描。这是容器规范化，**不是像素级重编码**，像素数据的兜底是内容扫描。）
- Reviews：用户评价服务者、服务者评价用户、评价盲期、公开资料和基础信用指标。
- Disputes：申诉、证据包和运营处理。
- Notifications：站内通知及后续外部渠道。
- Administration：审核、配置、下架、冻结和审计查询。

模块间通过应用层接口和领域事件协作，不直接跨模块修改数据库实体。

## 6. 数据与基础设施

### PostgreSQL

作为事实来源，保存业务状态、事件、审计、对话元数据和文件元数据。敏感字段按等级加密或令牌化。

> 实现现状（✅）：表结构由 EF Core Migration 单一来源维护，当前 29 个迁移覆盖 users、orders、tasks、task_applications、conversations、conversation_messages、notifications、order_messages、evidence、system_settings、system_setting_audits、admin_audit_entries、task_draft_revisions、idempotency_entries、risk_rule_catalog_revisions、task_risk_appeals、address_access_entries、ledger_entries、risk_decision_entries 等表；Development 启动执行 `Database.Migrate()`，早期 `EnsureCreated()` 建出的旧库会自动基线化。运营可编辑的风险规则目录以**只追加的版本快照**落在 `risk_rule_catalog_revisions`（第 24 个迁移 `AddRiskRuleCatalogRevisions`：`Version` 唯一索引 + `CreatedAt` 索引，读取取版本号最大的一版），数据库里还没有覆盖版本时判定回退到代码内置目录（版本 1），每次判定把当时的版本号记在 `tasks.RiskRuleVersion` 上，因此任何一次拦截都能回溯到具体哪一版规则。发布后复检的处置落在 `tasks` 上（第 25 个迁移 `AddTaskRiskEnforcement`：补 `RiskEnforcementStatus`/`RiskEnforcementReason`/`RiskEnforcedAt` 三列，状态默认值 `None` 回填存量行，并加 `(Status, RiskRuleVersion)` 索引供复检挑候选）；申诉留档落在 `task_risk_appeals`（第 26 个迁移 `AddTaskRiskAppeals`：一行一次申诉、只追加，含提交时的规则代码/版本/结论与理由，运营结论写回同一行，`(TaskId, SubmittedAt)` 与 `(OwnerId, SubmittedAt)` 两条索引分别服务“看轨迹”和“按人算次数”）。精确执行地址的访问留痕落在 `address_access_entries`（第 27 个迁移 `AddAddressAccessEntries`：`Id`/`TaskId`/`ViewerId`（可空，空表示匿名请求）/`ViewerRole`/`Outcome`/`OccurredAt`，**只追加、一次读取一行**，并给 `(TaskId, OccurredAt)` 与 `(ViewerId, OccurredAt)` 各建一条索引，分别服务“看某条任务被谁读过”和“看某个人读过哪些任务”；写入顺序是**先留痕再判断**，因此被拒绝的尝试同样在表里）。订单的托管与账本落在 `orders` 的七列与 `ledger_entries` 上（第 28 个迁移 `AddOrderEscrowAndLedger`：建资金流水表 `ledger_entries` 并给 `(OrderId, OccurredAt)` 与 `OccurredAt` 各建一条索引，同时给 `orders` 补 `EscrowStatus`/`EscrowAmount`/`ReleasedAmount`/`RefundedAmount`/`PaymentReference`/`EscrowHeldAt`/`EscrowSettledAt` 七列、`EscrowStatus` 用数据库默认值 `None` 回填存量订单，并加 `(EscrowStatus, CreatedAt)` 索引；**流水只追加**，一行就是一次账户间转账）。风险判定的留痕落在 `risk_decision_entries`（第 29 个迁移 `AddRiskDecisionEntries`：`Id`/`TaskId`/`Reason`（创建/编辑/回滚/发布/加价/复检）/`Verdict`/`RuleCode`/`Category`/`RuleVersion`/`RewardAmount`/`OccurredAt`，**只追加、一次判定一行**，并给 `(TaskId, OccurredAt)` 与 `(OccurredAt, RuleCode)` 各建一条索引，分别服务“看一条任务的判定轨迹”和“按原因代码出命中统计”；任务行上的风险字段仍然只保留最新一条判定，历史的唯一来源是这张表）。

### Redis

用于通知的多实例扇出：派发方先用条件 UPDATE 原子认领待派发记录，再发布到固定频道 `aitohuman:notifications:fanout`，订阅方把消息推给自己进程内的在线客户端；没配 `ConnectionStrings__Redis` 或运营开关 `notifications.fanout.enabled` 关闭时降级为本实例推送。开关每 5 秒复查一次，改完不需要重启实例。Redis 不能作为订单状态的唯一来源。

尚未接入：短期缓存与分布式锁。

**写接口限流当前不走 Redis**，而是由 API 进程内的固定窗口计数器实现（`WriteRateLimiter` 单例 + `WriteRateLimitMiddleware`）：只在 `/api/v1` 下的 `POST`/`PUT`/`PATCH`/`DELETE` 上计数，按「已认证用户 ID」分桶，未认证请求退化到「来源 IP」分桶，窗口是自然分钟（UTC 对齐）固定窗口，超限返回 `429`、`Retry-After` 与 `X-RateLimit-Limit`/`Remaining`/`Partition`；开关与上限（`ratelimit.enabled`、`ratelimit.writesPerMinute`，默认 240/分钟、可配 1–100000）每次请求现读设置，改完立即生效；`/api/v1/admin/settings` 的写入**永不限流**，作为「抢到限流后运维自救」的通道；`/health` 与 `health/ready` 及所有读接口不受影响。因为计数在进程内，**多实例部署时每个实例各记一份配额，跨实例的全局配额还没有实现**——要真正的分布式限流得把计数器换成 Redis（见 [安全、隐私与风控](../security/security-and-risk.md) 第 5 节）。

### Hangfire

用于到期提醒、任务过期、通知重试、文件异步检查等。作业必须具备幂等性，执行失败可观测。

> 实现现状：后台任务都是进程内的 `BackgroundService`（不是 Hangfire 作业），清单与周期如下——`TaskExpiryService` 每 60 秒把仍处于 `Published` 且已过截止时间的任务置为 `Expired`（已分配的 `Assigned` 任务不参与扫描）；`NotificationDispatcher` 原子认领未派发的通知并推送，失败退回 Outbox 等下一轮；`NotificationFanoutSubscriber` 在启用扇出时订阅 Redis 频道；`EvidenceRescanService` 对没有扫描结论的凭证退避重扫（30 秒退避、最多 5 次）；`IdempotencyCleanupService` 每小时清理幂等记录；`SettingsRefreshService` 轮询同步绕过 API 直接改库的运营配置；**`RiskRecheckService` 负责发布后风险复检——启动 30 秒后跑第一轮，之后每 5 分钟一轮**，只扫“仍在线且规则版本不是最新”的任务（单轮上限 200 条），判完把版本号刷新，因此天然幂等、不会重复处置或重复通知，运营也可以用 `POST /api/v1/admin/risk/recheck` 手动跑一轮。一轮失败只记日志、等下一轮，不影响服务本身。

### 对象存储

使用私有 Bucket。上传和下载采用短时效签名 URL，服务端核验订单权限后签发。禁止把永久公开 URL 保存为业务凭证。

> 实现现状（⚠️）：`IFileStorage` 有两个实现，由 `storage.provider` 选择：`local` 写本机私有目录，`s3` 走 S3 兼容对象存储（MinIO / OSS / AWS S3），后者用自研的 AWS SigV4 签名、路径风格请求、不依赖厂商 SDK，并实现了可选的 `IPresignedFileStorage`（短时直连下载地址，有效期 5 至 900 秒可配）。已用本机 MinIO 端到端验证（上传/下载/删除、篡改与过期都返回 403，并用独立客户端 `mc` 交叉核对字节）。下载有两条路：服务端鉴权后流式转发（两种存储通用）与短时直连签名地址（仅对象存储）。`IEvidenceScanner` 由 `HttpEvidenceScanner` 实现：`provider=none` 时显式放行，`provider=http` 时调用配置的扫描服务，扫描没有结论的凭证保持不可下载并由后台退避重扫；真实扫描服务商仍待选定。

## 7. AI 子系统

AI 采用供应商无关适配层：

```text
Conversation Orchestrator
  → Prompt/Policy Version
  → Model Provider
  → Structured Output Parser
  → Schema Validation
  → Deterministic Risk Rules
  → User Confirmation
```

要求：

- 使用结构化输出映射到版本化 DTO。
- 所有模型输出视为不可信输入，必须校验枚举、金额、时间和长度。
- 记录模型、提示版本、延迟、令牌用量和结果摘要，但避免记录不必要的敏感正文。
- 设置超时、有限重试、成本上限和降级文案。
- AI 不能直接调用内部数据库写方法，所有改变均经过应用用例和授权检查。

## 8. 一致性与并发

- EF Core 事务保证单个用例的数据库一致性。
- Task、Application、Order 使用并发令牌避免重复选人、重复加价和状态覆盖。
- 外部通知采用 Outbox Pattern，事务提交后异步发送。
- 写操作接受 `Idempotency-Key`，服务端缓存或持久化处理结果。
- 支付接入后，以支付方回调和内部账本为准，不相信前端结果。

> 实现现状：`tasks`/`orders` 的 `Version` 乐观并发令牌、`IUnitOfWork` 显式事务（选人建单、验收关单）与 Outbox 通知都已落地，冲突返回 `409`；写接口的 `Idempotency-Key` 也已实现——已认证的 `/api/v1` 写请求可带该请求头，命中时回放上一次的响应（带 `Idempotency-Replayed: true`），同键不同请求体或并发占位返回 `409`，`5xx` 与业务异常不缓存；**仍未实现**的是 ETag/版本字段返回（旧幂等记录的清理已有后台任务：每小时删除已完成超过 24 小时的记录与超时占位，见 [API 设计约定](../api/api-guidelines.md) 第 7 节）。本轮补上的一条是**资金与状态同事务**：冻结、放款、退款与争议分账都先让网关确认（`IPaymentGateway`）再改订单的托管字段并写一条只追加的流水（`ledger_entries`），网关失败即整笔回滚（订单状态、任务分配与流水都不落库），托管可整体关闭；真实支付回调、失败重试队列与对账仍未接入。中间件顺序上有一条硬约束：**限流中间件必须排在幂等中间件之前**（`Program.cs` 里 `UseMiddleware<WriteRateLimitMiddleware>()` 在 `UseMiddleware<IdempotencyMiddleware>()` 上方）——被限流拒绝的 `429` 不是「这个幂等键的处理结果」，如果顺序反了，幂等中间件会把它连同响应一起缓存下来，客户端换掉限流后重试同一个键仍会一直拿到 `429`。限流只读进程内计数、不落库也不调用外部依赖，因此进程重启即清零（它的定位是防脚本刷写，不是计费配额）。
>
> 测试形态（本轮新增）：**主机级端到端用例**（`backend/tests/AIToHuman.IntegrationTests/Host/`，集合 `host-e2e`）把已构建的 `AIToHuman.Api.dll` 当**子进程**起起来，配一个**随机命名的临时真库**（不提前建库，交给应用启动时的 `Migrate()` 自己建库并按顺序应用全部 29 个迁移，因此“空库能不能起来”也被真实覆盖）、随机空闲端口、临时 Data Protection 密钥环与对象存储目录，然后用**真实 HTTP** 打这个进程。它刻意不用 `WebApplicationFactory`：那需要 `Microsoft.AspNetCore.Mvc.Testing` 包，当前环境离线取不到，而子进程 + 真 HTTP 不依赖任何新包，且更接近部署形态（进程边界、真实端口）。这条路径专门覆盖**路由注册、DI 装配、中间件顺序（幂等键、异常映射、鉴权策略）、JSON 契约与启动期迁移**——单元测试与真库用例都碰不到、出问题时“单测全绿而接口不可用”的那一层。跳过语义与真库用例一致：缺 PostgreSQL、找不到已构建的程序集，或环境变量 `AITOHUMAN_TEST_HOST=0`（含 `false`）时，这 **9** 条用例**整组跳过而不是失败**。本轮新增的两条把运维面也拉进了真管线：一条在真进程上探 `/health` 与 `/health/ready`（postgres 项 `ok` 且报告“没有待应用的迁移”、storage 项 `ok`、未开扇出时 redis 项 `skipped`），另一条把 `ratelimit.writesPerMinute` 配成 2 后用真 HTTP 打第三个写请求，断言 `429`、`Retry-After`、`X-RateLimit-*`、读接口不受影响以及配置写入不被限流（跑完把上限改回 240）。

## 9. 可观测性

- 结构化日志：请求 ID、用户匿名标识、模块、结果码和耗时。
- 指标：API 延迟、错误率、AI 延迟与成本、队列积压、发布/报名/完成漏斗。
- 链路：使用 OpenTelemetry，外部调用传播关联标识。
- 告警：认证异常、失败作业、对象扫描失败、风险规则异常和状态机冲突。

> 实现现状（⚠️）：上面四项里**只有探针这一层落地了**，日志、指标、链路与告警仍是目标。
>
> - `GET /health`（存活）：只回答“进程还在不在”，恒 `200` + `{status, service, utc}`，**刻意不探测任何依赖**——数据库挂了重启进程没有意义，存活探针被依赖拖死只会让问题更糟（本来能服务的实例被编排系统反复重启）。
> - `GET /health/ready`（就绪）：逐项探测依赖，只有**全部健康**才 `200`，任一不健康返回 `503` 并从负载均衡摘流量。响应带 `status`/`service`/`utc`/`durationMs` 与 `checks[]`，每项含 `name`/`status`（`ok`/`failed`/`skipped`）/`detail`/`durationMs`。
>   - `postgres`：能否连上，并且**有没有待应用的迁移**（`Migrate()` 失败或漏跑迁移都会被这一项抓到）。
>   - `redis`：只在 `notifications.fanout.enabled=true`（且配了连接串）时才参与，否则记 `skipped`——单实例部署没接 Redis 时不该因此被判不健康。
>   - `storage`：本机目录实现会**写入再删除**一个探针对象（`readiness/probe.txt`），确认目录可读写（只查存在性抓不到“没权限/磁盘满”）；S3 实现改为对该探测键做一次**存在性检查**，不发写请求、不碰业务对象——签名或凭据被拒、桶不存在都会在这里暴露。
> - 三项探测**并行执行**，单项超时由 `readiness.timeoutSeconds` 控制（默认 3 秒、可配 1–30）；超时按该项不健康处理，且**任何异常都被吞成 `failed` 而不是冒泡成 `500`**，因此就绪探针本身不会再变成一个新的故障点。
> - 还没做的是带请求 ID 的结构化日志、指标导出、OpenTelemetry 链路与告警规则；备份与恢复演练见 [备份、恢复与演练](../operations/backup-and-restore.md)。

## 10. 演进路径

只有当容量或团队边界有证据支持时再拆服务。较可能优先独立的部分是实时消息、文件处理、通知和 AI 编排；任务、报名、订单和支付应尽量保持强一致边界。
