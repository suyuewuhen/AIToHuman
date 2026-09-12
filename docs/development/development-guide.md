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
- 本地秘密使用 .NET User Secrets 或环境变量。
- 本地联调可使用 `GET /api/v1/session/dev` 获取合成开发会话；该接口仅在 Development 环境开放，不能替代正式登录或授权。
- 数据库结构只通过 EF Core Migration 演进。
- 早期 `EnsureCreated()` 生成的本地开发库没有迁移历史；Development 启动时会检测并按情况处理：表与当前模型一致时把已有迁移整体标记为已应用（打警告日志），表不齐时直接报错并提示重建，避免在错误的 schema 上运行。
- 示例数据必须为合成数据。
- 一条命令应能启动依赖，一条命令应能执行全部必要检查。

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
- 集成测试：应用用例、事务边界与 AI 协议（`backend/tests/AIToHuman.IntegrationTests`）。当前这批用例走上游替身与内存仓储，不需要数据库；**真实 PostgreSQL、Redis 与对象存储兼容服务下的 API/持久化自动化测试尚未建立**，真实数据库目前依靠 `handoff.md` 第 11 节列出的手工端到端验证。
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
ObjectStorage__Endpoint
ObjectStorage__Bucket
ObjectStorage__AccessKey
ObjectStorage__SecretKey
ObjectStorage__LocalRoot
AI__Provider
AI__ApiKey
AI__Model
```

`ObjectStorage__LocalRoot` 只在 Development 有效（凭证默认写到应用目录下的 `evidence`）；接入真实 Bucket 前，`ObjectStorage__Endpoint/Bucket/AccessKey/SecretKey` 都不会被读取。

生产密钥由部署平台注入。仓库只保留非敏感默认值和变量说明。

## 8. CI 基线

Pull Request 至少执行：

- Markdown 和格式检查。
- `dotnet restore/build/test`。
- 前端安装、类型检查、Lint、单元测试和构建。
- OpenAPI 兼容性检查。
- 依赖和密钥扫描。

## 9. Definition of Done

参见根目录 `CONTRIBUTING.md`。涉及核心状态、AI、身份、位置、文件或支付的改动还需安全审查记录。
