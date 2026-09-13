# 开发指南

## 1. 前提条件

当前使用：

- .NET SDK 10（由 `global.json` 锁定）
- Node.js 24
- npm 11（由 `package.json` 声明）
- Docker Desktop / Docker Engine + Compose
- PostgreSQL、Redis 和 MinIO（推荐通过 Compose 启动）

仓库已经生成 Vue 与 ASP.NET Core 骨架。任务仓储支持 EF Core/PostgreSQL；未配置 `ConnectionStrings__Postgres` 时开发环境使用内存实现，方便在没有数据库的机器上验证领域规则与 API。配置了连接串但数据库暂时不可达时，API 会记录清晰错误并启动，后续数据库接口请求会失败，避免静默丢数据。版本通过 `global.json`、`packageManager` 和容器镜像固定。

## 2. 本地开发原则

- 所有可共享配置提供 `.env.example` 或 `appsettings` 示例，不包含密钥。
- 配置分两层：**部署级**（连接串、日志、`DataProtection__KeysPath`）只走环境变量或 User Secrets，永远不进配置表（Redis 连接串 `ConnectionStrings__Redis` 属于这一层，而“是否启用扇出”是运营级开关 `notifications.fanout.enabled`，可以在后台改）；**运营级**（三方集成参数与业务旋钮）优先从 `system_settings` 读取，环境变量与配置文件只是兜底，解析顺序固定为「数据库覆盖 → 环境变量/配置文件 → 代码默认值」。
- 运营级配置必须登记在 `SettingCatalog` 才能被后台读写：新增一个可配置项 = 在目录里加一行（类型、是否机密、默认值、兼容的配置键），再让消费方通过 `ISettingsProvider` 读取；不要直接读 `IConfiguration`。
- 机密（API Key、访问密钥）在配置表里是 Data Protection 密文，接口与审计只出现掩码与指纹。密钥环默认落在运行账户的配置目录里，容器或分布式部署请用 `DataProtection__KeysPath` 指到持久卷并让所有实例共享，否则重启或换实例后解不开已保存的密钥。
- 本地秘密使用 .NET User Secrets 或环境变量。
- 本地联调可使用 `GET /api/v1/session/dev` 获取合成开发会话；该接口仅在 Development 环境开放，不能替代正式登录或授权。
- **Windows PowerShell 5.1 联调要额外注意正文编码**：`Invoke-RestMethod -Body $json` 传**字符串**时按非 UTF-8 发送，正文里的中文会变成 `?`（本轮真机联调就是这样把一版风险规则目录写成了乱码，也顺带证明了“恢复内置目录”这条退路的必要）。正确做法是把 JSON 编成 UTF-8 **字节**再作为 `-Body` 传入，并显式带上 `charset=utf-8`：

  ```powershell
  $json = @{ reason = '试点期收紧词表'; highRewardThreshold = 8000; nightWindowStart = '00:00'; nightWindowEnd = '06:00'; rules = @(...) } | ConvertTo-Json -Depth 6
  Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:5188/api/v1/admin/risk/rules' `
    -Headers @{ Authorization = "Bearer $token" } `
    -ContentType 'application/json; charset=utf-8' `
    -Body ([Text.Encoding]::UTF8.GetBytes($json))
  ```

  提交后回读一次（例如 `GET /api/v1/admin/risk/rules/detail`）核对中文没有变成 `?`，再用 `curl.exe` 或 Postman 交叉验证一次；PowerShell 7 也建议显式写 `charset=utf-8`，不要依赖默认编码。
- **风险相关的真机联调有两个坑**（都是这一轮踩过的）：
  - ① **改完规则要跑一轮复检看在线任务**：改过风险规则目录（或手动收紧词表/阈值）之后，用管理员令牌调 `POST /api/v1/admin/risk/recheck`（可带 `?limit=`），看仍处于 `Published`/`Assigned` 的任务会不会被处置——响应里的 `scanned` / `refreshed` / `flagged` / `unpublished` / `frozen` / `skipped` 就是这一轮的条数：命中禁止类别且没有订单的被自动下架（`unpublished`）、已经有订单的改为冻结订单并进争议队列（`frozen`）、只命中“需人工复核”的保持在线并要求复检（`flagged`）。后台 `RiskRecheckService` 每 5 分钟也会自己跑一轮，所以“页面没动静”不等于“什么都没发生”。
  - ② **申诉节流是按人按天算的**：同一条任务累计最多申诉 3 次、同一个人 24 小时内最多 5 次，反复联调同一个账号很容易撞上限（`422` 文案分别是「这条任务累计申诉已达上限（3 次）：请先修改文案，规则会重新判定。」与「近 24 小时提交的申诉已达上限（5 次）：请明天再试，或先修改文案。」）。换一个账号，或清掉 `task_risk_appeals`（同一条任务的留档行）再试。
- 数据库结构只通过 EF Core Migration 演进。
- **前端不要自己拼 API 地址**：所有请求走 `frontend/src/api/base.ts` 的 `apiFetch()`（SignalR 用 `hubUrl()`），两个入口之间的跳转用 `APP_HOME_URL` / `OPS_HOME_URL`。默认是相对路径（同源 + 反向代理），跨域部署时才由构建期变量给出绝对地址：`VITE_API_BASE_URL`、`VITE_HUB_BASE_URL`、`VITE_APP_HOME_URL`、`VITE_OPS_HOME_URL`（声明在 `frontend/env.d.ts`）。
- 跨源部署必须让后端放行来源：`Cors__AllowedOrigins=https://ops.example.com,https://app.example.com`（也接受数组写法）。没配置时的兜底按环境区分：开发环境放行本机 5173 / 4173，其它环境一个都不放行（同源部署本来不需要 CORS）；生效清单每次启动都打进日志。策略里**必须保留 `AllowCredentials()`**：SignalR 协商默认带 credentials，漏掉它会出现“接口都正常、只有通知订阅报 CORS 错误”。想在没有域名的前提下复现跨源，用两个本机端口就够：`npm run build` 后用 `npm run preview`（4173）或任意静态服务器当第二个源、API 仍在本机 5188，再把该来源填进 `Cors__AllowedOrigins`。
- 早期 `EnsureCreated()` 生成的本地开发库没有迁移历史；Development 启动时会检测并按情况处理：表与当前模型一致时把已有迁移整体标记为已应用（打警告日志），表不齐时直接报错并提示重建，避免在错误的 schema 上运行。
- 示例数据必须为合成数据。
- 一条命令应能启动依赖，一条命令应能执行全部必要检查；真实外部依赖（PostgreSQL、Redis、S3 兼容存储）的回归用例在对应依赖不可用时都会自动跳过，不会挡住本地或 CI 的其余检查。

