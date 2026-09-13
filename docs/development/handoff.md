# AIToHuman 项目交接文档

最后更新：2026-09-13

仓库：[suyuewuhen/AIToHuman](https://github.com/suyuewuhen/AIToHuman)

当前分支：`main`

最新已提交基线：本轮之前 `main` 与 `origin/main` 同步在 `311bc0f`（资金托管与只追加账本）；本轮「主机级端到端测试骨架」是紧随其后的一个提交，并已推送保持两边一致（见第 2 节）。

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

尚未实现：**真实支付服务商接入**（托管与账本已实现：模拟网关 + 只追加账本 + 争议分账，缺的是接一家真实通道、失败重试队列与自动对账、佣金抽成）、实名认证、真实的病毒/内容扫描服务商（协议已接好，只差选定/部署服务）、争议申诉与处理时限、客服工单、凭证图片的**像素级重编码**（需要图像编解码库，离线取不到；当前做的是容器白名单化 + 结构 fail-closed，兜底靠内容扫描）、风险侧缺的是模型辅助分类、规则目录编辑的审批流、命中看板的阈值告警与趋势对比（命中次数、复检命中与被判误伤次数已能按原因代码统计）；另外禁止类别命中的已分配任务只做"冻结订单 + 运营按争议处置"，**赔付与退款现在可以按金额执行**（走托管账本），但仍需运营人工给出金额与依据——没有自动的责任判定。

## 2. 当前工作区状态

工作区状态：`main` 与 `origin/main` 同步；本轮的「依赖感知就绪检查、写接口限流与备份恢复演练」随本轮提交一起进入 `main` 并推送（`git log -1` 可见）。上一版交接文档描述的“多轮 AI 未提交实现”已经全部提交，本文不再区分“基线 / 未提交”两种状态。

从基线 `f196850` 到 `e45da76`（多轮 AI 与会话持久化那一轮）的主要变化：

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
backend/AIToHuman.Infrastructure/ Persistence/（TaskDbContext、EfUnitOfWork、29 个迁移）、
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
- 草稿字段可编辑（`PUT /api/v1/tasks/{id}`）：标题、描述、区域、截止时间、悬赏、验收标准、执行地址与报名截止时间都能改，字段校验与创建时**共用同一套规则**（不存在“创建拦得住、编辑能绕过”的缺口）。只有 `ReadyToPublish` 的草稿能改，已发布任务的正文不可变——发布后只能加价，要改内容就撤销后重建。
- 编辑之后会立即重跑确定性风险规则，并**清空原有的人工复核结论**（复核人、复核时间、依据都清空，按新文本重新判定）。原因是审核绑定的是当时那份文本：如果编辑后保留“已放行”，“先提一份干净文案过审、再改成代考”就是一条现成的绕过路径。反向同样成立：被运营驳回的草稿只要改掉了敏感内容，就会重新进入新一轮判定，而不是被永久钉死。
- AI 建议悬赏不等于交易金额，最终悬赏由用户确认。
- 发布后、分配服务者前只允许加价，不允许降价。
- 服务者按固定悬赏报名，不支持竞价、互相报价或服务者改价。
- 任务大厅展示区域级地点、悬赏、验收标准和报名数，不应公开精确地址或联系方式。
- 大厅列表按截止时间升序游标分页（`items`/`nextCursor`/`hasMore`），支持按区域、悬赏区间筛选；非法区间或非法游标返回 `400` 与可读原因。
- 公开详情只对已发布及之后的状态开放；`ReadyToPublish` 草稿仅所有者可见，其他人（含匿名）一律 `404`，避免草稿内容泄露。
- 精确执行地址属于订单参与者层信息：所有者始终可读，被选中的服务者在订单成立后可读，已报名但未被选中的服务者与其他人返回 `403`；大厅与公开详情只暴露 `hasExecutionAddress` 布尔值，永远不含地址本身。
- 需求方在选人前能看到服务者的公开评价摘要：报名列表（`GET /api/v1/tasks/{id}/applications`，仅所有者）每条报名都带 `workerAverageRating` 与 `workerReviewCount`，**只统计已公开的评价**（同一订单双方都提交，或订单完成满 7 天），盲期内的评价不计入；口径与 `GET /api/v1/users/{id}/review-summary` 完全一致（服务层用同一个 `PublicCredit` 计算）。没有公开评价的服务者显示 0 分 / 0 条，页面写“暂无公开评价”而不是伪造分数；每个报名只看自己那个 workerId，不会串号。
- **精确地址的访问审计**（`address_access_entries`，迁移 27）：执行地址是全系统最敏感的用户数据，所以"谁能看"（所有者与被选中的服务者，见下一条）之外还必须有"谁看过"。每一次读取都写一条只追加的留痕（`AddressAccessEntry`：任务、查看者、身份、结论、时间），**被拒绝的尝试同样留痕**——批量试探在日志里就藏在这些拒绝记录里。身份与结论全部由领域层从任务本身推出（所有者 / 被选中的服务者 / 其他人；有地址 + 参与者 = `Granted`，有地址但不是参与者 = `Denied`，任务没登记地址 = `NotSet`，不把"没有地址可看"算成越权），不信任调用方传进来的角色。匿名请求记成"没有 viewer id"，不占用任何真实用户。应用层的顺序是**先留痕再判断**：落库之后才决定抛 `403` 还是返回地址，避免留下一条"读了不记"的旁路（原来那个不留痕的 `TaskService.GetExecutionAddress` 已删除）。运营用 `GET /api/v1/admin/address-access?taskId=&viewerId=&limit=` 查（默认 50、上限 200），响应里带同一过滤条件下的**拒绝次数**，运营后台有"地址留痕"页签。

- 草稿历史（`task_draft_revisions`，迁移 21）：**一行就是一个版本的完整快照**，只追加、不可改。创建草稿时写入第 1 版（`changeSummary = "创建草稿"`，`editedBy` 是创建者），之后每次编辑追加一版，版本号在同一任务内单调递增。每版都记下该版文本对应的风险结论（`riskVerdict`/`riskRuleCode`/`riskRuleVersion`），所以能回答"第几版被判成禁止、当时用的是哪一版规则"。
- 每次编辑由领域层比对前后字段得出"改了哪些字段"（标题/描述/公开区域/截止时间/悬赏/验收标准/执行地址/报名截止时间），拼成 `changeSummary`，例如"标题、悬赏"；超过 6 项收敛成"…等 N 项"；一个字段都没变则写"无字段变化"。
- 任务写入与版本快照在**同一个工作单元**里：创建草稿也是一次事务边界（写任务 + 写第 1 版），编辑同样如此；并发落败的编辑不会留下多余版本——先撞 `tasks.Version` 乐观并发令牌，`(TaskId, Revision)` 唯一索引只作兜底。
- 读接口：`GET /api/v1/tasks/{id}/revisions?ownerId=...`，**只有所有者能读**（其他人 `403`，任务不存在 `404`），按版本号升序返回。
- **字段级差异由服务端算好**：每一项都带 `changes`，是与上一版逐字段比较的结果（`{ field, before, after }`），第一版为空数组。值统一格式化成可读文本（时间 `yyyy-MM-dd HH:mm UTC`、金额 `{数值} {币种}`、验收标准用 `；` 连接），字段名与 `changeSummary` 同一套中文名。放在服务端是为了避免每个客户端各写一套比较与格式化规则。
- **回滚**：`POST /api/v1/tasks/{id}/revisions/{revision}/restore?ownerId=...`（仅所有者；只有草稿可以回滚）。走的是与编辑**完全同一条**写入路径（领域层唯一的 `ApplyDraft`）：同一套字段校验、重新判定风险、作废人工复核结论与申诉状态。回滚不是绕过校验的后门——如果那一版的截止时间已经过去，回滚会像编辑一样被拒绝，而不是悄悄造出一个永远发布不了的草稿。
- 回滚**本身也追加一版**，摘要形如"回滚自第 1 版：标题、公开区域、悬赏"（内容与目标版一致时写"回滚自第 N 版（内容与该版一致）"）。**中间版本不会被覆盖或删除**，历史只会往前长；边界：版本不存在 `404`、非所有者 `403`、已发布 `422`、拿别的任务的快照 `422`（领域层拦"这一版不属于当前任务"）。
- 前端："我的任务"里每条任务都有"修改记录"，展开后逐版显示版本号、变更摘要、时间、修改人，以及该版的标题/区域/悬赏/截止时间和**逐字段差异（`字段：旧值 → 新值`）**；除最新一版外每版都有"恢复这一版"；非放行版本还会显示风险结论与原因代码。

### 风险规则与人工复核

- 判定是**确定性规则**，不是模型自由判断。规则目录有两层：代码内置的那一份（`backend/AIToHuman.Domain/Risk/RiskRuleCatalog.cs` 的 `RiskRuleCatalog.BuiltIn`，版本 1，任何部署都自带，是平台的硬底线）与**运营在后台编辑出来的覆盖版本**（版本 ≥ 2，只追加的版本快照，见下一条）。目录带 `Version`，判定时把当时的版本号记在任务上，所以历史判定能回溯到"当时用的哪一版规则"。
- 当前 10 条词表规则（6 条 `prohibited.*` 禁止类：代考与冒名顶替、违禁品与危险品、欺诈伪造与绕过身份核验、跟踪偷拍与骚扰、危害人身安全、必须由持证人员执行的医疗行为；4 条 `review.*` 转人工类：证件与重要文件、受限场所、敏感物品与照护对象、物品或用途不明）+ 2 条阈值规则：悬赏 > 5000 元（`review.high_reward`）、截止时间落在北京时间 00:00–06:00（`review.night_window`），共 12 个原因代码。
- 匹配范围是标题、描述、验收标准与执行地址；匹配词全部 ≥ 2 个字（单字如“代”会误伤“代取/代送”，有单元测试锁死这条约束）。
- 判定发生在四处：创建草稿（`TaskItem` 构造）、编辑草稿、回滚草稿与发布前（`Publish` 重新判定一次，因为悬赏与截止时间在草稿阶段会变）。四处用的都是**当次写入时生效的那一版目录**（`IRiskRuleCatalogProvider.GetEffective()`，无覆盖时就是内置目录），因此运营收紧规则后，已经存在的草稿在发布时同样会被拦住。被禁止的任务**仍然会创建出草稿**——用户能看到自己写的内容和原因，也可以自己撤销，只是发布这一关过不去；这比直接丢弃用户输入更好追溯。
- **发布后复检**（迁移 25）：判定不能只发生在草稿阶段。已经在线（甚至已经被接单）的任务会在两种时机被重新判定——一是运营改了规则目录（后台任务每 5 分钟扫一轮，启动 30 秒后跑第一轮；运营也可以 `POST /api/v1/admin/risk/recheck` 立刻跑一轮），二是所有者给已发布任务加价（`IncreaseReward` 加完立即重判，"先发一条普通任务、再改成高标准悬赏"因此不再能绕过高金额转人工）。复检只挑"仍在线且规则版本不是最新"的任务，判完把版本号刷新，所以天然幂等、不会重复处置或重复通知。
- 复检的处置口径分三档（`TaskItem.ReassessRisk`，结果由 `RiskEnforcementOutcome` 表达）：命中 `prohibited.*` 且**没有订单** → 当场自动下架（走 `Cancel`，理由写明命中的规则代码与版本）；命中 `prohibited.*` 但**已经有订单** → 不硬撤任务（否则会出现"任务没了、订单还挂在服务者名下"），改为冻结订单（`Order.SuspendByRisk`，复用既有争议路径，进运营争议队列，`DisputeOpenedBy` 留空表示平台发起）；只命中 `review.*` → 任务**保持在线**、进复核队列要求人工复检。草稿与已撤销/已结束的任务不接受复检。
- 复检不重复惊动：如果运营已经就**同一条规则**放行过（命中代码与上一次相同），或任务已经在队列里等人工，复检只刷新规则版本号，不重新排队也不重复通知——否则每改一次规则，所有含"身份证/医院/高金额"的在售任务都会再推一次队列、再发一轮通知。
- 平台自己的动作同样留痕：`AdminAuditEntry.SystemActorId`（固定身份 `00000000-0000-0000-0000-00000000ffff`）写审计，动作名 `task.risk.recheck.unpublish` / `task.risk.recheck.freezeOrder`，运营审计页把它显示成"平台（风控）"，一眼能区分人做的与系统做的。处置结果通知所有者（新类型 `task.riskEnforced`，载荷含处置状态、结论、规则代码与原因），被下架任务的报名者也收到报名失效通知。
- 发布门禁：`prohibited.*` 一律 `422` 且**人工也无权放行**；`review.*` 进入人工队列，运营放行后才能发布，驳回则不能发布。人工驳回是终态：即使规则后来不再命中，也不会自己变回可发布。
- 判定结果记在任务上：`RiskVerdict`、`RiskRuleCode`、`RiskCategory`、`RiskSummary`、`RiskRuleVersion`、`RiskAssessedAt`、`RiskReviewStatus`、`RiskReviewedBy`、`RiskReviewedAt`、`RiskReviewNote`。其中 `RiskSummary` 是给用户和运营看的说明，**刻意不含命中的具体词**，避免被逐字试探绕过。
- 运营复核走既有后台：`GET /api/v1/admin/risk/reviews?limit=` 列出待复核任务（按创建时间升序，先来先处理），`POST /api/v1/admin/risk/reviews/{taskId}/decide` 放行或驳回（依据必填 ≤200 字，写进 `admin_audit_entries`，动作名 `task.risk.approve` / `task.risk.reject`），`GET /api/v1/admin/risk/rules` 说明当前按什么规则拦（版本、阈值、规则清单与匹配词数量，不返回匹配词）。非管理员一律 `403`。
- **规则目录可由运营编辑**（迁移 24 `risk_rule_catalog_revisions`）：一版目录 = 一条只追加的快照行（完整 JSON + 自动生成的**变化摘要** + 运营填写的**变更依据** + 操作人 + 时间），读取永远取版本号最大的一版，数据库里没有覆盖时生效的就是内置目录。`GET /api/v1/admin/risk/rules/detail` 给出明细（含匹配词、是否内置、最近一版的摘要/依据/操作人），`GET /api/v1/admin/risk/rules/versions?limit=` 给出倒序的版本历史，`POST /api/v1/admin/risk/rules` 整份替换（规则 + 阈值 + 深夜时段）并让版本号自动 +1，`POST /api/v1/admin/risk/rules/reset` 一键**恢复到内置目录**（改坏了要有退路；同样是追加一版，被恢复掉的版本仍留在历史里）。
- 编辑规则的三道闸门：**依据必填**（≤200 字，与版本快照一起落库，并同步写一条运营审计 `task.risk.rules.update` / `task.risk.rules.reset`）；**乐观并发**（提交带 `ExpectedVersion`，与当前版本不一致返回 `409`「风险规则已被其他人修改（当前版本 vX，你读到的是 vY），请刷新后重试。」，避免把别人刚收紧的规则覆盖回旧样子）；**领域校验**（至少保留一条禁止类规则、原因代码唯一、结论只能是禁止/转人工、匹配词每词 2–20 字、`review.high_reward`/`review.night_window` 是保留代码、阈值与深夜时段的合法区间；内容没变或依据为空也一律 `422`，不占版本号）。
- 快照的可解释性是有意设计的：`RiskRuleCatalog.ToJson()`/`FromJson()` 能把任意一版完整还原，复盘一条历史拦截时直接拿那一版重放判定即可；**快照读不出来时抛错、绝不静默降级**——宁可让写入失败，也不能换一套规则去判定（那等于悄悄改了门禁的松紧）。
- 复核结论通知所有者：`task.riskReviewed`，载荷 `{ taskId, title, reviewStatus, verdict, ruleCode, canPublish }`，走既有 Outbox + SignalR 链路。
- 前端：草稿预览弹窗与“我的任务”会显示被拦/待复核的原因并禁用发布按钮；运营后台（独立页面 `/ops.html`）有“风险复核”“规则目录”“误拦申诉”“地址留痕”“命中统计”五个风控页签（看当前版本/阈值/时段/规则明细与版本历史，整份编辑后“保存为新版本”，或填依据后“恢复内置目录”；命中统计页签按窗口看每条规则被命中多少次、其中多少被判成误伤）。
- 误拦申诉：被拦下的任务，所有者可以申诉（`POST /api/v1/tasks/{id}/risk-appeals`，理由必填 ≤500 字），运营在 `GET /api/v1/admin/risk/appeals?limit=`（按提交时间升序）里看到队列，用 `POST /api/v1/admin/risk/appeals/{taskId}/decide`（`decision` 取 `Accept`/`Deny`，依据必填 ≤200 字，写进运营审计 `task.risk.appeal.accept|deny`）处置，所有者收到 `task.riskAppealDecided`。
- **申诉的处置能力分两档，这是本功能的安全边界**：转人工后被驳回的任务，申诉成立即放行（运营本来就有这个权限，申诉只是多一双眼睛）；被禁止类别命中的任务，申诉成立也**不会**获得发布许可，只记录"规则误伤"的结论并提示改文案后重新判定。运营接口返回 `canBeReleasedByAppeal` 明确告诉运营结论能不能真的放行，通知载荷里的 `canPublish` 同样区分这两种情况。
- 谁能申诉：只有所有者；只有"被禁止类别命中"或"转人工后被驳回"可以申诉——还在等复核（提示"不需要申诉，请等复核结果"）或已放行的都不行。
- **同一版内容只能申诉一次**：处置过之后（无论成立还是驳回）再申诉返回 `422`「这一版内容已经申诉过：请先修改草稿（改完会重新判定风险），再决定是否重新申诉。」；编辑草稿会把申诉状态清回 `None`（正文变了，原来的申诉不再针对同一份材料），改完可以重新申诉。这是为了避免同一份材料把运营队列刷成申诉墙。
- **申诉有次数上限，且每一次都留档**（迁移 26 `task_risk_appeals`）：同一条任务累计最多 **3** 次、同一个人 **24 小时**内最多 **5** 次（`RiskAppealPolicy`，有单元测试锁死这三个值）——"改一个字就能再申诉一次"不再能用来刷运营队列；超限返回 `422`「这条任务累计申诉已达上限（3 次）…」/「近 24 小时提交的申诉已达上限（5 次）…」。次数都在数据库上 COUNT，不信任客户端。
- 留档是**一次申诉一行、只追加**（`RiskAppealRecord`）：记下提交时的规则代码、规则目录版本与结论；运营给出结论后把结论写回同一行（只能处置一次）。`GET /api/v1/admin/risk/appeals/{taskId}/history` 返回这条任务的完整轨迹（含当前生效的两条上限），申诉队列每一项新增 `appealCount`；运营后台"误拦申诉"页签显示累计次数并可展开"申诉轨迹"。表上线之前提交的老申诉没有对应行，运营界面会如实显示"查不到轨迹"。
- 申诉状态记在任务上（`RiskAppealStatus`/`RiskAppealReason`/`RiskAppealedAt`/`RiskAppealDecidedBy`/`RiskAppealDecidedAt`/`RiskAppealDecisionNote`，迁移 23）——那是"当前状态"，历史轨迹看上面的留档表。前端在“我的任务”里给足资格的任务显示“申诉误判”（禁止类别会额外提示不会因此可发布），运营后台“误拦申诉”页签可看轨迹。
- **每一次判定都追加留档**（迁移 29 `risk_decision_entries`）：任务行上只保留最新一条判定，因此"这条规则到底拦了多少次、误伤多少、加词之后有没有好转"在库里原本无从回答。现在创建、编辑、回滚、发布、加价、发布后复检这六个判定点都会追加一行（`RiskDecisionReason` 表明是哪个动作触发的），行里记结论、原因代码、类别、当时的规则版本、当时的悬赏金额与判定时刻；表只追加、不更新不删除，与任务行上的"当前判定"是两种用途。写入与状态变更在同一个工作单元里，所以不会出现"任务记了新判定、历史里却没有"。
- **规则命中看板**（`GET /api/v1/admin/risk/stats?days=`）：窗口内按（原因代码 + 结论）分组统计 `hits`、其中复检命中的 `recheckedHits`、首次/最近命中时刻，并叠加同窗口内被**裁定**为误伤（申诉 `Accept`）的次数——于是每条规则都能看到"拦了多少 / 其中多少被判成误伤"，这就是调词的依据（判据来自 `task_risk_appeals` 的裁定时间，不是提交时间）。`GET /api/v1/admin/risk/decisions?taskId=` 给出单条任务的判定轨迹，运营排查"这条任务为什么被拦"时按时间轴看得到每一次判定。两个接口都只读、仅管理员（非管理员 `403`），看板只返回聚合与原因代码，**不返回申诉理由原文**。
- 尚未实现：客服工单与申诉时效；模型辅助分类与语义判断（现在只有字面词表匹配）；规则目录编辑的审批流（现在是一人一依据一审计）、按规则维度的报表与告警（现在有命中看板，但没有阈值告警与趋势对比）；申诉上限目前是**代码常量**（还没进运营配置目录）。留痕里**没有操作人字段**——触发者由 `reason` 加任务所有者推得（创建/编辑/回滚/发布/加价都是所有者，复检是平台），所以当前不需要额外字段；真要多角色运营时得补。统计只有"拦了多少"，**回答不了"漏了多少"**（漏拦要靠模型辅助分类与标注样本集）。**已知缺口**：禁止类别命中的已分配任务只做"冻结订单 + 运营按争议处置"（赔付与退款已能按金额执行），但没有平台自动退款或赔付。

### 订单、通知与评价

- 选中服务者后自动创建订单，并固化任务标题和悬赏快照。
- **资金托管与只追加账本**（迁移 28）：下单（选人）时冻结需求方资金、验收时放款给服务者、取消时退款、争议处置时按金额分账，每一步都同时改订单的托管状态并写一条账本。
  - 托管状态（`EscrowStatus`）：`None`（未托管：托管关掉或历史订单）/ `Held`（已冻结）/ `Released`（全额放款）/ `Refunded`（全额退款）/ `Settled`（部分放款 + 部分退款）；订单上另有 `EscrowAmount`（= 悬赏）、`ReleasedAmount`、`RefundedAmount`、`PaymentReference`、`EscrowHeldAt`、`EscrowSettledAt`，`EscrowBalance` 是还没动过的余额。
  - 账本是**复式记账、只追加**（`ledger_entries`）：一行 = 一次账户间转账（`OwnerFunds` 需求方资金 → `Escrow` 平台托管 → `WorkerPayout` 服务者应得），带动作（`Hold`/`Release`/`Refund`/`PartialRelease`/`PartialRefund`）、正数金额、币种与说明。这样"钱去哪了"是可对账的：同一订单的流水加总能还原冻结、放款、退款各多少；争议里的"一半赔付"天然表达成两笔转账。**本轮不抽佣金**（账本里没有平台收入账户，真有佣金时要新增账户与分账流水，而不是把差额留在托管账户）。
  - 分账必须**正好等于**托管金额：全给服务者 → `Released`，全退需求方 → `Refunded`，都有 → `Settled`；不相等、负数或全零一律 `422`（不允许出现"不知道去哪了"的差额）。
  - 网关是**可插拔端口**（`IPaymentGateway`：冻结/放款/退款），当前注册**模拟网关**（确定性、不持有金额状态，只发可对账凭据 `sim-hold-<orderId>`；测试可打开失败开关）。真实服务商要满足两条契约：同一动作能用幂等键重放、网关侧要有可对账流水号。
  - 运营配置 `payment.provider`：`simulated`（默认）走上面的流程；`disabled` 关闭托管——订单不带托管信息、也不写流水，行为与托管上线之前完全一致（既是本地联调的便利开关，也是网关出问题时的一键降级）。
  - 三条口径：**钱与状态一起动**（资金动作与订单保存在同一个工作单元，不允许"已完成但没放款"）、**失败就不改状态**（网关不通时抛错让事务回滚、用户可重试，宁可挡住一次验收也不留一笔说不清的钱）、**参与者可查流水**（`GET /api/v1/orders/{id}/ledger`，仅订单双方；运营另有 `GET /api/v1/admin/orders/{id}/ledger`）。已知缺口：失败重试队列与自动对账还没有（现在依赖调用方重试）。
- “我的订单”分为“我发布的订单”和“我接取的任务”。
- 已实现 `Accepted → InProgress → Submitted → Approved`，需求方也可将 `Submitted` 驳回为 `Rejected`。
- 被驳回的订单可以由服务者返工：`POST /api/v1/orders/{id}/resume` 把 `Rejected` 退回 `InProgress`，服务者可再次提交，`ReworkCount` 累加、`RejectionNote` 保留最近一次驳回原因。
- 需求方验收通过时，同一用例内把订单置为 `Approved` 并把任务从 `Assigned` 推进到 `Closed`；任务关闭后不再出现在任务大厅，公开详情仍可查询。
- 服务者提交时必须填写执行凭证或完成说明；需求方驳回时必须填写原因。
- 订单批准后双方可以分别评价；双方都提交后立即公开，只有一方提交时在 7 天后公开。
- 每方每个订单只能评价一次，重复提交由应用层判定并返回 `422`“你已经评价过该订单。”（数据库 `(OrderId, ReviewerId)` 唯一索引作为兜底，不再依赖它抛出 500）。
- 任务大厅与公开详情读取公开评价摘要，因此服务者在报名前能看到需求方信用；服务者信用目前要由需求方另行查询 `GET /api/v1/users/{id}/review-summary`（报名列表不内联，见上一节）。
- 通知走 Outbox：业务事务内写入 `notifications` 行，后台 `NotificationDispatcher` 每 2 秒扫描未派发记录。派发顺序是**先原子认领、再推送**：认领用一条条件更新 `UPDATE notifications SET "DispatchedAt" = @p WHERE "Id" = @id AND "DispatchedAt" IS NULL`，只有影响 1 行的实例负责推送，因此多实例同时扫到同一条记录也不会重复推送；推送失败就撤回认领、退回 Outbox 等下个周期，绝不把没推出去的通知标成已派发。
- 取消订单：需求方在服务者提交验收前可以取消，服务者只能在开始执行前取消；必须填写原因，原因、取消人（`CancelledBy`）与时间（`CancelledAt`）落库。同一事务里把任务放回大厅（未过截止时间，本次选中报名置 `Rejected`，服务者可重新报名）或直接置为过期（已过截止时间），并通知对方参与者（`order.cancelled`）。
- 任务撤销与过期：所有者可以撤销自己的草稿或尚未被选中的已发布任务（`POST /api/v1/tasks/{id}/cancel`，原因必填，报名中的服务者收到 `task.cancelled`）；超过截止时间仍无人被选中的已发布任务由后台 `TaskExpiryService` 每 60 秒扫描一次置为过期（记 `ExpiredAt`），`Pending` 报名一并置为 `Expired`，所有者与报名者各收到一条 `task.expired`。过期不可逆，任务不会留在“已发布”里等下一个服务者。
- 报名与报名窗口：任务可以设可选的**报名截止时间**（`ApplicationDeadline`），到点后只关闭新报名，已有报名仍可被选中；服务者可以撤回自己尚未被处理的报名（`POST /api/v1/tasks/{id}/applications/{applicationId}/withdraw`），撤回后记录保留为 `Withdrawn`、可以重新报名，所有者收到 `task.applicationWithdrawn`；服务者在“我的报名”（`GET /api/v1/tasks/applications/mine`）里能看到自己每一条报名的状态与是否还能撤回。
- 争议：需求方在 `Submitted`、服务者在 `Rejected` 可以发起争议（`POST /api/v1/orders/{id}/dispute`，原因必填 ≤500），订单随即进入 `Disputed` 并冻结（提交、验收、驳回、返工、取消全部拒绝），任务保持 `Assigned`；运营在后台“争议处置”里三选一处置（强制完成 / 退回返工 / 终止订单）并必须写明依据，动作记入 `order.dispute.{approve|rework|cancel}` 审计，双方都会收到 `order.disputed` 与 `order.disputeResolved`。
- **争议里的赔付与退款**：处置请求可以带一个可选金额（`AdminResolveDisputeRequest.amount`），**含义随结论而变**——强制完成时是放款给服务者的金额（差额退回需求方），终止订单时是退回需求方的金额（差额作为补偿给服务者），留空即全额；退回返工不涉及资金，填了金额会被拒绝（`422`）。越界（超过托管金额、负数）同样 `422`「赔付金额必须在 0 到托管金额（X）之间。」；运营审计的原因里会带上「（资金处置金额 X CNY）」，事后看审计就知道这次动了多少钱。
- 多实例扇出：`ConnectionStrings__Redis` 已配置且运营打开 `notifications.fanout.enabled` 时，认领方把通知广播到 Redis 频道 `aitohuman:notifications:fanout`（`RedisNotificationFanout`，StackExchange.Redis 发布/订阅），每个实例的 `NotificationFanoutSubscriber` 收到后推给连在自己身上的客户端——用户连在哪个实例都能收到。没配连接串或开关为 false 时，认领方直接推本实例客户端；广播不通（Redis 抖动/还没起来）时兜底推本实例在线客户端并打警告日志，其它实例的客户端靠 REST 补齐；只有广播和本地推送都失败才撤回认领，2 秒后重试。订阅方每 5 秒检查一次开关，运营开启后自动接入，不需要重启。
- 扇出是 at-most-once 的实时提示：广播时若没有任何实例在订阅，日志会给出警告；事实状态始终以 `notifications` 表与 REST 收件箱为准，客户端重连后重新拉取补齐。
- 推送使用版本化信封 `{ eventId, type, version, occurredAt, payload }`；`eventId` 是幂等键（订单创建用订单 ID，状态变化由订单 ID + 状态推导），同一业务事件重复入队只保留一条。
- 收件箱与未读数来自 `GET /api/v1/notifications`；`POST /api/v1/notifications/read` 支持按 ID 或整体标记已读。
- 事件只是刷新提示：客户端收到推送后重新拉取订单与通知列表，断线重连也能通过 REST 恢复事实状态。

### 执行凭证（文件）

- 只有订单服务者可以上传，且订单必须处于 `InProgress` 或 `Submitted`。份数与单份大小上限来自运营配置（`evidence.maxPerOrder` / `evidence.maxSizeBytes`，默认 10 份 / 5 MB），硬上限是 50 份 / 25 MB，运营只能收紧不能突破。
- 扫描没给出结论时凭证保持 `Pending`（不可下载）：后台 `EvidenceRescanService` 每 60 秒重扫一批，同一条凭证退避 30 秒、最多尝试 5 次；文件已不在存储里则直接判定为 `Rejected`。用尽次数后保留待扫描状态并写明“停止自动重试”，交给人工处理，不会无声无息地永远挂着。
- 凭证接口会返回检查次数与最近一次说明（`scanAttempts` / `lastScanNote` / `scanExhausted`），前端凭证面板直接显示，因此“为什么不可下载”对双方都是可见的。
- 类型白名单为 JPEG / PNG / WebP / PDF，且**刻意不做成运营配置**（放开它等于允许上传可执行内容）。服务端不信客户端声明的 MIME：先做白名单校验，再按**文件签名**核对内容（PNG 头、JPEG SOI、WebP RIFF+WEBP、`%PDF`），不一致直接拒绝。
- 上传时默认做**容器白名单化 + 结构校验**（`evidence.stripMetadata`，默认开启）：JPEG 丢掉**全部** APPn 扩展段（0xE0–0xEF，含 JFIF 与 ICC）与 COM 注释、PNG 只保留结构必需的 `IHDR`/`PLTE`/`tRNS`/`IDAT`/`IEND`、WebP 只保留 `VP8 `/`VP8L`/`VP8X`/`ALPH`/`ANIM`/`ANMF`（并同步清掉 VP8X 标志位与 RIFF 长度）；**像素数据逐字节不动**，也不需要图像库（纯字节解析）。
  - 口径是**白名单而不是黑名单**：不认识的扩展段/块一律丢掉——嵌入载荷最省事的藏法就是"造一个私有块"，黑名单永远追不上；丢掉之后文件仍然是合法的 JPEG/PNG/WebP（APPn 与辅助块都是可选的）。留痕里已知元数据写原名（`EXIF/XMP`、`JPEG 注释`、`PNG tEXt`…），其他写成 `JPEG APP13`、`PNG 未知块(prVt)`、`WebP 未知块(xxxx)`。
  - **结构坏了就拒绝**（fail closed）：JPEG 段长度越界、缺 SOS 之后的结束标记，PNG 缺 `IEND` 或没有 `IDAT`，WebP 块长度越界或没有图像数据块——这些情况返回 `422`「凭证内容不是合法的 PNG（缺少结束块 IEND），已拒绝保存。」，而不是把一份自己都解析不了的文件存下来交给下游解码器与扫描器去赌。剥离结果写进 `metadataRemoved` 并落库，前端凭证面板会显示"已在上传时移除元数据：…"；需要完整取证链时可以把这个开关关掉，保留原始文件。PDF 不做处理（没有统一的块结构）。
  - **明确不是像素级重编码**：重编码需要图像编解码库（当前环境离线取不到）。真正的兜底是内容扫描（`http`/`clamav` 两条路与 `failMode` 的 closed/open 语义都有用例覆盖）。
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
- 尚未实现：图片像素级重新编码；真实的病毒库由部署方自己维护（协议已实现并验证，缺的是部署一个真实的 clamd 或用真实服务商替换 `http` 实现）；扫描结果的追加式历史（现在只保留最新一次结论）与按人/按订单的扫描统计。

### 订单会话（聊天）

- 只有订单双方可以读写会话消息；非参与者返回 `403`，订单不存在返回 `404`。
- 消息内容 1 至 2000 字；消息写入与通知入队在同一个事务里完成，避免“消息发出但对方收不到提示”的中间态。
- 未读按“对方发来的且未标记已读”计算：单条消息只有一个 `ReadAt`，因为会话只有两个参与者。打开会话即标记已读。
- 订单列表会为每个订单返回会话未读数（`unreadMessageCount`），前端在“消息”按钮上显示徽标。新消息通过 `order.messageCreated` 通知推送，载荷带标题与 60 字预览，完整内容走 REST。

### 运营可配置的三方集成参数

- 设置目录（白名单）在 `backend/AIToHuman.Application/Settings/SettingCatalog.cs`：目前 30 个键，分 AI 服务商、对象存储、凭证上传、内容扫描、通知推送、资金托管、运维与限流七组。只有登记在册的键才能被后台读写，`ConnectionStrings__Postgres`、`ConnectionStrings__Redis`、日志、密钥环路径这类部署级配置永远不会出现在配置表里。
- 生效值的解析顺序固定为「数据库覆盖 → 环境变量/配置文件 → 代码默认值」。删除覆盖记录就等于恢复默认，不需要额外的启用/停用开关。
- 运营接口（需管理员身份）：`GET /api/v1/admin/settings`、`GET /api/v1/admin/settings/{key}`、`PUT /api/v1/admin/settings/{key}`、`DELETE /api/v1/admin/settings/{key}`（恢复默认）、`POST /api/v1/admin/settings/{key}/test`（只读自检）、`GET /api/v1/admin/settings/audits`。
- 机密（`ai.apiKey`、`storage.s3.secretAccessKey`、`evidence.scanner.apiKey`）用 Data Protection 加密后落库，密文带 `dp1:` 前缀；接口只返回 `****末四位` 与指纹，审计记录同样只留掩码与指纹，明文只在服务端内存里出现。
- 写入成功后立即刷新进程内快照，消费方每次调用都重新读，所以改完下一轮对话/下一次上传就生效，不需要重启；另有 15 秒一次的后台轮询，用来跟上“绕过 API 直接改库”或其他实例的改动。
- 每次写入在同一事务里追加一条审计记录（键、动作、脱敏前后值、操作人、时间），覆盖行带 `Version` 并发令牌，`PUT` 可带 `expectedVersion`，冲突返回 `409`。
- 管理员名单来自部署配置 `Admin__UserIds` / `Admin__Emails`（也接受 `admin` 角色声明），刻意不放进可后台修改的配置表，避免任何能改配置的人把自己提权；没有配置管理员时运营接口对所有人返回 `403`。
- “测试连接”只做只读自检：本机存储目录是否可写、AI 服务与扫描服务是否可达且接受当前密钥。
- 前端入口：`GET /api/v1/auth/me` 会返回 `isAdmin`。运营相关的一切（配置项、变更记录、任务/用户检索、争议处置、风险复核、误拦申诉、规则目录）现在都在**独立页面 `/ops.html`** 上；主应用顶栏只对管理员显示一个“运营后台 ↗”链接（`target="_blank"`），点了跳到那个页面。隐藏入口只是提示，服务端仍会独立校验。
- 运营页面自己处理会话：没有本地令牌时显示登录卡片，登录后向 `/auth/me` 确认是不是管理员；不是管理员（不在 `Admin__UserIds` / `Admin__Emails` 名单里）就显示“这个账号没有运营权限”，不会去请求任何运营接口。页面顶部有“← 返回任务工作台”和“退出登录”。
- 配置面板按分组列出配置项，标注来源（后台已改 / 部署配置 / 默认值）与是否机密，支持保存（带 `expectedVersion`，冲突时提示刷新后重试）、恢复默认、测试连接，以及“变更记录”页签。
- 机密项在页面上只显示掩码，输入框留空表示“不修改”；要清空必须点专门的“清空”按钮，避免把 `****1234` 当成新值写回去。
- 前端文件：`frontend/ops.html`（独立入口）、`frontend/src/ops/main.ts` 与 `frontend/src/ops/OpsConsole.vue`（运营后台页面本身）、`frontend/src/api/settings.ts` 与 `frontend/src/api/admin.ts`（接口客户端）；样式复用 `frontend/src/styles.css` 的 `.setting-*` / `.admin-*` / `.ops-*` 几节。两个入口在 `frontend/vite.config.ts` 里分别声明（`rollupOptions.input`），互不打包进对方。

### 可观测性与限流

- **存活探针 `/health`**：恒返回 `200` 与 `{status:"healthy"}`，**不探测任何依赖**。这一条是刻意的：依赖挂了让编排系统重启进程解决不了问题，只会让所有实例一起抖动。子进程与冒烟脚本用的都是它。
- **就绪探针 `/health/ready`**：逐项探测依赖，任一不健康返回 `503`，编排系统据此摘流量。三项检查并行执行、各自有超时上限（`readiness.timeoutSeconds`，默认 3 秒，1–30 可调）：
  - `postgres`：能连上**且没有待应用的迁移**。只查"连得上"不够——库连得上但少一个迁移时接口照样会在运行时炸，所以顺便读一次迁移历史。
  - `redis`：只在开了 `notifications.fanout.enabled` 时才参与判定（没开扇出时 Redis 用不上，报 `skipped` 而不是 `failed`，否则单实例部署会因为没配 Redis 而永远"未就绪"）；真发一次协议 PING，走扇出组件自己的连接。
  - `storage`：本机目录模式**写一条探针对象再删掉**（能抓到"目录不存在/没权限/磁盘满"，只查存在性做不到），S3 模式做一次存在性探测（返回"不存在"也是成功，关键是请求发得出去、服务端认这份签名与凭据）。
  - 超时是**真的封顶**：`StackExchange.Redis` 的 `ConnectAsync` 没有带取消令牌的重载，只靠 `CancellationToken` 会拖到十几秒，因此单项探测包在 `Task.WhenAny` 里硬截断（实测黑洞地址下端点 3.0 秒返回 503，而不是等着建连超时）。
  - 探测抛异常一律翻译成"这一项不健康"并带上异常类型与消息，**绝不让就绪端点自己 500**——就绪端点挂掉是最没用的监控信号。
- **写接口限流**：按用户（未登录按来源 IP）限制固定一分钟窗口内的写请求数，超限返回 `429` + `Retry-After` + `X-RateLimit-Limit/Remaining/Partition`，消息是中文可读的（"一分钟内的写请求次数已达上限（N 次），请在 M 秒后重试。"）。口径与理由：
  - 只压 `/api/v1` 的 `POST/PUT/PATCH/DELETE`：读接口、`/health`、SignalR 协商都不受限——限流是为了挡脚本刷写，不是为了让人打不开页面。
  - **`/api/v1/admin/settings` 永不限流**：上限本身是配置项，如果配小了连改回来的请求都被挡，运营就会把自己锁在门外整整一个窗口（真机联调时确实撞到过这件事，因此专门留了这条"自救通道"）；其余运营写接口照常计数。
  - **必须排在幂等中间件之前**：幂等中间件会缓存非 5xx 的 JSON 响应并回放，如果限流在它之后，一个 `429` 会被当成"这个键的正常结果"缓存下来，客户端之后的重试会一直拿到 429。
  - 未登录的注册/登录也计数（按来源 IP），否则撞库没有成本；代价是同一出口 IP 后面的所有人共用一份额度，因此默认上限给得很宽（240 次/分钟，`ratelimit.writesPerMinute` 可调 1–100000）。AI 流式端点（`POST /api/v1/ai/plan/stream`）同样按写请求计数——一轮对话算一次，正常使用远达不到 240/分钟。
  - 计数是**进程内**的：多实例部署时每个实例各记一份，实际配额是"每实例 × 上限"。真正的跨实例配额需要 Redis 计数器，属于后续工作；`ratelimit.enabled=false` 是一键降级（连计数都不做）。
  - 响应头里带 `X-RateLimit-Partition`（`user:<id>` 或 `ip:<addr>`）：排查限流时最常见的问题是"我跟别人共用一个 IP"，只给剩余次数是看不出来的。
- **恢复演练**：备份对象清单（库 + 凭证文件 + **Data Protection 密钥环** + 部署配置）、RPO/RTO、可执行步骤与定期清单都写在 [备份、恢复与演练](../operations/backup-and-restore.md)，并在本机真实跑过一遍（库逐表核对一致、用恢复库把应用起起来、对象存储镜像回灌比对哈希），结果记在该文档第 4 节。

## 4. 已确定产品规则

1. 一个账号对应一个用户 ID，角色切换只改变当前操作权限。
2. AI 负责澄清、规划和建议，不得绕过用户确认发布任务。
3. AI 每轮只问一个问题，避免把表单问题清单伪装成聊天。
4. 内部任务梳理默认不可见；信息足够后才展示可检查的最终草稿。
5. 悬赏由用户结合 AI 建议确认；服务者不能竞价，用户只能在分配前主动加价。
6. 服务者先看需求方评价再报名，需求方先看服务者评价再选择。
7. 公开大厅仅展示区域级位置；精确地址和联系方式属于订单执行阶段私密信息。
8. 资金托管与账本已经落地（模拟网关），但**不接真实支付通道，也不以模拟支付冒充合规交易能力**：`payment.provider=simulated` 时钱只在平台内部记账（冻结/放款/退款/分账都可查），上线前必须接一家真实通道并补齐对账与失败重试。
9. 聊天、未读数和系统通知复用 SignalR 推送，但事实状态一律以 REST 为准：推送只是刷新提示，持久化与重试由 `notifications` 表（Outbox）保证，客户端断线重连后靠重新拉取恢复。
10. 取消订单的权限按“谁承担后果”划分：服务者只能在**开始执行前**（`Accepted`）取消，一旦开工就要由需求方发起终止，避免接了单又甩单；需求方在服务者提交验收前（`Accepted`/`InProgress`）都可以取消；提交验收后（`Submitted`）双方都不能取消，只能先验收或驳回。取消必须填原因（≤200 字），原因、取消人与时间都落库。
11. 订单取消后不留死任务：任务未过截止时间就回到大厅重新招募（该次选择作废，服务者可以重新报名），已过截止时间就直接置为过期。
12. 超过截止时间仍无人被选中的已发布任务由后台扫描置为过期并作废其报名；“已分配”的任务不参与扫描——它归订单流程管，不会因为过了截止时间就从服务者名下消失。
13. 服务者可以撤回自己**尚未被处理**的报名：记录保留为 `Withdrawn` 便于追溯，撤回后可以重新报名，任务所有者会收到通知；一旦被选中就只能走订单取消，不能悄悄退出。
14. 任务可以设可选的**报名截止时间**：它只关闭“新报名”，已经报过名的人仍然可以被选中；报名截止时间必须落在创建之后、任务截止时间之前，否则发布会被拒绝。
15. 争议是双方谈不拢时的兜底，不是常规流程：需求方在服务者提交验收后（`Submitted`）发起，服务者在验收被驳回后（`Rejected`）发起；争议期间订单冻结，双方都动不了，等平台处置。
16. 风险拦截先做确定性规则，再谈模型：禁止与转人工两档都由代码内版本化的规则目录给出，每条结论带原因代码与规则版本。禁止类别（代考、违禁品、跟踪偷拍等）**任何情况下都不能发布，人工也无权放行**；转人工类别必须由运营写明依据放行后才能发布，运营驳回是终态。
17. 被规则拦住的草稿不会被丢掉：任务照样创建出来，用户能看到自己写的内容与原因、也可以自己撤销，只是发布这一关过不去——比直接丢弃输入更容易追溯，也更容易向用户解释。
18. 草稿可以改，但“改过”就意味着重新来过：编辑只对还没发布的草稿开放，字段校验与创建时同一套；只要正文变了，之前的人工复核结论一律作废、按新文本重新判定。审核认的是文本，不是任务 ID——否则先拿干净文案过审、再改成禁止内容，门禁就形同虚设。
16. 争议只有运营能处置，且必须写明依据，三种结果都是终局动作：强制完成（订单通过并关单）、退回返工（回到执行中，返工次数累加）、终止订单（订单取消，任务回大厅或过期；这不是任何一方“取消”，因此不记录取消人）。

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
backend/AIToHuman.Api/                  # 路由、认证、AI SSE、SignalR Hub、通知派发与扇出订阅、凭证重扫、运营配置与管理接口
backend/AIToHuman.Application/         # TaskService、会话、订单会话、凭证、通知用例与扇出抽象、设置目录与配置用例
backend/AIToHuman.Contracts/           # API record 与对话/通知/消息/凭证/设置 DTO
backend/AIToHuman.Domain/              # TaskItem、Order、Review、Conversation、OrderMessage、Notification、SystemSetting
backend/AIToHuman.Infrastructure/      # EF Core/PostgreSQL、内存仓储、本机文件存储、内容扫描适配、Redis 通知扇出
backend/tests/AIToHuman.Domain.Tests/  # 领域单元测试
backend/tests/AIToHuman.IntegrationTests/ # 用例与 AI 多轮协议/SSE 集成测试（上游替身 + 内存仓储）
frontend/index.html                     # 主应用入口（对话工作台、草稿、大厅、订单、会话、凭证、评价）
frontend/ops.html                       # 运营后台入口（独立页面：配置、检索、争议、风险复核、申诉、规则目录）
frontend/src/App.vue                    # 主应用（顶栏只留一个“运营后台 ↗”入口链接）
frontend/src/ops/OpsConsole.vue         # 运营后台页面本身（从 App.vue 里拆出来的那一大块）
frontend/src/ops/main.ts                # 运营后台入口的挂载脚本
frontend/src/utils/format.ts            # 两个入口共用的时间格式化
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

### 真实依赖回归测试（PostgreSQL / Redis / 对象存储）

`backend/tests/AIToHuman.IntegrationTests/` 下有三组跑在真实依赖上的回归用例，本机装了什么就跑什么、没装就跳过，不需要手工准备：

```powershell
dotnet test AIToHuman.sln --no-build --no-restore                    # 本机有 PostgreSQL / Redis / MinIO 就会跑
$env:AITOHUMAN_TEST_POSTGRES = "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=123456"
$env:AITOHUMAN_TEST_REDIS = "127.0.0.1:6379"
$env:AITOHUMAN_TEST_S3_ENDPOINT = "http://127.0.0.1:9000"
$env:AITOHUMAN_TEST_S3_BUCKET = "aitohuman-evidence"
dotnet test AIToHuman.sln --no-build --no-restore                    # 指定依赖地址
```

**PostgreSQL**（`Postgres/`，12 条）：并发、EF 变更跟踪（`Save` 是否真的写了所有列）、乐观并发令牌、迁移从零建库、取消订单的连带效果、争议冻结、风险门禁与申诉、幂等记录、草稿版本编号。

- 连接串来源顺序：环境变量 `AITOHUMAN_TEST_POSTGRES` → `backend/AIToHuman.Api/appsettings.Development.json`（该文件不进 Git，凭据不会写进代码）。
- 测试库名带随机后缀（`aitohuman_regression_<随机>`），不会碰开发库；一次运行内共享，每个用例开始前清表，结束尽力删库。

**Redis**（`External/RedisFanoutTests.cs`，5 条）：广播的消息能被订阅方按字段原样收到；**处理函数抛异常不会中断订阅**；开关关闭时 `Enabled=false` 且广播抛异常（派发方据此退回单实例推送）；未启用时订阅立即返回；**配了连不上的 Redis 时广播会失败并返回**，不会把派发周期卡住。

**S3 兼容对象存储**（`External/S3StorageTests.cs`，5 条）：对象往返读写与删除；**预签名地址不带鉴权头就能取回字节**且附件名生效；**篡改签名 → 403**；**换对象路径 → 403**（路径参与签名）；**过期后 → 403**。

- 依赖地址与凭据来源：`AITOHUMAN_TEST_REDIS`（默认 `127.0.0.1:6379`）、`AITOHUMAN_TEST_S3_ENDPOINT` / `_REGION` / `_BUCKET` / `_ACCESS_KEY` / `_SECRET_KEY`（默认 `http://127.0.0.1:9000` / `us-east-1` / `aitohuman-evidence` / `minioadmin` / `minioadmin`，其中默认凭据只是本机开发的兜底，其他环境请用环境变量注入）。
- **对象存储的可用性是真的写一条探针对象再删掉来判定的**：桶不存在、密钥不对、服务没起来都会在这里暴露，跳过信息里会写明原因。
- **任何依赖不可用时，对应那批用例标记为“跳过”而不是失败**（`[PostgresFact]`/`[RedisFact]`/`[MinioFact]`），所以没装这些服务的机器、以及默认 CI 都能跑完其余测试；跳过信息里会写清原因。
- **过期判定由服务端负责**：有效期是按 `X-Amz-Date + X-Amz-Expires` 与服务端时钟算的，把客户端时钟往前推没有意义——那条用例是真的等 7 秒再取。
- CI 里已经起了 `postgres:16`、`redis:7`、`minio/minio` 三个服务并带 healthcheck，测试前还会用 `minio/mc` 容器把测试用的桶建出来（MinIO 起来时是空实例），所以这三组用例在流水线里是真跑的（见 `.github/workflows/ci.yml`）。

### 主机级端到端测试（真进程 + 真 HTTP）

`backend/tests/AIToHuman.IntegrationTests/Host/` 下还有一组**主机级**用例：它把**已构建的 `AIToHuman.Api.dll` 当子进程起起来**，用真 HTTP 打接口。为什么需要它——路由注册、DI 装配、中间件顺序（幂等键、异常映射、鉴权策略）、JSON 契约、启动期迁移这一层，单测与真库用例都碰不到，而这些地方出错时单测全绿、接口却不可用（本项目出现过幂等中间件取不到请求头的缺陷）。

```powershell
dotnet build AIToHuman.sln                                          # 必须先构建：用例要拉起真实的 API 程序集
dotnet test backend/tests/AIToHuman.IntegrationTests --no-build --no-restore --filter "FullyQualifiedName~HostE2ETests"
$env:AITOHUMAN_TEST_HOST = "0"                                      # 显式关掉这组用例（缺库或想快跑时用）
```

- 每个测试运行会新起一个 API 进程、配一个**随机命名的临时库**与一个**随机空闲端口**；临时库不提前创建，由应用启动时的 `Migrate()` 自己建库并应用全部迁移（顺带验证"空库能不能起来"）。`DataProtection__KeysPath` 与对象存储根目录都指向临时目录，跑完杀进程树、删库、删临时目录。
- 就绪探测只认 `/health` 返回 200（最多 90 秒）；起不来时异常消息里带子进程日志，不用去猜。
- 用了什么、跳过什么：用了 `dotnet` + 子进程 + `HttpClient`（**不依赖离线取不到的 `Microsoft.AspNetCore.Mvc.Testing`**，比进程内宿主更接近部署形态）；缺 PostgreSQL、缺已构建的程序集、或 `AITOHUMAN_TEST_HOST=0` 时整组**跳过而不是失败**（`[HostFact]`）。
- 覆盖的 6 条：真实进程健康与开发会话端点、**整条任务到资金链路的真 HTTP 走通**（含选人冻结与验收放款在账本里的两条流水）、可读错误与鉴权（重复邮箱 `409`、缺角色 `422`、非管理员 `403`、不存在资源 `404`）、管理员名单里的邮箱确实拿到运营权限、**幂等键在真管线上回放**（同键同体回放带 `Idempotency-Replayed: true`，同键不同体 `409`）、加价到高金额当场转人工并进复检队列。
- CI 里不需要新增步骤：测试前的 `dotnet build --configuration Release` 已经把 API 构建好，`dotnet test --configuration Release` 会自动跑到这批用例（工作流里那一步的说明已注明包含主机级 E2E）。
- **仍未自动化**："两个真实 API 实例 + 两个客户端"的通知扇出整链路（Redis 扇出这组用例覆盖的是"广播能到达订阅方"这一层，多实例整链路仍是手工验证）。


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

访问 <http://localhost:5173>（主应用：对话工作台、任务大厅、订单）。**运营后台是同一套前端里的独立页面**，两个地址都能用：<http://localhost:5173/ops/>（子路径，推荐）或 <http://localhost:5173/ops.html>；也可以从主应用顶栏的“运营后台 ↗”进入（只对管理员显示）。Vite 将 `/api`、`/health` 和 `/hubs` 代理到 `http://127.0.0.1:5188`，两个页面共用这个代理——**本地是同源，所以不需要配任何环境变量、也不需要开 CORS**。

`npm run build` 会产出三个 HTML：`dist/index.html`（主应用）、`dist/ops.html`（运营后台）与 `dist/ops/index.html`（同一份运营后台，让静态托管能直接按 `/ops/` 访问，不必为子路径写服务端规则），外加共享的分包。静态托管时整个 `dist` 一起发布；运营后台不依赖主应用，单独打开也能用。

前端可配置项写在 `frontend/.env.example` 里（复制成 `.env.local` 再改，默认全空 = 同源）：
`VITE_API_BASE_URL`、`VITE_HUB_BASE_URL`、`VITE_APP_HOME_URL`、`VITE_OPS_HOME_URL`。

### 把运营后台放到自己的域名或子路径

本地开发不需要域名：上面的 `/ops/` 就是子路径形态，`npm run dev` 已经按它跑。等有了真实域名再按下面的方式落配置——**推荐的放法是子域名**（运营后台 `ops.example.com`、主应用 `app.example.com`、后端 `api.example.com`）：三个来源各自独立，谁也不影响谁，运营后台也不必和主应用抢同一份静态托管的路径规则。

两种放法（构建产物相同，区别只在托管与配置）：

| 放法 | 访问地址 | 构建时的变量 | 后端要放行的来源 |
| --- | --- | --- | --- |
| 子域名（推荐） | `https://ops.example.com/` | `VITE_API_BASE_URL=https://api.example.com`<br>`VITE_APP_HOME_URL=https://app.example.com/`<br>`VITE_OPS_HOME_URL=https://ops.example.com/` | `Cors__AllowedOrigins=https://ops.example.com,https://app.example.com` |
| 子路径（同域） | `https://example.com/ops/` | 一般不需要（同源相对路径）<br>跨源时才填 `VITE_API_BASE_URL` | 同源不需要 CORS；跨源时列出前端来源 |

- 变量在**构建前**通过环境变量或 `frontend/.env.local` 传入，例如：`$env:VITE_API_BASE_URL='https://api.example.com'; npm run build`。含义见 `frontend/src/api/base.ts` 顶部与 `frontend/env.d.ts`。前端所有请求都经过 `apiFetch()`，不存在“漏改一处、线上 404”的散落拼接。
- 跨源时后端必须放行来源，否则浏览器在预检阶段就拦掉请求：`Cors__AllowedOrigins`（逗号/分号/空格分隔，也接受 `Cors__AllowedOrigins__0=` 数组写法，规则见 `Program.cs` 里的 `ReadList`）。**没配置时的兜底按环境区分**：开发环境放行本机 5173 / 4173（本地彩排用），其它环境一个都不放行（同源部署本来不需要 CORS）；生效清单每次启动都打进日志，配没配一眼可见。策略同时开了 `AllowCredentials`：SignalR 的 JS 客户端协商默认带 credentials，不开这一项会出现“页面功能都正常、只有通知订阅在控制台报 CORS 错误”。接口认证走 Bearer 令牌、不用 Cookie，来源又是明确列出的，所以这不构成额外的跨站请求伪造面。
- 子路径托管：构建产物已经带了 `dist/ops/index.html`，**任何会按目录找 index.html 的静态托管直接把 `/ops/` 指过去就能用**（资源地址是根路径绝对的 `/assets/...`）。要自己写规则也行，nginx 例：

```nginx
root /srv/aitohuman/dist;
location /ops/ { try_files $uri /ops/index.html; }   # 运营后台在子路径（dist/ops/index.html 已由构建产出）
location / { try_files $uri /index.html; }          # 主应用（前端自带兜底，可按需换成 =404）
location /api/ { proxy_pass http://127.0.0.1:5188; }
location /hubs/ { proxy_pass http://127.0.0.1:5188; proxy_set_header Upgrade $http_upgrade; proxy_set_header Connection "upgrade"; }
```

- 本地也能按子路径试：`npm run dev` / `npm run preview` 已内置 `/ops` 与 `/ops/` → `/ops.html` 的改写（见 `vite.config.ts` 的 `opsSubpath` 插件），所以“本地怎么访问、线上就怎么访问”。
- 想在没有域名的前提下彩排“跨源部署”，用本机两个端口就够了：`npm run build` 后用 `npm run preview`（4173）或任意静态服务器当第二个源、API 仍在 5188，给 API 加上 `Cors__AllowedOrigins=http://localhost:4173`（或静态服务器实际使用的来源）即可；第 11 节有实测记录。
- 两个入口的登录态是**按来源隔离**的（JWT 存在 `localStorage`）：运营后台在自己的域名下有自己的登录卡片，不需要、也不会共享主应用的会话。这正是独立页面该有的行为，部署时不必额外做单点登录。

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
- 推送的事件类型目前有 `order.created`（选人）、`order.statusChanged`（开始、提交、驳回、返工、验收）、`order.messageCreated`（新消息）、`order.cancelled`（订单取消）、`order.disputed`（进入争议）、`order.disputeResolved`（运营处置完成）、`task.expired`（任务到期自动过期）、`task.cancelled`（任务被撤销/下架）和 `task.applicationWithdrawn`（有人撤回报名）；收件箱与未读数走 REST，推送只是刷新提示。
- 多实例投递：派发前先用条件更新原子认领（只有影响 1 行的实例负责推送），启用扇出后认领方广播到 Redis 频道 `aitohuman:notifications:fanout`，各实例推给本机客户端；未配 `ConnectionStrings__Redis` 或开关 `notifications.fanout.enabled` 为 false 时退化为单实例直接推送，Redis 不可用时撤回认领并每 2 秒重试。与运营配置的接线见第 3 节。

双浏览器验证：A 以 owner 发布任务，B 以 worker 报名，A 选择 B；B 无需刷新应立即收到通知并更新“我接取的任务”。Network 面板应看到 `/hubs/notifications` 的 WebSocket 或长轮询连接。

## 9. API 与 Hub 清单

所有业务 JSON 使用 camelCase。已认证的写请求可以带 `Idempotency-Key` 请求头做重试去重（命中时回放原响应并带 `Idempotency-Replayed: true`，详见第 10 节）。

| 方法 | 路径 | 认证/说明 |
| --- | --- | --- |
| GET | `/health` | 存活探针（恒 `200`，不探测依赖） |
| GET | `/api/v1/session/dev` | 仅 Development 合成会话 |
| POST | `/api/v1/auth/register` | 匿名注册 |
| POST | `/api/v1/auth/login` | 匿名登录 |
| POST | `/api/v1/auth/switch-role` | JWT，切换 owner/worker |
| GET | `/api/v1/auth/me` | JWT，当前用户；带 `isAdmin` 供前端决定是否展示运营配置入口 |
| GET | `/health/ready` | 就绪探针（依赖感知）：探测数据库（含"有没有待应用的迁移"）、Redis（仅在开扇出时）与文件存储，任一失败返回 `503`，响应含每项的 `name`/`status`(`ok`/`failed`/`skipped`)/`detail`/`durationMs`；单项超时由 `readiness.timeoutSeconds` 控制 |
| POST | `/api/v1/conversations` | 创建对话会话，写入开场白 |
| GET | `/api/v1/conversations/{id}?userId=...` | 读取历史与当前草稿，用于刷新恢复；仅所有者可读 |
| GET | `/api/v1/conversations?userId=...&limit=...` | 该用户的会话列表（含预览与是否有草稿） |
| POST | `/api/v1/ai/plan/stream` | 多轮 AI 澄清，SSE；按 `conversationId` 读取服务端历史并落库完成的回合 |
| POST | `/api/v1/reward-suggestions` | 本地规则悬赏建议 |
| GET | `/api/v1/tasks?district=&minReward=&maxReward=&limit=&cursor=` | 大厅列表，游标分页与筛选 |
| GET | `/api/v1/tasks/mine?ownerId=...` | 所有者查看自己的全部任务及状态（草稿、已发布、已分配、已结束） |
| GET | `/api/v1/tasks/{id}` | 公开任务详情；草稿仅所有者可见，其他人 404 |
| GET | `/api/v1/tasks/{id}/execution-address` | 精确执行地址；仅所有者与被选中的服务者（其余 403）；**每次读取都留痕** |
| POST | `/api/v1/tasks` | owner 创建任务草稿 |
| PUT | `/api/v1/tasks/{id}` | 所有者编辑草稿（仅 `ReadyToPublish`；字段校验与创建一致，改完重判风险并作废原人工复核结论，同时追加一版快照） |
| GET | `/api/v1/tasks/{id}/revisions?ownerId=...` | 草稿历史（仅所有者）：每版快照、变更摘要、当版风险结论，以及**与上一版的逐字段差异**（`changes`） |
| POST | `/api/v1/tasks/{id}/revisions/{revision}/restore?ownerId=...` | 回滚到某一版（仅所有者、仅草稿）：走编辑同一套校验与风险重判，回滚也追加一版，中间版本不丢 |
| POST | `/api/v1/tasks/{id}/publish` | 所有者发布 |
| POST | `/api/v1/tasks/{id}/increase-reward` | 分配前加价 |
| POST | `/api/v1/tasks/{id}/applications` | worker 报名 |
| GET | `/api/v1/tasks/{id}/applications?ownerId=...` | 所有者查看报名，每条带该服务者的公开评价摘要（`workerAverageRating` / `workerReviewCount`，盲期不计入） |
| POST | `/api/v1/tasks/{id}/applications/{applicationId}/select` | 选人并创建订单 |
| GET | `/api/v1/tasks/{id}/order` | 当前实现的订单查询入口，需参与者身份 |
| GET | `/api/v1/orders?userId=...` | 当前用户订单列表，含各订单会话未读数 |
| POST | `/api/v1/orders/{id}/start` | worker 开始履约 |
| POST | `/api/v1/orders/{id}/submit` | worker 提交凭证或完成说明 |
| POST | `/api/v1/orders/{id}/approve` | owner 验收通过 |
| POST | `/api/v1/orders/{id}/reject` | owner 驳回并说明原因 |
| POST | `/api/v1/orders/{id}/resume` | worker 按驳回原因返工，`Rejected → InProgress` |
| POST | `/api/v1/orders/{id}/cancel` | 取消订单；服务者仅 `Accepted`、需求方到 `Submitted` 之前，原因必填；任务回到大厅或直接过期 |
| POST | `/api/v1/tasks/{id}/cancel` | 所有者撤销自己的任务（草稿或已发布未被选中），原因必填；报名的服务者会收到通知 |
| POST | `/api/v1/tasks/{id}/applications/{applicationId}/withdraw` | 服务者撤回自己尚未被处理的报名；撤回后可重新报名 |
| GET | `/api/v1/tasks/applications/mine?workerId=...` | “我的报名”：每条报名的状态与是否还能撤回 |
| POST | `/api/v1/orders/{id}/dispute` | 发起争议（需求方 `Submitted`、服务者 `Rejected`），原因必填；争议期间订单冻结 |
| GET | `/api/v1/admin/orders?status=&limit=` | 运营：争议订单列表（默认只看待处置，`all` 看全部） |
| POST | `/api/v1/admin/orders/{id}/resolve` | 运营处置争议：`Approve`/`Rework`/`Cancel` + 必填依据，进审计并通知双方 |
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
| GET | `/api/v1/admin/address-access?taskId=&viewerId=&limit=` | 精确地址的访问留痕（含被拒绝的尝试），返回条目与同一过滤条件下的拒绝次数 |
| GET | `/api/v1/admin/tasks?keyword=&status=&limit=` | 跨所有者检索任务（标题/描述/区域模糊匹配 + 状态过滤） |
| GET | `/api/v1/admin/tasks/{id}` | 任务详情（含报名者与订单状态） |
| POST | `/api/v1/admin/tasks/{id}/cancel` | 人工下架（必须给原因，原因进运营审计；已分配任务返回 422） |
| GET | `/api/v1/admin/users?keyword=&limit=` | 用户检索（邮箱/昵称）；无 PostgreSQL 时返回空列表 |
| GET | `/api/v1/admin/risk/reviews?limit=` | 风险复核队列（判定为“需人工复核”的任务，按创建时间升序） |
| POST | `/api/v1/admin/risk/reviews/{taskId}/decide` | 放行或驳回风险复核（`decision=Approve\|Reject`，依据必填 ≤200 字）；禁止类别不在队列里，调用返回 422 |
| GET | `/api/v1/admin/risk/rules` | 风险规则目录自述（版本、高金额阈值、规则清单与匹配词数量，不返回匹配词） |
| GET | `/api/v1/admin/risk/rules/detail` | 规则明细（含匹配词、`isBuiltIn`、最近一版的摘要/依据/操作人） |
| GET | `/api/v1/admin/risk/rules/versions?limit=` | 规则目录版本历史（倒序，含摘要、依据、操作人、当版条数/禁止条数/阈值/时段） |
| POST | `/api/v1/admin/risk/rules` | 整份替换规则目录（版本号自动 +1，依据必填 ≤200 字）；`expectedVersion` 不一致 409，校验不过 422 |
| POST | `/api/v1/admin/risk/rules/reset` | 恢复到代码内置目录（同样追加一版，历史不丢；依据必填） |
| POST | `/api/v1/admin/risk/recheck?limit=` | 立刻跑一轮发布后复检（返回扫描/仅刷新/要求复检/自动下架/冻结订单/跳过的条数；后台每 5 分钟也会自动跑） |
| POST | `/api/v1/tasks/{id}/risk-appeals` | 所有者提交误拦申诉（理由必填 ≤500 字）；只有被禁止类别命中或转人工被驳回的任务可以申诉，同一版只能申诉一次，且受「单任务 3 次 / 单人 24 小时 5 次」节流 |
| GET | `/api/v1/admin/risk/appeals?limit=` | 误拦申诉队列（按提交时间升序），返回 `canBeReleasedByAppeal`（能否真的放行）与 `appealCount`（累计申诉次数） |
| GET | `/api/v1/admin/risk/appeals/{taskId}/history` | 某条任务的申诉轨迹（每次提交时的规则代码/版本/结论、理由、运营结论与依据，以及当前生效的两条上限） |
| POST | `/api/v1/admin/risk/appeals/{taskId}/decide` | 处置申诉（`decision=Accept\|Deny`，依据必填 ≤200 字）；禁止类别即使 Accept 也不会放行 |
| GET | `/api/v1/admin/risk/decisions?taskId=&limit=` | 某条任务的风险判定轨迹（只追加历史：原因 `Created`/`Edited`/`Restored`/`Published`/`RewardRaised`/`Rechecked`、结论、原因代码、类别、规则版本、当时悬赏与判定时刻；按时间升序，最多 200 条）。不带 `taskId` 或任务不存在都返回空数组而不是报错（运营页面永远带 `taskId`） |
| GET | `/api/v1/admin/risk/stats?days=` | 规则命中看板（窗口 1–365 天，默认 30，最多取 2 万条原始留痕）：判定总数与各结论计数、复检次数，以及按（原因代码 + 结论）分组的 `hits`/`recheckedHits` 与窗口内**被裁定**为误伤的 `acceptedAppeals`（按裁定时刻计） |
| GET/WS | `/hubs/notifications` | SignalR 主动通知；推送 `notification.created` 信封 |

普通 API 错误使用 Problem Details，主要映射为 `400`、`401`、`403`、`404`、`409`、`422`、`502` 和 `504`。`409` 既用于资源冲突（例如邮箱已注册，`ConflictException`），也用于乐观并发冲突。**用户能看懂的错误要给出原因**：`ValidationException`（请求本身写错，如缺 `role`、密码过短）→ `422` + 原样消息，`ConflictException` → `409` + 原样消息，`DomainException` → `422` + 原样消息，AI 侧未配置 → `502` + 可行动的排查线索；**未预期的 `InvalidOperationException` 仍然只返回通用文案**（它的消息可能带内部细节，不外泄）。AI SSE 在响应开始后的错误使用流内 `error` 事件。

写请求还有一个额外的失败码：`429`。按用户（未登录按来源 IP）限制固定一分钟窗口内的写请求数，超限返回 Problem Details + `Retry-After` + `X-RateLimit-Limit/Remaining/Partition`；读接口与 `/health` 不受限流影响，`/api/v1/admin/settings` 作为"自救通道"永不限流（否则把上限配小之后就改不回来了）。口径与理由见第 3 节「可观测性与限流」。

## 10. 数据库、权限与状态

Development + PostgreSQL 启动时改为应用 EF Core 迁移（`Database.Migrate()`），不再使用 `EnsureCreated()` 和幂等建表 SQL。生产环境由部署流程执行迁移，不在应用启动时自动迁移。

迁移历史（29 个）：`AddUsers` → `AddOrders` → `AddOrderEvidence` → `AddReviews` → `AddOrderRework` → `AddConversations` → `AddTaskTables` → `AddConcurrencyTokens` → `AddNotifications` → `AddOrderMessages` → `AddTaskExecutionAddress` → `AddEvidence` → `AddSystemSettings` → `AddEvidenceScanAttempts` → `AddEvidenceMetadataRemoved` → `AddAdminAudit` → `AddOrderCancellationAndTaskExpiry` → `AddApplicationDeadline` → `AddOrderDispute` → `AddTaskRiskAssessment` → `AddTaskDraftRevisions` → `AddIdempotencyEntries` → `AddRiskAppeal` → `AddRiskRuleCatalogRevisions` → `AddTaskRiskEnforcement` → `AddTaskRiskAppeals` → `AddAddressAccessEntries` → `AddOrderEscrowAndLedger` → `AddRiskDecisionEntries`。其中 `AddTaskTables` 补上了此前只由 `EnsureCreated()` 建出、从未纳入迁移的 `tasks` 与 `task_applications` 两张核心表；`AddConcurrencyTokens` 给 `tasks`/`orders` 加 `Version` 乐观并发令牌；`AddTaskExecutionAddress` 给 `tasks` 加参与者层的精确执行地址；`AddEvidence` 建 `evidence` 表；`AddSystemSettings` 建运营配置的两张表；`AddEvidenceScanAttempts` 给 `evidence` 补上扫描尝试次数、最近一次说明与尝试时间；`AddEvidenceMetadataRemoved` 补上“已移除元数据”说明与按人限速用的索引；`AddAdminAudit` 建运营操作审计表 `admin_audit_entries`（人工下架等动作只追加留痕）；`AddOrderCancellationAndTaskExpiry` 给 `orders` 加取消三列、给 `tasks` 加 `ExpiredAt`/`CancelledAt`/`CancellationReason`；`AddApplicationDeadline` 给 `tasks` 加可选的报名截止时间；`AddOrderDispute` 给 `orders` 加争议六列并补 `(Status, CreatedAt)` 索引供运营按状态检索；`AddTaskRiskAssessment` 给 `tasks` 加风险判定的 10 列并补 `(RiskReviewStatus, CreatedAt)` 索引供复核队列扫描——存量行用数据库默认值 `Allowed`/`NotRequired` 回填，读取时对未知取值也做防御性解析（历史行不会因为枚举名不认识而读不出来）；`AddTaskDraftRevisions` 建草稿历史表 `task_draft_revisions`（一行一个版本的完整快照 + `ChangeSummary` + 当版风险结论）并给 `(TaskId, Revision)` 建唯一索引；`AddIdempotencyEntries` 建幂等记录表 `idempotency_entries`（主键 `(UserId, Key)` + `StartedAt` 索引）；`AddRiskAppeal` 给 `tasks` 加误拦申诉的 6 列并补 `(RiskAppealStatus, RiskAppealedAt)` 索引供申诉队列扫描；`AddRiskRuleCatalogRevisions` 建风险规则目录的版本表 `risk_rule_catalog_revisions`（一行一版完整 JSON 快照 + 变化摘要 + 变更依据 + 操作人）并给 `Version` 建唯一索引、`CreatedAt` 建索引；`AddTaskRiskEnforcement` 给 `tasks` 加发布后风控处置的三列（`RiskEnforcementStatus` 带默认值 `None`，存量行回填成"没有处置过"）并补 `(Status, RiskRuleVersion)` 索引供复检扫描；`AddTaskRiskAppeals` 建申诉留档表 `task_risk_appeals`（一次申诉一行，含提交时的规则代码/版本/结论与理由、运营结论与依据）并给 `(TaskId, SubmittedAt)`（看轨迹）与 `(OwnerId, SubmittedAt)`（按人算节流次数）各建索引；`AddAddressAccessEntries` 建精确地址的访问留痕表 `address_access_entries`（一次读取一行，含被拒绝的尝试；`ViewerId` 可空表示匿名）并给 `(TaskId, OccurredAt)`（看这条地址被谁看过）与 `(ViewerId, OccurredAt)`（看这个人查过多少地址）各建索引；`AddOrderEscrowAndLedger` 建资金账本表 `ledger_entries`（一行一次账户间转账，只追加）并给 `(OrderId, OccurredAt)` 与 `OccurredAt` 各建索引，同时给 `orders` 加 7 个托管字段（`EscrowStatus` 带默认值 `None`，存量订单回填成"没有托管"）与 `(EscrowStatus, CreatedAt)` 索引供运营排查"托管还没结清"的订单；`AddRiskDecisionEntries` 建风险判定的只追加历史表 `risk_decision_entries`（一次判定一行，含原因、结论、原因代码、类别、规则版本、当时的悬赏金额与判定时刻）并给 `(TaskId, OccurredAt)`（看一条任务的判定轨迹）与 `(OccurredAt, RuleCode)`（按原因代码出统计）各建索引。

早期 `EnsureCreated()` 建出的本地库没有迁移历史记录。启动逻辑会检测这种情况：先用模型对比物理表的「表名.列名」，只有结构完全对得上时，才把当时已有的迁移整体标记为已应用并打警告日志；一旦缺表或缺列就直接报错说明缺了什么，提示删除重建，避免在错误的 schema 上继续运行。基线化之后新增的迁移会正常应用——例如 `AddConcurrencyTokens` 就是在基线化之后自动补上的 `Version` 列。目标库不存在时由 `Migrate()` 负责建库。

对话数据：`conversations` 属于单个用户，`conversation_messages` 按 `(ConversationId, Sequence)` 唯一，序号在历史截断后仍保持单调递增。消息写入后不可变，仓储只做新增与删除。

订单会话数据：`order_messages` 按 `(OrderId, CreatedAt)` 建索引，只有订单双方可读写；未读用“发送者不是查看者且 `ReadAt` 为空”判定，因此一条消息只有一个已读时间。

任务隐私分层：`tasks.District` 是公开层（大厅展示）；`tasks.ExecutionAddress`（≤200 字，可空）属于参与者层，只在订单成立后按角色披露，且从不进入大厅列表与公开详情。精确地址的访问审计**已实现**（迁移 27 建 `address_access_entries`，一次读取一行，含被拒绝的尝试，见第 11 节本轮条目）。

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

写接口的幂等键：

- 已登录用户在 `/api/v1` 下的写请求（POST/PUT/PATCH/DELETE）可以带请求头 `Idempotency-Key`，服务端按「用户 + 键」记录并**回放上一次的响应**：客户端超时重试不会把加价、报名、选人、提交、状态转换这类动作做两遍。**不带该头时行为完全不变**，所以是可选能力。
- 回放命中时返回原来的状态码与响应体，并加响应头 `Idempotency-Replayed: true`。实测：发布接口第一次 `200 Published`，同一个键重试依旧是 `200 Published`（带回放头），而**换一个键**重试会拿到 `422 任务当前状态 Published 不允许该操作`——这正是幂等键要解决的问题。
- 同一个键配**不同请求体**返回 `409`（请求指纹 = 方法 + 路径 + 查询串 + 请求体 的 SHA-256）：客户端复用键是错误，不能拿旧响应糊过去。
- 主键是 `(UserId, Key)`，并发请求里只有一个能占位，另一个若发现记录尚未完成返回 `409`「正在处理中」；占位后 2 分钟仍未完成（上次执行中途挂了）视为过期，允许重试真正执行；键按用户隔离，不同用户可以用相同的键。
- 不缓存的情形：`5xx`（服务端出错时重试应当真的重跑）、响应体超过 32000 字符、非 JSON 响应（例如文件下载）以及业务抛异常——这些情况下占位会被清掉，重试会重新执行。
- 不参与幂等的请求：匿名请求（`/api/v1/auth/*`、`/api/v1/session/*`）与 SSE 流 `/api/v1/ai/plan/stream`（长连接，回放没有意义）。
- 存储是 `idempotency_entries`（迁移 22），中间件在认证/授权之后执行。
- **清理是自动的**：`IdempotencyCleanupService`（后台任务，每小时一轮）按保留策略删除过期记录——已完成记录保留 24 小时、未完成占位保留 10 分钟，单次最多 500 条（分档判定，避免把刚开始处理的请求清掉）；启动日志会打印策略原文，清理失败只记警告并在下一轮重试。客户端的重试窗口只有几秒到几分钟，24 小时足够覆盖真实场景。
- **尚未实现**：ETag/版本字段返回，以及前端「自动生成并复用幂等键」的改造（目前前端按钮是置灰防重复点击，没有真正用上这个头）。

```text
Task:  ReadyToPublish → Published → Assigned → Closed
                         └→ Expired / Cancelled（到期自动过期、所有者撤销与运营下架都已接入）

Order: Accepted → InProgress → Submitted → Approved
                      ↑              │
                      └── Rejected ←─┘   （POST /orders/{id}/resume 返工）
                         └→ Disputed（争议发起与运营处置已接入）/ Cancelled（双方按阶段取消已接入）
```

当前限制：返工期间任务保持 `Assigned`，不会重新出现在大厅；订单 `Approved` 后任务自动 `Closed`，但返工次数没有上限。取消、到期自动过期与争议处置都已经可用（规则见第 4 节，实现见第 3 节）。风险门禁是任务侧的最后一道闸：被规则判为禁止类别的任务永远进不了大厅，判为需人工复核的任务要等运营放行。

## 11. 验证结果与命令

2026-09-12 整理的验证记录。第一批条目是在提交 `e45da76` 上做的（工作区干净）；之后每一轮的结果按「本轮（…）新增验证」分段列在后面（**越新的轮次排在越靠前**，最上面的是最近一轮）。每条都写明了当时的环境与提交。

- `dotnet build AIToHuman.sln --no-restore`：通过，0 警告、0 错误。
- 领域测试：58/58 通过。覆盖任务加价与报名约束、执行地址校验与分阶段披露、订单履约与返工闭环、批准后关单、对话回合不变量与历史截断、通知字段校验与标记幂等、订单会话消息校验与未读语义、执行凭证的类型/大小/签名校验与扫描状态机、本地偏移量时间归一化和角色校验文案。
- 集成测试：114/114 通过（`AIToHuman.IntegrationTests`）。覆盖 AI 多轮协议、输出严格校验与重试、SSE 线格式、会话用例、事务边界、通知骨干、订单会话、评价盲期、大厅分页筛选与地址披露，以及执行凭证（上传/下载/权限/扫描门禁/上限）。
- 通知端到端（真实 PostgreSQL + 真实 WebSocket 客户端）：连上 `/hubs/notifications` 后选人 → 收到 `{"type":1,"target":"notification.created","arguments":[{eventId,type:"order.created",version:1,occurredAt,payload:{orderId,status,title}}]}`；`GET /api/v1/notifications` 返回 1 条未读，`POST /read` 返回 `{"unreadCount":0}` 且 `readAt` 已写入；无关用户收件箱为空；服务者开始执行并提交后，需求方收到 2 条 `order.statusChanged`，最新状态 `Submitted`。
- 订单会话端到端（真实 PostgreSQL）：需求方连发两条 → 服务者视角消息 2 条、未读 2、订单列表徽标 `unreadMessageCount=2`、收到 2 条 `order.messageCreated` 且预览为最后一条内容；服务者回复后标记已读返回 `{"unreadCount":0}`，需求方此时未读 1（服务者那条）；无关用户读取与发送均 `403`。
- 分页与披露端到端（真实 PostgreSQL）：5 个同截止时间任务 + 历史任务共 9 条，`limit=2` 逐页翻完 9 条且无重复；`district` 与 `minReward/maxReward` 筛选各命中 2/3 条；非法区间与非法游标返回 `400` 与中文原因；草稿详情所有者 `200`、他人与匿名 `404`；执行地址选人前仅所有者 `200`、报名者 `403`，选人后被选中 `200`、未被选中 `403`；大厅与公开详情序列化结果中不含地址文本。
- 执行凭证端到端（真实 PostgreSQL + 本机存储目录，multipart 上传 67 字节 PNG）：上传 `200`（`scanStatus=Clean`、SHA-256 摘要、`isDownloadable=true`）→ 需求方列表 1 条 → 下载 `200`、字节完全一致、`Content-Type: image/png`、`X-Content-Type-Options: nosniff`、`Content-Disposition` 为系统生成的 `evidence-<id>.png`；无关用户列表与下载均 `403`；需求方上传 `403`；`text/plain` 与伪造 PNG 各返回 `422` 与对应原因；5 MB + 1 字节返回 `413`；落盘路径为 `<根目录>/<orderId>/<evidenceId>.png`，不含任何用户输入。
- `npm run typecheck`：通过。
- Vite 生产构建：通过；若默认 `frontend/dist` 被运行中的进程占用，先停止该进程或输出到临时目录。
- EF Core 迁移（本条写于当时）：那时共 14 个（当前为 20 个，清单见第 10 节）；当时新增 `AddOrderRework`（`orders` 两列）、`AddConversations`（会话两表）、`AddTaskTables`（补上此前缺失的 `tasks`、`task_applications`）、`AddConcurrencyTokens`（`Version` 列）、`AddNotifications`、`AddOrderMessages`、`AddTaskExecutionAddress`、`AddEvidence`、`AddSystemSettings`、`AddEvidenceScanAttempts`（扫描尝试次数与说明）。
- 全新数据库路径（当时含全部 14 个迁移）：另起空库 `aitohuman_evidence_check` 启动 API，`Migrate()` 从零建库并应用全部 14 个迁移，随后在该库上完成下面的凭证扫描闭环用例。
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
- 运营配置目录当时共 20 个键、四个分组：AI 服务商 / 对象存储 / 凭证上传 / 内容扫描（现在是 30 个、七个分组：凭证上传组多了直连下载有效期、元数据开关、按人配额，内容扫描组多了 ClamAV 的地址与端口，通知推送、资金托管与运维及限流三组是后来加的）lamAV 的地址与端口，通知推送组是后加的多实例扇出开关）。
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

- 领域单元测试 163 个（`AIToHuman.Domain.Tests`）：本轮新增 23 个覆盖报名撤回与争议（报名截止时间的构造与发布校验、到点后拒绝新报名但保留已报名、只能撤回自己的且仅限待处理、撤回后可重新报名、选中后不可撤回；争议的发起角色×阶段矩阵、原因必填与上限、争议期间订单冻结、三种处置结果分别对订单与任务的影响、处置依据必填与只能处置一次、争议留痕的 rehydrate）；此前新增 22 个覆盖取消与过期；其余覆盖任务加价、禁止自己报名、禁止重复报名、选择服务者、订单参与者权限、履约状态流、返工闭环与批准后关单、执行地址校验；对话回合不变量、历史窗口与截断；通知字段校验与标记幂等；订单会话消息校验与未读语义；执行凭证的类型/大小/签名校验、扫描状态机与扫描尝试记账、上传限额边界、元数据剥离（JPEG/PNG/WebP 的段结构、损坏文件不改写）；配置键形状、取值上限、版本自增与审计脱敏。
- 集成测试 243 个（`AIToHuman.IntegrationTests`）：本轮新增 9 个覆盖争议（发起与冻结、双方发起权限、三种处置结果对任务的影响、单一工作单元、审计、只允许处置争议中的订单、运营列表默认只看待处置与非法入参）；此前新增 14 个覆盖取消与过期、6 个覆盖报名撤回与报名截止；其余覆盖 AI 多轮协议、输出严格校验与可控重试、SSE 线格式、会话用例、`IUnitOfWork` 事务边界、通知骨干、多实例派发语义、订单会话、评价盲期、大厅分页筛选与地址披露、执行凭证（上传/下载/权限/扫描门禁/上限/重扫闭环/直连地址签发/元数据剥离/按人配额），ClamAV 的 INSTREAM 协议与扫描实现分派，S3 兼容存储（请求形态、签名确定性、预签名参数与有效期、403/404 映射、缺配置不发请求）、EF 模型快照，以及运营配置（设置目录校验、解析顺序、加密与脱敏、审计、并发冲突、自检、管理员名单判定与优先级、AI 配置热更新）。全部走上游替身与内存仓储，不需要网络和数据库。
- 尚缺：认证与授权、PostgreSQL 仓储的自动化测试（目前只有手工端到端验证）、SignalR 重连与派发失败重试、运营后台的其余能力；主机级端到端测试**已经补上**（见第 7 节"主机级端到端测试"——真进程 + 真 HTTP，不依赖离线取不到的 `Microsoft.AspNetCore.Mvc.Testing`）。et 无法还原 `Microsoft.AspNetCore.Mvc.Testing`，所以没有 `WebApplicationFactory` 用例；网络可用后补该包，就能把本节的手工联调步骤逐步自动化。

本轮（运营后台检索与人工下架）新增验证：

- 运营后台端到端（真实 PostgreSQL + 真实 JWT）：管理员按关键字 `takedown` 检索到 1 条任务并带回 `ownerEmail` 与状态；按 `status=Published` 过滤命中 2 条；`GET /admin/users?keyword=ops.` 命中 2 条；任务详情返回状态与验收标准。
- 人工下架：`POST /admin/tasks/{id}/cancel` 返回 `200`、状态变 `Cancelled`，该任务随即从大厅（匿名 `GET /api/v1/tasks`）消失；`GET /admin/audits` 出现 1 条 `action=task.cancel`、`targetType=task`、原因与提交内容一致、`actorId` 等于管理员用户 id 的记录。
- 失败的操作不留痕迹：审计行数在下架后为 1；同一轮联调里紧接着的另外两步（报名、选人）中的选人步骤失败了（见第 12 节的已知线索），因此“已分配任务拒绝下架”这条规则在这一轮没有通过真实链路复现，它目前由领域单元测试覆盖（8 条：草稿/已发布可下架、已分配与已关闭被拒、原因必填与长度上限、审计字段与 UTC 归一化）。
- 脚本侧两次自摆乌龙（与产品无关，记录以免重复踩）：E2E 脚本首次混入中文导致 PowerShell 5.1 按 ANSI 读而语法错误；随后又用了不存在的字段名 `rewardAmount`（契约里是 `reward`）却未检查创建任务的返回状态，导致后续步骤全部 404。

本轮（报名撤回/报名截止时间与争议处理）新增验证：

- 迁移与 schema：真实 PostgreSQL 上依次应用第 18、19 个迁移，`information_schema` 里能看到 `tasks.ApplicationDeadline` 与 `orders` 的 `DisputeReason`/`DisputeOpenedBy`/`DisputeOpenedAt`/`DisputeResolution`/`DisputeResolutionNote`/`DisputeResolvedAt`，`__EFMigrationsHistory` 计数为 19。
- 报名撤回：`GET /api/v1/tasks/applications/mine` 返回 1 条 `Pending` 且 `canWithdraw=true`；撤回返回 `Withdrawn`、`canWithdraw` 变 false、所有者收件箱出现 `task.applicationWithdrawn`；服务者随后可以重新报名（该任务下变成 `Pending` + `Withdrawn` 两条记录），再次重复报名被 `422` 拦住。
- 报名截止时间：带 30 秒报名窗口的任务发布时 `acceptingApplications=true`；窗口过后新报名返回 `422`、公开详情 `acceptingApplications=false` 且 `applicationDeadline` 有值，但**窗口内已报名的人仍然被成功选中**（任务变 `Assigned`、订单 `Accepted`）——即“到点只关新报名”。
- 争议发起与冻结：服务者提交验收后需求方 `POST /orders/{id}/dispute` 返回 `Disputed`（含原因与发起人），服务者收到 `order.disputed`；争议期间需求方取消与验收都被 `422` 拦住。
- 运营处置：`GET /api/v1/admin/orders` 默认只列出这条待处置争议（含 `disputeReason`）；`Rework` 处置后订单回到 `InProgress`、`reworkCount=1`，服务者能继续提交；再次提交后需求方再发起争议、运营 `Approve` 后订单 `Approved` 且任务 `Closed`；服务者在 `Rejected` 发起争议后运营 `Cancel` 得到订单 `Cancelled`（`cancelledBy` 为空）、任务回到 `Published`；双方都收到 `order.disputeResolved`。
- 审计与错误路径：`GET /api/v1/admin/audits` 里能看到 `order.dispute.rework`、`order.dispute.approve`、`order.dispute.cancel`；验收通过后再发起争议返回 `422`。
- 说明：用匿名合成用户跑这个 E2E 时，运营争议列表里的双方邮箱为空——那些用户没有注册进 `users` 表；注册用户的真实联调会带出邮箱（用户目录按 ID 查库）。

本轮（订单取消与任务过期）新增验证：

- 迁移与 schema：真实 PostgreSQL 上启动后应用第 17 个迁移 `AddOrderCancellationAndTaskExpiry`，`information_schema` 里能看到 `orders.CancelledAt/CancelledBy/CancellationReason` 与 `tasks.ExpiredAt/CancelledAt/CancellationReason` 六列，`__EFMigrationsHistory` 计数为 17。
- 需求方取消（真实库）：`POST /api/v1/orders/{id}/cancel` 返回 `Cancelled` 与原因、`cancelledBy`；随后任务状态为 `Published`、该次报名变 `Rejected`、服务者收件箱里出现 `order.cancelled`；同一任务可以再次报名并重新选人（新报名为 `Pending`），订单列表里能读到 `cancellationReason`/`cancelledBy`/`cancelledAt`。
- 服务者取消：未开始时取消返回 `Cancelled`，需求方收到 `order.cancelled`。
- 规则拒绝（全部 `422`）：服务者开工后取消、需求方在服务者提交验收后取消、非参与者取消、撤销一条已分配任务、验收通过后再取消；验收本身仍正常（任务随之 `Closed`）。
- 过期扫描：一条 40 秒后到期的已发布任务在 **70 秒内**被后台扫描置为 `Expired`（`tasks.ExpiredAt` 落库为 `2026-09-13 11:36:59+08`），报名变 `Expired`，所有者与报名者各收到一条 `task.expired`；对照组的“已分配且已过截止时间”任务保持 `Assigned`、也没有 `task.expired`。
- 撤销任务：`POST /api/v1/tasks/{id}/cancel` 返回 `Cancelled` 与原因，报名者收到 `task.cancelled`；重复撤销返回 `422`。
- 前端：`npm run typecheck` 与 `npm run build` 均通过（构建产物 `dist/assets/index-*.js` 190 kB）；取消/撤销按钮的可见性由页内谓词与后端状态机双重把关。

本轮（多实例通知扇出）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln -c Debug` 0 警告 0 错误；领域 109 + 集成 214 = 323 个用例全通过（`dotnet test AIToHuman.sln -c Debug --no-build`）。
- 环境：本机 Redis 5.0.14 standalone（127.0.0.1:6379）+ PostgreSQL + 同一份代码的两个 API 实例（5080 / 5081），两个实例都配了 `ConnectionStrings__Redis`。
- 开关关闭（默认值，`GET /api/v1/admin/settings/notifications.fanout.enabled` → `value=false`、`source=default`）：跑通“创建任务 → 发布 → 报名 → 选人”，服务者收到 `order.created` 通知，`notifications` 表 `DispatchedAt` 全部写满（22/22），`redis-cli monitor` 在整个过程中**没有任何 PUBLISH**；实例 B 的启动日志打印“通知扇出当前未启用……开启后 5 秒内自动接入”，并且没有建立 Redis 连接。
- 运营后台打开开关（`PUT /api/v1/admin/settings/notifications.fanout.enabled`，`value=true`）：返回 `source=database`、`version=1`、`updatedBy` 为管理员 id；`GET /api/v1/admin/settings/audits` 新增 1 条 `Update`（后来恢复默认时再记一条 `Reset`）。
- 跨实例投递：SignalR 客户端（Node 内置 WebSocket，握手帧 `{"protocol":"json","version":1}`）以 owner 身份连到实例 **B**，而业务请求 `POST /api/v1/orders/{id}/start` 打到实例 **A**；实例 B 收到 `{"type":1,"target":"notification.created","arguments":[...]}`，`eventId` 与 `occurredAt` 和库里那条通知一致，`redis-cli monitor` 抓到完整的 `PUBLISH "aitohuman:notifications:fanout"` 载荷（含 `userId`/`notificationId`/`eventId`/`type`/`payloadJson`）。再以 worker 身份连 B、由 A 触发 `approve`，同样收到 `order.statusChanged`（`Approved`），`notifications` 表 22/22 全部已派发。
- 运行时开关生效：实例 B 是在开关**关闭**状态下启动的，运营打开开关后 5 秒内日志出现“通知扇出已连接 Redis（127.0.0.1:6379）”“已订阅 Redis 频道 aitohuman:notifications:fanout”，没有重启进程；验证结束后用 `DELETE` 把覆盖值恢复默认（`source=default`），避免开发库残留覆盖。
- 数据库侧可见的原子认领：实例日志里出现条件更新 `UPDATE notifications AS n SET "DispatchedAt" = @p WHERE n."Id" = @id AND n."DispatchedAt" IS NULL`——“推送成功后才标记”的旧写法已从代码里删除。
- 降级路径：把 `ConnectionStrings__Redis` 指向不可达端口（127.0.0.1:6399）并打开开关后启动，日志出现“通知扇出订阅失败，5 秒后重试”与“通知扇出广播失败，改为只推本实例在线客户端。”；连在该实例上的客户端仍然实时收到 `order.messageCreated`（`preview` 与发送内容一致），收件箱从 2 条变 3 条——即 Redis 不通只影响跨实例实时性，不影响本实例投递与落库。

本轮（风险规则与禁止任务拦截）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 217 + 集成 252 = 469 个用例全通过（`dotnet test AIToHuman.sln --no-build --no-restore`）。此前 406 个用例在本轮改动后全部保持通过——这一点尤其重要，因为新规则会命中任务正文，若不慎放宽匹配词就会把既有用例（例如“代取文件”）判成禁止。
- 规则目录自述（真实 PostgreSQL + 管理员 JWT）：`GET /api/v1/admin/risk/rules` 返回 `version=1`、`highRewardThreshold=5000`、`rules=10`，其中 `Blocked` 6 条；响应里只有匹配词**数量**，没有任何匹配词本身；非管理员访问队列 `403`。
- 禁止类别端到端（空库启动，`Migrate()` 一次应用全部 20 个迁移）：创建 `帮我代考英语四级` → 草稿 `status=ReadyToPublish`、`riskVerdict=Blocked`、`riskRuleCode=prohibited.exam_impersonation`、`riskPublishBlocked=true`；`POST /tasks/{id}/publish` 返回 `422` 与“该任务属于平台禁止的类别（prohibited.exam_impersonation · 代考与冒名顶替），不能发布：……一句完整说明”；大厅列表里查不到这条任务；对它调用运营复核接口返回 `422`“该任务没有待处置的风险复核。”（人工无权放行禁止类别）。
- 转人工端到端：创建 `帮我把身份证从家里送到公司` → `riskVerdict=NeedsReview`、`riskRuleCode=review.identity_documents`、`riskReviewStatus=Pending`，发布 `422`“该任务需要人工复核（review.identity_documents · 证件与重要文件），复核通过后才能发布：……”；它在 `GET /api/v1/admin/risk/reviews` 里出现，并被判为“没有待处置的禁止类别”之外的可处置项。
- 放行路径：`帮我把营业执照原件送到银行` 由运营 `Approve`（依据“已核实营业执照用途与收件人”，操作人 id 落库）后，同一任务发布 `200`、状态 `Published`、大厅里能查到。
- 驳回路径：`帮我把身份证送到酒店前台` 由运营 `Reject`（依据“无法核实证件用途，请改为自行递送”）后发布 `422`“该任务未通过人工复核，不能发布：无法核实证件用途，请改为自行递送”。
- 复核队列与审计：`GET /api/v1/admin/risk/reviews` 按创建时间升序返回待复核任务（本轮实测一次列出 3 条、处置两条后剩 2 条，先来先处理）；`GET /api/v1/admin/audits` 出现 `task.risk.approve[已核实营业执照用途与收件人]` 与 `task.risk.reject[无法核实营业执照用途，请改为自行递送]`；重复处置返回 `422`“该任务的风险复核已经处置过”，非法取值返回 `422`“风险复核结论 Maybe 不存在：可选值为 Approve（放行）或 Reject（驳回）。”，且失败的操作不留审计行。
- 通知：需求方收件箱收到 2 条 `task.riskReviewed`，载荷分别是 `{"reviewStatus":"Approved","canPublish":true,...}` 与 `{"reviewStatus":"Rejected","canPublish":false,...}`（含 `taskId`/`title`/`verdict`/`ruleCode`），未读数同步。
- 草稿可见性与状态：`GET /api/v1/tasks/mine` 同时显示 `帮我代考执业资格考试:ReadyToPublish/Blocked/NotRequired`、`帮我把身份证送到酒店前台:ReadyToPublish/NeedsReview/Rejected`、`帮我把营业执照原件送到银行:Published/NeedsReview/Approved`，被拦的草稿对所有者可见、对大厅不可见。
- 前端：`npm run typecheck`（strict + `noUncheckedIndexedAccess`）与 `npm run build`（产物 `dist/assets/index-*.js` 210 kB）均通过；草稿预览弹窗与“我的任务”显示风险原因并禁用发布按钮，运营配置新增“风险复核”页签。
- 存量数据兼容：新列对既有行用数据库默认值回填（`RiskVerdict='Allowed'`、`RiskReviewStatus='NotRequired'`），EF 读取侧对空值/未知枚举名做防御性解析；`TaskDbContextModelTests` 仍锁定并发令牌与唯一索引不被改坏。

本轮（草稿字段编辑）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 233 + 集成 259 = 492 个用例全通过（改前的 470 个一个没坏）。前端 `npm run typecheck` 与 `npm run build` 通过（产物 `index-*.js` 214.77 kB）。
- 编辑落库端到端（真实 PostgreSQL + 本机库 `aitohuman_risk_check`）：`PUT /api/v1/tasks/{id}` 改掉标题、区域、悬赏、验收标准与截止时间后，`GET` 回来的字段与提交一致，随后发布 `200` 且状态 `Published`。
- 状态与权限：编辑已发布任务返回 `422`“任务当前状态 Published 不允许该操作，期望状态为 ReadyToPublish。”；非所有者（服务者身份）编辑返回 `403`；`draftEditable` 只在草稿上为 `true`。
- 字段校验：截止时间在过去 → `422`“截止时间必须晚于当前时间。”；验收标准全为空白 → `422`“任务至少需要一项验收标准。”（字段校验与创建时共用同一套实现，不是复制了一份）。
- 防绕过（本轮最关键的一条）：敏感草稿先由运营放行 → 编辑（仍是草稿）→ 返回 `reviewStatus=Pending`、`reviewedBy`/`reviewedAt`/`reviewNote` 全部清空、重新出现在 `GET /api/v1/admin/risk/reviews`、发布 `422`；再次放行后发布 `200`。审计里因此留下两条 `task.risk.approve`（对应两版文本）。
- 禁止类别可自救：`帮我代考英语四级` 建出草稿后发布 `422`，编辑成“帮我去图书馆还两本书”后 `riskVerdict=Allowed`、发布 `200`——被规则拦住不等于账号或草稿被判死刑。
- 并发令牌未被破坏：8 个请求同时发布同一草稿 → `200×1 + 422×7`，最终只有一条 `Published`，没有重复发布。
- **顺带修掉一个只有真机才暴露的缺陷**：`EfTaskRepository.Save` 原本逐列手写复制，写它的时候草稿正文不可变，因此它**不复制** `Title`/`Description`/`District`/`Deadline`/`AcceptanceCriteriaJson`/`ExecutionAddress`。草稿编辑上线后表现为“接口返回了新内容、库里没落库、发布时仍按旧文本判风险”——内存仓储保存的是同一个对象引用，所以 492 个内存用例全都看不出来。现已改为 `db.Entry(record).CurrentValues.SetValues(ToRecord(task))` 整体覆盖（`Version` 单独自增以保住并发令牌），以后给 `tasks` 加列只要 `ToRecord` 填了值就会自动带上。
- 前端：“我的任务”草稿行新增“编辑草稿”与内联表单，保存后刷新列表并提示是否仍被风险规则拦住；执行地址不随任务载荷下发，编辑时由所有者单独读 `GET /tasks/{id}/execution-address` 预填。

本轮（真实 PostgreSQL 回归测试基建）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 233 + 集成 **267** = **500** 个用例全通过，其中 8 个是新增的在真实 PostgreSQL 上跑的回归用例（单独跑约 2 秒）。
- 新增文件：`backend/tests/AIToHuman.IntegrationTests/Postgres/PostgresTestEnvironment.cs`（连接串解析 + 可用性探测 + `[PostgresFact]` 跳过语义）、`PostgresRegressionFixture.cs`（共享测试库 + 清表 + `PostgresWorld` 请求作用域）、`PostgresRegressionTests.cs`（8 个用例）。
- 覆盖的场景（全部是内存替身测不出来的类型）：空库 `Migrate()` 全量应用且无待执行；**草稿编辑后每一列都真的落库**（上一轮 `Save` 漏列缺陷的回归）；两个作用域读同一版本、后写入者拿到 `DbUpdateConcurrencyException`（乐观并发令牌真的生效）；12 路并发选人只产生 1 个订单 / 1 条 `Selected` 报名 / 任务进入 `Assigned`；取消订单的连带效果落到 tasks、task_applications、orders 三张表且任务回到大厅；争议冻结在库里生效（状态、发起人、时间落库，验收与提交都被拒）；风险复核队列按状态过滤且“放行 → 编辑 → 重新排队 → 再放行”成立；`+08:00` 偏移归一化为 UTC、中文文本原样往返。
- 跳过语义已实测：把 `AITOHUMAN_TEST_POSTGRES` 指向不可达端口后，这 8 个用例全部标记为“已跳过”、0 失败——没有数据库的机器与默认 CI 不会被它们拖红。
- 测试库隔离：每次运行用 `aitohuman_regression_<随机>` 建库、共享、逐个用例清表、结束删库；连接串只来自环境变量或本机 `appsettings.Development.json`，**凭据不写进仓库**。
- CI：`.github/workflows/ci.yml` 的 backend job 增加 `postgres:16` 服务（带 healthcheck）并把 `AITOHUMAN_TEST_POSTGRES` 注入 `dotnet test`，因此这组用例在流水线里是真跑的；frontend job 补上 `npm run typecheck`。
- 顺带说明：这组用例第一次跑就抓到了我自己测试代码里的一个错误假设（12 个服务者报名后不应只存在一条 `Pending` 报名），也算验证了它确实在跑真库。

本轮（报名列表内联服务者信用）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 233 + 集成 **272** = **505** 个用例全通过；前端 `npm run typecheck` 与 `npm run build` 通过（215.07 kB）。
- 新增 5 个集成用例（`WorkerCreditInApplicationsTests`）：带公开评价时列表显示均分与样本量；仍在盲期的评价不计入；没有公开评价时是 0 分 / 0 条而不是编造分数；同一任务的多个报名各带自己的信用（不串号）；报名列表与 `GET /users/{id}/review-summary` 口径一致。
- 真实 PostgreSQL + 真实 HTTP 端到端：服务者完成第一单且双方互评（立即公开，需求方给 5 分），第二单只有需求方单方评 1 分（仍在盲期）；随后需求方在自己的任务报名列表里读到 `评分=5 条数=1`——盲期那条 1 分没有计入，且与 `GET /api/v1/users/{id}/review-summary` 返回的 `5 / 1` 完全一致；服务者身份读别人的报名列表返回 `403`；另一名新服务者的报名显示 `0 / 0`，没有串到别人的分数。
- 实现要点：只用既有的 `IReviewRepository`，**没有改 `TaskService` 构造函数**（避免波及既有测试）；列表按 distinct workerId 批量取信用，不是每条报名各查一次。

本轮（草稿版本号与编辑历史）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 244 + 集成 **279** = **523** 个用例全通过（新增 11 个领域用例、6 个用例层用例、1 个真库用例）；前端 `npm run typecheck` 与 `npm run build` 通过（216.84 kB）。
- 迁移：既有开发库启动时自动应用第 21 个迁移 `AddTaskDraftRevisions`（日志“共 21 个迁移”），说明自愈基线化路径对新迁移依然有效；真库回归用例在随机空库上 `Migrate()` 也从零建出这张表。
- 真机端到端（真实 PostgreSQL + 真实 HTTP）：创建后 1 版（`changeSummary=创建草稿`、`riskVerdict=Allowed`）→ 编辑标题/截止时间/悬赏后 2 版（`changeSummary=标题、截止时间、悬赏`，快照里的标题与悬赏都是新值）→ 改成“帮我代考英语四级”后 3 版（`changeSummary=标题、描述、公开区域、悬赏、验收标准`、`riskVerdict=Blocked`、`riskRuleCode=prohibited.exam_impersonation`、`riskRuleVersion=1`）；历史里保留每一版的原文（第 1 版是原来的标题、第 3 版是禁止内容），所以“从哪一版开始变味”是可查的。
- 权限与边界：非所有者读历史 `403`，任务不存在 `404`；被判定为禁止的草稿仍然**可以继续编辑**（编辑不是发布门禁的一部分，用户要能改好它），但发布依旧 `422`。
- 真库并发：两个作用域读到同一版本，先写成功、后写抛 `DbUpdateConcurrencyException`，事后历史仍是 1/2/3 且不包含落败那次编辑的内容——版本快照与任务写入在同一个事务里。
- 顺带补的字段上限（创建与编辑共用）：描述 ≤4000 字、公开区域 ≤120 字、验收标准 ≤12 条且每条 ≤200 字；这些也是历史快照的列宽依据。
- 顺带的测试维护：`TaskService` 构造新增 `ITaskRevisionRepository`，仓库里 10 个手工构造 `TaskService` 的测试文件同步补参数；`TaskServiceUnitOfWorkTests` 里两处“Select 恰好一次工作单元”的断言改成相对基线（创建草稿现在自己就用掉一次事务边界）。

本轮（写接口幂等键）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 244 + 集成 **289** = **533** 个用例全通过（新增 9 个中间件用例 + 1 个真库用例）。
- 中间件用例（`IdempotencyMiddlewareTests`，用 `DefaultHttpContext` 直接驱动中间件，不需要起主机）：同一个键重试只执行一次且返回同一响应与回放头；不同键各自执行；同键不同请求体 `409`；不带键行为不变；只覆盖写方法（GET 不参与）；匿名请求与 `/ai/plan/stream` 不参与；`5xx` 不缓存、重试能真正重跑；占位过期后可以重试；占位未完成时第二个请求 `409`。
- 真机 HTTP 端到端（真实 PostgreSQL + 真实 HTTP）：同一个键连续提交 4 次创建任务 → HTTP 全是 `201`、**唯一任务 id 数 = 1、回放 3 次、库里实际只有 1 条**；三个不同键 → 3 条任务（总计 4 条）；同键配不同请求体 → `409`「这个幂等键已经用于另一个请求…」；不带键两次 → 2 条任务；发布用同一个键重试回放 `200 Published` 且带 `Idempotency-Replayed: true`，换一个键则 `422 任务当前状态 Published 不允许该操作`。
- 真库用例（`PostgresRegressionTests`）：记录跨作用域（等价于跨请求/跨实例）可读回并保留响应体与状态码；同一用户同一键只能占一次并交回已有记录；不同用户可以用相同的键——验证了 `(UserId, Key)` 主键与迁移 22。
- 顺带记录一个由测试当场抓到的自身缺陷：`TryResolveKey` 忘了把请求头的值赋给输出参数 `key`，导致键恒为空、领域层抛「幂等键长度必须在 1 到 120 个字符之间」。这正是把中间件也纳入自动化测试的价值——这类错误只有真正走一遍请求才会暴露。

本轮（误拦申诉）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 255 + 集成 **296** = **551** 个用例全通过（新增 11 个领域用例 + 6 个用例层用例 + 1 个真库用例）；前端 `npm run typecheck` 与 `npm run build` 通过（223.07 kB）。
- 真机 HTTP（真实 PostgreSQL，既有开发库自动应用第 23 个迁移）：转人工 → 运营复核驳回 → 所有者申诉 → 运营 `Accept` → 发布 `200 Published`；禁止类别的草稿（`帮我代考英语四级`）申诉 → 运营 `Accept` → 发布仍 `422`「该任务属于平台禁止的类别（prohibited.exam_impersonation · 代考与冒名顶替）…」，通知里 `canPublish=false`——**这条就是本功能的安全边界**。
- 边界（全部实测）：对已放行任务申诉 `422`「这条任务没有被风险规则判定为需要申诉的状态。」；还在等复核的敏感草稿申诉 `422`「这条任务还在等人工复核（或已经放行），不需要申诉：请等复核结果。」（列表里 `canAppealRisk=false`）；同一版重复申诉 `422`「这一版内容已经申诉过：请先修改草稿…」；非所有者申诉 `403`；处置不写依据 `422`；非管理员看队列 `403`；`admin_audit_entries` 出现两条 `task.risk.appeal.accept` 且带依据。
- 改文案后重新判定：把被禁止的草稿改成普通内容 → `verdict=Allowed`、`appeal=None`，发布成功（"改文案后重新判定"这条引导是可行的）。
- 真库用例：申诉与处置结论落库（`RiskAppealStatus`/`RiskAppealReason`/`RiskAppealDecidedBy`）、跨作用域可读回、禁止类别申诉成立后依旧不能发布、审计两条。
- 设计取舍（有意为之，写进文档）：申诉成立**不改变** `prohibited.*` 的可发布性——平台红线不因为多了一个入口而放开；因此运营侧接口与通知载荷都显式给出"这次结论能不能真的放行"。

本轮（加固：清理与可读错误）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 255 + 集成 **303** = **558** 个用例全通过（新增 4 个清理用例、1 个真库清理用例、2 个显示名用例）；前端 `npm run typecheck` 与 `npm run build` 通过（223.10 kB）。
- 幂等记录清理：`IdempotencyCleanup` 的策略是"已完成保留 24 小时、未完成占位保留 10 分钟、单次最多 500 条"；用例覆盖"只删过期的、还在重试窗口内的必须留下""批量上限分批删完""空扫返回 0"；真库用例验证 EF 侧走一条 `DELETE`（`ExecuteDelete`）而不是把记录读进内存，且不误删新鲜记录。后台任务启动时会打印策略原文（实测启动日志：`幂等记录清理已启动：已完成记录保留 24 小时、未完成占位保留 10 分钟，单次最多清理 500 条。`）。
- 可读错误（本轮真正想解决的问题）：以前缺字段的注册请求只返回 `422「请求暂时无法处理。」`，连"少了 role"都要靠猜。现在实测：缺 `role` → `422请求参数不符合要求 / 角色必须是 owner 或 worker。`；密码过短 → `422 / 密码至少需要 8 个字符。`；昵称超长 → `422 / 昵称必须为 1 到 80 个字符。`；非法角色 → `422 / 角色必须是 owner 或 worker。`；**重复邮箱 → `409资源冲突 / 该邮箱已注册。`**（以前状态码对、原因被吞）。
- 实现方式：新增 `ValidationException`（→422+消息）与 `ConflictException`（→409+消息）两个明确的类型，把 AuthService、AiPlanningService 的对话校验、Program.cs 里的消息长度与申诉结论解析都改成前者；重复邮箱从"靠消息里包含『已注册』做字符串匹配"改成明确的 `ConflictException`（那条脆弱的匹配分支已删除）；AI 未配置密钥改用新的 `AiPlanningNotConfiguredException`（→502 + "去运营后台补 ai.apiKey"的可行动线索）。**未预期的 `InvalidOperationException` 仍然只返回通用文案**，避免把 EF 之类的内部消息漏给客户端。
- 报名列表显示服务者名字：`TaskApplicationResponse` 增加 `workerDisplayName`，与信用摘要同一次批量取（`IUserDirectory.FindMany`）；实测报名行返回 `workerDisplayName=张师傅`；拿不到名字（无 PostgreSQL 时是空实现）时返回 `null`，页面退化成显示 id 前缀——用例锁住了"缺名字不炸列表"。

本轮（把自动化扩展到 Redis 与对象存储）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 255 + 集成 **313** = **568** 个用例全通过，其中新增 10 条真实外部依赖用例（Redis 5 + 对象存储 5）。带上真库那批一起跑约 21 秒（对象存储那条"过期"用例真的要等 7 秒）。
- Redis 扇出（真实 Redis 6379）：广播出去的消息被订阅方**按字段原样**收到（`NotificationFanoutMessage` 相等，`PayloadJson` 不被重新序列化）；处理函数抛异常后**订阅仍然活着**（第二条消息照样收到）；开关关闭时 `Enabled=false` 且 `PublishAsync` 抛 `InvalidOperationException`（派发方据此退回单实例推送）；未启用时 `SubscribeAsync` 立即返回；配了不可达的 Redis（`127.0.0.1:6399`）时 `PublishAsync` 会失败并返回，**不会把派发周期无限卡住**。
- 对象存储（真实 MinIO 9000 + 桶 `aitohuman-evidence`）：对象往返读写与删除、`ExistsAsync` 真假、不存在的对象读取返回 `null` 而不是抛异常；**预签名地址不带任何鉴权头就能取回字节**，`Content-Disposition` 的附件名生效；**篡改签名末位 → 403**；**拿 A 的签名去取 B 的路径 → 403**；**过期后 → 403**。最后三条是这轮最有价值的部分：它们验证的是我们自研的 SigV4 真的被服务端认账，而不是"看起来像签名"。
- 跳过语义已实测：把 `AITOHUMAN_TEST_REDIS` 指向 `127.0.0.1:6399`、`AITOHUMAN_TEST_S3_ENDPOINT` 指向 `http://127.0.0.1:9099` 后，这 10 条里 9 条**跳过**、0 失败，只有那条"不可达 Redis 要失败并返回"的纯断言用例继续运行并通过。
- 两条自己踩过的坑（如实记录，避免后来者重犯）：① 过期那条第一版用假时钟把"现在"推到有效期之后，结果服务端照样返回 `200`——**有效期是服务端按 `X-Amz-Date + X-Amz-Expires` 与它自己的时钟判定的，改客户端时钟没有意义**，现在改成真等 7 秒；② 路径替换那条一开始用 `Uri.EscapeDataString(key)` 去替换 URL，而 URL 里是未转义的路径片段，替换没生效、地址根本没变，于是"因为没改而通过"（`200`），现在加了 `Assert.NotEqual(presigned, swapped)` 防止这种假通过。
- CI：backend job 现在同时起 `postgres:16`、`redis:7`、`minio/minio`（都带 healthcheck），并在测试前用 `minio/mc` 容器建出测试用的桶——MinIO 起来是空实例，不建桶对象存储用例会跳过。

本轮（本地开发形态收口：零配置 + 纯静态也能用子路径）新增验证：

- 构建产物：`npm run build` 现在产出三份 HTML——`dist/index.html`、`dist/ops.html`、`dist/ops/index.html`（第三份由 `vite.config.ts` 的 `opsSubpath` 插件在构建后拷贝，指向同一份 `/assets/ops-*.js`）；`frontend/.env.example` 记录了四个前端变量，默认全空。
- 开发零配置真机检查（Playwright + Chromium，连本机 Vite 5173 与真实后端 5188，未设置任何环境变量）：`http://localhost:5173/ops/` 能打开运营后台、地址栏停在 `/ops/`、用管理员账号登录后页签与数据齐全（配置项 26 … 规则目录 v5）；主应用的运营入口仍是同源 `/ops.html`；无 console 错误。
- **纯静态托管 + 跨源 API** 真机检查（不经过 Vite、也没有 nginx：`npm run build` 时给 `VITE_API_BASE_URL=http://127.0.0.1:5188`，用 `python -m http.server 4180 --directory dist` 当静态源，API 侧 `Cors__AllowedOrigins=http://localhost:4180`）：`http://localhost:4180/ops/` 与 `http://localhost:4180/ops.html` 都返回 `200` 并能登录取数（页签同样齐全），`http://localhost:4180/` 也能打开；无 console 错误。这条同时证明了“子路径不需要服务端改写规则”。
- 后端启动日志会打印生效的跨域来源，实测输出：`跨域允许来源：http://localhost:4180（同源部署不需要 CORS；跨域部署请用 Cors__AllowedOrigins 配置）`；未配置时开发环境回退到本机 5173 / 4173，其它环境为空（仅接受同源）。
- 回归：前端 `typecheck` 与 `build` 通过；后端 `dotnet build` 0 警告 0 错误，领域 283 + 集成 331 = 614 个用例全通过。

本轮（运营后台跨域/子路径部署）新增验证：

- 前端：`npm run typecheck` 与 `npm run build` 通过；默认构建里**没有绝对 API 地址**（全部走相对路径），跨域构建会把 `VITE_API_BASE_URL` 等值内联进共享分包。
- 跨域 + 子路径真机检查（Playwright + Chromium；页面来自 `http://localhost:4173/ops/`，API 在 `http://127.0.0.1:5188`，两个不同的源；10 项断言全通过、页面无 console 错误）：
  - 子路径可用：`/ops/` 与 `/ops` 都返回运营后台（`vite preview` 里的 `/ops/` → `ops.html` 改写），地址栏停在 `/ops/`，资源仍从根路径 `/assets/...` 加载。
  - 跨域可用：在页面上用管理员账号登录成功并进入后台（页签「配置项 26 / 变更记录 / 任务检索 / 用户检索 / 争议处置 / 风险复核 / 误拦申诉 / 规则目录 v5」），确实打了 4 个到另一个源的请求（例如 `http://127.0.0.1:5188/api/v1/auth/login`）。
  - 配置生效：「返回任务工作台」指向 `VITE_APP_HOME_URL`（`http://localhost:4173/`），主应用顶栏的运营入口指向 `VITE_OPS_HOME_URL`（`http://localhost:4173/ops/`）。
  - SignalR 跨域：主应用在另一个源上订阅通知时 `POST /hubs/notifications/negotiate` 返回 `200`，控制台 0 错误——这是本轮唯一一处**必须先修才有**的行为：只写 `WithOrigins + AllowAnyHeader + AllowAnyMethod` 时，SignalR 协商因“响应缺少 `Access-Control-Allow-Credentials: true`”被预检拦掉（当时实测控制台 4 条错误），补上 `AllowCredentials()` 后消失。
  - 反向验证（确认 CORS 真的在拦，而不是形同虚设）：把 API 的 `Cors__AllowedOrigins` 留空（只放行本机 5173）后重跑同一个页面，登录请求在预检阶段被浏览器拒绝——`No 'Access-Control-Allow-Origin' header is present on the requested resource`，页面停在登录卡片并显示“Failed to fetch”。
- 回归：同源默认配置下重跑上一轮的 20 项运营后台浏览器检查，全部仍然通过（说明把 62 处 `fetch('/api/v1...')` 统一换成 `apiFetch()` 没有改变同源行为）。

本轮（运营后台独立成页）新增验证：

- 前端：`npm run typecheck` 与 `npm run build` 通过；构建产出**两个入口**——主应用 `dist/index.html`（JS 从 233.42 kB 降到 127.97 kB，运营后台整块搬走）+ 运营后台 `dist/ops.html`（`ops-*.js` 36.51 kB）+ 共享分包 `styles-*.js` 71.73 kB；后端未改动，`dotnet test` 仍是领域 283 + 集成 331 = 614 全通过。
- 真机浏览器检查（Playwright + Chromium，连本机 Vite 开发服务器与真实后端、真实 PostgreSQL，共 20 项断言全部通过，页面无 console 错误、无失败请求）：
  - `/ops.html` 未登录时显示登录卡片；用管理员账号登录后进入后台，顶栏显示操作人；页签为「配置项 26 / 变更记录 / 任务检索 / 用户检索 / 争议处置 / 风险复核 / 误拦申诉 / 规则目录 v5」，规则目录页签上的版本号与生效版本一致。
  - 各页签真实取数：规则目录渲染出 10 条规则 + 4 版历史（14 行）、任务检索按关键字“文件”命中 6 条、变更记录 15 条（含 `task.risk.rules.update` / `task.risk.rules.reset`）。
  - 版面：内容区 1180px、页面 1240px、`max-height: none`、横向溢出 0px——确认是整页铺开而不是原来那个 780×88vh 的对话框。
  - 主应用：顶栏出现「运营后台 ↗ → /ops.html」链接（管理员才有），页面里已经**没有**运营弹窗（`.auth-backdrop .settings-dialog` 计数为 0）。
  - 非管理员路径：退出登录 → 换一个不在管理员名单里的账号登录 → 看到“这个账号没有运营权限”（说明文字指出名单来自 `Admin__UserIds` / `Admin__Emails`），且页面上没有任何后台内容。
- 说明：截图存在 `.scratch/ops-1-login.png` … `.scratch/ops-7-not-admin.png`（本机临时目录，不入库）；我这次的模型不具备图片输入能力，所以上述结论来自 DOM 断言与尺寸测量，截图请人工过一眼。

本轮（依赖感知就绪检查、写接口限流与备份恢复演练）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 **330** + 集成 **389** = **719** 个用例全通过、**0 跳过**（本轮新增 15 条：限流口径 6 条、就绪检查 7 条、主机级 2 条）；迁移仍是 **29** 个（本轮没有迁移）；前端本轮未改动。
- 就绪探针真机 HTTP（真实 PostgreSQL + 本机 Redis 6379 + 本机目录存储）：
  - 扇出关闭时 `GET /health/ready` → `200`，三项分别是 `postgres` ok（"已应用的迁移 29 个，没有待应用的迁移"）、`redis` **skipped**（"没有开启多实例通知扇出，本次不探测 Redis"）、`storage` ok（"本机目录可读写（写探测对象 readiness/probe.txt 后已删除）"），整轮 30 毫秒。
  - 打开 `notifications.fanout.enabled` 后同一个端点变成 `redis` ok（"连接正常，Ping 往返 0–1 毫秒"）——探测真的去 PING 了一次，不是"配置看起来对"。
  - `/health` 始终 `200`：依赖探测不影响存活探针（实测 Redis 黑洞时它照样 `200`）。
- 就绪探针的负向验证（把 `ConnectionStrings__Redis` 指向黑洞地址 `10.255.255.1:6379`）：`GET /health/ready` 返回 `503`、`status=unhealthy`，`postgres` 与 `storage` 仍 ok、`redis` failed 且说明是"探测超过 3 秒没有返回，按不健康处理"；服务端自报耗时 **3024 毫秒**、客户端观测 **3052 毫秒**——这正是本轮修掉的问题：`StackExchange.Redis` 的 `ConnectAsync` 不接受取消令牌，只靠 `CancellationToken` 时第一次建连会拖到 5 秒以上（上一版实测 5087 毫秒且最终报"健康"），现在用 `Task.WhenAny` 硬截断，就绪端点不会骗人。
- 无需重启即可恢复：把 `notifications.fanout.enabled` 关掉后，下一秒的 `/health/ready` 就回到 `200`（`redis` 回到 skipped）。
- 写接口限流真机 HTTP（真实 PostgreSQL，上限临时改成 2）：同一个用户的三个写请求得到 `[201, 201, 429]`，三个响应的 `X-RateLimit-Partition` 都是 `user:<该用户 id>`；被拒的那条带 `Retry-After: 24`、`X-RateLimit-Limit: 2`、`X-RateLimit-Remaining: 0`，响应体是「请求过于频繁 / 一分钟内的写请求次数已达上限（2 次），请在 24 秒后重试。」；同一时刻的读接口（大厅列表）照常 `200`。另一个刚注册的用户的第一个写请求 `201`（`X-RateLimit-Partition` 是自己的 id，剩余 2）——**额度是按用户分区的，不会被别人的用量牵连**。
- 匿名写请求按来源 IP 计数（实测）：登录与注册的 `X-RateLimit-Partition` 都是 `ip:127.0.0.1`；这也解释了联调时的一个现象——把上限压到 2 之后，同一台机器上的注册请求会先被挡住（同一出口 IP 共用一份额度），因此默认 240 次/分钟是刻意给宽的。
- 自救通道真机验证：把上限改成 `1` 之后连续两次写 `ratelimit.writesPerMinute` 都是 `200`（`/api/v1/admin/settings` 豁免成功），而同一时刻的其它运营写接口（跑一轮 `POST /api/v1/admin/risk/recheck`）照常计数、`X-RateLimit-Limit` 显示当时的生效值。
- **本轮踩到的坑（如实记录）**：最初没有豁免配置写入，联调脚本把上限配成 2 之后，管理员自己的第 3 个写请求（想改回 240）也被 `429` 挡住，只能等窗口过去——这就是"自救通道"的由来；真机上确实复现过一次（脚本里连续三次恢复配置：`429`、`429`、`200`）。
- 备份与恢复演练（本机真实执行，完整步骤与结论见 [备份、恢复与演练](../operations/backup-and-restore.md) 第 4 节）：
  - `pg_dump -Fc` 产出 86,138 字节 → `pg_restore` 到新库 `aitohuman_restore_check`（退出码 0）→ 逐表核对**行数全部一致**（`users` 45、`tasks` 87、`orders` 16、`task_applications` 22、`risk_decision_entries` 13、`task_risk_appeals` 6、`ledger_entries` 16、`address_access_entries` 4、`system_settings` 3；不一致 0 张；`__EFMigrationsHistory` 两边都是 29 行）。
  - 用**恢复库**启动 API：`/health/ready` 报 `healthy`（"已应用的迁移 29 个，没有待应用的迁移"）、真实账号登录 `200`、`GET /api/v1/admin/settings` 返回 30 个键 / 7 个分组（**证明 Data Protection 密钥环也是可用的**，否则机密解不开）、`GET /api/v1/admin/risk/stats?days=30` 返回判定总数 13（拦 6、放行 7、2 条规则命中）、大厅列表返回 5 条且 `hasMore=true`。
  - 对象存储：开发环境的 `aitohuman-evidence` 桶当时是空的，`mc mirror` 两边都是 0 个对象会"假通过"，因此先放 2 个探针对象再演练——镜像到本机目录 2 个文件、回灌到临时桶 2 个对象、抽查对象的 SHA-256 与镜像一致；收尾删掉探针对象与临时桶（桶回到 0 个对象）。演练库已 `DROP`，开发库未受影响。
  - 顺带确认两个坑：**密钥环不备份等于配置页打不开**（`system_settings` 里是 `dp1:` 密文，必须与库成对恢复）；`__EFMigrationsHistory` 是大小写混合表名，`select count(*) from __EFMigrationsHistory` 会被 Postgres 折成小写并报"关系不存在"（演练时踩到，改成用 `psql -f` 喂带双引号的语句）。

本轮（规则命中统计与追加式风险决策历史）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 **330** + 集成 **374** = **704** 个用例全通过、**0 跳过**（本轮新增 10 条：领域 3 条、用例层 5 条、真库 1 条、主机级 1 条）；迁移总数 **29**；前端 `npm run typecheck` 与 `npm run build` 通过（`index` 130.68 kB、`ops` 46.96 kB）。
- 迁移自愈：开发库启动时自动应用第 29 个迁移 `AddRiskDecisionEntries`（日志 `Applying migration '20260913124817_AddRiskDecisionEntries'.` 与"数据库迁移已应用，共 29 个迁移。"）；真库回归用例在随机空库上 `Migrate()` 也从零建出这张表，并验证"换作用域读回来仍然一字不差"。
- 判定留痕真机 HTTP（真实 PostgreSQL，一条被禁止的任务 + 一条正常任务）：同一进程内创建被拦任务（`prohibited.exam_impersonation`、规则第 9 版）、把它编辑一次、创建并发布一条正常任务，再让那条被拦任务的申诉被判成误伤——随后 `GET /api/v1/admin/risk/stats?days=7` 返回 `totalDecisions=4`、`allowed=2`、`blocked=2`、`rechecked=0`，明细里 `prohibited.exam_impersonation` 一行是 `hits=2`、`recheckedHits=0`、`acceptedAppeals=1`（**这几个数字就是本功能的意义**：拦了 2 次、其中 1 次被判成误伤，规则太宽的信号一眼可见）。
- 判定轨迹真机 HTTP：`GET /api/v1/admin/risk/decisions?taskId=<被拦任务>` 返回 2 条，按时间升序依次是 `Created` 与 `Edited`，两条都带 `verdict=Blocked`、`ruleCode=prohibited.exam_impersonation`、`ruleVersion=9`——**同一条任务被判定过两次这件事，以前在库里是查不到的**（任务行上只有最新一条）。
- 窗口与鉴权（真机）：`days=0` 夹到 1 天、`days=99999` 夹到 365 天、不带 `days` 是 30 天（三次都 `200`，只改变 `windowDays` 与统计范围）；非管理员访问看板 `403`；匿名访问看板与轨迹都是 `401`；不带 `taskId` 与不存在的任务都返回空数组（有意不报错，运营页面永远带 `taskId`）。
- 两条口径写进文档（避免以后被当成 bug）：
  - `acceptedAppeals` 按申诉的**裁定时间**（`DecidedAt`）落在窗口内计数，不是按提交时间——"这个窗口里我们认了多少次误伤"才是调参要看的东西。
  - 统计是"按（原因代码 + 结论）分组"：同一条规则可能既有 `Blocked` 也有 `NeedsReview` 的命中（目录改了结论就会换组），**不合并成一行**，因为把禁止类与转人工类混在一起会让看板失去意义；放行（`Allowed`）的行没有原因代码，只进总数与 `allowed` 计数。
- 真库用例 `Risk_decision_history_and_rule_statistics_round_trip`：六个记录点各写一行后，按任务读回的轨迹顺序与内容正确、`Summarize` 的分组计数与误伤次数正确、只追加（没有更新与删除的入口）。
- 主机级用例 `The_rule_statistics_endpoint_is_admin_only_and_returns_the_dashboard_shape`：真进程 + 真 HTTP + 临时真库上，管理员拿到完整看板形状（总数、分结论计数、按规则明细），非管理员 `403`。
- 顺带修掉的过时说法：第 10 节原来写着"精确地址的访问审计尚未实现"（第 27 个迁移早就做了）；运营后台页签清单也漏了"地址留痕"与"命中统计"两个页签，本轮一并补齐。

本轮（主机级端到端测试骨架）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 **327** + 集成 **367** = **694** 个用例全通过、**0 跳过**（集成从 361 增至 367，新增的 6 条全部是主机级用例，说明它们在本机真的跑起来了：真进程 + 临时真库 + 真 HTTP）。
- 主机级用例真的起了子进程：每条用例都通过 `http://127.0.0.1:<随机端口>` 打真实 API——`/health` 返回 `healthy`、开发环境的 `/api/v1/session/dev` 返回 `200`（顺带证明 `ASPNETCORE_ENVIRONMENT` 生效）；整条任务到资金链路走通（建任务 → 发布 → 报名 → 选人（订单 `Held` + 账本一条 `Hold`）→ 开工 → 提交 → 验收（`Released` + 账本多一条 `Release`））。
- 真管线上的契约与中间件：重复邮箱 `409`「该邮箱已注册」、缺角色 `422`「角色必须是 owner 或 worker」、非管理员访问运营接口 `403`、不存在的订单 `404`；管理员名单里配置的邮箱 `GET /api/v1/admin/settings` 返回 ≥27 项且 `/api/v1/auth/me` 的 `isAdmin` 为真；**幂等键回放**——同一个 `Idempotency-Key` 配同一个请求体，第二次的状态码与响应体与第一次完全一致并带 `Idempotency-Replayed: true`，换成不同请求体则 `409`（这条只有真管线才验证得到）；加价到 9000 元当场 `NeedsReview` + `review.high_reward` + `RecheckRequired`。
- 优雅跳过也验证过：`AITOHUMAN_TEST_HOST=0` 时这 6 条**全部跳过**（0 失败、0 通过、6 跳过），与真库/Redis/对象存储用例的跳过语义一致——没装数据库的机器与默认 CI 仍能跑完其余测试。
- 整组主机级用例共用同一个进程与临时库，执行约 1 秒；进程输出被捕获，起不来时异常里直接带日志（不用去猜端口或迁移问题）。
- 顺带修掉的过时说法：文档里"主机级端到端做不到，因为离线取不到 `Microsoft.AspNetCore.Mvc.Testing`"已经过期——不用那包也能做（子进程 + 真 HTTP）。

本轮（资金托管与只追加账本）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 **327** + 集成 **361** = **688** 个用例全通过（本轮新增领域 16 条、用例层 13 条、真库 3 条）；迁移总数 **28**；前端 `npm run typecheck` 与 `npm run build` 通过（`index` 130.68 kB、`ops` 43.06 kB）。
- 迁移自愈：开发库启动时自动应用第 28 个迁移 `AddOrderEscrowAndLedger`（日志"共 28 个迁移"）；真库回归用例在随机空库上 `Migrate()` 也从零建出账本表与托管字段。
- 真机 HTTP（真实 PostgreSQL）跑通四条资金路径：
  - **验收放款**：选人时订单 `Held`（托管 60），服务者提交后需求方验收 → `Released`、已放款 60；流水两条：`OwnerFunds → Escrow` 60（Hold）、`Escrow → WorkerPayout` 60（Release）。
  - **取消退款**：选人时冻结 80，需求方取消订单 → `Refunded`、已退款 80；流水两条：Hold + `Escrow → OwnerFunds` 80（Refund）。
  - **争议分账**：托管 100，双方进入争议后运营按"放款 40、退款 60"处置 → 订单 `Approved`、托管 `Settled`、已放款 40、已退款 60；流水三条：Hold 100 + `PartialRelease` 40 + `PartialRefund` 60，运营审计原因是「按完成一半结算（资金处置金额 40 CNY）」。
  - **边界**：外人读订单流水 `403`；赔付金额超过托管额 `422`「赔付金额必须在 0 到托管金额（100）之间。」；把 `payment.provider` 改成 `disabled` 后新建订单托管状态 `None`、金额 0、**没有任何流水**，改回 `simulated` 后恢复托管（说明这个开关既能做联调降级，也能真的把托管整体关掉）。
- 真库用例另外断言两条只有真事务能验证的事：①**网关冻结失败时整体回滚**——任务仍是 `Published`、数据库里没有订单、没有任何流水、报名仍是 `Pending`（内存装配做不到这件事，所以这条只放在真库用例里）；②托管字段与账本在真库上的往返——换作用域读回来仍是 `Released` + 已放款 60，账本两行且借/贷账户正确（托管字段是逐列写回的，漏一列就会出现"钱扣了但订单看起来没托管"）。
- 一条实现上的取舍（写进文档）：**模拟网关不持有金额状态**——真正的账在 `ledger_entries` 与订单托管字段上，网关只负责"动作能不能成功"并返回可对账凭据。理由：真实服务商的状态在它那边，服务端再存一份必然会对不上，凭据 + 本地流水才是可对账的组合。

本轮（精确地址访问审计 + 凭证容器白名单化）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 **311** + 集成 **347** = **658** 个用例全通过（本轮新增/改写领域 14 条、用例层 6 条、真库 1 条）；迁移总数 **27**；前端 `npm run typecheck` 与 `npm run build` 通过（`index` 128.50 kB、`ops` 41.76 kB）。
- 迁移自愈：开发库启动时自动应用第 27 个迁移 `AddAddressAccessEntries`（日志"共 27 个迁移"）；真库回归用例在随机空库上 `Migrate()` 也从零建出这张表并验证往返。
- 地址留痕真机 HTTP（真实 PostgreSQL）：一条带精确地址的任务上——选人前所有者读取 `200` 且拿到地址、报名者读取 `403`；选中其中一位之后被选中者 `200`、另一位仍 `403`；运营 `GET /api/v1/admin/address-access?taskId=…` 返回 **4 条留痕**（所有者 `Granted`、报名者选人前 `Denied`、被选中者 `Granted`、另一位 `Denied`）且 `deniedCount=2`；按 `viewerId` 过滤能看到那位未被选中者"查过 1 次、被拒 1 次"；非管理员访问留痕 `403`、匿名 `401`。
- 凭证规范化真机 HTTP（同一进程内走真实 multipart 上传，订单链路完整：发布 → 报名 → 选人 → 开工）：
  - 造一个 89 字节的 PNG，在 `IDAT` 前塞一个私有块 `prVt`（载荷是 ZIP 魔数 `PK\x03\x04`）——上传返回 `200`，**存储为 62 字节**（`sizeBytes: 62`，即私有块被剥掉），响应里的 `metadataRemoved` 是 `PNG 未知块(prVt)`，订单凭证列表里也只有这一份（`scanStatus=Clean`、可下载）。
  - 缺 `IEND` 的截断 PNG 被拒：`422`「凭证内容不是合法的 PNG（缺少结束块 IEND），已拒绝保存。」；把 JPEG 改名成 `.png` 上传同样被拒：`422`「凭证内容与声明的类型不一致，已拒绝保存。」——两条都留在同一个订单上，凭证列表里仍然只有第一份，说明被拒的上传没有留下任何文件。
- 口径说明（写进缺口）：这里是**容器白名单化 + 结构 fail-closed**，不是像素级重编码（重编码需要图像编解码库，离线取不到）；内容扫描（`http`/`clamav` 与 `failMode` 的 closed/open 语义）此前已有用例覆盖，本轮未改动。

本轮（发布后风险复检 + 申诉节流与留档）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 **302** + 集成 **344** = **646** 个用例全通过（本轮新增领域 19 条、用例层 11 条、真库 2 条）；迁移总数 **26**；前端 `npm run typecheck` 与 `npm run build` 通过（`index` 128.50 kB 与 `ops` 39.37 kB 两个入口）。
- 迁移自愈：开发库启动时依次自动应用第 25 个迁移 `AddTaskRiskEnforcement` 与第 26 个 `AddTaskRiskAppeals`（日志"共 26 个迁移"）；真库回归用例在随机空库上 `Migrate()` 也从零建出这两张/批结构，并验证新列与新表的往返。
- 复检端到端（真机 HTTP + 真实 PostgreSQL）：一条已发布、且已被服务者报名的任务，在规则里新增 `prohibited.no_drones` 之后手动跑一轮 `POST /api/v1/admin/risk/recheck` → 统计 `{scanned:23, refreshed:22, unpublished:1}`；该任务变成 `Cancelled`、处置 `Suspended`、原因写明「风险复检命中平台禁止的类别（prohibited.no_drones · 未经许可的无人机作业，规则第 8 版）」，运营审计出现 `task.risk.recheck.unpublish` 且 `actorId` 是系统身份 `00000000-0000-0000-0000-00000000ffff`，所有者收到 `task.riskEnforced`、报名者收到 `task.cancelled`，所有者能在"我的任务"里看到下架原因。
- 加价即重判（真机，修掉本轮之前记录的绕过路径）：先发一条悬赏 50 元的任务并发布，再 `POST /tasks/{id}/increase-reward` 到 9000 元 → 返回 `riskVerdict=NeedsReview`、`riskRuleCode=review.high_reward`、`riskReviewStatus=Pending`、`riskEnforcementStatus=RecheckRequired`，并出现在 `GET /api/v1/admin/risk/reviews`（队列项带 `enforcementStatus` 与 `enforcementReason`）；运营放行后 `enforcementStatus` 回到 `None`、任务继续在线。
- 复检不再重复惊动（真机，连续两轮不同版本）：第 6 版目录跑一轮 `flagged=6`，修掉收敛口径后同一批任务在第 8 版只剩 `flagged=0`、`refreshed=22`——即规则版本升级只刷新版本号，不会把已经在队列里/已被人工放行的任务反复推回队列并重复通知。
- 申诉节流（真机）：同一条任务申诉 3 次都被受理（每次处置后改文案重置状态），第 4 次 `422`「这条任务累计申诉已达上限（3 次）：请先修改文案，规则会重新判定。」；换上另一条任务继续申诉，第 5 次受理、第 6 次 `422`「近 24 小时提交的申诉已达上限（5 次）：请明天再试，或先修改文案。」。
- 申诉轨迹（真机）：`GET /api/v1/admin/risk/appeals/{taskId}/history` 返回 3 条留档，每条都带 `ruleCode=prohibited.exam_impersonation`、`ruleVersion`、`status=Denied` 与运营结论原文，并回传当前生效的两条上限（3 / 5）；申诉队列项 `appealCount` 正确（这张表上线之前提交的老申诉没有对应行，界面会显示 0 并说明查不到轨迹）。
- 真库用例：复检在真库上的落库（新三列写入、换作用域读回、审计与通知落库、第二轮不再扫到）；申诉留档在真库上的往返（一次一行、规则代码与版本扣上、`CountByOwnerSince` 的窗口内外语义、批量计数、结论写回同一行）。
- 本轮踩到的两个坑（如实记录）：①在固定时钟下两次申诉的 `SubmittedAt` 完全相同，留档按「提交时间 + Id」排序时顺序其实是随机的，用例因此偶发失败——真实世界里两次申诉本来有先后，用例改成推进时钟让顺序确定；②"已经有人报名"的任务在下架时必须一并通知报名者，否则服务者会以为报名还有效。

本轮（风险规则目录后台编辑）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 283 + 集成 **331** = **614** 个用例全通过（本轮新增 20 条领域用例、12 条用例层用例、1 条真库用例）；前端 `npm run typecheck` 与 `npm run build` 通过（233.42 kB）。
- 迁移自愈：开发库启动时自动应用第 24 个迁移 `AddRiskRuleCatalogRevisions`（日志"共 24 个迁移"），真库回归用例在随机空库上 `Migrate()` 也从零建出 `risk_rule_catalog_revisions`（`Version` 唯一索引）。
- 真机 HTTP 全链路（管理员 JWT，基线是内置目录内容）：基线版本下「帮我把一台无人机送到郊区」`Allowed`（`ruleVersion=3`）→ 保存一版新目录（新增 `prohibited.no_drones`、匹配词"无人机/穿越机"，高金额阈值 5000 → 8000）返回摘要「v4：新增规则 1 条（prohibited.no_drones）；高金额阈值 5000 → 8000 元。」→ **同一条文本变成 `Blocked`（`prohibited.no_drones`，规则第 4 版）**。
- 发布时按"发布那一刻"的规则重判（实测）：早先那条草稿记录的仍是第 3 版 `Allowed`，用它发布返回 `422`「该任务属于平台禁止的类别（prohibited.no_drones · 未经许可的无人机作业），不能发布：任务涉及未经许可的无人机飞行，属于受管制活动。」——规则收紧后老草稿同样发不出去，而历史判定不被改写。
- 阈值随目录走（实测）：6000 元在新阈值下 `Allowed`、9000 元 `NeedsReview`（`review.high_reward`），两者都记第 4 版。
- 编辑的三道闸门（实测）：旧版本号提交 `409`「风险规则已被其他人修改（当前版本 v4，你读到的是 v3），请刷新后重试。」；一条禁止规则都不留 `422`「风险规则目录至少要保留一条禁止类规则……」；没写依据 `422`「修改风险规则必须写明依据。」；内容没变 `422`「风险规则内容没有变化，无需更新。」；单字匹配词 `422`「匹配词"机"太短：匹配词至少 2 个字。」；这 5 次失败提交之后版本仍是 v4、版本历史仍是 3 条（**失败不留版本**）。
- 审计（实测）：`GET /api/v1/admin/audits` 里出现 `task.risk.rules.update`（原因「v4：联调：把无人机作业列为禁止类别，并把高金额阈值抬到 8000 元」）与 `task.risk.rules.reset`（原因「v3：联调前清理：把目录恢复到代码内置的那一版」），对象类型 `risk_rule_catalog`。
- 恢复内置目录（实测）：一键恢复得到第 5 版（10 条规则、阈值回到 5000），版本历史保留全部 4 版——**被恢复掉的那一版仍然查得到**，回退本身也是一次有依据、有操作人的追加。
- 鉴权（实测）：匿名读规则明细 `401`、非管理员读概述/明细与调用恢复 `403`。
- 真库用例：规则目录在真实 PostgreSQL 上的往返（追加两版、读取取最新一版、`CatalogJson` 里能读到新规则、`Version` 唯一索引挡住并发重复追加、任务上记的规则版本号跟着新目录走且老任务停在旧版本）。
- 本轮踩到的坑（写进开发指南）：Windows PowerShell 5.1 的 `Invoke-RestMethod` 用字符串正文时会按非 UTF-8 发送，中文全部变成「?」——第一次联调因此把一版规则目录（关键词与说明）写成了乱码，正是靠"恢复内置目录"这条退路把它清回内置版本；正确做法是把 JSON 编成 UTF-8 字节再作为 `-Body` 传入，并显式带 `Content-Type: application/json; charset=utf-8`。

本轮（草稿字段级差异与回滚）新增验证：

- 编译与测试：`dotnet build AIToHuman.sln --no-restore` 0 警告 0 错误；领域 263 + 集成 **318** = **581** 个用例全通过（新增 7 条领域用例、4 条用例层用例、1 条真库用例）；前端 `npm run typecheck` 与 `npm run build` 通过（224.03 kB）。
- 字段级差异真机 HTTP：第 1 版 `changes` 为空；编辑一次后第 2 版列出 6 条差异，每条都是可读的"旧值 → 新值"——`标题：明天下午帮我去前台取一份文件 → 改成去菜市场买两斤苹果`、`截止时间：2026-09-14 09:46 UTC → 2026-09-15 09:46 UTC`、`悬赏：50 CNY → 66 CNY`、`验收标准：按时送达 → 苹果完好；送到家门口` 等。
- 回滚真机 HTTP：回滚到第 1 版后任务字段全部回来（标题/区域/悬赏/验收标准），历史版本号仍是 `1,2,3`（**中间那版还在**），第 3 版摘要是"回滚自第 1 版：标题、描述、公开区域、截止时间、悬赏、验收标准"并自带一组反向差异。
- 回滚的边界（实测）：版本不存在 `404`；非所有者 `403`；任务发布后再回滚 `422`；跨任务的快照被领域层拒绝。
- 真库用例：回滚后的内容换一个作用域读回来一致（确认真的落库），`task_draft_revisions` 里是 1/2/3 三行、第 2 版的标题仍是编辑后的内容（没有被覆盖）、第 3 版摘要以"回滚自第 1 版"开头。
- 一条重要的设计取舍（写进文档）：**回滚与编辑共用领域层唯一的 `ApplyDraft` 写入路径**，因此回滚同样会重判风险、作废人工复核与申诉；如果目标版本的截止时间已过去，回滚会被拒绝——宁可让用户先改截止时间，也不造出一个发布不了的草稿。
- 顺带记录我自己在写用例时踩的两个坑（都是"快照必须来自同一个任务"）：一开始拿**另一个任务实例**的初始版本来当回滚目标，被领域层的"这一版不属于当前任务"正确拦住——这条校验因此也被用例固定下来了。

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

- 配置骨架：新增 `system_settings`（覆盖值 + `Version` 并发令牌）与 `system_setting_audits`（只追加、脱敏）两张表（迁移 `AddSystemSettings`）、设置目录（白名单 + 类型校验 + 兼容的环境变量名，当时为 21 个键，后续轮次增至 26 个）、固定的解析顺序（数据库 → 环境变量 → 默认值）、Data Protection 加密的机密、写入即刷新的内存快照与 15 秒后台轮询、运营接口与“测试连接”自检、管理员名单策略；决策见 [ADR-0003](../architecture/decisions/0003-operator-configurable-settings.md)。
- 消费方接线：AI 服务商（地址、密钥、模型、无活动超时）、对象存储 provider、内容扫描 provider 都改为从配置读取；写死的 `NoOpEvidenceScanner`（该类型已删除）换成按配置工作的 `HttpEvidenceScanner`，`LocalFileStorage` 的根目录也改为运行时解析。
- 运营配置页面：顶栏入口（仅管理员）、按分组列出配置项与来源徽标、机密脱敏输入、保存（带版本冲突提示）、恢复默认、测试连接、变更记录；见第 3 节。
- 凭证扫描闭环与可配置上传上限：`evidence` 表新增扫描尝试次数、最近说明与尝试时间（迁移 `AddEvidenceScanAttempts`）；`EvidenceRescanService` 每 60 秒按 30 秒退避重扫 `Pending` 凭证、最多 5 次，文件缺失直接判定 `Rejected`，用尽次数后保留说明交给人工；`evidence.maxSizeBytes` / `evidence.maxPerOrder` 纳入设置目录（硬上限 25 MB / 50 份），凭证接口与页面展示检查次数与说明；见第 3、10 节。
- S3 兼容对象存储：新增 `S3FileStorage`（自研 AWS SigV4，不依赖厂商 SDK），按 `storage.s3.*` 做上传/下载/存在性/删除；`SettingsFileStorage` 按 `storage.provider` 分派 local 与 s3；配置不全、Bucket 不存在、密钥无权限都会翻译成可行动的说明；已用本机 MinIO 加独立客户端 `mc` 端到端验证（见第 11 节）。
- 短时直连下载地址：`S3FileStorage` 实现可选的 `IPresignedFileStorage`（SigV4 查询串签名，含对象路径、有效期与附件名），新增 `GET /api/v1/evidence/{id}/download-url` 与 `evidence.downloadUrlLifetimeSeconds`（5 至 900 秒，默认 120），前端在支持时直接跳转签名地址、否则回退流式下载；篡改与过期都由对象存储自己拒绝（403），已用真实 MinIO 验证。
- 元数据剥离与按人限速：`EvidenceContentSanitizer`（领域层，纯字节解析）处理 JPEG/PNG/WebP 的元数据段，`evidence.stripMetadata` 控制开关、剥离结果落库到 `evidence.MetadataRemoved`（迁移 `AddEvidenceMetadataRemoved`）并在接口与页面展示；`evidence.uploadsPerUserPerHour`（默认 60）按上传者限速，计数走数据库并配了 `(UploadedBy, CreatedAt)` 索引，多实例一致。
- 运营后台（人工兜底，后端与页面都已完成）：跨所有者检索任务与用户、查看任务详情、把**尚未分配**的任务下架并强制填写原因（原因写进 `admin_audit_entries`，`GET /api/v1/admin/audits` 可查）；已产生订单的任务会被领域规则拦住（`422`，提示先处理订单）。运营入口当时是顶栏弹窗，后来（见下方“运营后台独立成页”）整块搬到独立页面 `/ops.html`；现在那里的页签是：配置项 / 变更记录（配置审计 + 运营审计）/ 任务检索（含下架）/ 用户检索 / 争议处置 / 风险复核 / 误拦申诉 / 规则目录 / 地址留痕 / 命中统计（后两个页签是后续批次加的，见下方对应条目）。
- 已知线索（下轮排查）：运营联调时“服务者报名 → 需求方选人”这一步返回 `422 报名不存在。`，而报名接口当时返回了 `200` 与报名 id。**2026-09-12 复核**：同样的“创建 → 发布 → 报名 → 选人”流程在真实 PostgreSQL 上跑通（`task.status=Assigned`、`order.status=Accepted`、报名列表 1 条），因此这条线索更可能是当轮联调里任务/报名的状态问题或脚本取错 ID，而不是持久化缺陷；若再次出现，按“写一个针对性的仓储测试定位 EF 导航集合是否刷新”的方向排查。

本轮追加（多实例通知扇出）：

- 原子认领：`INotificationRepository.TryClaim`/`ReleaseDispatch` 与 `NotificationService.TryClaimDispatch`/`ReleaseDispatch` 替换原先“推送成功后才标记”的写法，EF 侧用条件 `ExecuteUpdate`、内存侧加锁；领域层新增 `Notification.ReleaseDispatch()` 作为失败补偿。
- 扇出：新增 `INotificationFanout` 与 `NotificationFanoutMessage`（Application）、`RedisNotificationFanout`（Infrastructure，StackExchange.Redis 3.1.3，频道 `aitohuman:notifications:fanout`）、`NotificationFanoutSubscriber`（Api，订阅断开每 5 秒重连、未启用时同样按 5 秒轮询等开关）；`NotificationDispatcher` 改为「认领 → 广播或本地推送 → 失败回滚」。官方 `Microsoft.AspNetCore.SignalR.StackExchangeRedis` backplane 包在本机离线取不到，因此直接基于 Redis 发布/订阅实现同一机制（客户端推送仍走 SignalR）；决策见 [ADR-0004](../architecture/decisions/0004-notification-fanout.md)。
- 配置：`notifications.fanout.enabled`（分组“通知推送”，默认 false）进设置目录；`ConnectionStrings__Redis` 保持部署级，不进配置表。

本轮追加（订单取消与任务过期）：

- 领域规则：`Order.Cancel(actorId, reason, now)` 实现「服务者仅 `Accepted`、需求方到 `Submitted` 之前」的取消矩阵并记录 `CancelledAt`/`CancelledBy`/`CancellationReason`；`TaskItem.Expire(now)` 把过期的已发布任务连同 `Pending` 报名一起作废（返回受影响的报名者，供通知使用）；`TaskItem.ReleaseAfterOrderCancelled(now)` 决定任务回到大厅还是直接过期，并把被选中的报名置为 `Rejected`（同时收回对该服务者的执行地址披露）；`TaskItem.Cancel` 现在也把撤销时间与原因记在任务上。
- 迁移 17 `AddOrderCancellationAndTaskExpiry`：给 `orders` 加取消三列、给 `tasks` 加 `ExpiredAt`/`CancelledAt`/`CancellationReason`。
- 后台过期扫描：`TaskExpiryService`（Api）每 60 秒调用 `TaskService.ExpireOverdueTasks(100)`，返回 `TaskExpiryResult(Expired, Skipped)`；`ITaskRepository.ListOverduePublished` 在 EF 侧保持跟踪以便并发令牌生效，单条冲突或状态已变只跳过该条。
- 接口与契约：`POST /api/v1/orders/{id}/cancel`（复用 `OrderActionRequest`，`note` 即原因）、`POST /api/v1/tasks/{id}/cancel`（新 `CancelTaskRequest(OwnerId, Reason)`）；`OrderResponse` 与 `TaskResponse` 补相应字段；通知新增 `order.cancelled`/`task.expired`/`task.cancelled` 与 `TaskNotificationPayload`（同一任务对多个接收者的事件键按接收者派生，否则只会入队第一条）。
- 前端：订单与任务上的“取消订单/撤销任务”按钮（内联必填原因 + 二次确认，条件由 `canCancelOrder`/`canCancelTask` 与后端双重把关）、取消原因展示、报名状态中文化、我的任务过期与撤销提示、运营状态筛选补 `Expired`、大厅报名按钮加 `Published` 判断与 `.order-actions button.danger` 样式。

本轮追加（报名撤回/报名截止时间与争议处理）：

- 报名：`TaskItem.WithdrawApplication`（只能撤回自己的、`Pending` 的记录，作废后保留为 `Withdrawn` 且允许重新报名）、`TaskItem.AcceptingApplications(now)` 与可选的 `ApplicationDeadline`（迁移 18）；`TaskService.WithdrawApplication`/`ListMyApplications` 与端点 `POST /tasks/{id}/applications/{applicationId}/withdraw`、`GET /tasks/applications/mine`；通知 `task.applicationWithdrawn`；`TaskResponse`/`TaskSummaryResponse` 补 `applicationDeadline` 与 `acceptingApplications`。
- 争议：`Order.OpenDispute`（需求方 `Submitted`、服务者 `Rejected`）与 `Order.ResolveDispute(DisputeResolution)`（`Approve`/`Rework`/`Cancel`，依据必填），新增枚举 `DisputeResolution` 与六个落库字段（迁移 19）；`TaskService.OpenDispute`、`AdminConsoleService.ResolveDispute`/`SearchOrders`、新查询接口 `IAdminOrderQuery`（EF 与内存两套实现，并把内存模式下“普通仓储与运营检索共用同一实例”的注册修对）；端点 `POST /orders/{id}/dispute`、`GET /admin/orders`、`POST /admin/orders/{id}/resolve`；通知 `order.disputed`、`order.disputeResolved`（按接收者派生事件键）；运营处置写审计 `order.dispute.{approve|rework|cancel}`。
- 前端：大厅显示报名截止与“报名已截止”、新增“我的报名”列表与撤回入口、订单行新增“申请平台介入”与争议原因/处置结果展示、运营弹窗新增“争议处置”页签（三条处置动作 + 必填依据）。

本轮追加（风险规则与禁止任务拦截）：

- 领域规则目录：`backend/AIToHuman.Domain/Risk/` 新增 `RiskVerdict`、`RiskReviewStatus`、`RiskRule`、`RiskAssessment`、`RiskRuleCatalog`（版本 1）。10 条词表规则（6 条 `prohibited.*` 禁止 + 4 条 `review.*` 转人工）加 2 条阈值规则（悬赏 > 5000 元、北京时间 00:00–06:00 截止），共 12 个原因代码；匹配词全部 ≥ 2 字，并有单元测试锁死（单字会误伤“代取/代送”这类正常任务）。
- 门禁与状态：`TaskItem` 构造时判定一次、`Publish` 前重新判定一次；新增 `IsRiskBlocked`/`AwaitingRiskReview`/`IsPublishBlockedByRisk` 与 `ApproveRiskReview`/`RejectRiskReview`（依据必填 ≤200 字）。禁止类别一律 `422` 且人工不能放行；人工驳回是终态，规则不再命中也不会变回可发布。风险判定复用任务行，**没有新增决策表**（只保留最新一条判定，追加式历史留作后续）。
- 迁移 20 `AddTaskRiskAssessment`：`tasks` 加 10 列（判定结论、原因代码、类别、说明、规则版本、判定时刻、复核状态、复核人、复核时刻、复核依据）与 `(RiskReviewStatus, CreatedAt)` 索引；存量行用数据库默认值 `Allowed`/`NotRequired` 回填，EF 读取侧对空值与未知枚举名做防御性解析。
- 应用层与契约：`ITaskRepository.ListPendingRiskReview`（EF 侧保持跟踪以便复用乐观并发令牌，内存侧同步实现）、`AdminConsoleService.ListRiskReviews`/`DecideRiskReview`/`DescribeRiskRules`；`TaskResponse`/`TaskSummaryResponse` 补 `riskVerdict`/`riskRuleCode`/`riskCategory`/`riskSummary`/`riskRuleVersion`/`riskAssessedAt`/`riskReviewStatus`/`riskReviewedAt`/`riskReviewNote`/`riskPublishBlocked`。
- 接口与通知：`GET /admin/risk/reviews`、`POST /admin/risk/reviews/{taskId}/decide`、`GET /admin/risk/rules`；新事件 `task.riskReviewed`（载荷含 `canPublish`），审计动作 `task.risk.approve` / `task.risk.reject`。
- 前端：草稿预览与“我的任务”显示被拦/待复核原因并禁用发布按钮；运营弹窗新增“风险复核”页签（队列 + 放行/驳回 + 必填依据 + 规则目录版本说明）；`api/admin.ts` 与 `api/tasks.ts` 补对应类型与客户端。

本轮追加（草稿字段编辑）：

- 领域：`TaskItem.UpdateDraft(...)`（只有 `ReadyToPublish` 可改），并把字段校验抽成 `EnsureDraftFields`、验收标准归一化抽成 `NormalizeCriteria`，创建与编辑共用同一套——避免“创建拦得住、编辑能绕过”。`AssessRisk(now, resetHumanDecision: true)` 让“文本被改过”时作废原有人工结论并按新文本重新判定。
- 契约与接口：新增 `UpdateTaskDraftRequest`、`PUT /api/v1/tasks/{id}`（所有者）、`TaskResponse`/`TaskSummaryResponse` 新增 `DraftEditable`。执行地址**没有**进任务载荷（`TaskResponse` 也会返回给服务者），编辑时由所有者单独读 `GET /tasks/{id}/execution-address`。
- 基础设施修复：`EfTaskRepository.Save` 改为 `db.Entry(record).CurrentValues.SetValues(ToRecord(task))` 整体覆盖，`Version` 单独自增。原实现逐列手写复制，漏掉了草稿正文那几列——这个缺陷只有真实 PostgreSQL 端到端能暴露（内存仓储保存的是同一个对象引用），详见第 11 节。
- 前端：`api/tasks.ts` 新增 `updateTaskDraft`、`TaskItem` 补 `draftEditable`；“我的任务”草稿行新增“编辑草稿”与内联表单（`styles.css` 的 `.draft-edit*`），保存后刷新并提示是否仍被风险规则拦住。

本轮追加（真实 PostgreSQL 回归测试基建）：

- 测试基建：`PostgresTestEnvironment`（连接串解析与探测、`[PostgresFact]` 跳过语义）、`PostgresRegressionFixture`（随机命名测试库、`Migrate()` 建库、逐用例清表、结束删库）、`PostgresWorld`（按 API 注册方式搭 EF 仓储与应用服务，`NewScope()` 模拟并发请求）。放在 `backend/tests/AIToHuman.IntegrationTests/Postgres/`。
- 8 个真库用例：见第 11 节本轮条目。重点是把“内存替身测不出来”的缺陷类型固定在回归里：并发写入与乐观并发令牌、EF 变更跟踪（`Save` 是否真的写了所有列）、迁移与真实 SQL 语义。
- CI 接线：backend job 起 `postgres:16` 服务并注入 `AITOHUMAN_TEST_POSTGRES`；frontend job 补 `npm run typecheck`。

本轮追加（报名列表内联服务者信用）：

- 契约：`TaskApplicationResponse` 补 `WorkerAverageRating`（默认 0）与 `WorkerReviewCount`（默认 0）。
- 服务层：`TaskService.PublicCredit(userId)` 把“哪些评价算公开”的判定收敛成一处，`GetReviewSummary` 与报名列表共用，避免两处口径漂移；`LoadWorkerCredit` 按 distinct workerId 批量取，`MapApplication` 接收摘要映射。
- 前端：`api/tasks.ts` 的 `TaskApplication` 补两个字段；“查看报名”每条显示“公开评价 X.X 分 · N 条”，没有公开评价时说明公开条件（双方都提交或完成满 7 天）。

本轮追加（草稿版本号与编辑历史）：

- 领域：新增 `TaskDraftRevision`（只追加的版本快照，`Initial` 写第 1 版、`FromEdit` 追加后续版本，`ChangeSummary` 由字段名列表拼成、超过 6 项收成“等 N 项”）；`TaskItem.UpdateDraft` 现在返回“这次改了哪些字段”，并在改之前做前后比对。
- 存储：`ITaskRevisionRepository`（EF 与内存两套实现）+ `task_draft_revisions` 表（迁移 21，`(TaskId, Revision)` 唯一索引）；`TaskService.Create` 与 `UpdateDraft` 都在同一个工作单元里写任务与快照。
- 接口与前端：`GET /api/v1/tasks/{id}/revisions`（仅所有者）+“我的任务”里的“修改记录”展开面板（`styles.css` 的 `.draft-history*`）。
- 顺带：创建/编辑共用更严的字段上限（描述 4000、区域 120、验收标准 ≤12 条且每条 ≤200），既是产品约束也是历史快照的列宽依据。

本轮追加（写接口幂等键）：

- 领域：`IdempotencyEntry`（占位 → 完成两段式，`Matches` 校验请求指纹、`IsStale` 处理"上次执行中途挂了"、响应体超长不缓存）。
- 应用/基础设施：`IIdempotencyStore`（`TryStart` 靠 `(UserId, Key)` 唯一约束挡并发）+ `EfIdempotencyStore`/`InMemoryIdempotencyStore`；`idempotency_entries` 表与迁移 22。
- API：`IdempotencyMiddleware` 在认证/授权之后执行，覆盖已登录用户的 `/api/v1` 写请求；回放带 `Idempotency-Replayed: true`；`5xx`、非 JSON、超长响应与业务异常都不缓存。

本轮追加（误拦申诉）：

- 领域：`RiskAppealStatus` 枚举与 `TaskItem.OpenRiskAppeal`/`ResolveRiskAppeal`/`CanAppealRisk`/`AwaitingRiskAppeal`；6 个落库字段（迁移 23）。两档处置能力写在领域方法里并配了测试：转人工被驳回的申诉成立即放行，禁止类别只留结论。
- 应用：`RiskAppealService`（运营侧队列与处置：`ListAppeals`、`DecideAppeal`，返回 `canBeReleasedByAppeal`）；所有者提交走 `TaskService.OpenRiskAppeal`（复用任务载荷映射）。`ITaskRepository.ListPendingRiskAppeal` 有 EF 与内存两套实现。
- 接口与通知：`POST /tasks/{id}/risk-appeals`、`GET/POST /admin/risk/appeals`（处置），新事件 `task.riskAppealDecided`（载荷含 `canPublish` 与 `decisionNote`），审计动作 `task.risk.appeal.accept|deny`。
- 前端：“我的任务”的“申诉误判”（禁止类别额外提示不会因此可发布）+ 申诉状态与运营结论展示；运营弹窗新增“误拦申诉”页签。

本轮追加（加固：清理与可读错误）：

- 幂等清理：`IIdempotencyStore.DeleteExpired`（EF 走 `ExecuteDelete`，内存实现逐个摘除）+ `IdempotencyCleanup`（策略与批量上限）+ `IdempotencyCleanupService`（每小时一轮的后台任务，失败只警告）。
- 可读错误：新增 `ValidationException`/`ConflictException`/`AiPlanningNotConfiguredException` 并在异常映射里逐类给出状态码与"消息是否外泄"，删掉了"消息里包含『已注册』"那条字符串匹配分支。
- 报名列表：`workerDisplayName`（批量取，取不到返回 null）；`TaskService` 构造新增 `IUserDirectory` 参数，12 个手工构造它的测试文件同步补参数。

本轮追加（把自动化扩展到 Redis 与对象存储）：

- 测试基建：`backend/tests/AIToHuman.IntegrationTests/External/ExternalTestEnvironment.cs`（探测 + `[RedisFact]`/`[MinioFact]` + 字典版设置提供者，对象存储的可用性靠"写一条探针对象再删掉"判定）；`RedisFanoutTests.cs`（5 条）；`S3StorageTests.cs`（5 条）。
- CI：三个依赖服务 + 建桶步骤（见 `.github/workflows/ci.yml`）。

本轮追加（本地开发形态收口）：

- 前端构建：`vite.config.ts` 的 `opsSubpath` 插件在 `closeBundle` 时把 `dist/ops.html` 拷成 `dist/ops/index.html`，于是纯静态托管（会按目录找 index.html）直接支持 `/ops/`，不必再写 `try_files`；新增 `frontend/.env.example`（四个 `VITE_*` 变量，默认全空 = 同源）。
- 后端：CORS 兜底改成按环境区分——开发环境放行本机 5173 / 4173（本地彩排跨域用），其它环境一个都不放行（同源部署不需要 CORS）；启动时把生效来源打进日志。
- 文档：第 7 节重写为「本地零配置 → 有域名后再落配置」的顺序，明确推荐子域名放法，并给出无需域名即可彩排跨域的两个端口做法。

本轮追加（运营后台跨域/子路径部署）：

- 前端地址口子：新增 `frontend/src/api/base.ts`（`apiFetch` / `apiUrl` / `hubUrl` / `API_BASE_URL` / `HUB_BASE_URL` / `APP_HOME_URL` / `OPS_HOME_URL`），把 12 个 api 模块里 62 处 `fetch('/api/v1...')`、SignalR 的 `/hubs/notifications` 以及两个入口之间的跳转链接统一收口；默认全部是相对路径，跨域部署时才由构建期变量给出绝对地址（`env.d.ts` 里声明了这四个键）。
- 后端：CORS 策略从“只在 Development 放行 `http://localhost:5173`”改成**所有环境统一挂载、允许来源由部署配置决定**（`Cors__AllowedOrigins`，逗号/分号/空格分隔或数组写法，未配置时回退到本机 5173），并补上 `AllowCredentials()`——SignalR 协商默认带 credentials，缺这一项跨域时只有通知订阅会失败。
- 本地开发：`vite.config.ts` 增加 `opsSubpath` 插件，把 `/ops` 与 `/ops/` 改写到 `/ops.html`，让开发/预览服务器也能按线上的子路径方式访问。
- 文档：第 7 节新增《把运营后台放到自己的域名或子路径》（两种放法对照表、四个构建变量、`Cors__AllowedOrigins`、nginx 例、登录态按来源隔离的说明）。

本轮追加（运营后台独立成页）：

- 前端结构：新增 `frontend/ops.html` + `frontend/src/ops/main.ts` + `frontend/src/ops/OpsConsole.vue`，把原先塞在 `App.vue` 里的运营弹窗整块搬过去（`App.vue` 从 2440 行降到 1641 行）；`frontend/vite.config.ts` 声明两个入口，构建产出 `dist/index.html` 与 `dist/ops.html`。
- 页面能力：独立会话（未登录给登录卡片、登录后向 `/auth/me` 确认是不是管理员、不是管理员只给一句说明且不请求任何运营接口）、顶部“← 返回任务工作台”与“退出登录”、整页铺开的版面（不再限宽限高）。
- 主应用：顶栏只保留一个“运营后台 ↗”链接（`target="_blank"`，仅管理员可见），运营相关状态与引用（`settingsOpen`、配置项/审计草稿、各队列数据）全部移出，退出登录时不再需要清理这些状态。
- 顺带抽取：时间格式化搬到 `frontend/src/utils/format.ts`，两个入口共用一份，避免口径漂移。

本轮追加（依赖感知的就绪检查、写接口限流与备份恢复演练）：

- 运维配置：设置目录新增「运维与限流」一组三个键（`ratelimit.enabled`、`ratelimit.writesPerMinute` 默认 240、`readiness.timeoutSeconds` 默认 3），目录变成 **30 个键、7 个分组**；三个键都走既有的热点刷新，改完立即生效。
- 就绪探针：新增 `ReadinessService`（`IReadinessCheck` / `ReadinessCheckStatus` / `ReadinessReport`）与三项基础设施探测（`PostgresReadinessCheck` 连得上**且没有待应用迁移**才算健康、`RedisReadinessCheck` 仅在开扇出时参与、`StorageReadinessCheck` 本机目录写探针再删 / S3 做存在性探测），端点 `GET /health/ready`（任一失败 `503`）；`/health` 保持"只看进程"的存活语义。
- 单项超时是**真的封顶**：探测并行走 `Task.WhenAny`，因为第三方客户端里有的调用根本不接受取消令牌（`StackExchange.Redis` 的 `ConnectAsync` 就是），只靠 `CancellationToken` 会让就绪端点陪着一起等。
- 写接口限流：`WriteRateLimiter`（固定一分钟窗口、按分区计数、设置现读、关掉开关就连计数都不做）+ `WriteRateLimitMiddleware`（按用户/来源 IP 分区，`429` + `Retry-After` + `X-RateLimit-*`，`/api/v1/admin/settings` 作为自救通道永不限流，且必须排在幂等中间件之前——否则被限流的 `429` 会被幂等回放缓存下来）。
- 恢复演练：新增 [备份、恢复与演练](../operations/backup-and-restore.md)（四样资产清单、RPO/RTO、可直接执行的 `pg_dump`/`pg_restore`/`mc mirror` 步骤、恢复顺序、三个容易踩的坑、定期清单），并在本机把整套流程真跑了一遍。
- 已知缺口（有意保留，写进文档）：限流计数是**进程内**的，多实例部署下实际配额是"每实例 × 上限"，跨实例配额要等 Redis 计数器；没有自动备份任务（生产需要由部署流程补 WAL 归档或定时 dump）；没有指标（Prometheus）与链路追踪，也没有告警规则，当前的可观测手段是就绪探针 + 结构化日志。

本轮追加（规则命中统计与追加式风险决策历史）：

- 领域：新增只追加的 `RiskDecisionEntry`（一次判定一行：`RiskDecisionReason` 说明是哪个动作触发的——创建 / 编辑 / 回滚 / 发布 / 加价 / 发布后复检，另有结论、原因代码、类别、当时的规则版本、当时的悬赏金额与判定时刻）。任务行上的那些风险字段仍然只保留"最新一条判定"，历史一律看这张新表：前者回答"这条任务现在能不能发"，后者回答"这条规则到底拦了多少次、误伤多少"。
- 应用：`IRiskDecisionRepository`（按任务取轨迹、按时间窗口取原始留痕）与 `IRiskAppealStatistics`（按原因代码数窗口内被**裁定**为误伤的申诉），`RiskDecisionService` 提供 `Record`/`ListByTask`/`Summarize`；窗口默认 30 天、上限 365 天，聚合口径全部在应用层算，EF 与内存两套实现共用同一段代码，避免两套数字对不上。
- 记录点：`TaskService` 的创建、发布、编辑草稿、回滚草稿、加价五处，加上 `RiskEnforcementService.Recheck` 一处——**每个判定点都必须记**，漏一个统计里就会出现"看不见的那部分"。留痕与任务写入在同一个工作单元里，不会出现"任务记了新判定、历史里却没有"。
- 存储与迁移：第 29 个迁移 `AddRiskDecisionEntries` 建 `risk_decision_entries`，`(TaskId, OccurredAt)` 服务"看一条任务的判定轨迹"、`(OccurredAt, RuleCode)` 服务"按原因代码出统计"。
- 接口与契约：`GET /api/v1/admin/risk/stats?days=`（判定总数、各结论计数、复检次数，以及按「原因代码 + 结论」分组的 `hits` / `recheckedHits` / 首末命中时刻 / `acceptedAppeals`）、`GET /api/v1/admin/risk/decisions?taskId=&limit=`（单条任务的判定轨迹）；两者都只读、仅管理员（非管理员 `403`），看板只回聚合与原因代码，不回申诉理由原文。
- 前端：运营后台新增"命中统计"页签（7/30/90/365 天窗口切换、总数说明、按规则的命中与误伤行，并带一个"按任务查判定轨迹"输入框），`api/admin.ts` 补 `RiskDecisionStats`/`RiskRuleHit`/`RiskDecisionEntry` 与两个客户端方法。

本轮追加（主机级端到端测试骨架）：

- 测试基建：`backend/tests/AIToHuman.IntegrationTests/Host/`（`HostE2EFixture.cs` 的 `HostTestEnvironment`/`HostE2EFixture`/`HostFactAttribute` + `HostE2ETests.cs`），把已构建的 API 起成子进程、连随机命名的临时库、用真 HTTP 打接口；配套 6 条用例覆盖健康与开发会话、任务到资金整链路、错误映射与鉴权、管理员名单、幂等键回放、加价重判。
- CI：测试那一步的说明与名字更新为"包含真库/Redis/对象存储套件**与主机级 E2E**"，不需要新增步骤（测试前的构建已经把 API 构建好）。

本轮追加（资金托管与只追加账本）：

- 领域：`EscrowStatus`、只追加的 `LedgerEntry`（复式记账：行 = 一次账户间转账，金额为正、借贷不同账户、必须挂订单）、`LedgerAccount`（需求方资金 / 平台托管 / 服务者应得，**没有平台收入账户**）、`LedgerEntryKind`（冻结/放款/退款/部分放款/部分退款）；`Order` 新增托管字段与 `HoldEscrow`/`ReleaseEscrow`/`RefundEscrow`/`SettleEscrow`（分账之和必须等于托管金额）。
- 应用：`IPaymentGateway` 端口 + `PaymentService`（`HoldFor`/`ReleaseFor`/`RefundFor`/`SettleFor`/流水查询），三条口径是"钱与状态一起动、失败就不改状态、可以整体关掉"；选人、验收、取消、争议处置四处接入。
- 存储与迁移：第 28 个迁移建 `ledger_entries` 并给 `orders` 加 7 个托管字段与 `(EscrowStatus, CreatedAt)` 索引；EF 与内存两套账本实现。
- 配置：新增 `payment.provider`（`simulated` 默认 / `disabled` 关掉托管），设置目录变为 27 个键、六个分组。
- 接口与前端：`OrderResponse` 与 `AdminOrderItemResponse` 带上托管字段，`AdminResolveDisputeRequest.amount` 支持按金额赔付/退款，新增参与者与运营两条流水接口；用户侧订单卡片显示托管状态并可展开"资金流水"，运营后台争议处置可填金额并看到资金去向。

本轮追加（精确地址访问审计 + 凭证容器白名单化）：

- 领域：新增只追加的 `AddressAccessEntry`（身份与结论由任务本身推出：`Owner`/`SelectedWorker`/`Other` × `Granted`/`Denied`/`NotSet`，匿名不留 viewer id，"没有地址可看"不算越权）与 `AddressAccessRole`/`AddressAccessOutcome`；`EvidenceContentSanitizer` 由黑名单改为**白名单 + 结构校验**（JPEG 丢全部 APPn/COM、PNG 只留 IHDR/PLTE/tRNS/IDAT/IEND、WebP 只留图像与动画块），结构坏了抛 `DomainException`（fail closed，不再原样放行）。
- 应用与接口：`AddressAccessService`（先留痕再判断：无论给不给都落库，然后才 403 或返回地址）替换掉原来不留痕的 `TaskService.GetExecutionAddress`；新增 `GET /api/v1/admin/address-access?taskId=&viewerId=&limit=`。
- 存储与迁移：第 27 个迁移建 `address_access_entries`（两条索引：看这条地址被谁看过 / 看这个人查过多少地址）。
- 前端：运营后台新增"地址留痕"页签（按任务或按人过滤，列出身份、结论、时间与查看者邮箱，并单独标出拒绝次数）。

本轮追加（发布后风险复检 + 申诉节流与留档）：

- 领域：`TaskItem.ReassessRisk`（发布后复检：禁止类别当场下架、有订单改为冻结订单、只命中转人工的保持在线并要求复检；同一条规则已放行或已在队列时只刷新版本号）、`IncreaseReward` 加价即重判、`Order.SuspendByRisk`（平台发起争议冻结订单）、`RiskEnforcementStatus`/`RiskEnforcementOutcome`、`AdminAuditEntry.SystemActorId`（平台动作的固定操作人身份）；`RiskAppealPolicy`（单任务 3 次 / 单人 24 小时 5 次）与 `RiskAppealRecord`（一次申诉一行、结论写回同一行）。
- 应用与后台任务：`RiskEnforcementService`（单轮复检 + 加价后的副作用）、`RiskRecheckService`（启动 30 秒后第一轮、之后每 5 分钟一轮，只扫"在线且规则版本不是最新"的任务，因此幂等）、`TaskService.OpenRiskAppeal` 的两道节流、`RiskAppealService.DecideAppeal` 写回留档与 `ListHistory`。
- 接口与通知：`POST /api/v1/admin/risk/recheck?limit=`、`GET /api/v1/admin/risk/appeals/{taskId}/history`、申诉队列新增 `appealCount`、任务与复核项新增风控处置三字段；新通知类型 `task.riskEnforced`；新审计动作 `task.risk.recheck.unpublish` / `freezeOrder`。
- 存储与迁移：第 25 个迁移给 `tasks` 加处置三列与 `(Status, RiskRuleVersion)` 索引；第 26 个迁移建 `task_risk_appeals`（两条索引分别服务"看轨迹"和"按人算次数"）。
- 前端：运营后台"风险复核"页签显示复检要求/平台处置与"立即复检在线任务"按钮、"误拦申诉"页签显示累计次数并可展开"申诉轨迹"、变更记录把系统身份显示成"平台（风控）"；"我的任务"显示平台已按风控处置及其原因。

本轮追加（风险规则目录后台编辑）：

- 领域：`RiskRuleCatalog` 从静态类变成**实例目录**（`BuiltIn` 是内置版本 1，构造时校验：至少一条禁止类规则、原因代码唯一、结论只能是禁止/转人工、匹配词 2–20 字、保留代码不能被顶替、阈值与深夜时段的合法区间），新增 `ToJson`/`FromJson` 快照往返与 `RiskRuleCatalogRevision`（只追加的版本快照 + 摘要 + 依据 + 操作人）。`TaskItem` 的创建、编辑、回滚与发布四处判定都接受"生效目录"参数（不传则退回内置目录）。
- 应用与接口：`IRiskRuleCatalogStore`（EF 与内存两套实现，读取取版本号最大的一版）、`RiskRuleCatalogService`（概述/明细/版本历史/整份替换/恢复内置），五条 admin 路由；`TaskService` 每次写入都取一次生效目录。
- 存储与迁移：第 24 个迁移建 `risk_rule_catalog_revisions`（`Version` 唯一索引 + `CreatedAt` 索引），只追加；配套 `PostgresWorld` 与真库用例。
- 前端：运营后台新增“规则目录”页签（当前版本/阈值/时段/规则明细与版本历史、整份编辑保存为新版本、填依据后恢复内置目录）。

本轮追加（草稿字段级差异与回滚）：

- 领域：把草稿字段的写入收敛成唯一的 `ApplyDraft`（编辑 `UpdateDraft` 与回滚 `RestoreDraft` 共用，避免两套校验漂移）；新增 `TaskDraftFieldChange` 与 `TaskDraftRevision.Diff(previous, current)`（值格式化成可读文本）；`TaskDraftRevision.FromRestore` 产出"回滚自第 N 版：…"的摘要。
- 应用与接口：`ListDraftRevisions` 为每一项附上差异；新增 `RestoreDraftRevision` 与 `POST /tasks/{id}/revisions/{revision}/restore`（仅所有者、仅草稿）。
- 前端：“修改记录”里逐版显示 `字段：旧值 → 新值`，除最新一版外提供“恢复这一版”（`styles.css` 的 `.draft-change`）。

### 后续跟进（原 P1 的延伸项）

- 幂等键的收尾：没有 ETag/版本字段返回；前端还没有"自动生成并复用幂等键"，目前只靠按钮置灰防重复点击（见第 10 节）。记录清理已经自动化（24 小时 / 10 分钟 / 每小时一轮）。
- 对象存储的短时签名 URL 已实现（见第 3、11 节）；如果以后要让前端完全绕开后端，需要补 CORS 配置与审计补偿。
- 选定病毒/内容扫描服务后把 `evidence.scanner.provider` 切成 `http` + `failMode=closed`（重扫闭环已经就绪，只差真实服务商）。
- 运营后台的其余部分：客服工单、**争议的责任判定**（赔付与退款已能按金额执行并走托管账本）、争议申诉与处理时限。风险复核队列、误拦申诉（含**单任务 3 次 / 单人 24 小时 5 次的节流与逐次留档**）、人工下架、**风险规则目录的后台编辑**、**发布后复检**与**精确地址访问审计**都已实现（改规则不再需要发版：版本号自动 +1、依据必填、只追加、可一键恢复内置目录；改完之后在线的任务会被重新判定并按口径处置；地址的每次读取都留痕，含被拒绝的尝试）。规则编辑仍然没有双人复核；禁止类别命中的已分配任务只做"冻结订单 + 运营按争议处置"，**没有自动退款或赔付**（等支付与托管那一批）。上传大小与份数上限已经进了设置目录；凭证类型白名单**故意不进**（放开等于允许上传可执行内容）。
- 风险判定的增强：模型辅助分类（现在只有字面词表匹配，语义变体容易漏）、**追加式决策历史与规则命中看板已实现**（迁移 29 `risk_decision_entries`：六个判定点各追加一行，看板按原因代码给出命中次数、复检命中与被判误伤次数；仍未做的是阈值告警、趋势对比与按运营人的报表）、误拦与漏拦的回归测试集扩充（当前是 17 条禁止 + 6 条转人工 + 8 条正常用例）。
- 草稿的版本与历史：版本号、编辑历史、**字段级差异与回滚**都已实现（`task_draft_revisions` 只追加，编辑与回滚都追加一版、都不会覆盖已有版本）。仍然没有独立的 `TaskDraft` 聚合（编辑直接改 `ReadyToPublish` 的任务）；历史的可见范围只有所有者，运营后台看不到。
- 真实基础设施的自动化回归测试：**PostgreSQL、Redis、S3 兼容对象存储与主机级端到端四块都已建立**（见第 7 节与第 11 节各轮条目）。剩下的自动化缺口是"两个真实实例 + 客户端"的通知扇出整链路。
- 通知的更多事件类型（任务发布、加价、评价公开）与推送渠道（短信、邮件）。
- 会话消息的分页与历史截断、消息撤回与编辑。
- 大厅排序选项（悬赏、距离）与任务分类筛选；精确地址的访问审计**已实现**（见第 3、11 节），剩下的隐私侧缺口是地址的"访问配额/异常告警"（现在只有留痕与运营查询）。
- 凭证图片的**像素级重新编码**（需要图像编解码库，当前环境离线取不到；已经做到的是容器白名单化 + 结构 fail-closed）、凭证与验收项关联，以及运营后台的申诉工单。

### P2

- Redis backplane 已用自研发布/订阅落地（见第 3、11 节，ADR-0004）；剩余的是缓存、分布式锁与限流，以及多实例部署本身的编排与可观测性。
- 实名认证、真实支付通道接入（托管、账本与争议分账已实现，缺的是真实通道、对账与失败重试）与合规评审。
- 运营审核后台与高风险任务人工复核。

## 13. 交接检查清单

- [ ] 执行 `git status` / `git log --oneline -3`，确认工作区干净，并核对最新几条提交与本文第 2 节记录的状态一致（若已有更新提交，先核对第 3 节的实现描述是否仍然成立）。
- [ ] 确认 `VolcengineAI:Model` 是火山控制台真实启用的模型或接入点 ID。
- [ ] API Key 仅存在于 User Secrets 或环境变量，没有进入 Git。
- [ ] PostgreSQL 已启动，`/health` 返回 `healthy`；`/health/ready` 返回 `200`，其中 `postgres` 项的说明应包含"没有待应用的迁移"（有待应用的迁移时它会直接报不健康——别忽略这条）。
- [ ] 把 `ratelimit.writesPerMinute` 临时改成 2，用一个新账号连发三个写请求：前两个成功、第三个 `429` 且带 `Retry-After` 与「一分钟内的写请求次数已达上限（2 次）」；同一时刻读接口照常 200；改回 240 时**不会**被限流挡住（配置写入是自救通道）。
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
- [ ] 在部署配置里设置 `Admin__UserIds` 或 `Admin__Emails`，用它登录后访问 `/api/v1/admin/settings`：非管理员应拿到 `401/403`，管理员拿到 26 个配置项。
- [ ] 通过运营接口把 `ai.apiKey` 换成新密钥：响应只显示 `****末四位`；接着发起一轮 AI 对话应立刻用新密钥，不需要重启进程。
- [ ] 把 `evidence.scanner.provider` 改成 `http` 但不填扫描地址，服务者上传凭证应返回“待扫描、不可下载”，文件不被删除也不放行。
- [ ] 重启进程后重新读取运营配置：机密仍能解密（说明 `DataProtection__KeysPath` 指向了持久目录，而不是临时目录）。
- [ ] 用管理员账户打开 <http://localhost:5173/ops.html>（或点主应用顶栏的“运营后台 ↗”）：能看到 30 个配置项、七个分组与来源徽标；非管理员账户打开同一个地址应看到“这个账号没有运营权限”，且一个运营接口都不会被调用。
- [ ] 切到“任务检索”页签：按标题关键字能搜到任务并看到需求方邮箱与报名数；对一条大厅中的任务点“下架”并填写原因后，任务从大厅消失、审计里出现对应记录；对一条已分配的尝试下架应看到可读的拒绝提示。
- [ ] 在页面上改一个机密项并保存：列表立刻显示新的掩码与“后台已改”，点“测试连接”能看到自检结果，切到“变更记录”能看到这次修改；把某条改坏（例如把超时填成 1）保存应看到可读的校验提示。
- [ ] 把 `evidence.maxSizeBytes` 调成 1024 后上传一张 2 KB 的图片：应返回“凭证大小不能超过 1 KB”，恢复默认后能正常上传。
- [ ] 把 `evidence.scanner.provider` 改成 `http` 并把扫描地址清空（或指向不可用地址），上传凭证：状态应是“检查中 / 不可下载”并写明原因；扫描服务恢复后，一分钟内后台重扫会把状态改成“已通过检查”。
- [ ] 在对象存储里建好私有 Bucket，运营配置里把 `storage.provider` 换成 `s3` 并填好 endpoint/region/bucket/密钥：上传凭证后应在 Bucket 里看到 `<prefix>/<orderId>/<evidenceId>.png`，且本机目录不再新增文件。
- [ ] 故意把 `storage.s3.secretAccessKey` 改错：上传应返回可读的错误（提到密钥或权限、并带上 S3 的错误码），而不是 500 或静默写本机。
- [ ] 对象存储模式下点凭证“下载”：地址是带 `X-Amz-Signature` 的短时链接，浏览器直接拿到文件；把 `evidence.downloadUrlLifetimeSeconds` 改成 5 秒后重新下载，等 8 秒再点应被对象存储拒绝（403）。
- [ ] 上传一张带 GPS 的截图（手机原图即可）：凭证面板应显示“已在上传时移除元数据：PNG tEXt（或 EXIF/XMP）”，下载下来的文件里搜不到拍摄地点。
- [ ] 部署一个 clamd（或用替身）并把 `evidence.scanner.provider` 改成 `clamav`：上传正常图片应 `200`；上传含 EICAR 测试串的文件应被 `422` 拒绝，且存储里不留文件。
- [ ] 把 `evidence.uploadsPerUserPerHour` 调成 2，连续上传三次：第三次应返回可读的限流提示。
- [ ] 多实例投递：给两个实例都配 `ConnectionStrings__Redis`，一个浏览器连实例 A、另一个连实例 B；在 A 上触发一次状态变更，B 的页面应不刷新就收到通知。再在运营后台把 `notifications.fanout.enabled` 关掉重试：通知仍然落库、收件箱照常，只是不再跨实例实时推送（实例 B 的日志会写“当前未启用”）。
- [ ] 取消订单：服务者开始执行前点“取消订单”应成功，任务回到大厅并可以再次被选人；服务者开工后该按钮应消失（直接调接口返回 `422`）；需求方在服务者提交验收后也不应能取消。
- [ ] 到期过期：把任务的截止时间设成 1 分钟后并发布（不要选人），一分钟左右刷新“我的任务”，状态应变成“已过期”，报名过的服务者应收到 `task.expired`；已分配的任务即使过了截止时间也不应变成过期。
- [ ] 报名撤回与报名截止：以服务者身份报名后到“我的报名”里撤回，应看到“已撤回”且可以再次报名；创建一个带报名截止时间的任务，到点后大厅应显示“报名已截止”，但此前报名的人仍能被选中。
- [ ] 争议：服务者提交后由需求方发起争议，双方应看到订单冻结（按钮消失或接口返回 `422`）；以管理员打开“争议处置”页签，分别验证“退回返工 / 强制完成 / 终止订单”三条路径对订单、任务与双方通知的影响，并确认审计里出现 `order.dispute.*`。
- [ ] 用一条禁止类任务（例如“帮我代考××考试”）走完创建与发布：草稿能建出来但 `riskVerdict=Blocked`，发布返回 `422` 且大厅里查不到；对同一条任务调用运营复核接口应返回 `422`（人工无权放行禁止类别）。
- [ ] 用一条敏感任务（例如“帮我把身份证送到××”）走完复核：`riskReviewStatus=Pending`、发布 `422`；运营在“风险复核”页签写依据放行后可以发布；驳回后发布 `422` 并显示驳回依据；需求方收件箱出现 `task.riskReviewed`。
- [ ] `GET /api/v1/admin/risk/rules` 返回规则版本与规则清单，但**只有匹配词数量、没有匹配词本身**；非管理员访问返回 `403`。
- [ ] 跨域部署：在一台静态托管（另一个端口/域名）上打开运营后台，用管理员账号登录应一切正常；同时把后端 `Cors__AllowedOrigins` 里的来源去掉再试一次，浏览器应该在预检阶段就报 CORS 错误（页面显示“Failed to fetch”），说明这份名单真的在拦。
- [ ] 跨域下通知订阅：主应用与 API 不同源时，登录后 `POST /hubs/notifications/negotiate` 应返回 `200`、控制台没有 CORS 报错；如果这里报“缺少 Access-Control-Allow-Credentials”，检查 CORS 策略是不是漏了 `AllowCredentials()`。
- [ ] 子路径部署：让 `/ops/` 指向 `ops.html`（本地可直接用 `npm run preview` 访问 `/ops/`），页面应正常加载、资源从 `/assets/...` 取；主应用顶栏的“运营后台”入口与运营页的“返回任务工作台”都应跳到配置好的地址。
- [ ] 运营后台是独立页面：用管理员账户打开主应用，顶栏应有“运营后台 ↗”链接（普通账户没有），点开新标签页落在 `/ops.html`；`npm run build` 之后 `dist/` 里应同时有 `index.html` 与 `ops.html`，把 `dist` 整体托管时两个地址都能打开。
- [ ] 运营后台的会话处理：清掉浏览器本地存储后打开 `/ops.html` 应看到登录卡片（而不是空白或报错）；`GET /api/v1/tasks` 之类的前台接口在这个页面上不应该被调用；点“退出登录”回到登录卡片。
- [ ] 编辑一条草稿（改标题、悬赏、验收标准与截止时间后保存）：`GET /api/v1/tasks/{id}` 返回的字段应与提交一致，随后能发布；编辑已发布的任务应返回 `422`。
- [ ] 编辑一条草稿两三次，然后点“修改记录”：应看到 1/2/3… 逐版列出、每版有变更摘要、时间与**逐字段差异（旧值 → 新值）**；把草稿改成禁止内容后，最新那版应显示 `Blocked` 与原因代码；用别的账号读历史应返回 `403`。
- [ ] 回滚：对较早的一版点“恢复这一版”，任务字段应回到那一版、历史里应多出一版（摘要“回滚自第 N 版：…”）而**中间版本仍在**；对已发布的任务回滚应返回 `422`。
- [ ] 用敏感草稿验证防绕过：先由运营放行 → 再编辑草稿 → 复核状态应回到“待复核”、复核人与依据清空、重新出现在“风险复核”队列、发布被 `422` 拦住；再次放行后可以发布。
- [ ] 规则目录编辑：在“规则目录”页签里加一条禁止类规则（例如 `prohibited.no_drones`，匹配词“无人机”），填依据后保存——版本号应 +1、摘要写清改了什么、审计里出现 `task.risk.rules.update`；随后创建一条含“无人机”的草稿应变成 `Blocked`，而**保存之前建的**同类草稿在发布时同样被 `422` 拦住（规则按发布那一刻生效）。
- [ ] 规则目录的边界：拿旧的版本号再提交应返回 `409`；把禁止类规则全删应返回 `422`；不填依据或内容没变也应返回 `422`，且这些失败都不产生新版本；点“恢复内置目录”后版本号继续 +1、内容回到内置那一版，历史里被恢复掉的版本仍然查得到。
- [ ] 规则目录的可见性：`GET /api/v1/admin/risk/rules` 只有匹配词数量；`/detail` 才给词本身，且非管理员访问 `/detail`、`/versions` 与两个写接口都是 `403`、匿名是 `401`。
- [ ] 发布后复检：发一条普通任务（悬赏 50 元）并在“规则目录”页签里加一条命中它的禁止类规则，保存后在“风险复核”页签点“立即复检在线任务”——该任务应被自动下架（状态变“已下架”、显示平台处置原因），运营审计里出现 `task.risk.recheck.unpublish` 且操作人显示为“平台（风控）”，所有者与报名过的服务者都收到通知。
- [ ] 加价重判：先发一条普通任务并发布，再把它加到 9000 元——返回值应直接变成“需人工复核（review.high_reward）”，并出现在“风险复核”队列里（队列项写明“规则升级后的复检要求”）；运营放行后复检标记消失、任务继续在线。
- [ ] 复检不重复惊动：连续保存两版目录（内容都命中同一批在售任务），跑两轮复检——第二轮应该只刷新规则版本号，不会把这些任务重新推回队列或再发一轮通知。
- [ ] 申诉节流：对同一条任务反复申诉（每次处置后改一下文案）——第 4 次应返回 `422`「这条任务累计申诉已达上限（3 次）…」；换一条任务继续申诉到当天第 6 次，应返回 `422`「近 24 小时提交的申诉已达上限（5 次）…」。
- [ ] 申诉轨迹：在“误拦申诉”页签点某条任务的“申诉轨迹”，应看到每次申诉的规则版本、理由、运营结论与依据，以及当前生效的两条上限（3 / 5）。
- [ ] 命中统计与判定轨迹：在“命中统计”页签切换 7 / 30 天窗口，应看到窗口内的判定总数与各结论计数，以及按规则列出的命中次数、复检命中与"被判误伤"次数（`acceptedAppeals` 按申诉**裁定**时间计，不是提交时间）；`GET /api/v1/admin/risk/decisions?taskId=<任务>` 应给出这条任务的完整判定轨迹（创建 / 编辑 / 发布 / 复检各一行，含当时的规则版本）；非管理员访问这两个接口都应 `403`。
- [ ] 地址留痕：用所有者读一次 `/api/v1/tasks/{id}/execution-address`（应 `200`），再让一位没被选中的服务者读一次（应 `403`），然后以管理员打开运营后台"地址留痕"页签：应看到这两条记录（一条"已披露"、一条"已拒绝"），拒绝计数为 1；按查看者 ID 过滤应只剩那位服务者的记录。
- [ ] 凭证规范化：上传一张 PNG，其中夹一个私有块（例如在 `IDAT` 前插一个 `prVt` 块，载荷写 `PK\x03\x04`）——应上传成功，且凭证面板的"已在上传时移除元数据"里出现 `PNG 未知块(prVt)`，文件的 `sizeBytes` 比上传前小；再把同一张图截断（去掉 `IEND`）后上传，应返回 `422`「凭证内容不是合法的 PNG（缺少结束块 IEND），已拒绝保存。」且凭证列表里不新增条目。
- [ ] 资金托管：发布一条 60 元的任务并选中一位服务者——“我的订单”里应显示"资金托管：已冻结（等待放款或退款）60 CNY"，点"资金流水"应看到一条 `需求方资金 → 平台托管`；服务者提交后需求方点"确认完成"，托管状态应变成"已全额放款给服务者"，流水里多一条 `平台托管 → 服务者应得`。
- [ ] 取消退款：另发一条任务并选中服务者，然后取消订单——托管状态应变成"已全额退回需求方"，流水里应有一条 `平台托管 → 需求方资金`；用别的账号读这笔订单的流水应返回 `403`。
- [ ] 争议分账：让一条订单进入争议，在运营后台"争议处置"里选"强制完成"并把金额填成托管额的一半——处置后订单托管状态应是"已分账"，流水里应同时出现"部分放款"与"部分退款"两条，审计里应看到带「（资金处置金额 X CNY）」的原因；金额填成超过托管额应返回 `422`。
- [ ] 托管开关：在运营配置里把 `payment.provider` 改成 `disabled`，再发一条任务并选人——订单不应带托管信息、也不产生任何流水；改回 `simulated` 后恢复正常。
- [ ] 主机级用例：先 `dotnet build AIToHuman.sln`，再跑 `dotnet test backend/tests/AIToHuman.IntegrationTests --no-build --no-restore --filter "FullyQualifiedName~HostE2ETests"`：9 条应**全部通过**（它们会真的把 API 起成子进程、连临时库、用真 HTTP 打接口）；把 `AITOHUMAN_TEST_HOST` 设为 `0` 再跑一次，应变成**跳过**而不是失败。
- [ ] 跑一次 `dotnet test AIToHuman.sln --no-build --no-restore`：确认 `PostgresRegressionTests` 是**通过**而不是**跳过**（跳过说明本机没连上测试库，见第 7 节“真实数据库回归测试”）；再把 `AITOHUMAN_TEST_POSTGRES` 指向不可达端口确认它们变成跳过而不是失败。
- [ ] 用两个账号跑一遍双向信用：服务者完成一单且双方互评后，需求方在自己的任务“查看报名”里应看到该服务者的公开评分与条数，且与 `GET /api/v1/users/{id}/review-summary` 一致；只有单方评价（盲期内）时列表里应为 0 分 / 0 条。
- [ ] 幂等键：对同一个写接口用同一个 `Idempotency-Key` 连发两次，第二次应返回与第一次相同的状态码和响应体，并带 `Idempotency-Replayed: true`；把请求体改掉再用同一个键，应返回 `409`；不带这个头时行为应和以前完全一样。
- [ ] 误拦申诉：把一条敏感草稿提交申诉，运营在“误拦申诉”页签里给出结论（依据必填）；如果命中的是禁止类别，申诉成立后任务**依旧不能发布**，所有者收到的通知里 `canPublish` 应为 false；同一版内容再次申诉应返回 `422`，改过文案后可以重新申诉（总次数受单任务 3 次与单人 24 小时 5 次两条上限约束）。
- [ ] 外部依赖回归：跑一次 `dotnet test AIToHuman.sln --no-build --no-restore`，确认 `RedisFanoutTests` 与 `S3StorageTests` 是**通过**而不是**跳过**（跳过说明本机 Redis/MinIO 没起来或桶不存在，跳过信息里有原因）；再把 `AITOHUMAN_TEST_REDIS` 指向 `127.0.0.1:6399` 确认它们变成跳过而不是失败。
- [ ] 幂等清理：启动 API 后日志应出现「幂等记录清理已启动：已完成记录保留 24 小时、未完成占位保留 10 分钟，单次最多清理 500 条。」；把库里某条 `idempotency_entries` 的 `CompletedAt` 手工改成两天前，下一个整点（或重启后第一轮）应被清掉。
- [ ] 可读错误：故意发一个缺 `role` 的注册请求，应返回 `422` 且 detail 是「角色必须是 owner 或 worker。」而不是「请求暂时无法处理。」；用已注册邮箱再注册应返回 `409` 且 detail 是「该邮箱已注册。」。
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
- [备份、恢复与演练](../operations/backup-and-restore.md)
- [模块化单体决策](../architecture/decisions/0001-modular-monolith.md)
- [固定悬赏与双向选择决策](../architecture/decisions/0002-fixed-reward-and-mutual-selection.md)
- [三方集成参数可配置决策](../architecture/decisions/0003-operator-configurable-settings.md)
- [多实例通知投递决策](../architecture/decisions/0004-notification-fanout.md)
