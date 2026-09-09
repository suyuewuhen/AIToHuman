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
- Tasks：草稿、任务、步骤、位置摘要、发布和取消。
- Risk：规则检查、AI 辅助分类、审核队列和决策记录。
- Applications：按固定悬赏报名、撤回、选择和并发控制，不承载服务者价格。
- Orders：交易快照、状态机、执行事件和验收。
- Messaging：订单会话、消息和实时推送。
- Evidence：上传授权、元数据、病毒扫描状态和访问控制。
- Reviews：用户评价服务者、服务者评价用户、评价盲期、公开资料和基础信用指标。
- Disputes：申诉、证据包和运营处理。
- Notifications：站内通知及后续外部渠道。
- Administration：审核、配置、下架、冻结和审计查询。

模块间通过应用层接口和领域事件协作，不直接跨模块修改数据库实体。

## 6. 数据与基础设施

### PostgreSQL

作为事实来源，保存业务状态、事件、审计、对话元数据和文件元数据。敏感字段按等级加密或令牌化。

### Redis

用于短期缓存、分布式锁、频率限制和 SignalR 扩展。Redis 不能作为订单状态的唯一来源。

### Hangfire

用于到期提醒、任务过期、通知重试、文件异步检查等。作业必须具备幂等性，执行失败可观测。

### 对象存储

使用私有 Bucket。上传和下载采用短时效签名 URL，服务端核验订单权限后签发。禁止把永久公开 URL 保存为业务凭证。

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

## 9. 可观测性

- 结构化日志：请求 ID、用户匿名标识、模块、结果码和耗时。
- 指标：API 延迟、错误率、AI 延迟与成本、队列积压、发布/报名/完成漏斗。
- 链路：使用 OpenTelemetry，外部调用传播关联标识。
- 告警：认证异常、失败作业、对象扫描失败、风险规则异常和状态机冲突。

## 10. 演进路径

只有当容量或团队边界有证据支持时再拆服务。较可能优先独立的部分是实时消息、文件处理、通知和 AI 编排；任务、报名、订单和支付应尽量保持强一致边界。