## 3. 代码约定

### C#

- 启用 nullable reference types 和隐式 using。
- 公开 API 使用明确请求/响应类型，不直接暴露 EF 实体。
- 异步 I/O 接收并传递 `CancellationToken`。
- 领域规则由实体、值对象或领域服务承载，不放在 Controller。
- 使用 UTC/`DateTimeOffset`，通过可注入时钟获取当前时间。
- 金额使用 `decimal` 和币种值对象。

### Vue / TypeScript

- Vue Composition API 与 `<script setup lang="ts">`。
- TypeScript 严格模式，禁止无说明的 `any`。
- 业务功能按 feature 组织，通用组件不依赖具体页面。
- 远程数据状态与纯界面状态分开处理。
- 所有用户输入在后端再次校验。

## 4. 测试策略

- 单元测试：领域状态机、风险规则、金额和权限决策（`backend/tests/AIToHuman.Domain.Tests`）。
- 集成测试：应用用例、事务边界与 AI 协议（`backend/tests/AIToHuman.IntegrationTests`）。大多数用例走上游替身与内存仓储，不需要数据库；**真实 PostgreSQL 的回归测试已经建立**，位于 `backend/tests/AIToHuman.IntegrationTests/Postgres/`：
  - `PostgresTestEnvironment` 负责解析测试库连接串，顺序是环境变量 `AITOHUMAN_TEST_POSTGRES` → 本机 `backend/AIToHuman.Api/appsettings.Development.json` 的 `ConnectionStrings:Postgres`（该文件被 Git 忽略，凭据不进仓库），启动时用 3 秒超时探测一次连通性；连不上或没配置就判定为不可用。
  - 用例用 `[PostgresFact]` 标注：数据库不可用时**标记为跳过而不是失败**，所以没装数据库的机器和默认 CI 仍能跑完其余测试。
  - `PostgresRegressionFixture` 在一次测试运行里共用一个随机命名的空库（`aitohuman_regression_<随机>`），用 `Database.Migrate()` 从零建库并应用全部迁移（顺带验证“空库能不能起来”），每个用例前 `TRUNCATE` 所有表，运行结束尽力删库；`PostgresWorld` 按 API 的注册方式搭一套 EF 仓储 + 应用服务，代表“一次请求作用域”，`NewScope()` 用来模拟并发请求（共用一个 `DbContext` 是测不出并发问题的）。
  - 这组用例专门覆盖**内存替身测不出来**的缺陷类型：空库迁移、草稿编辑后每一列真的落库、乐观并发令牌真的生效（后写入者拿到 `DbUpdateConcurrencyException`）、12 路并发选人只产生一个订单、取消订单在 `orders`/`tasks`/`task_applications` 三张表上的连带效果、争议冻结落库、风险复核队列与“放行→编辑→重新排队”的防绕过链路、规则目录版本快照在真库上的写入与还原（`risk_rule_catalog_revisions` 往返 + 版本号唯一索引）、发布后复检的处置落库（自动下架与订单冻结、以系统身份写的运营审计）与申诉留档（`task_risk_appeals` 往返 + 按人/按任务的计数口径）、带 `+08:00` 偏移的截止时间归一化为 UTC。
  - 真实 **Redis**（通知扇出）与 **S3 兼容对象存储**（MinIO / OSS / S3）的回归测试也已建立，位于 `backend/tests/AIToHuman.IntegrationTests/External/`：
    - `ExternalTestEnvironment` 解析并探测两类依赖。Redis 用环境变量 `AITOHUMAN_TEST_REDIS`（默认 `127.0.0.1:6379`）；对象存储用 `AITOHUMAN_TEST_S3_ENDPOINT` / `_REGION` / `_BUCKET` / `_ACCESS_KEY` / `_SECRET_KEY`（默认 `http://127.0.0.1:9000`、`us-east-1`、桶 `aitohuman-evidence`、`minioadmin/minioadmin`——默认凭据只是**本机开发**兜底，其他环境请用环境变量注入）。对象存储的探测方式是**真的写一条探针对象再删掉**：桶不存在、密钥不对、服务没起来都会在这里就暴露出来。
    - 用例用 `[RedisFact]` / `[MinioFact]` 标注，依赖不可用时**标记为跳过而不是失败**（与 `[PostgresFact]` 同一套取舍）。
    - Redis 侧覆盖：广播的消息按字段原样到达订阅方（含 `PayloadJson` 原样跨进程传递）；处理函数抛异常不会中断订阅（第二条消息仍能收到）；开关关闭时 `Enabled=false` 且 `PublishAsync` 抛 `InvalidOperationException`（派发方据此退回单实例推送）、`SubscribeAsync` 立即返回；**配了一个连不上的 Redis 时 `PublishAsync` 会失败并返回**，断言的是"不会把派发周期无限卡住"（上限 30 秒）。
    - 对象存储侧覆盖：对象往返读写与存在性判断、删除后不存在（读不存在的对象返回 `null` 而不抛异常）；预签名地址**不带任何鉴权头**就能取回字节，且 `Content-Disposition` 的附件名生效；**篡改签名末位 → 403**；**拿 A 的签名去取 B 的路径 → 403**（路径参与签名）；**过期之后取 → 403**。过期这条只能真实等待：有效期由对象存储按 `X-Amz-Date + X-Amz-Expires` 与它自己的时钟判定，**改客户端时钟没用**，所以用的是"签 5 秒 → 立刻取应当 200 → 真等 7 秒 → 再取应当 403"，S3 那批约 21 秒。
  - 仍**未**自动化的是**主机级端到端**（`WebApplicationFactory`：本机离线还原不到 `Microsoft.AspNetCore.Mvc.Testing`）与**两个真实 API 实例之间的整链路投递**——本轮覆盖的是"广播能到达订阅方"这一层，"两个实例 + 两个客户端"的全链路仍靠手工端到端；其余关键路径仍靠 `handoff.md` 第 11 节列出的手工步骤。
