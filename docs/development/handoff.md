# AIToHuman 项目交接文档

最后更新：2026-09-12

仓库：[suyuewuhen/AIToHuman](https://github.com/suyuewuhen/AIToHuman)

当前分支：`main`

最新已提交基线：`e45da76`（工作区干净，`main` 与 `origin/main` 同步）

本文面向接手开发、代码评审和本地联调人员。内容以已提交代码为准；上一版文档里“已提交基线 / 当前未提交实现”的双轨描述已经过时——所有多轮 AI、通知、会话与凭证实现都已提交。文中每条“已实现 / 未实现”的结论都对应第 11 节可复现的验证步骤。

## 1. 产品定位与当前闭环

AIToHuman 是“AI 任务管家 + 真人服务任务大厅”。用户不需要填写复杂表单，而是与 AI 一句一句对话；AI 每轮只询问一个关键问题，在后台逐步理解目标、地点或执行方式、时间、验收标准和重要限制。信息足够后才生成任务草稿，经用户确认悬赏并主动发布，由服务者按固定价格报名。

当前闭环：

```text
AI 多轮澄清（每轮一个问题）
→ 信息完整后生成结构化草稿
→ 用户检查草稿和固定悬赏
→ 发布到任务大厅
→ 服务者查看需求方评价并报名
→ 需求方查看服务者评价并选择
→ 自动创建 Accepted 订单并主动通知服务者
→ 双方在订单内沟通、服务者上传执行凭证
→ 需求方验收或驳回，驳回后服务者返工并重新提交
→ 双方评价，双方提交或 7 天后公开
```

尚未实现：真实支付和托管、实名认证、禁止任务风险拦截、真实的病毒/内容扫描服务商（协议已接好，只差选定/部署服务）、争议处理、取消与超时、精确地址访问审计、图片重新编码；运营后台目前只有配置页面，任务/用户检索与风险复核等仍未实现；多实例可靠投递（Redis backplane）也未落地。

## 2. 当前工作区状态

工作区干净：HEAD 为 `e45da76`，`main` 与 `origin/main` 一致，没有未提交修改。上一版交接文档描述的“多轮 AI 未提交实现”已经全部提交，本文不再区分“基线 / 未提交”两种状态。

从上一版基线 `f196850` 到当前 `e45da76` 的主要变化：

- 火山引擎请求改为 SSE 流式响应，单轮“一句话生成草稿”改为多轮需求澄清。
- 浏览器只显示 `assistantMessage`，后端不会把内部任务 JSON 作为增量发送给用户；`readyToDraft=false` 时右侧草稿保持锁定，不再用演示任务或默认字段兜底。
- AI 无活动超时 120 秒，每收到上游数据后重新计时；模型输出改为严格校验 + 可控重试（第 6 节）。
- 对话改为服务端持久化：新增 `Conversation` 聚合与 `conversations`、`conversation_messages` 两张表，AI 流端点按会话历史生成请求并落库回合，前端刷新后可恢复对话与草稿；`AiTaskPlanRequest` 由 `messages` 数组改为 `conversationId` + `userId` + `message`，旧前端不再兼容。
- 订单驳回后新增返工闭环（`ReworkCount`、`RejectionNote`），验收通过后同事务把任务从 `Assigned` 推进到 `Closed`。
- 启动建表改为 EF Core 迁移（含旧 `EnsureCreated` 库的自愈基线化），`tasks`/`orders` 增加 `Version` 乐观并发令牌，选人与验收放进显式事务。
- 通知骨干落地：版本化事件信封 + `notifications` 表兼做 Outbox + 后台派发重试 + 收件箱未读数；订单会话消息与未读、执行凭证文件上传与鉴权下载随后补齐。
- 前端补齐：对话恢复与新建、通知未读徽标、订单会话弹窗、执行凭证面板、大厅筛选与加载更多、执行地址输入与查看。

涉及的主要文件（可用 `git diff --stat f196850..e45da76` 复核）：

```text
README.md
backend/AIToHuman.Api/            AiPlanningService、AiPlanStreamWriter、AiTaskPlanValidator、
                                  Notifications/NotificationDispatcher、Program、appsettings.json
backend/AIToHuman.Application/    Common/（IUnitOfWork）、Conversations/、Notifications/、
                                  Orders/（OrderChatService、EvidenceService）、Tasks/TaskService
backend/AIToHuman.Contracts/      Conversations/、Notifications/、Orders/（消息与凭证）、Tasks/
backend/AIToHuman.Domain/         Common/UtcTimestamp、Conversations/、Notifications/、Orders/、Tasks/
backend/AIToHuman.Infrastructure/ Persistence/（TaskDbContext、EfUnitOfWork、15 个迁移）、
                                  Conversations/、Notifications/、Orders/（消息与凭证仓储）、
                                  Storage/（本机目录与 S3 兼容对象存储）
backend/tests/AIToHuman.Domain.Tests/      新增 ConversationTests、NotificationTests、
                                           OrderMessageTests、OrderEvidenceTests
backend/tests/AIToHuman.IntegrationTests/  新增工程：AI 协议与校验、SSE 写线、会话、事务、
                                           通知、订单会话、评价盲期、大厅检索与披露、凭证
frontend/src/App.vue
frontend/src/styles.css
frontend/src/api/                 ai、conversations、messages、evidence、tasks、notifications
docs/                             ai-planning、api/api-guidelines、architecture/domain-model、
                                  development/{handoff,roadmap,development-guide}
```

提交前仍要确认两点：`AIToHuman.Api.csproj` 里的 `UserSecretsId` 是本机值，别覆盖成别人的；`backend/AIToHuman.Api/appsettings.Development.json` 与任何 API Key 都没有进入 Git。

## 3. 已实现功能

### 账户与身份

- 邮箱、密码和显示名称注册，登录后返回 JWT Access Token。
- `GET /api/v1/auth/me` 查询当前用户。
- 同一账号可在需求方 `owner` 和服务者 `worker` 间切换；用户 ID 与历史归属不变。
- 密码使用 PBKDF2 加盐哈希保存。
- Development 环境提供固定合成会话；生产环境不允许该回退。

### AI 多轮澄清与草稿

- 对话历史以服务端为准：`POST /api/v1/conversations` 创建会话（含开场白），每轮只提交新消息，服务端按保存的历史构造模型请求并把完成的回合落库。
- 后端最多取 29 条历史加本轮消息共 30 条，每条 1 至 4000 个字符，最后一条必须是 `user`；超出的历史从最早的回合开始丢弃。
- 模型输出内部 JSON：`assistantMessage`、`readyToDraft`、`plan`。
- 后端从流式 JSON 中只提取 `assistantMessage` 发给页面；用户看不到字段名、内部分析和半截任务 JSON。
- AI 每轮应回应已有信息并只问一个最关键问题；用户不确定时应提供少量例子或选择。
- 草稿未完成时不能创建任务；完成后用户仍须检查任务字段和固定悬赏，再确认发布。
- 刷新页面或换设备后，前端用会话 ID 读取历史并恢复对话与草稿；`readyToDraft` 的回合连同 `plan` 一起持久化。
- 回合只在模型完整成功后才落库：AI 失败时对话保持原样，前端把刚才的输入还给用户重试。

### 任务、悬赏与报名

- 创建任务初始状态为 `ReadyToPublish`，预览确认后才发布。
- AI 建议悬赏不等于交易金额，最终悬赏由用户确认。
- 发布后、分配服务者前只允许加价，不允许降价。
- 服务者按固定悬赏报名，不支持竞价、互相报价或服务者改价。
- 任务大厅展示区域级地点、悬赏、验收标准和报名数，不应公开精确地址或联系方式。
- 大厅列表按截止时间升序游标分页（`items`/`nextCursor`/`hasMore`），支持按区域、悬赏区间筛选；非法区间或非法游标返回 `400` 与可读原因。
- 公开详情只对已发布及之后的状态开放；`ReadyToPublish` 草稿仅所有者可见，其他人（含匿名）一律 `404`，避免草稿内容泄露。
- 精确执行地址属于订单参与者层信息：所有者始终可读，被选中的服务者在订单成立后可读，已报名但未被选中的服务者与其他人返回 `403`；大厅与公开详情只暴露 `hasExecutionAddress` 布尔值，永远不含地址本身。
- 需求方可查看报名者和评价摘要，再选择服务者。

### 订单、通知与评价

- 选中服务者后自动创建订单，并固化任务标题和悬赏快照。
- “我的订单”分为“我发布的订单”和“我接取的任务”。
- 已实现 `Accepted → InProgress → Submitted → Approved`，需求方也可将 `Submitted` 驳回为 `Rejected`。
- 被驳回的订单可以由服务者返工：`POST /api/v1/orders/{id}/resume` 把 `Rejected` 退回 `InProgress`，服务者可再次提交，`ReworkCount` 累加、`RejectionNote` 保留最近一次驳回原因。
- 需求方验收通过时，同一用例内把订单置为 `Approved` 并把任务从 `Assigned` 推进到 `Closed`；任务关闭后不再出现在任务大厅，公开详情仍可查询。
- 服务者提交时必须填写执行凭证或完成说明；需求方驳回时必须填写原因。
- 订单批准后双方可以分别评价；双方都提交后立即公开，只有一方提交时在 7 天后公开。
- 每方每个订单只能评价一次，重复提交由应用层判定并返回 `422`“你已经评价过该订单。”（数据库 `(OrderId, ReviewerId)` 唯一索引作为兜底，不再依赖它抛出 500）。
- 任务大厅与公开详情读取公开评价摘要，因此服务者能看需求方信用、需求方能看服务者信用。
- 通知走 Outbox：业务事务内写入 `notifications` 行，后台 `NotificationDispatcher` 每 2 秒扫描未派发记录并通过 SignalR 推 `notification.created`，成功后标记派发时间；推送失败不标记，下个周期重试，进程重启也不会丢。
- 推送使用版本化信封 `{ eventId, type, version, occurredAt, payload }`；`eventId` 是幂等键（订单创建用订单 ID，状态变化由订单 ID + 状态推导），同一业务事件重复入队只保留一条。
- 收件箱与未读数来自 `GET /api/v1/notifications`；`POST /api/v1/notifications/read` 支持按 ID 或整体标记已读。
- 事件只是刷新提示：客户端收到推送后重新拉取订单与通知列表，断线重连也能通过 REST 恢复事实状态。

### 执行凭证（文件）

- 只有订单服务者可以上传，且订单必须处于 `InProgress` 或 `Submitted`。份数与单份大小上限来自运营配置（`evidence.maxPerOrder` / `evidence.maxSizeBytes`，默认 10 份 / 5 MB），硬上限是 50 份 / 25 MB，运营只能收紧不能突破。
- 扫描没给出结论时凭证保持 `Pending`（不可下载）：后台 `EvidenceRescanService` 每 60 秒重扫一批，同一条凭证退避 30 秒、最多尝试 5 次；文件已不在存储里则直接判定为 `Rejected`。用尽次数后保留待扫描状态并写明“停止自动重试”，交给人工处理，不会无声无息地永远挂着。
- 凭证接口会返回检查次数与最近一次说明（`scanAttempts` / `lastScanNote` / `scanExhausted`），前端凭证面板直接显示，因此“为什么不可下载”对双方都是可见的。
- 类型白名单为 JPEG / PNG / WebP / PDF，且**刻意不做成运营配置**（放开它等于允许上传可执行内容）。服务端不信客户端声明的 MIME：先做白名单校验，再按**文件签名**核对内容（PNG 头、JPEG SOI、WebP RIFF+WEBP、`%PDF`），不一致直接拒绝。
- 上传时默认剥离元数据（`evidence.stripMetadata`，默认开启）：JPEG 丢掉 EXIF/XMP（含 GPS）与注释段、PNG 丢掉 `tEXt`/`zTXt`/`iTXt`/`eXIf`/`tIME` 块、WebP 丢掉 `EXIF`/`XMP ` 子块并同步清掉 VP8X 的对应标志位与 RIFF 长度；**像素数据逐字节不动**，也不需要图像库（纯字节解析）。剥离结果写进 `metadataRemoved` 并落库，前端凭证面板会显示“已在上传时移除元数据：…”；需要完整取证链时可以把这个开关关掉，保留原始文件。PDF 不做处理（没有统一的元数据块结构）。
- 按人限速（`evidence.uploadsPerUserPerHour`，默认 60）：同一个上传者一小时内提交的凭证数上限，计数直接查数据库（`IX_evidence_UploadedBy_CreatedAt`），因此多实例部署也一致；超限返回 `422` 与“一小时内的凭证上传次数已达上限（N 次），请稍后再试。”
- 摘要与大小都按**真正存下来的内容**计算（先剥离、再核对签名与大小、最后算 SHA-256），所以剥离不会绕过单份上限。
- 存储键完全由系统生成（`{orderId:N}/{evidenceId:N}.{ext}`），原始文件名只作为展示元数据，永不参与路径拼接。
- 文件存私有存储：`local` 写本机目录（`ObjectStorage__LocalRoot`，默认应用目录下的 `evidence`），`s3` 走对象存储。下载前都要重新校验当前用户与订单关系以及扫描状态。
- 下载有两条路：默认 `GET /api/v1/evidence/{id}/content`（服务端鉴权后流式转发，两种存储都支持）；对象存储可用时还可以走 `GET /api/v1/evidence/{id}/download-url` 拿一条**短时直连签名地址**，浏览器直接去 Bucket 取字节，省掉一次转发。列表响应里的 `presignedDownloadAvailable` 告诉客户端该走哪条路。
- 直连地址的有效期由 `evidence.downloadUrlLifetimeSeconds` 控制（默认 120 秒，范围 5 至 900）。有效期、对象路径、`response-content-disposition` 都参与签名，客户端改任何一项都会被对象存储拒绝（403）；权限与扫描门禁的判定和流式下载完全一致。
- 扫描状态：`Pending`（不可下载）/`Clean`/`Rejected`，终态不可回退；被拒绝的内容不落库也不留在存储里。
- 扫描方式三选一（`evidence.scanner.provider`）：`none` 显式放行并打警告日志；`http` 把内容 POST 给配置的扫描服务；`clamav` 直连 clamd 的 INSTREAM 端口（`evidence.scanner.clamavHost` / `clamavPort`，默认 127.0.0.1:3310）。扫描不可用时按 `failMode` 处理（`closed` 保持待扫描、不可下载，`open` 放行）。三者都由 `SettingsEvidenceScanner` 按配置分派，因此在后台切换不需要重启。
- ClamAV 走的是官方 INSTREAM 协议：连接后发 `zINSTREAM\0`，随后是「4 字节大端长度 + 数据」分块（64 KB 一块），最后发零长度块结束；clamd 回 `stream: OK` / `stream: <签名> FOUND` / `... ERROR`。**内容不落临时文件**，也不依赖任何厂商 SDK。
- S3 兼容存储（`S3FileStorage`）：不依赖厂商 SDK，只用 `HttpClient` 加自己实现的 AWS Signature V4；路径风格请求 `{endpoint}/{bucket}/{prefix}{key}`，只签 `host`、`x-amz-content-sha256`、`x-amz-date`。上传、下载、存在性判断与删除四个操作齐全；密钥不全时按缺哪项报哪项，Bucket 不存在或密钥无权限时把 S3 的错误码翻译成可行动的说明。MinIO、阿里云 OSS、AWS S3 都能用，换服务商只改运营配置。
- 切换 `storage.provider` **不会迁移已有文件**：从 s3 切回 local 之后，之前写进 Bucket 的凭证在本机目录里找不到，下载会 `404`。生产切换要么保持同一 provider，要么先把对象搬过去。
- 尚未实现：图片像素级重新编码；真实的病毒库由部署方自己维护（协议已实现并验证，缺的是部署一个真实的 clamd 或用真实服务商替换 `http` 实现）；运营后台的风险规则引擎与运营页面里的任务/用户检索入口。

### 订单会话（聊天）

- 只有订单双方可以读写会话消息；非参与者返回 `403`，订单不存在返回 `404`。
- 消息内容 1 至 2000 字；消息写入与通知入队在同一个事务里完成，避免“消息发出但对方收不到提示”的中间态。
- 未读按“对方发来的且未标记已读”计算：单条消息只有一个 `ReadAt`，因为会话只有两个参与者。打开会话即标记已读。
- 订单列表会为每个订单返回会话未读数（`unreadMessageCount`），前端在“消息”按钮上显示徽标。新消息通过 `order.messageCreated` 通知推送，载荷带标题与 60 字预览，完整内容走 REST。

### 运营可配置的三方集成参数

- 设置目录（白名单）在 `backend/AIToHuman.Application/Settings/SettingCatalog.cs`：目前 25 个键，分 AI 服务商、对象存储、凭证上传、内容扫描四组。只有登记在册的键才能被后台读写，`ConnectionStrings__Postgres`、日志、密钥环路径这类部署级配置永远不会出现在配置表里。
- 生效值的解析顺序固定为「数据库覆盖 → 环境变量/配置文件 → 代码默认值」。删除覆盖记录就等于恢复默认，不需要额外的启用/停用开关。
- 运营接口（需管理员身份）：`GET /api/v1/admin/settings`、`GET /api/v1/admin/settings/{key}`、`PUT /api/v1/admin/settings/{key}`、`DELETE /api/v1/admin/settings/{key}`（恢复默认）、`POST /api/v1/admin/settings/{key}/test`（只读自检）、`GET /api/v1/admin/settings/audits`。
- 机密（`ai.apiKey`、`storage.s3.secretAccessKey`、`evidence.scanner.apiKey`）用 Data Protection 加密后落库，密文带 `dp1:` 前缀；接口只返回 `****末四位` 与指纹，审计记录同样只留掩码与指纹，明文只在服务端内存里出现。
- 写入成功后立即刷新进程内快照，消费方每次调用都重新读，所以改完下一轮对话/下一次上传就生效，不需要重启；另有 15 秒一次的后台轮询，用来跟上“绕过 API 直接改库”或其他实例的改动。
- 每次写入在同一事务里追加一条审计记录（键、动作、脱敏前后值、操作人、时间），覆盖行带 `Version` 并发令牌，`PUT` 可带 `expectedVersion`，冲突返回 `409`。
- 管理员名单来自部署配置 `Admin__UserIds` / `Admin__Emails`（也接受 `admin` 角色声明），刻意不放进可后台修改的配置表，避免任何能改配置的人把自己提权；没有配置管理员时运营接口对所有人返回 `403`。
- “测试连接”只做只读自检：本机存储目录是否可写、AI 服务与扫描服务是否可达且接受当前密钥。
- 前端入口：`GET /api/v1/auth/me` 会返回 `isAdmin`，只有管理员在顶栏看到“运营配置”。面板按分组列出配置项，标注来源（后台已改 / 部署配置 / 默认值）与是否机密，支持保存（带 `expectedVersion`，冲突时提示刷新后重试）、恢复默认、测试连接，以及“变更记录”页签。
- 机密项在页面上只显示掩码，输入框留空表示“不修改”；要清空必须点专门的“清空”按钮，避免把 `****1234` 当成新值写回去。
- 前端文件：`frontend/src/api/settings.ts`（接口客户端）与 `frontend/src/App.vue`（“运营配置”弹窗），样式在 `frontend/src/styles.css` 的 `.setting-*` 一节。

## 4. 已确定产品规则

1. 一个账号对应一个用户 ID，角色切换只改变当前操作权限。
2. AI 负责澄清、规划和建议，不得绕过用户确认发布任务。
3. AI 每轮只问一个问题，避免把表单问题清单伪装成聊天。
4. 内部任务梳理默认不可见；信息足够后才展示可检查的最终草稿。
5. 悬赏由用户结合 AI 建议确认；服务者不能竞价，用户只能在分配前主动加价。
6. 服务者先看需求方评价再报名，需求方先看服务者评价再选择。
7. 公开大厅仅展示区域级位置；精确地址和联系方式属于订单执行阶段私密信息。
8. 当前不接入真实支付，也不以模拟支付冒充合规交易能力。
9. 聊天、未读数和系统通知复用 SignalR 推送，但事实状态一律以 REST 为准：推送只是刷新提示，持久化与重试由 `notifications` 表（Outbox）保证，客户端断线重连后靠重新拉取恢复。

## 5. 技术架构与目录

```text
Vue 3 + TypeScript + Vite
        │ REST/JSON + SSE + SignalR
        ▼
ASP.NET Core 10 Minimal API / C#
        ├─ Api：HTTP、JWT、SSE、SignalR、火山引擎适配
        ├─ Application：任务、订单、评价和通知用例
        ├─ Domain：实体、值对象、状态与业务规则
        ├─ Infrastructure：EF Core、PostgreSQL、内存仓储
        └─ Contracts：跨层请求与响应 DTO
        ▼
PostgreSQL（当前开发事实来源）
```

```text
backend/AIToHuman.Api/                  # 路由、认证、AI SSE、SignalR Hub、通知派发、凭证重扫、运营配置接口
backend/AIToHuman.Application/         # TaskService、会话、订单会话、凭证、通知用例、设置目录与配置用例
backend/AIToHuman.Contracts/           # API record 与对话/通知/消息/凭证/设置 DTO
backend/AIToHuman.Domain/              # TaskItem、Order、Review、Conversation、OrderMessage、Notification、SystemSetting
backend/AIToHuman.Infrastructure/      # EF Core/PostgreSQL、内存仓储、本机文件存储、内容扫描适配
backend/tests/AIToHuman.Domain.Tests/  # 领域单元测试
backend/tests/AIToHuman.IntegrationTests/ # 用例与 AI 多轮协议/SSE 集成测试（上游替身 + 内存仓储）
frontend/src/App.vue                   # 对话工作台、草稿、大厅、订单、会话弹窗、凭证面板、评价与运营配置
frontend/src/api/ai.ts                 # SSE 客户端与多轮对话协议
frontend/src/api/conversations.ts       # 会话创建、读取与列表
frontend/src/api/messages.ts           # 订单会话消息与未读数
frontend/src/api/evidence.ts           # 凭证上传、列表与鉴权下载
frontend/src/api/settings.ts           # 运营配置：读取、写入、恢复默认、自检与变更记录
frontend/src/api/tasks.ts              # 任务、订单和评价 API
frontend/src/api/notifications.ts      # 通知 REST 与 SignalR 客户端
frontend/src/styles.css                # 全局响应式样式
```

依赖方向保持为 `Api → Application → Domain`，`Infrastructure → Application + Domain`。领域层不得引用 EF Core、HTTP、SignalR 或模型供应商代码。

## 6. AI 对话与 SSE 协议

接口：`POST /api/v1/ai/plan/stream`

请求示例：

```json
{
  "conversationId": "0f0b1a2c-...",
  "userId": "11111111-1111-1111-1111-111111111111",
  "message": "明天下午帮我取一份文件"
}
```

`userId` 只在 Development 合成会话下作为回退使用；携带 JWT 时以令牌中的用户为准，会话归属不匹配返回 `403`。

SSE 事件：

```text
event: delta
data: {"text":"可以，"}

event: delta
data: {"text":"你希望在哪里取件？"}

event: complete
data: {"turn":{"assistantMessage":"可以，你希望在哪里取件？","readyToDraft":false,"plan":null}}
```

模型输出没通过校验、服务端要重试这一轮时，会先插入一个清屏事件，页面必须丢弃已经显示的半截回复：

```text
event: restart
data: {"reason":"AI 返回的草稿缺少任务标题，请重试。"}
```

- `delta` 只能包含可见的自然语言回复。
- `complete.turn.readyToDraft=false` 时 `plan` 必须为 `null`。
- 信息完整后 `readyToDraft=true`，`plan` 包含标题、描述、区域、截止时间、验收标准、建议悬赏和空的澄清数组。
- `restart` 只出现在重试之前；收到后前端应清空本轮正在显示的流式文本，再接收后续 `delta`。
- 会话归属、消息长度这类可在开始前判定的问题仍然返回普通 HTTP 状态码：`403` 会话不属于当前用户、`404` 会话不存在、`422` 消息为空或超长。一旦开始写 SSE，模型侧的问题才通过 `event: error` 返回，不能再依赖 HTTP 状态码表达失败。
- 落库失败（例如数据库写入异常）也走流内 `error`，文案为“本轮对话保存失败，请重试。”；因为响应已经开始，不能用状态码表达。
- 前端必须同时处理流正常结束但没有 `complete` 的情况。
- 线格式由 `backend/AIToHuman.Api/AiPlanStreamWriter.cs` 统一写出，端点只负责事件分发；该文件与协议解析都有集成测试覆盖（见第 11 节）。
- 上游响应可能分片到任意字节边界，增量只从 `assistantMessage` 字段里提取；模型把整个 JSON 分片发送时，拼接后的增量必须等于最终 `assistantMessage`。
- 模型输出由 `backend/AIToHuman.Api/AiTaskPlanValidator.cs` 严格校验：`assistantMessage` 非空且不超过 4000 字；`readyToDraft=true` 时必须带 `plan`，且标题 1 至 80 字、描述与区域非空、验收标准至少 1 条且每条不超过 200 字、建议悬赏大于 0 且不超过 100000；验收标准与澄清分别截断到 6 条；`readyToDraft=false` 时即使模型带了 `plan` 也一律丢弃。字段缺失不再抛 `KeyNotFoundException`，而是给出可展示的原因。
- 校验失败最多重试一次（共 2 次尝试），失败原因会作为修复指令追加到对话尾部；超时和上游不可用不重试。两次都失败时用流内 `error` 返回校验原因。

火山引擎配置优先级遵循 ASP.NET Core 默认规则：环境变量和 User Secrets 会覆盖 `appsettings.json`。当前 `appsettings.json` 配置模型为 `glm-4-7-251222`、无活动超时为 120 秒；`AiPlanningService` 中的模型属性只在配置缺失时作为默认值。实际模型应以火山控制台中已启用的模型 ID 或 `ep-...` 推理接入点为准。

API Key 不得写入 Git：

```powershell
dotnet user-secrets --project backend/AIToHuman.Api set "VolcengineAI:ApiKey" "你的 API Key"
dotnet user-secrets --project backend/AIToHuman.Api set "VolcengineAI:Model" "控制台中的模型或 ep-... ID"
```

## 7. 本地开发

### 前置条件

- .NET SDK 10（根目录 `global.json`）。
- Node.js 24、npm 11。
- 本机 PostgreSQL；Docker 不是必需条件。
- PowerShell。
- 凭证文件默认写到应用目录下的 `evidence`；可用 `ObjectStorage__LocalRoot` 指定其他目录。

### PostgreSQL

当前本机开发连接：

```text
Host=localhost
Port=5432
Database=postgres
Username=postgres
Password=123456
```

连接串存放在被 Git 忽略的 `backend/AIToHuman.Api/appsettings.Development.json`，严禁提交。其他环境通过 `ConnectionStrings__Postgres` 覆盖。

### 启动后端

```powershell
dotnet restore AIToHuman.sln --configfile NuGet.Config
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project backend/AIToHuman.Api --launch-profile http
```

确认后端：

```powershell
Invoke-RestMethod http://127.0.0.1:5188/health
Invoke-RestMethod http://127.0.0.1:5188/api/v1/session/dev
```

### 启动前端

另开 PowerShell：

```powershell
Set-Location frontend
npm install
npm run dev
```

访问 <http://localhost:5173>。Vite 将 `/api`、`/health` 和 `/hubs` 代理到 `http://127.0.0.1:5188`。必须先启动后端，否则会出现 `ECONNREFUSED 127.0.0.1:5188`，AI、登录、任务和 SignalR 都不可用。

如果端口已占用：

```powershell
netstat -ano | Select-String ':5188|:5173'
```

不要直接结束不确定归属的进程；先确认是否为本项目旧实例。代码更新后必须重启后端，旧进程不会自动加载新程序集。

未配置 PostgreSQL 时，任务和订单可回退到内存仓储，但注册、登录和角色切换不可用，只能使用 Development 合成会话。仓库提供的 Docker Compose 数据库使用不同的用户和数据库，需要显式覆盖连接串。

## 8. SignalR 联调

- Hub：`/hubs/notifications`。
- 登录用户通过 JWT `NameIdentifier` 加入 `user:{userId:N}` 组。
- Development 可用 `?userId=...` 连接合成用户；生产环境拒绝该回退。
- JWT 通过 SignalR `access_token` 查询参数传递，后端只在 `/hubs` 路径读取。
- 通知不再 fire-and-forget：业务事务内写入 `notifications` 行（该表兼作 Outbox），后台 `NotificationDispatcher` 每 2 秒扫描待派发记录，推送成功后写 `DispatchedAt`，失败留到下一轮，服务重启也不丢。
- 推送的事件类型目前有 `order.created`（选人）、`order.statusChanged`（开始、提交、驳回、返工、验收）和 `order.messageCreated`（新消息）；收件箱与未读数走 REST，推送只是刷新提示。
- 派发仍是单进程轮询：多实例部署需要 Redis backplane 或共享消息总线，否则每个实例只推自己派发出去的记录。

双浏览器验证：A 以 owner 发布任务，B 以 worker 报名，A 选择 B；B 无需刷新应立即收到通知并更新“我接取的任务”。Network 面板应看到 `/hubs/notifications` 的 WebSocket 或长轮询连接。

## 9. API 与 Hub 清单

所有业务 JSON 使用 camelCase。

| 方法 | 路径 | 认证/说明 |
| --- | --- | --- |
| GET | `/health` | 健康检查 |
| GET | `/api/v1/session/dev` | 仅 Development 合成会话 |
| POST | `/api/v1/auth/register` | 匿名注册 |
| POST | `/api/v1/auth/login` | 匿名登录 |
| POST | `/api/v1/auth/switch-role` | JWT，切换 owner/worker |
| GET | `/api/v1/auth/me` | JWT，当前用户；带 `isAdmin` 供前端决定是否展示运营配置入口 |
| POST | `/api/v1/conversations` | 创建对话会话，写入开场白 |
| GET | `/api/v1/conversations/{id}?userId=...` | 读取历史与当前草稿，用于刷新恢复；仅所有者可读 |
| GET | `/api/v1/conversations?userId=...&limit=...` | 该用户的会话列表（含预览与是否有草稿） |
| POST | `/api/v1/ai/plan/stream` | 多轮 AI 澄清，SSE；按 `conversationId` 读取服务端历史并落库完成的回合 |
| POST | `/api/v1/reward-suggestions` | 本地规则悬赏建议 |
| GET | `/api/v1/tasks?district=&minReward=&maxReward=&limit=&cursor=` | 大厅列表，游标分页与筛选 |
| GET | `/api/v1/tasks/mine?ownerId=...` | 所有者查看自己的全部任务及状态（草稿、已发布、已分配、已结束） |
| GET | `/api/v1/tasks/{id}` | 公开任务详情；草稿仅所有者可见，其他人 404 |
| GET | `/api/v1/tasks/{id}/execution-address` | 精确执行地址；仅所有者与被选中的服务者 |
| POST | `/api/v1/tasks` | owner 创建任务草稿 |
| POST | `/api/v1/tasks/{id}/publish` | 所有者发布 |
| POST | `/api/v1/tasks/{id}/increase-reward` | 分配前加价 |
| POST | `/api/v1/tasks/{id}/applications` | worker 报名 |
| GET | `/api/v1/tasks/{id}/applications?ownerId=...` | 所有者查看报名 |
| POST | `/api/v1/tasks/{id}/applications/{applicationId}/select` | 选人并创建订单 |
| GET | `/api/v1/tasks/{id}/order` | 当前实现的订单查询入口，需参与者身份 |
| GET | `/api/v1/orders?userId=...` | 当前用户订单列表，含各订单会话未读数 |
| POST | `/api/v1/orders/{id}/start` | worker 开始履约 |
| POST | `/api/v1/orders/{id}/submit` | worker 提交凭证或完成说明 |
| POST | `/api/v1/orders/{id}/approve` | owner 验收通过 |
| POST | `/api/v1/orders/{id}/reject` | owner 驳回并说明原因 |
| POST | `/api/v1/orders/{id}/resume` | worker 按驳回原因返工，`Rejected → InProgress` |
| GET | `/api/v1/orders/{id}/messages?userId=...&limit=...` | 订单会话消息与未读数；仅参与者 |
| POST | `/api/v1/orders/{id}/messages` | 发送会话消息，同时通知对方 |
| POST | `/api/v1/orders/{id}/messages/read?userId=...` | 标记会话已读，返回最新未读数 |
| GET | `/api/v1/orders/{id}/evidence?userId=...` | 执行凭证列表；仅参与者 |
| POST | `/api/v1/orders/{id}/evidence` | multipart 上传凭证；仅订单服务者 |
| GET | `/api/v1/evidence/{id}/content?userId=...` | 鉴权后流式下载；仅参与者且扫描通过 |
| GET | `/api/v1/evidence/{id}/download-url?userId=...` | 短时直连下载地址（仅对象存储；本机目录返回 `422` 与可读原因） |
| GET | `/api/v1/orders/{id}/reviews` | 参与者读取双向评价 |
| POST | `/api/v1/orders/{id}/reviews` | Approved 后提交评价 |
| GET | `/api/v1/users/{id}/review-summary` | 用户公开评价摘要 |
| GET | `/api/v1/notifications?userId=...&limit=...` | 收件箱列表与未读数 |
| POST | `/api/v1/notifications/read` | 按 ID 或整体标记已读，返回最新未读数 |
| GET | `/api/v1/admin/settings` | 运营配置列表（机密只返回掩码与指纹）；需管理员 |
| GET | `/api/v1/admin/settings/{key}` | 单条配置及来源（database/configuration/default） |
| PUT | `/api/v1/admin/settings/{key}` | 写入覆盖值，可带 `expectedVersion`；未注册键 400、非法值 422、版本冲突 409 |
| DELETE | `/api/v1/admin/settings/{key}` | 删除覆盖，恢复环境变量或默认值 |
| POST | `/api/v1/admin/settings/{key}/test` | 只读自检（目录可写、服务可达） |
| GET | `/api/v1/admin/settings/audits?limit=` | 配置变更审计，按时间倒序 |
| GET | `/api/v1/admin/audits?limit=` | 运营操作审计（人工下架等），按时间倒序 |
| GET | `/api/v1/admin/tasks?keyword=&status=&limit=` | 跨所有者检索任务（标题/描述/区域模糊匹配 + 状态过滤） |
| GET | `/api/v1/admin/tasks/{id}` | 任务详情（含报名者与订单状态） |
| POST | `/api/v1/admin/tasks/{id}/cancel` | 人工下架（必须给原因，原因进运营审计；已分配任务返回 422） |
| GET | `/api/v1/admin/users?keyword=&limit=` | 用户检索（邮箱/昵称）；无 PostgreSQL 时返回空列表 |
| GET/WS | `/hubs/notifications` | SignalR 主动通知；推送 `notification.created` 信封 |

普通 API 错误使用 Problem Details，主要映射为 `400`、`401`、`403`、`404`、`409`、`422`、`502` 和 `504`。`409` 既用于资源冲突（例如邮箱已注册），也用于乐观并发冲突。AI SSE 在响应开始后的错误使用流内 `error` 事件。

## 10. 数据库、权限与状态

Development + PostgreSQL 启动时改为应用 EF Core 迁移（`Database.Migrate()`），不再使用 `EnsureCreated()` 和幂等建表 SQL。生产环境由部署流程执行迁移，不在应用启动时自动迁移。

迁移历史（16 个）：`AddUsers` → `AddOrders` → `AddOrderEvidence` → `AddReviews` → `AddOrderRework` → `AddConversations` → `AddTaskTables` → `AddConcurrencyTokens` → `AddNotifications` → `AddOrderMessages` → `AddTaskExecutionAddress` → `AddEvidence` → `AddSystemSettings` → `AddEvidenceScanAttempts` → `AddEvidenceMetadataRemoved` → `AddAdminAudit`。其中 `AddTaskTables` 补上了此前只由 `EnsureCreated()` 建出、从未纳入迁移的 `tasks` 与 `task_applications` 两张核心表；`AddConcurrencyTokens` 给 `tasks`/`orders` 加 `Version` 乐观并发令牌；`AddTaskExecutionAddress` 给 `tasks` 加参与者层的精确执行地址；`AddEvidence` 建 `evidence` 表；`AddSystemSettings` 建运营配置的两张表；`AddEvidenceScanAttempts` 给 `evidence` 补上扫描尝试次数、最近一次说明与尝试时间；`AddEvidenceMetadataRemoved` 补上“已移除元数据”说明与按人限速用的索引；`AddAdminAudit` 建运营操作审计表 `admin_audit_entries`（人工下架等动作只追加留痕）。

早期 `EnsureCreated()` 建出的本地库没有迁移历史记录。启动逻辑会检测这种情况：先用模型对比物理表的「表名.列名」，只有结构完全对得上时，才把当时已有的迁移整体标记为已应用并打警告日志；一旦缺表或缺列就直接报错说明缺了什么，提示删除重建，避免在错误的 schema 上继续运行。基线化之后新增的迁移会正常应用——例如 `AddConcurrencyTokens` 就是在基线化之后自动补上的 `Version` 列。目标库不存在时由 `Migrate()` 负责建库。

对话数据：`conversations` 属于单个用户，`conversation_messages` 按 `(ConversationId, Sequence)` 唯一，序号在历史截断后仍保持单调递增。消息写入后不可变，仓储只做新增与删除。

订单会话数据：`order_messages` 按 `(OrderId, CreatedAt)` 建索引，只有订单双方可读写；未读用“发送者不是查看者且 `ReadAt` 为空”判定，因此一条消息只有一个已读时间。

任务隐私分层：`tasks.District` 是公开层（大厅展示）；`tasks.ExecutionAddress`（≤200 字，可空）属于参与者层，只在订单成立后按角色披露，且从不进入大厅列表与公开详情。精确地址的访问审计尚未实现。

凭证存储：元数据在 `evidence` 表（`StorageKey` 唯一索引、`ScanStatus` 为字符串），文件内容在私有存储里。`IFileStorage` 是可插拔抽象，由 `SettingsFileStorage` 按 `storage.provider` 分派：`local` 用本机目录（路径解析限制在根目录内），`s3` 用 `S3FileStorage`（自研 SigV4 签名、路径风格、无 SDK 依赖）。切到 `s3` 时如果关键参数没填全会直接报错（`422` 且写明缺哪一项），不会静默退回本机目录；Bucket 需要事先创建且保持私有。`S3FileStorage` 另外实现了可选的 `IPresignedFileStorage`（签发短时直连下载地址），本机目录没有这个能力，因此客户端要靠 `presignedDownloadAvailable` 判断。`IEvidenceScanner` 由 `HttpEvidenceScanner` 实现，按 `evidence.scanner.provider` 在“显式放行”与“调用外部扫描服务”之间切换。

运营配置数据：`system_settings` 一行就是“某个设置键被后台覆盖过”，主键是配置键，`Version` 是乐观并发令牌；`system_setting_audits` 只追加，记录键、动作、脱敏前后的值与操作人。机密值在 `system_settings.Value` 里是 Data Protection 密文（`dp1:` 前缀），加密密钥环由部署环境提供（`DataProtection__KeysPath`），不入库；管理员名单来自部署配置 `Admin__UserIds`/`Admin__Emails`。审计按发生时间倒序返回，同一时刻写入的多条记录之间的先后顺序不做保证。

权限原则：

- `owner`：创建、发布、加价、查看报名、选择服务者、验收或驳回。
- `worker`：报名、开始订单、提交凭证。
- 订单和订单评价：仅订单双方可查看或操作。
- 对话：仅会话所有者可以读取或追加，越权返回 `403`。
- 前端隐藏按钮不构成安全边界，后端必须继续校验 JWT 身份、角色和资源归属。
- `ownerId`/`workerId`/`actorId` 这类请求字段只在 Development 合成会话下作为回退；携带 JWT 时以令牌身份为准，因此不强制要求查询参数。

并发与事务：

- `tasks` 与 `orders` 各有一个 `Version` 整数列作为乐观并发令牌（迁移 `AddConcurrencyTokens`），仓储每次 `Save` 自增。并发加价、并发选人、并发提交或验收时，后写入者拿到 `DbUpdateConcurrencyException`，API 返回 `409` 与“该任务或订单刚刚被其他人更新，请刷新后重试。”。
- 令牌依赖仓储的 `Get` 保持 EF 跟踪状态：`Save` 用「业务判断时读到的版本」做 WHERE 条件。把 `Get` 改回 `AsNoTracking` 会让并发保护静默失效。
- 选人（写任务 + 建订单）与验收（写订单 + 关任务）通过 `IUnitOfWork` 放进同一个数据库事务，失败整体回滚；内存仓储实现为空操作。
- `orders.TaskId` 的唯一索引是最后一道防线，保证一个任务最多一个订单。

通知与 Outbox：

- `notifications` 一张表同时承担持久化通知与 Outbox：`DispatchedAt` 为空即待推送，`ReadAt` 为空即未读，`EventId` 上有唯一索引作为幂等兜底。
- 写入发生在业务事务内（`IUnitOfWork`），所以不会出现“状态变了但通知没落库”；派发由 `NotificationDispatcher`（`BackgroundService`）完成，失败自动重试。后续如果引入 Hangfire，可以直接把派发换成作业而不是自建轮询。
- 幂等判断用的是「先查 EventId 再插入」；并发下极小概率撞到唯一索引会让该业务事务失败，用户重试即可。

```text
Task:  ReadyToPublish → Published → Assigned → Closed
                         └→ Expired / Cancelled（领域枚举存在，流程未完整接入）

Order: Accepted → InProgress → Submitted → Approved
                      ↑              │
                      └── Rejected ←─┘   （POST /orders/{id}/resume 返工）
                         └→ Disputed / Cancelled（枚举存在，接口未实现）
```

当前限制：返工期间任务保持 `Assigned`，不会重新出现在大厅；订单 `Approved` 后任务自动 `Closed`，但返工次数没有上限，也没有超时或期限约束；取消、争议和超时尚无完整用例。

## 11. 验证结果与命令

2026-09-12 整理的验证记录。第一批条目是在提交 `e45da76` 上做的（工作区干净），最近两轮（运营配置、凭证扫描闭环）的结果按“本轮（…）新增验证”分段列在后面；每条都写明了当时的环境与提交。

- `dotnet build AIToHuman.sln --no-restore`：通过，0 警告、0 错误。
- 领域测试：58/58 通过。覆盖任务加价与报名约束、执行地址校验与分阶段披露、订单履约与返工闭环、批准后关单、对话回合不变量与历史截断、通知字段校验与标记幂等、订单会话消息校验与未读语义、执行凭证的类型/大小/签名校验与扫描状态机、本地偏移量时间归一化和角色校验文案。
- 集成测试：114/114 通过（`AIToHuman.IntegrationTests`）。覆盖 AI 多轮协议、输出严格校验与重试、SSE 线格式、会话用例、事务边界、通知骨干、订单会话、评价盲期、大厅分页筛选与地址披露，以及执行凭证（上传/下载/权限/扫描门禁/上限）。
- 通知端到端（真实 PostgreSQL + 真实 WebSocket 客户端）：连上 `/hubs/notifications` 后选人 → 收到 `{"type":1,"target":"notification.created","arguments":[{eventId,type:"order.created",version:1,occurredAt,payload:{orderId,status,title}}]}`；`GET /api/v1/notifications` 返回 1 条未读，`POST /read` 返回 `{"unreadCount":0}` 且 `readAt` 已写入；无关用户收件箱为空；服务者开始执行并提交后，需求方收到 2 条 `order.statusChanged`，最新状态 `Submitted`。
- 订单会话端到端（真实 PostgreSQL）：需求方连发两条 → 服务者视角消息 2 条、未读 2、订单列表徽标 `unreadMessageCount=2`、收到 2 条 `order.messageCreated` 且预览为最后一条内容；服务者回复后标记已读返回 `{"unreadCount":0}`，需求方此时未读 1（服务者那条）；无关用户读取与发送均 `403`。
- 分页与披露端到端（真实 PostgreSQL）：5 个同截止时间任务 + 历史任务共 9 条，`limit=2` 逐页翻完 9 条且无重复；`district` 与 `minReward/maxReward` 筛选各命中 2/3 条；非法区间与非法游标返回 `400` 与中文原因；草稿详情所有者 `200`、他人与匿名 `404`；执行地址选人前仅所有者 `200`、报名者 `403`，选人后被选中 `200`、未被选中 `403`；大厅与公开详情序列化结果中不含地址文本。
- 执行凭证端到端（真实 PostgreSQL + 本机存储目录，multipart 上传 67 字节 PNG）：上传 `200`（`scanStatus=Clean`、SHA-256 摘要、`isDownloadable=true`）→ 需求方列表 1 条 → 下载 `200`、字节完全一致、`Content-Type: image/png`、`X-Content-Type-Options: nosniff`、`Content-Disposition` 为系统生成的 `evidence-<id>.png`；无关用户列表与下载均 `403`；需求方上传 `403`；`text/plain` 与伪造 PNG 各返回 `422` 与对应原因；5 MB + 1 字节返回 `413`；落盘路径为 `<根目录>/<orderId>/<evidenceId>.png`，不含任何用户输入。
- `npm run typecheck`：通过。
- Vite 生产构建：通过；若默认 `frontend/dist` 被运行中的进程占用，先停止该进程或输出到临时目录。
- EF Core 迁移：当前共 14 个（清单见第 10 节）；本轮新增 `AddOrderRework`（`orders` 两列）、`AddConversations`（会话两表）、`AddTaskTables`（补上此前缺失的 `tasks`、`task_applications`）、`AddConcurrencyTokens`（`Version` 列）、`AddNotifications`、`AddOrderMessages`、`AddTaskExecutionAddress`、`AddEvidence`、`AddSystemSettings`、`AddEvidenceScanAttempts`（扫描尝试次数与说明）。
- 全新数据库路径（**最新一次，含全部 14 个迁移**）：另起空库 `aitohuman_evidence_check` 启动 API，`Migrate()` 从零建库并应用全部 14 个迁移，随后在该库上完成下面的凭证扫描闭环用例。
- 全新数据库路径（2026-09-12，含 `AddSystemSettings`）：空库 `aitohuman_settings_check2` 启动 API，直接查 `__EFMigrationsHistory` 得到 `migrations_applied=13`，最后一条是 `20260912083117_AddSystemSettings`；随后在同一库上完成运营配置端到端用例。
- 全新数据库路径（2026-09-12 复核，当时 12 个迁移）：空库 `aitohuman_migration_check_v2` 启动 API，日志按顺序打印 12 条 `Applying migration '...'`，`/health` 返回 `healthy`。
- 全新数据库路径（更早一版，当时 8 个迁移）：空库 `aitohuman_migration_check` 上跑通任务创建、发布、报名与会话创建，`dotnet ef migrations list` 显示全部已应用、无待执行。
- 既有开发库路径：用原来的 `postgres` 库启动，日志出现“检测到由早期 EnsureCreated 建出的数据库，已把 N 个迁移记为已应用”，随后 `No migrations were applied. The database is already up to date.`；原有数据仍可正常读取。
- 既有开发库自愈：基线化之后新增的 `AddConcurrencyTokens` 会自动应用（补上 `Version` 列），随后创建任务、发布、报名、选人、开始执行全部正常，8 条既有会话记录完好；之后的 `AddNotifications`、`AddOrderMessages`、`AddTaskExecutionAddress`、`AddEvidence` 也在同一开发库上自动应用（第 426 至 429 行的通知、会话、分页与凭证端到端用例都跑在该库上）。
- 并发端到端（真实 PostgreSQL + 12 个并行请求）：同一任务被 12 个请求同时选人 → `200×1、409×2、422×9`，该任务只有 1 个订单、只有 1 条报名被选中；同一服务者 12 个并发报名 → `200×1、409×3、422×8`，最终只有 1 条报名记录。修复前同样的用例会出现 500 且可能多写订单。
- 并发验收：两个请求同时验收同一订单 → `200 + 422`，订单 `Approved`、任务 `Closed`，没有重复写入。
- JWT 全链路（注册两个账号，不带 `ownerId` 查询参数）：创建任务 `201` → 发布 `200` → 服务者报名 `200` → 选人 `200` → `GET /tasks/{id}/order` 需求方 `200`、服务者 `200`、无关用户 `403`；订单列表无同任务重复。
- 订单端到端联调（Development + 本机 PostgreSQL）：创建任务（`deadline` 带 `+08:00`）→ 发布 → 报名 → 选人 → 开始 → 提交 → 驳回 → 返工 → 再次提交 → 验收通过，全部符合预期；批准后任务为 `Closed` 且不再出现在大厅；越权返工返回 `422`。
- 对话端到端联调（本机 SSE 替身作为上游 + 真实 PostgreSQL 落库）：创建会话 → 第一轮流式返回 `delta, delta, complete` → 重新读取得到 `assistant,user,assistant` 三条消息且草稿完整（标题、区域、悬赏、截止时间、验收标准）→ 第二轮后为 5 条，历史在服务端累积；上游不可用时返回流内 `event: error` 且消息数保持不变；其他用户读取会话返回 `403`，会话不存在返回 `404`，超长消息返回 `422`。
- `POST /api/v1/ai/plan/stream`：响应为 `text/event-stream`；开始写流之后的模型或落库失败都以流内 `event: error` 返回。
- 输出校验与重试端到端联调（替身上游第一次返回缺少标题的草稿、第二次返回完整草稿）：页面收到 `delta`（第一次的回复）→ `restart`（原因“AI 返回的草稿缺少任务标题，请重试。”）→ 第二次的 `delta` → `complete`（完整草稿）；重新读取会话得到 3 条消息，且只保留了重试后的那一轮回复与草稿。

本轮（运营可配置三方参数）新增验证：

- 运营配置端到端（真实 PostgreSQL + 真实 Data Protection 密钥环 + 真实 JWT）：匿名读配置 `401`、服务者读配置 `403`、管理员读配置 `200`（当时目录是 18 个键，现在是 20 个）；把 `ai.apiKey` 写成 `sk-e2e-abcdef123456` 后接口返回 `****3456`（`source=database`、`overrideVersion=1`、指纹 `7ab5f1bce26a`）；用过期版本提交返回 `409`，超范围整数与非法枚举返回 `422`，未注册键（`ConnectionStrings.Postgres`）返回 `400` 且不落库。
- 机密落库形态（直查数据库）：`system_settings` 里 `storage.s3.secretAccessKey` 的原始值形如 `dp1:CfDJ8JO_Jf94B9pDvePGjLigiTsAd3QS9lmc…`，即 Data Protection 密文；`system_setting_audits` 里只有脱敏结果：`'(未设置)' -> '****3456(7ab5f1bce26a)'`，操作人就是发起修改的管理员 ID，明文不出现在任何响应与审计里。
- 恢复默认：`DELETE /api/v1/admin/settings/ai.apiKey` 后接口回到 `****9999`（来自 `VolcengineAI__ApiKey`）、`source=configuration`、`hasOverride=false`，并追加一条 `Reset` 审计。
- 自检接口：`storage.localRoot/test` 返回 `ok=true` 与实际目录；`evidence.scanner.provider/test` 在 `provider=none` 时明确提示“凭证会直接放行，生产建议切换为 http”。
- 配置热更新（集成测试）：同一个 `AiPlanningService` 实例连续两轮对话，第二轮改用新模型与新密钥，请求体与 `Authorization` 头随之变化，证明配置不是构造时读一次的缓存值；存储根目录改到新路径后，同一个 `LocalFileStorage` 实例的下一次写入就落在新目录。

本轮（运营配置页面）新增验证：

- `/api/v1/auth/me` 在真实 JWT 下对管理员返回 `isAdmin=true`、对普通服务者返回 `isAdmin=false`，页面入口据此显隐；`GET /api/v1/admin/settings` 返回 18 项、三个分组（AI 服务商 / 对象存储 / 内容扫描）。
- 页面链路：`npm run typecheck`（strict + `noUncheckedIndexedAccess`，vue-tsc 会一起校验模板绑定）与 Vite 生产构建均通过。
- 说明：本机没有 Playwright 与浏览器驱动，离线也装不上，所以**浏览器交互没有自动化用例**，界面部分只做了构建校验与第 13 节的人工清单。

本轮（凭证扫描闭环）新增验证：

- 扫描闭环端到端（真实 PostgreSQL + 真实 HTTP + 本地 TCP 扫描替身）：上传 67 字节 PNG 时替身返回 `pending` → 接口 `200`、`scanStatus=Pending`、`scanAttempts=1`、`isDownloadable=false`、下载 `403`；把替身改成 `clean` 后，后台重扫在 60 秒那一轮把它变为 `Clean`（`scanAttempts=2`、说明“重新扫描通过。”），需求方再下载拿到完全一致的 67 字节。替身日志显示两次调用依次是 `pending`、`clean`。
- 可配置上传上限生效：把 `evidence.maxSizeBytes` 改成 1024 后上传 2048 字节返回 `413 凭证大小不能超过 1 KB。`，且磁盘上不留文件；`DELETE` 该配置后回到默认 5242880（`source=default`）。
- 运营配置目录当时共 20 个键、四个分组：AI 服务商 / 对象存储 / 凭证上传 / 内容扫描（现在是 25 个：凭证上传组多了直连下载有效期、元数据开关、按人配额，内容扫描组多了 ClamAV 的地址与端口）。
- 全新数据库：同一轮启动时 `Migrate()` 从零建库并应用全部 14 个迁移，随后在该库上完成上面的用例。

本轮（S3 兼容对象存储）新增验证：

- 对象存储端到端（真实 PostgreSQL + 本机 MinIO `127.0.0.1:9000` + 独立客户端 `mc.exe` 交叉核对）：`storage.provider=s3` 下通过 API 上传 67 字节 PNG → `200`、`scanStatus=Clean`、SHA-256 `ebf4f635…9d2a`；`mc ls --recursive` 看到对象落在 `evidence/<orderId:N>/<evidenceId:N>.png`、大小 67 B、Bucket 保持 private；`mc stat` 与 `mc cp` 拉回的字节哈希与上传完全一致；API 的鉴权下载同样返回 67 字节且哈希一致；整个过程本机目录里 **0 个文件**（确认没有静默退回 local）。
- 删除路径：把扫描方式切成 `http` 并指向一个返回 `rejected` 的本地替身 → 上传返回 `422 凭证未通过安全检查，已拒绝保存。`，`mc ls` 的对象数量 **前后不变**（被拒绝的对象确实从 MinIO 删掉了），替身日志显示恰好一次扫描调用。
- 签名正确性说明：AWS SigV4 是自研实现（不依赖 SDK），因此“签名是否被真实服务接受”只能由真实服务回答——上面的 `200` 与对象落桶就是 MinIO 独立校验通过的结果；单元测试另外固定了请求形态（`Credential=minioadmin/<日期>/us-east-1/s3/aws4_request`、`SignedHeaders=host;x-amz-content-sha256;x-amz-date`、载荷摘要、固定时钟下签名可复现、换密钥签名变化、403/404 映射、缺配置不发请求）。
- 短时直连下载地址端到端（真实 MinIO）：`GET /evidence/{id}/download-url` 返回带 `X-Amz-Signature` 的地址（`expiresAt` = 签发时刻 + 120 秒）；用**不带任何鉴权头的普通 HTTP GET**（等价于浏览器直接用这条地址）取回 67 字节且哈希与上传一致，响应头是 `Content-Disposition: attachment; filename="evidence-<id>.png"`（签名里的附件名参数生效）。
- 防篡改与有效期（真实 MinIO 自己判定）：把签名末位改掉 → `403`；把路径里的对象 ID 换掉 → `403`（说明路径也在签名覆盖内）；把有效期配成 5 秒，签发后立刻取 → `200`，8 秒后再取 → `403`。非订单参与者申请地址 → `403`。
- 不支持时的表现：把 `storage.provider` 切回 `local` 后，列表里 `presignedDownloadAvailable=false`，申请地址返回 `422 当前是本机目录存储，不能签发短时直连下载地址：请改用鉴权后的 /content 接口下载，或把 storage.provider 切到 s3。`（顺带修掉了“存储类错误文案被通用兜底吃掉”的问题，见下方修复）。

本轮（元数据剥离、按人限速与 ClamAV）新增验证：

- 元数据剥离端到端（真实 PostgreSQL + 本机目录存储）：上传 107 字节、带 `tEXt`（内含 `GPS 39.9042,116.4074`）的 PNG → 接口返回 `200`、`metadataRemoved=PNG tEXt`、`sizeBytes=67`（即剥离后的长度）；需求方下载 67 字节，内容里**不含 `GPS` 也不含 `tEXt`**，而 `IDAT`（像素数据）保留。
- ClamAV 协议端到端（本机 clamd 协议替身，独立记录它收到了什么）：替身日志显示 `command=zINSTREAM.`（z 前缀 + NUL 结尾的命令）与 `payload=67`（**剥离后的字节**，证明剥离发生在扫描之前）；干净内容收到 `stream: OK` → 上传 `200`/`Clean`；把 EICAR 测试串藏进 IDAT 的上传（剥离后 125 字节）收到 `stream: Eicar-Test-Signature FOUND` → 上传 `422 凭证未通过安全检查，已拒绝保存。`，且存储目录里的文件数**前后不变**（被拒绝的内容没有留下）。
- 按人限速端到端（真实 PostgreSQL）：把 `evidence.uploadsPerUserPerHour` 配成 3 → 第 2、3 次上传 `200`，第 4 次返回 `422 一小时内的凭证上传次数已达上限（3 次），请稍后再试。`
- 顺带发现并修掉一个只有真机启动才会暴露的问题：`ClamAvEvidenceScanner` 一开始注册成单例，而它依赖 scoped 的 `IFileStorage`，DI 校验会在启动时直接拒绝（`Cannot consume scoped service ... from singleton`）。单元测试看不到这个，只有把 API 真正拉起来才会报。

本轮修复：

- 运营管理员名单的读取顺序有缺陷：`appsettings` 里写成数组（`Admin:Emails:0`）时，优先级更高的环境变量标量 `Admin__Emails` 会被数组子项盖掉，表现为“明明配了管理员却仍然 403”。现在标量优先、数组兜底，并补了对应测试。这个缺陷是端到端联调时才暴露的，纯单元测试用的是干净的配置源，覆盖不到。
- 存储类的用户可见错误原本用 `InvalidOperationException`，而 API 的兜底映射不会把它的文案返回给调用方，运营只会看到“请求暂时无法处理。”。现在对象存储缺配置 / 不可达 / 被拒绝、以及“本机存储不支持直连下载”都改用 `DomainException`（`422` 且原样返回中文原因），例如“对象存储还没有配置完整：请在运营后台补齐 storage.s3.bucket、storage.s3.secretAccessKey。”。

本轮修复：

- 客户端或 AI 提交带本地偏移量（如 `+08:00`）的 `deadline` 时，PostgreSQL 写入抛出 `ArgumentException`，创建任务返回 `500`。现在领域层统一归一化为 UTC（`UtcTimestamp.Normalize`），并由单元测试固化。
- 会话 EF 仓储最初把新消息记录加入已跟踪的导航集合，EF 因主键已有值而生成 `UPDATE` 并触发 `DbUpdateConcurrencyException`。改为显式按 Id 差集新增/删除，并把落库失败纳入流内错误文案。该缺陷由上面的对话端到端联调发现，纯单元测试无法覆盖。
- 历史截断后回合序号会与保留消息重复（违反 `(ConversationId, Sequence)` 唯一索引），改为聚合内单调递增序号。
- 迁移集并不完整：`tasks` 与 `task_applications` 一直只由 `EnsureCreated()` 建出，从未纳入迁移，因此用迁移建出的新库上创建任务会 500。已补 `AddTaskTables` 迁移（用临时从模型快照移除两个实体、再交给 `dotnet ef migrations add` 生成的方式，保证迁移与快照一致）。
- 启动迁移的探测顺序有误：目标库还不存在时先探测表结构会直接抛“数据库不存在”，导致新库起不来。改为探测失败即视为“无历史包袱”，交给 `Migrate()` 建库。
- 乐观并发令牌最初形同虚设：仓储 `Get` 用了 `AsNoTracking`，`Save` 又按当前库值重新读取并自增版本，于是并发请求读到新版本后照样能写成功（12 个并发选人出现过多写订单的中间态）。改为 `Get` 保持跟踪，`Save` 用业务判断时读到的版本做 WHERE。
- `publish`、`increase-reward`、查看报名的 `ownerId` 原本是必填查询参数，带 JWT 调用时缺它会在模型绑定阶段直接失败（映射成 500）。改为可选，身份以 JWT 为准，并给 `BadHttpRequestException` 补了 `400` 映射。
- `GET /api/v1/tasks/{id}/order` 一直用任务 ID 去查订单表，参与者也只能拿到 `404`。改为按 `TaskId` 查询。

常用命令：

```powershell
dotnet build AIToHuman.sln --no-restore
dotnet test AIToHuman.sln --no-build --no-restore
dotnet test backend/tests/AIToHuman.IntegrationTests/AIToHuman.IntegrationTests.csproj --no-build --no-restore
Set-Location frontend
npm run typecheck
npm run build
```

测试现状：

- 领域单元测试 101 个（`AIToHuman.Domain.Tests`）：任务加价、禁止自己报名、禁止重复报名、选择服务者、订单参与者权限、履约状态流、返工闭环与批准后关单、执行地址校验；对话回合不变量、历史窗口与截断；通知字段校验与标记幂等；订单会话消息校验与未读语义；执行凭证的类型/大小/签名校验、扫描状态机与扫描尝试记账、上传限额边界、元数据剥离（JPEG/PNG/WebP 的段结构、损坏文件不改写）；配置键形状、取值上限、版本自增与审计脱敏。
- 集成测试 210 个（`AIToHuman.IntegrationTests`）：AI 多轮协议、输出严格校验与可控重试、SSE 线格式、会话用例、`IUnitOfWork` 事务边界、通知骨干、订单会话、评价盲期、大厅分页筛选与地址披露、执行凭证（上传/下载/权限/扫描门禁/上限/重扫闭环/直连地址签发/元数据剥离/按人配额），ClamAV 的 INSTREAM 协议与扫描实现分派，S3 兼容存储（请求形态、签名确定性、预签名参数与有效期、403/404 映射、缺配置不发请求）、EF 模型快照，以及运营配置（设置目录校验、解析顺序、加密与脱敏、审计、并发冲突、自检、管理员名单判定与优先级、AI 配置热更新）。全部走上游替身与内存仓储，不需要网络和数据库。
- 尚缺：认证与授权、PostgreSQL 仓储的自动化测试（目前只有手工端到端验证）、SignalR 重连与派发失败重试、运营后台的其余能力，以及主机级端到端测试——本机 NuGet 无法还原 `Microsoft.AspNetCore.Mvc.Testing`，所以没有 `WebApplicationFactory` 用例；网络可用后补该包，就能把本节的手工联调步骤逐步自动化。

## 12. 完成状态与后续顺序

### P0

当前没有未完成的 P0 项。本轮按顺序完成的 P0 与 P1 都列在下方“已完成（P0 与 P1）”，其中 P0 指“模型输出严格校验与重试、EF Core 迁移替换 Development 建表、订单/报名/选人并发令牌与事务”，P1 指“通知骨干、订单聊天与未读、评价盲期集成测试、分页筛选与地址分阶段披露、凭证文件上传与安全校验”。

### 已完成（P0 与 P1）

原 P0：

- 订单驳回后的返工/重新提交路径，以及批准后同步关闭任务；实现见第 3 节，状态机见第 10 节。
- 顺带修复：带本地时区偏移量的 `deadline` 会导致创建任务返回 `500`，现在领域层统一归一化为 UTC。
- AI 多轮协议的后端集成测试：新增 `AIToHuman.IntegrationTests`，用模拟火山 SSE 的上游替身覆盖分片、转义、缺少结束标记、截断、超时和上游错误；SSE 线格式由新增的 `AiPlanStreamWriter` 承载并单独测试。
- 对话持久化：新增 `Conversation` 聚合并落库消息与草稿状态，AI 流端点改按服务端历史生成请求，前端支持刷新恢复、新建对话，并在 AI 失败时回滚本轮输入让用户重试；见第 3 节和第 6 节。
- 模型输出严格校验与可控重试：新增 `AiTaskPlanValidator` 与 `AiPlanningFormatException`，校验失败最多重试一次并把失败原因作为修复指令追加到对话；重试前用新增的 `restart` 事件让页面清空半截回复，避免两段内容拼接；字段缺失不再抛 `KeyNotFoundException`。
- 启动建表改为标准 EF Core Migration：删除 `EnsureCreated()` 与幂等 SQL，补上缺失的 `AddTaskTables` 迁移，并让既有 EnsureCreated 库自动 baseline；见第 10 节。
- 并发令牌与事务：`tasks`/`orders` 增加 `Version` 乐观并发令牌，选人与验收放进同一个事务，冲突统一返回 `409`；见第 10 节。

原 P1：

- 通知骨干：版本化事件信封、`notifications` 表兼做 Outbox、后台派发与重试、收件箱与未读数接口，前端顶栏加未读徽标；见第 3 节与第 10 节。原 `IOrderNotificationPublisher` 的 fire-and-forget 推送已删除。
- 订单会话（聊天）：`OrderMessage` 实体与 `order_messages` 表、参与者权限、未读语义与订单列表徽标、新消息通知，前端会话弹窗与消息按钮徽标；见第 3 节。
- 评价盲期与公开摘要补上集成测试（单方隐藏、双方公开、满 7 天公开、摘要样本量、重复评价与越权）；顺带把“每方每单只能评价一次”从数据库唯一索引提升为应用层判定，避免 EF 路径抛 500。
- 大厅分页筛选与地址分阶段披露：`GET /tasks` 改为游标分页 + 区域/悬赏区间筛选；公开详情不再返回他人草稿；`tasks.ExecutionAddress` 只在订单成立后向所有者与被选中服务者披露；前端加筛选表单、加载更多与执行地址输入/查看。
- 执行凭证：`OrderEvidence` 实体与 `evidence` 表、类型白名单 + 文件签名校验 + 大小与摘要校验、扫描状态机、按需上传与鉴权下载，前端加凭证面板（上传/列表/下载）；存储与扫描通过 `IFileStorage`/`IEvidenceScanner` 抽象，Development 用本机目录 + 占位扫描。

本轮追加（运营可配置三方集成参数）：

- 配置骨架：新增 `system_settings`（覆盖值 + `Version` 并发令牌）与 `system_setting_audits`（只追加、脱敏）两张表（迁移 `AddSystemSettings`）、设置目录（白名单 + 类型校验 + 兼容的环境变量名，现为 21 个键）、固定的解析顺序（数据库 → 环境变量 → 默认值）、Data Protection 加密的机密、写入即刷新的内存快照与 15 秒后台轮询、运营接口与“测试连接”自检、管理员名单策略；决策见 [ADR-0003](../architecture/decisions/0003-operator-configurable-settings.md)。
- 消费方接线：AI 服务商（地址、密钥、模型、无活动超时）、对象存储 provider、内容扫描 provider 都改为从配置读取；写死的 `NoOpEvidenceScanner`（该类型已删除）换成按配置工作的 `HttpEvidenceScanner`，`LocalFileStorage` 的根目录也改为运行时解析。
- 运营配置页面：顶栏入口（仅管理员）、按分组列出配置项与来源徽标、机密脱敏输入、保存（带版本冲突提示）、恢复默认、测试连接、变更记录；见第 3 节。
- 凭证扫描闭环与可配置上传上限：`evidence` 表新增扫描尝试次数、最近说明与尝试时间（迁移 `AddEvidenceScanAttempts`）；`EvidenceRescanService` 每 60 秒按 30 秒退避重扫 `Pending` 凭证、最多 5 次，文件缺失直接判定 `Rejected`，用尽次数后保留说明交给人工；`evidence.maxSizeBytes` / `evidence.maxPerOrder` 纳入设置目录（硬上限 25 MB / 50 份），凭证接口与页面展示检查次数与说明；见第 3、10 节。
- S3 兼容对象存储：新增 `S3FileStorage`（自研 AWS SigV4，不依赖厂商 SDK），按 `storage.s3.*` 做上传/下载/存在性/删除；`SettingsFileStorage` 按 `storage.provider` 分派 local 与 s3；配置不全、Bucket 不存在、密钥无权限都会翻译成可行动的说明；已用本机 MinIO 加独立客户端 `mc` 端到端验证（见第 11 节）。
- 短时直连下载地址：`S3FileStorage` 实现可选的 `IPresignedFileStorage`（SigV4 查询串签名，含对象路径、有效期与附件名），新增 `GET /api/v1/evidence/{id}/download-url` 与 `evidence.downloadUrlLifetimeSeconds`（5 至 900 秒，默认 120），前端在支持时直接跳转签名地址、否则回退流式下载；篡改与过期都由对象存储自己拒绝（403），已用真实 MinIO 验证。
- 元数据剥离与按人限速：`EvidenceContentSanitizer`（领域层，纯字节解析）处理 JPEG/PNG/WebP 的元数据段，`evidence.stripMetadata` 控制开关、剥离结果落库到 `evidence.MetadataRemoved`（迁移 `AddEvidenceMetadataRemoved`）并在接口与页面展示；`evidence.uploadsPerUserPerHour`（默认 60）按上传者限速，计数走数据库并配了 `(UploadedBy, CreatedAt)` 索引，多实例一致。
- 运营后台（人工兜底，后端已完成）：跨所有者检索任务与用户、查看任务详情、把**尚未分配**的任务下架并强制填写原因（原因写进 `admin_audit_entries`，`GET /api/v1/admin/audits` 可查）；已产生订单的任务会被领域规则拦住（`422`，提示先处理订单）。风险规则引擎仍未实现，所以这里**没有任何自动判定**，纯粹是人工介入入口。
- 待补：运营页面上还没有“任务/用户检索”页签（目前只有配置项与变更记录两个页签，任务下架要调接口），以及这一块的端到端联调（单元/集成测试已覆盖领域规则；`admin-e2e` 脚本首版因为混入中文被 PowerShell 5.1 按 ANSI 读而无法解析，需要改成纯 ASCII 后重跑）。

### 后续跟进（原 P1 的延伸项）

- 对象存储的短时签名 URL 已实现（见第 3、11 节）；如果以后要让前端完全绕开后端，需要补 CORS 配置与审计补偿。
- 选定病毒/内容扫描服务后把 `evidence.scanner.provider` 切成 `http` + `failMode=closed`（重扫闭环已经就绪，只差真实服务商）。
- 运营后台的其余部分：任务/用户/订单检索、风险记录与高风险任务人工复核、争议处理看板。上传大小与份数上限已经进了设置目录；凭证类型白名单**故意不进**（放开等于允许上传可执行内容）。
- 通知的更多事件类型（报名、评价公开、任务过期）与推送渠道（短信、邮件）。
- 会话消息的分页与历史截断、消息撤回与编辑。
- 大厅排序选项（悬赏、距离）、任务分类筛选，以及精确地址的访问审计。
- 图片像素级重新编码、凭证与验收项关联，以及运营后台的任务/用户检索与风险复核。

### P2

- Redis backplane、多实例部署、审计与可观测性。
- 实名认证、真实支付托管、退款、争议和合规评审。
- 运营审核后台与高风险任务人工复核。

## 13. 交接检查清单

- [ ] 执行 `git status` / `git log --oneline -3`，确认工作区干净、HEAD 与本文记录的 `e45da76` 一致（若已有更新提交，先核对第 3 节的实现描述是否仍然成立）。
- [ ] 确认 `VolcengineAI:Model` 是火山控制台真实启用的模型或接入点 ID。
- [ ] API Key 仅存在于 User Secrets 或环境变量，没有进入 Git。
- [ ] PostgreSQL 已启动，`/health` 返回 `healthy`。
- [ ] 后端先于前端启动，`5173` 能代理到 `5188`。
- [ ] 连续完成至少两轮 AI 对话，未完成前看不到草稿，完成后可预览但不会自动发布。
- [ ] 对话进行到一半刷新页面，历史与草稿都能恢复；点击“新建对话”后回到只有开场白的空会话。
- [ ] 断开 AI 上游后发送一句话：界面给出错误提示，输入内容仍在输入框中，且会话消息数没有增加。
- [ ] owner/worker 切换后用户 ID 不变。
- [ ] 完成发布、报名、选人、订单开始、提交、验收/驳回和双方评价。
- [ ] 驳回一次后由服务者返工并再次提交，界面上能看到驳回原因和返工次数。
- [ ] 验收通过后任务在大厅消失，公开详情状态为 `Closed`。
- [ ] 两浏览器验证选人后服务者无需刷新即可收到订单。
- [ ] 订单会话里发一条消息：对方未刷新就能看到未读徽标，标记已读后归零；无关用户读写会话返回 `403`。
- [ ] 以服务者上传一张小于当前上限（默认 5 MB）的 PNG 凭证：列表出现、可下载；把文本文件改名成 `.png` 上传应被 `422` 拒绝，超过当前上限返回 `413`。
- [ ] 大厅能按区域与悬赏区间筛选，并能“加载更多”翻页；大厅与公开详情的响应里不含精确地址文本。
- [ ] 用一个全新空库启动 API：`Database.Migrate()` 一次应用全部迁移；用早期 `EnsureCreated` 建出的旧库启动会打印基线化警告后正常工作。
- [ ] 在部署配置里设置 `Admin__UserIds` 或 `Admin__Emails`，用它登录后访问 `/api/v1/admin/settings`：非管理员应拿到 `401/403`，管理员拿到 25 个配置项。
- [ ] 通过运营接口把 `ai.apiKey` 换成新密钥：响应只显示 `****末四位`；接着发起一轮 AI 对话应立刻用新密钥，不需要重启进程。
- [ ] 把 `evidence.scanner.provider` 改成 `http` 但不填扫描地址，服务者上传凭证应返回“待扫描、不可下载”，文件不被删除也不放行。
- [ ] 重启进程后重新读取运营配置：机密仍能解密（说明 `DataProtection__KeysPath` 指向了持久目录，而不是临时目录）。
- [ ] 用管理员账户登录后打开顶栏“运营配置”：能看到 25 个配置项、四个分组与来源徽标；普通服务者账户看不到这个入口。
- [ ] 在页面上改一个机密项并保存：列表立刻显示新的掩码与“后台已改”，点“测试连接”能看到自检结果，切到“变更记录”能看到这次修改；把某条改坏（例如把超时填成 1）保存应看到可读的校验提示。
- [ ] 把 `evidence.maxSizeBytes` 调成 1024 后上传一张 2 KB 的图片：应返回“凭证大小不能超过 1 KB”，恢复默认后能正常上传。
- [ ] 把 `evidence.scanner.provider` 改成 `http` 并把扫描地址清空（或指向不可用地址），上传凭证：状态应是“检查中 / 不可下载”并写明原因；扫描服务恢复后，一分钟内后台重扫会把状态改成“已通过检查”。
- [ ] 在对象存储里建好私有 Bucket，运营配置里把 `storage.provider` 换成 `s3` 并填好 endpoint/region/bucket/密钥：上传凭证后应在 Bucket 里看到 `<prefix>/<orderId>/<evidenceId>.png`，且本机目录不再新增文件。
- [ ] 故意把 `storage.s3.secretAccessKey` 改错：上传应返回可读的错误（提到密钥或权限、并带上 S3 的错误码），而不是 500 或静默写本机。
- [ ] 对象存储模式下点凭证“下载”：地址是带 `X-Amz-Signature` 的短时链接，浏览器直接拿到文件；把 `evidence.downloadUrlLifetimeSeconds` 改成 5 秒后重新下载，等 8 秒再点应被对象存储拒绝（403）。
- [ ] 上传一张带 GPS 的截图（手机原图即可）：凭证面板应显示“已在上传时移除元数据：PNG tEXt（或 EXIF/XMP）”，下载下来的文件里搜不到拍摄地点。
- [ ] 部署一个 clamd（或用替身）并把 `evidence.scanner.provider` 改成 `clamav`：上传正常图片应 `200`；上传含 EICAR 测试串的文件应被 `422` 拒绝，且存储里不留文件。
- [ ] 把 `evidence.uploadsPerUserPerHour` 调成 2，连续上传三次：第三次应返回可读的限流提示。
- [ ] 新功能先补 Contract、领域规则和测试，再扩展页面。

## 14. 相关文档

- [项目 README](../../README.md)
- [产品愿景](../product/product-vision.md)
- [MVP PRD](../product/mvp-prd.md)
- [用户故事](../product/user-stories.md)
- [AI 多轮需求澄清配置](../ai-planning.md)
- [开发指南](./development-guide.md)
- [路线图](./roadmap.md)
- [系统架构](../architecture/system-architecture.md)
- [领域模型与状态机](../architecture/domain-model.md)
- [API 设计约定](../api/api-guidelines.md)
- [安全、隐私与风控](../security/security-and-risk.md)
- [模块化单体决策](../architecture/decisions/0001-modular-monolith.md)
- [固定悬赏与双向选择决策](../architecture/decisions/0002-fixed-reward-and-mutual-selection.md)
- [三方集成参数可配置决策](../architecture/decisions/0003-operator-configurable-settings.md)
