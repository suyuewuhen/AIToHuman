# AIToHuman

AIToHuman 是一个“AI 任务管家 + 真人服务任务大厅”平台。用户只需描述想完成的事情，AI 负责澄清需求、规划步骤并完成可自动化的部分；需要现实世界执行的部分，经用户确认后发布到任务大厅，由合适的服务者接单、执行并提交凭证。

> 产品主张：你只需要说清楚想完成什么，AI 帮你想明白，真人帮你做到。

## 当前状态

项目处于产品设计和 MVP 准备阶段。首个版本聚焦同城低风险任务，优先打通以下闭环：

```text
需求对话 → AI 澄清与规划 → 任务草稿 → 用户确认发布
→ 服务者报价/接单 → 执行并提交凭证 → 用户验收 → 评价
```

支付、实名认证、复杂风控和自动结算将在核心业务闭环稳定后逐步接入；在正式交易能力上线前，不以模拟支付替代真实合规方案。

## 技术方向

- 前端：Vue 3、TypeScript、Vite、Pinia、Vue Router、Element Plus
- 后端：ASP.NET Core Web API、C#、Entity Framework Core
- 数据：PostgreSQL、Redis
- 实时通信：SignalR
- 后台任务：Hangfire
- 文件存储：S3 兼容对象存储（开发环境可用 MinIO）
- AI：通过可替换的模型适配层接入支持结构化输出的模型服务
- 部署：Docker Compose、Nginx
- 架构：前后端分离的模块化单体

## 仓库规划

```text
AIToHuman/
├── frontend/                  # Vue 用户端与服务者端（后续创建）
├── backend/                   # .NET 后端（后续创建）
├── docs/
│   ├── product/              # 产品定位、MVP 与用户故事
│   ├── architecture/         # 系统设计、领域模型与 ADR
│   ├── api/                  # API 约定
│   ├── security/             # 安全、隐私、风控与合规边界
│   └── development/          # 开发规范与路线图
├── .editorconfig
├── .gitignore
└── README.md
```

## 文档索引

- [产品愿景与范围](docs/product/product-vision.md)
- [MVP 产品需求文档](docs/product/mvp-prd.md)
- [用户故事与验收标准](docs/product/user-stories.md)
- [系统架构](docs/architecture/system-architecture.md)
- [领域模型与状态机](docs/architecture/domain-model.md)
- [架构决策记录](docs/architecture/decisions/0001-modular-monolith.md)
- [API 设计约定](docs/api/api-guidelines.md)
- [安全、隐私与风控](docs/security/security-and-risk.md)
- [开发指南](docs/development/development-guide.md)
- [路线图](docs/development/roadmap.md)
- [贡献指南](CONTRIBUTING.md)

## MVP 成功标准

- 用户能在 AI 引导下生成一份字段完整、可编辑的任务草稿。
- 用户能明确确认后发布任务，AI 不能绕过确认直接发布。
- 服务者能浏览适配任务并提交报价或接单申请。
- 双方能围绕订单沟通、更新进度并上传执行凭证。
- 用户能验收、拒绝并说明原因，系统完整保留审计记录。
- 禁止任务会被拦截，高风险任务不会自动进入大厅。

## 参与开发

项目当前优先完善产品边界和技术基线。开始编码前请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [开发指南](docs/development/development-guide.md)。

## 许可证

尚未确定。确定开源策略前，保留全部权利；请勿将仓库内容视为已获开放源代码许可。