- AI 协议集成测试：`backend/tests/AIToHuman.IntegrationTests` 用替身上游覆盖 SSE 分片、转义、缺少结束标记、超时与上游错误，不联网也不依赖数据库。
- 契约测试：OpenAPI、生成客户端和 Problem Details。
- 端到端测试：AI 草稿到任务完成的关键路径。
- 安全测试：跨用户访问、角色提升、文件 ID 枚举、状态绕过和速率限制。
- AI 评估：固定测试集验证必填字段、禁止类别、提示注入和结构化输出。

模型相关测试分为：不联网的适配器/模式测试，以及受控执行的真实供应商评估；普通 CI 不依赖随机模型响应。

## 5. 数据库迁移

- 每个结构变化附 EF Core Migration。
- Migration 名称描述业务变化。
- 生产环境由部署流程执行迁移，不由每个 API 实例启动时自动执行；Development 启动时自动应用迁移。
- 破坏性变化先扩展、迁移数据、再收缩，明确回滚方案。

## 6. API 开发流程

1. 定义用户故事和权限。
2. 定义 Contract 与 OpenAPI 示例。
3. 实现应用用例和领域规则。
4. 添加单元与集成测试。
5. 生成或更新前端客户端。
6. 验证错误处理、幂等、审计和日志脱敏。

