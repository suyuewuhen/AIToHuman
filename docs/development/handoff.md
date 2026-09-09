# AIToHuman 项目交接文档

最后更新：2026-09-09  
仓库：[suyuewuhen/AIToHuman](https://github.com/suyuewuhen/AIToHuman)  
当前分支：`main`  
当前基线：`cd733e5 feat: add SignalR order notifications`

本文面向接手项目的开发者、评审者和本地联调人员。内容以当前代码为准；产品文档中标记为“规划中”的模块不代表已经实现。

## 1. 项目定位与当前闭环

AIToHuman 是“AI 任务管家 + 真人服务任务大厅”。用户先用自然语言描述需求，AI 帮助澄清、规划并建议悬赏；需要现实世界执行的部分，经用户确认后发布到任务大厅，由其他用户按固定悬赏报名，需求方选择服务者并形成订单。

当前已经打通的 MVP 闭环：

```text
AI 需求输入 → 规则/AI 建议悬赏 → 创建任务草稿 → 用户确认并预览
→ 发布到任务大厅 → 服务者按固定悬赏报名 → 需求方查看报名并选择服务者
→ 自动创建 Accepted 订单 → SignalR 主动通知被选中的服务者
```

尚未实现：真实支付和托管、实名认证、文件凭证、订单履约状态流转、双向评价盲期、争议处理、运营后台和正式消息中心。

## 2. 当前已实现功能

### 账户与身份

- 邮箱、密码、显示名称注册；登录后返回 JWT Access Token。
- `GET /api/v1/auth/me` 查询当前用户。
- 一个账号同时支持需求方（`owner`）和服务者（`worker`），可在右上角身份菜单切换；用户 ID、历史任务和订单归属不变。
- 密码使用 PBKDF2 加盐哈希保存，不保存明文密码。
- Development 环境提供固定合成联调会话；生产环境不可用。

### 任务、悬赏与报名

- 创建任务初始状态为 `ReadyToPublish`。
- 悬赏建议只返回建议金额、区间、因素和数据充分度，不绕过用户确认。
- 确认草稿后先创建任务并打开预览，再由用户点击最终确认发布。
- 发布后、分配服务者前可以提高悬赏，不支持降价。
- 服务者只能按固定悬赏报名，不支持竞价、互相报价或私下加价。
- 公开任务列表只返回区域级信息、悬赏、验收标准摘要和报名人数。

### 订单与主动通知

- 需求方选择服务者后自动创建 `Accepted` 订单，并保存任务标题和悬赏金额快照。
- “我的订单”分为“我发布的订单”和“我接取的任务”两个页签，按 `ownerId` / `workerId` 分类。
- 前端通过 SignalR 连接 `/hubs/notifications`。
- 选择服务者后，后端向被选服务者用户组发送 `OrderCreated`；服务者页面收到后立即显示通知并更新接取任务。
- 当前用户组和通知发布器为单实例内存实现；多实例部署需要 Redis backplane 或共享消息总线。

## 3. 已确定的产品规则

1. 一个账号对应一个用户 ID，可在需求方和服务者身份之间切换。
2. 当前角色只决定本次操作权限，不代表两个独立账户。
3. AI 只能建议和规划，不能绕过用户确认直接发布任务。
4. 悬赏由用户结合 AI 建议确认；服务者不能互相竞价，用户可以在分配前主动加价。
5. 需求方先看服务者评价再选择；服务者先看需求方评价再报名。双方完成订单后互评，计划采用双方提交或 7 天后公开的盲评规则，当前尚未实现。
6. 任务分配前只展示区域级位置，避免公开精确地址。
7. 订单创建后向被选服务者主动推送；聊天、未读数和系统通知应复用 SignalR 通道扩展。
8. 当前不接入真实支付，也不使用模拟支付冒充合规支付方案。

## 4. 技术架构与目录

```text
Vue 3 + TypeScript + Vite
        │ REST/JSON + SignalR
        ▼
ASP.NET Core 10 Minimal API / C#
        ├─ Domain：实体、值对象、状态和规则
        ├─ Application：任务、订单、通知抽象
        ├─ Infrastructure：EF Core、PostgreSQL、内存仓储
        └─ Contracts：请求/响应 DTO
        ▼
PostgreSQL（当前开发事实来源）
```

```text
backend/AIToHuman.Api/                  # 路由、JWT、SignalR Hub、异常处理
backend/AIToHuman.Application/         # TaskService、仓储/通知接口
backend/AIToHuman.Contracts/           # 请求/响应 record
backend/AIToHuman.Domain/              # TaskItem、TaskApplication、Order、Money
backend/AIToHuman.Infrastructure/      # EF Core/PostgreSQL 与内存仓储
backend/tests/AIToHuman.Domain.Tests/  # 领域单元测试
frontend/src/App.vue                   # 工作台、任务大厅、订单页签
frontend/src/api/                      # auth、tasks、rewards、notifications
frontend/src/styles.css                # 全局样式
```

依赖方向保持为：`Api → Application → Domain`，`Infrastructure → Application + Domain`。领域层不得引用 EF Core、HTTP、SignalR 或 AI SDK；SignalR 具体实现放在 Api 层，通过 Application 的通知接口调用。

## 5. 本地开发

### 前置条件

- .NET SDK 10（由根目录 `global.json` 锁定）。
- Node.js 24、npm 11。
- 本机 PostgreSQL，或 Docker Compose PostgreSQL。
- PowerShell。

### PostgreSQL 连接

```text
Host=localhost
Port=5432
Database=postgres
Username=postgres
Password=123456
```

连接串位于被 Git 忽略的 `backend/AIToHuman.Api/appsettings.Development.json`，严禁提交。其他机器请通过 `ConnectionStrings__Postgres` 环境变量覆盖。

### 启动后端

```powershell
dotnet restore AIToHuman.sln --configfile NuGet.Config
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project backend/AIToHuman.Api --urls http://127.0.0.1:5188
```

检查：

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

访问 <http://localhost:5173>。Vite 将 `/api`、`/health` 和支持 WebSocket 的 `/hubs` 代理到 `http://127.0.0.1:5188`。必须先启动后端，否则会看到 `ECONNREFUSED 127.0.0.1:5188`。

Docker 不是必需的；当前 MVP 直接使用本机 PostgreSQL。没有数据库时可执行：

```powershell
docker compose up -d postgres
$env:ConnectionStrings__Postgres = "Host=localhost;Port=5432;Database=aitohuman;Username=aitohuman;Password=local-development-only"
```

Compose 创建的 `aitohuman` 用户/数据库与本机默认的 `postgres` 用户/数据库不同，必须设置上面的连接串。未配置 PostgreSQL 时 API 可回退到内存任务/订单仓储，但注册、登录和角色切换不可用，只能使用 Development 合成会话。

## 6. SignalR 联调说明

- Hub 地址：`/hubs/notifications`；前端封装在 `frontend/src/api/notifications.ts`。
- 登录用户通过 JWT `NameIdentifier` 加入 `user:{userId:N}` 用户组。
- Development 环境允许通过 `?userId=...` 连接合成会话；生产环境拒绝该回退身份。
- JWT 通过 SignalR 的 `access_token` 查询参数传给 Hub，后端只在 `/hubs` 路径读取。

订单通知流程：

```text
owner 选择报名者 → TaskService 创建 Order → 发布 OrderCreated
→ 找到 worker 用户组 → worker 浏览器收到事件 → 更新“我接取的任务”
```

两浏览器验证：浏览器 A 用 owner 发布任务；浏览器 B 用 worker 报名；A 查看并选择 B；B 无需刷新应立即看到通知和订单。Network 面板应看到 `/hubs/notifications` 的 WebSocket 或长轮询连接。

## 7. API 与 Hub 清单

所有业务 API 前缀为 `/api/v1`，JSON 使用 camelCase。

| 方法 | 路径 | 认证/说明 |
| --- | --- | --- |
| GET | `/health` | 健康检查 |
| GET | `/api/v1/session/dev` | 仅 Development 合成会话 |
| POST | `/api/v1/auth/register` | 匿名注册 |
| POST | `/api/v1/auth/login` | 匿名登录 |
| POST | `/api/v1/auth/switch-role` | JWT，切换 owner/worker |
| GET | `/api/v1/auth/me` | JWT，当前用户 |
| POST | `/api/v1/reward-suggestions` | 规则/AI 悬赏建议 |
| GET | `/api/v1/tasks` | 已发布任务列表 |
| GET | `/api/v1/tasks/{id}` | 公开详情 |
| POST | `/api/v1/tasks` | owner 创建任务 |
| POST | `/api/v1/tasks/{id}/publish` | 所有者发布 |
| POST | `/api/v1/tasks/{id}/increase-reward` | 分配前加价 |
| POST | `/api/v1/tasks/{id}/applications` | worker 报名 |
| GET | `/api/v1/tasks/{id}/applications?ownerId=...` | 所有者查看报名 |
| POST | `/api/v1/tasks/{id}/applications/{applicationId}/select` | 选择服务者并创建订单 |
| GET | `/api/v1/tasks/{id}/order` | 订单参与者查询 |
| GET | `/api/v1/orders?userId=...` | 当前用户订单列表 |
| GET/WS | `/hubs/notifications` | SignalR 主动通知 |

后端错误使用 Problem Details 风格，通常映射为 `401`、`403`、`404`、`409`、`422`。Development 仍保留部分 `ownerId` / `workerId` 显式 ID 回退以兼容演示，生产环境应移除。

## 8. 数据库、权限与状态

Development + PostgreSQL 启动时会调用 `EnsureCreated()` 并用幂等 SQL 补齐 `users`、`orders` 表；这是本地过渡逻辑，不应复制到生产。当前 Migration 为 `AddUsers`、`AddOrders`，后续应使用独立迁移作业和备份/恢复流程。

权限原则：`owner` 才能创建、发布、加价、查看报名和选人；`worker` 才能报名；订单仅允许 owner 或被选 worker 查看。前端隐藏按钮不能替代后端授权。

```text
Task:  ReadyToPublish → Published → Assigned → Closed
                         └→ Expired / Cancelled
Order: Accepted → InProgress → Submitted → Approved
                                  └→ Rejected
                         └→ Disputed / Cancelled
```

不要提交数据库密码、JWT 签名密钥或真实用户数据。生产环境必须配置强随机 `Authentication__SigningKey`；公开任务不得返回精确地址、联系方式或报名备注。

## 9. 验证、已知问题与后续顺序

验证命令：

```powershell
dotnet build AIToHuman.sln --no-restore
dotnet test AIToHuman.sln --no-build
Set-Location frontend
npm run typecheck
npm run build
```

当前领域测试共 4 个，核心流程已验证：注册 owner/worker → 创建发布 → 报名 → 选人 → 创建订单 → 双方查询；SignalR 还需人工验证双浏览器即时通知、断线重连和重复事件。

主要技术债：订单履约状态流转、双向评价和盲期、取消/超时/争议、分页筛选、开发 ID 回退、标准 Migration、SignalR Redis backplane、Outbox 可靠投递、消息历史/未读数、文件安全、真实支付和正式 AI 适配层。

推荐顺序：

1. P0：订单状态机、开始履约、提交凭证、验收/拒绝、审计和幂等。
2. P1：SignalR 事件信封与持久化、Redis backplane、聊天/未读数、双向评价、集成测试。
3. P2：清理开发回退、标准 Migration、对象存储与扫描、AI 供应商适配、支付与合规评审。

## 10. 交接检查清单

- [ ] PostgreSQL 已启动，`/health` 返回 `healthy`。
- [ ] 后端和前端均能启动并打开 `http://localhost:5173`。
- [ ] 账号可在 owner / worker 间切换且用户 ID 不变。
- [ ] 能完成创建、发布、报名、查看报名、选人和订单查询。
- [ ] 两浏览器验证 SignalR：选人后服务者收到主动通知。
- [ ] “我的订单”两个页签正确显示发布订单和接取任务。
- [ ] 已阅读固定悬赏、双向选择和安全文档。
- [ ] 新功能先补用户故事、API Contract、领域规则和测试。

## 11. 相关文档

- [项目 README](../../README.md)
- [开发指南](./development-guide.md)
- [路线图](./roadmap.md)
- [系统架构](../architecture/system-architecture.md)
- [领域模型与状态机](../architecture/domain-model.md)
- [API 设计约定](../api/api-guidelines.md)
- [安全、隐私与风控](../security/security-and-risk.md)
- [固定悬赏与双向选择决策](../architecture/decisions/0002-fixed-reward-and-mutual-selection.md)
