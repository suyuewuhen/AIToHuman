# AIToHuman

AIToHuman 是一个“AI 任务管家 + 真人服务任务大厅”平台。用户只需描述想完成的事情，AI 负责澄清需求、规划步骤并完成可自动化的部分；需要现实世界执行的部分，经用户确认后发布到任务大厅，由服务者报名、用户选择，再执行并提交凭证。

> 产品主张：你只需要说清楚想完成什么，AI 帮你想明白，真人帮你做到。

## 当前状态

项目已进入 MVP 开发阶段。首个版本聚焦同城低风险任务，优先打通以下闭环：

```text
需求对话 → AI 澄清与规划 → 任务草稿 → 用户确认发布
→ 服务者查看用户信用并报名 → 用户查看服务者信用并选择
→ 执行并提交凭证 → 用户验收 → 双向评价
```

支付、实名认证、复杂风控和自动结算将在核心业务闭环稳定后逐步接入；在正式交易能力上线前，不以模拟支付替代真实合规方案。

## 技术方向

已接入并有代码可验证：

- 前端：Vue 3、TypeScript、Vite（严格模式，`npm run typecheck`）
- 后端：ASP.NET Core 10 Minimal API、C#、Entity Framework Core
- 架构：前后端分离的模块化单体，依赖方向 `Api → Application → Domain`
- 数据：PostgreSQL（当前开发事实来源；未配置连接串时回退内存仓储）
- 实时通信：SignalR（订单创建、状态变化与新消息通知；持久化 Outbox + 后台派发 + 未读数收件箱，推送只是刷新提示；多实例通过 Redis 扇出投递，运营可开关，见 [ADR-0004](docs/architecture/decisions/0004-notification-fanout.md)）
- AI：火山引擎 Ark OpenAI 兼容接口，SSE 流式多轮澄清
- 凭证存储：本机私有目录或 S3 兼容对象存储（MinIO / 阿里云 OSS / AWS S3）可切换，签名是自研的 AWS SigV4（不依赖厂商 SDK），下载支持短时直连签名地址，已用本机 MinIO 端到端验证
- 运营配置：设置目录（白名单）+ 加密机密 + 变更审计 + 写入即生效；管理员在顶栏“运营配置”页面即可调整模型、对象存储、内容扫描参数与凭证上传上限（见 [ADR-0003](docs/architecture/decisions/0003-operator-configurable-settings.md)）
- 本地依赖：`compose.yaml` 定义 PostgreSQL、Redis 和 MinIO

规划中、代码尚未接入：

- 前端 Pinia、Vue Router、Element Plus（当前是单页 `App.vue`，未引入路由和状态库）
- Redis 缓存与分布式锁、Hangfire 后台作业
- Nginx 与生产环境部署编排

## 仓库结构

```text
AIToHuman/
├── frontend/                       # Vue 用户端与服务者端（单页工作台）
│   └── src/
│       ├── api/                    # AI SSE、认证、会话、任务、订单消息、凭证、评价、运营配置，SignalR 客户端
│       ├── App.vue                 # 对话工作台、草稿、大厅、订单、会话弹窗、凭证面板与运营配置
│       └── styles.css
├── backend/
│   ├── AIToHuman.Api/              # HTTP、JWT、AI SSE、SignalR Hub、通知后台派发、运营配置接口
│   ├── AIToHuman.Application/      # 用例、仓储、通知接口与设置目录
│   ├── AIToHuman.Domain/           # 实体、值对象与状态机
│   ├── AIToHuman.Infrastructure/   # EF Core、PostgreSQL、内存仓储、本机文件存储与内容扫描适配
│   ├── AIToHuman.Contracts/        # 请求与响应 DTO
│   └── tests/
│       ├── AIToHuman.Domain.Tests/          # 领域单元测试
│       └── AIToHuman.IntegrationTests/      # 用例与 AI 多轮协议/SSE 集成测试
├── docs/
│   ├── product/                    # 产品定位、MVP 与用户故事
│   ├── architecture/               # 系统设计、领域模型与 ADR
│   ├── api/                        # API 约定
│   ├── security/                   # 安全、隐私、风控与合规边界
│   ├── development/                # 开发规范、路线图与交接文档
│   └── ai-planning.md              # AI 多轮澄清与火山引擎配置
├── .github/workflows/ci.yml        # 后端构建测试与前端构建
├── compose.yaml                    # 本地 PostgreSQL、Redis、MinIO
├── AIToHuman.sln
├── .editorconfig
├── .env.example
├── CONTRIBUTING.md
└── README.md
```

## 文档索引