## 7. 配置命名建议

```text
ConnectionStrings__Postgres
ConnectionStrings__Redis
ObjectStorage__LocalRoot
VolcengineAI__BaseUrl
VolcengineAI__ApiKey
VolcengineAI__Model
VolcengineAI__TimeoutSeconds
Settings__storage__provider
Settings__storage__s3__endpoint
Settings__storage__s3__region
Settings__storage__s3__bucket
Settings__storage__s3__accessKeyId
Settings__storage__s3__secretAccessKey
Settings__evidence__scanner__provider
Settings__evidence__scanner__endpoint
Settings__evidence__scanner__apiKey
Settings__evidence__scanner__failMode
Settings__evidence__scanner__clamavHost
Settings__evidence__scanner__clamavPort
Settings__evidence__maxSizeBytes
Settings__evidence__maxPerOrder
Settings__evidence__stripMetadata
Settings__evidence__uploadsPerUserPerHour
Settings__evidence__downloadUrlLifetimeSeconds
Admin__UserIds
Admin__Emails
DataProtection__KeysPath
```

命名规则：`Settings__<设置键里点号换成双下划线>` 与设置目录里的键一一对应（例如 `Settings__evidence__scanner__provider` ↔ `evidence.scanner.provider`），作用是兜底；日常调整在运营后台完成，改完立即生效。`ObjectStorage__LocalRoot` 只在 `storage.provider=local` 时生效（留空则用应用目录下的 `evidence`）。`Admin__UserIds` / `Admin__Emails` 决定谁能访问运营接口，留空等于关闭运营接口。`DataProtection__KeysPath` 指向密钥环目录，生产必须持久化并在实例间共享。

对象存储：`storage.provider=s3` 之前需要先在服务端建好 Bucket 并保持私有（用 MinIO 的话 `mc mb` + `mc anonymous set none` 即可），然后填 `storage.s3.endpoint/region/bucket/accessKeyId/secretAccessKey`；本机 MinIO 的 region 用 `us-east-1`，endpoint 形如 `http://127.0.0.1:9000`。密钥填错或 Bucket 不存在时接口会返回可读错误（`422`，带上 S3 的错误码），不会退回本机目录。`evidence.downloadUrlLifetimeSeconds` 控制直连下载地址的有效期（5 至 900 秒，默认 120）。

切换存储 provider **不会迁移已有文件**：从 s3 切回 local 之后，之前写进 Bucket 的凭证在本机目录里找不到，下载会 404。本地反复切换时记得把对象搬过去，或者干脆用不同的 Bucket / 目录区分环境。

生产密钥由部署平台注入。仓库只保留非敏感默认值和变量说明。

## 8. CI 基线

Pull Request 至少执行：

- Markdown 和格式检查。
- `dotnet restore/build/test`。
- 前端安装、类型检查、Lint、单元测试和构建。
- OpenAPI 兼容性检查。
- 依赖和密钥扫描。

现状（2026-09-13）：`.github/workflows/ci.yml` 的后端 job 已经同时起 `postgres:16`、`redis:7` 与 `minio/minio` 三个服务（都带 healthcheck），把连接串分别注入 `dotnet test`，并在测试前用 `minio/mc` 容器把测试用的桶（`aitohuman-evidence`）建出来——MinIO 起来时是空实例，不建桶对象存储用例会全部跳过。因此真实 PostgreSQL、Redis 与对象存储的回归用例在 CI 里会真正运行；同一批用例在本机缺少对应依赖时是跳过而不是失败。前端 job 已经执行 `npm ci`、`npm run typecheck` 与 `npm run build`。上面清单里仍缺的是 Markdown 与格式检查、Lint、OpenAPI 兼容性检查，以及依赖与密钥扫描。

## 9. Definition of Done

参见根目录 `CONTRIBUTING.md`。涉及核心状态、AI、身份、位置、文件或支付的改动还需安全审查记录。
