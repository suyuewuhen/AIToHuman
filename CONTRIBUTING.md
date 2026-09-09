# 贡献指南

感谢参与 AIToHuman。当前阶段以快速验证 MVP、保持领域规则清晰和控制现实世界任务风险为优先目标。

## 开始之前

1. 阅读 `README.md`、MVP PRD 和系统架构文档。
2. 对需求边界不明确的改动先创建 Issue 讨论。
3. 安全、支付、身份、隐私、任务状态和 AI 自动执行相关改动必须说明风险。

## 分支与提交

- 功能分支：`feat/<short-name>`
- 修复分支：`fix/<short-name>`
- 文档分支：`docs/<short-name>`
- 重构分支：`refactor/<short-name>`

提交信息建议遵循 Conventional Commits：

```text
feat(tasks): add task draft endpoint
fix(orders): reject invalid state transition
docs(product): clarify MVP exclusions
```

## Pull Request 要求

- 描述解决的问题、实现范围和明确未处理的内容。
- 关联对应 Issue。
- 提供必要测试；界面改动附截图或录屏。
- 数据库变更包含迁移说明和回滚考虑。
- API 变更同步更新 OpenAPI 和相关文档。
- 不提交密钥、令牌、个人身份信息或真实任务凭证。

## 完成定义

- 功能满足验收标准。
- 自动化测试通过。
- 日志不包含敏感数据。
- 权限、并发与非法状态转换经过验证。
- 用户可见文案和错误信息可理解。
- 相关文档已更新。

## 安全问题

不要在公开 Issue 中披露可被利用的漏洞或真实个人信息。仓库建立安全联系渠道后，应通过私密渠道报告；在此之前请直接联系仓库所有者。