- [产品愿景与范围](docs/product/product-vision.md)
- [MVP 产品需求文档](docs/product/mvp-prd.md)
- [用户故事与验收标准](docs/product/user-stories.md)
- [系统架构](docs/architecture/system-architecture.md)
- [领域模型与状态机](docs/architecture/domain-model.md)
- [架构决策记录](docs/architecture/decisions/0001-modular-monolith.md)
- [固定悬赏与双向选择决策](docs/architecture/decisions/0002-fixed-reward-and-mutual-selection.md)
- [API 设计约定](docs/api/api-guidelines.md)
- [安全、隐私与风控](docs/security/security-and-risk.md)
- [开发指南](docs/development/development-guide.md)
- [项目交接文档](docs/development/handoff.md)
- [AI 多轮需求澄清配置](docs/ai-planning.md)
- [路线图](docs/development/roadmap.md)
- [贡献指南](CONTRIBUTING.md)

## MVP 成功标准

以下为 MVP 目标，不代表当前已具备的能力。截至 2026-09-12，AI 建单、固定悬赏报名、双向选择、订单状态流转、订单内沟通（消息与未读）、执行凭证上传与双向评价已可端到端演示；禁止任务拦截和审计记录尚未实现；凭证侧已接入 ClamAV 的 INSTREAM 协议与外部扫描服务两种方式，缺的是部署真实病毒库（扫描不可用时凭证不会被放行，而是保持不可下载并自动重试）。

- ⚠️ 用户能在 AI 引导下生成一份字段完整、可编辑的任务草稿（草稿已生成，但只有悬赏可按 +5 调整，标题、描述、截止时间和验收标准不可在页面编辑）。
- ✅ 用户能明确确认后发布任务，AI 不能绕过确认直接发布。
- ✅ 服务者能查看任务悬赏和用户历史评价，并按固定悬赏报名。
- ⚠️ 用户能查看报名服务者的历史评价并选择合适人选（选择报名者已实现，但报名列表不返回服务者评价，需求方目前看不到服务者信用）。
- ✅ 双方能围绕订单沟通、更新进度并上传执行凭证（订单内消息与未读数、凭证上传与鉴权下载已实现，扫描未通过的凭证不可下载且会自动重试；扫描服务本身仍是占位/可配置实现，未接入真实杀毒厂商）。
- ⚠️ 用户能验收、拒绝并说明原因，系统完整保留审计记录（验收与驳回原因已实现，审计记录未实现）。
- ⛔ 禁止任务会被拦截，高风险任务不会自动进入大厅。

## 参与开发

项目当前优先完善产品边界和技术基线。开始编码前请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [开发指南](docs/development/development-guide.md)。

### 本地启动

前提：.NET 10 SDK、Node.js 24、npm，以及本机 PostgreSQL。当前本机开发连接使用 `localhost:5432` 的 `postgres` 用户和 `postgres` 数据库，密码保存在被 Git 忽略的 `backend/AIToHuman.Api/appsettings.Development.json` 中。部署或其他机器请通过 `ConnectionStrings__Postgres` 覆盖。

```powershell
dotnet restore AIToHuman.sln --configfile NuGet.Config
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet run --project backend/AIToHuman.Api --urls http://127.0.0.1:5188
```

如果本机没有 PostgreSQL，也可以使用 `docker compose up -d postgres` 启动仓库定义的隔离开发数据库；此时请用环境变量覆盖连接串。

另开一个终端：

```powershell
Set-Location frontend
npm install
npm run dev
```

必须先启动后端，再启动 Vite；否则浏览器会看到 `vite http proxy error: ECONNREFUSED 127.0.0.1:5188`。可以先访问 `http://127.0.0.1:5188/health`，确认返回 `healthy` 后再打开前端。

前端地址为 `http://localhost:5173`，API 健康检查为 `http://localhost:5188/health`。任务与报名使用 PostgreSQL 持久化；Redis 已用于通知的多实例扇出（由运营开关 `notifications.fanout.enabled` 控制，未启用时按单实例推送），MinIO 作为本地依赖供 S3 兼容对象存储使用（凭证默认仍写本机私有目录）。

打开前端后，点击右上角“开发会话”可登录或注册真实账户。一个账户同时支持需求方和服务者身份，登录后点击右上角身份菜单即可切换，不需要重复注册。切换只改变当前 JWT 的操作角色，不会改变账户 ID、历史任务或订单归属。任务创建、报名、查看报名和选择服务者会使用 JWT 身份；未登录时仅保留 Development 环境的合成会话用于联调。

如果 Vite 输出 `http proxy error: ECONNREFUSED`，先确认后端终端仍在运行，并访问 `http://localhost:5188/health`。前端代理固定使用 `127.0.0.1:5188`，避免 Windows 将 `localhost` 解析到未监听的 IPv6 地址。

## 许可证

尚未确定。确定开源策略前，保留全部权利；请勿将仓库内容视为已获开放源代码许可。
