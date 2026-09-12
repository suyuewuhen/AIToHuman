# AI 多轮需求澄清配置

AIToHuman 使用火山引擎 Ark 的 OpenAI 兼容接口，通过一句一问的多轮对话帮助用户明确需求。用户只看到自然语言回复；模型返回的结构化状态由后端解析，需求完整后才生成任务草稿。

## 本机配置

不要把 API Key 写入 Git。推荐通过环境变量配置：

```powershell
$env:VolcengineAI__ApiKey = "你的火山引擎 API Key"
dotnet run --project backend/AIToHuman.Api
```

也可以使用 .NET User Secrets：

```powershell
dotnet user-secrets --project backend/AIToHuman.Api init
dotnet user-secrets --project backend/AIToHuman.Api set "VolcengineAI:ApiKey" "你的火山引擎 API Key"
```

默认配置：

- BaseUrl：`https://ark.cn-beijing.volces.com/api/v3`
- Model：`glm-4-7-251222`
- TimeoutSeconds：`120`（允许范围 5 至 600 秒，按连续无数据时间计算）
- Endpoint：`POST /api/v1/ai/plan/stream`

以上四项都是**运营可配置**的：生效值按「运营后台里的 `ai.*` 覆盖 → 环境变量/User Secrets → 代码默认值」解析，页面入口是顶栏“运营配置”（需要管理员身份），改完下一轮对话立即生效，不需要重启服务。下面这些环境变量只是兜底写法，适合首次部署或没有后台入口时使用。

请求体包含按顺序排列的 `messages`，每项只允许 `user` 或 `assistant` 角色，最后一项必须来自用户。接口使用 Server-Sent Events（SSE）转发流式响应：`delta` 只包含可展示给用户的自然语言回复，内部 JSON 不会发送到页面；`complete` 返回本轮状态，并在需求完整时附带结构化草稿；`error` 返回流内错误。

AI 每轮只询问一个最关键问题，并结合完整对话判断目标、执行方式、时间、验收标准和重要限制是否明确。信息不完整时页面不会用演示数据创建任务；信息完整后用户仍需检查地点、时间、验收标准和固定悬赏，再主动确认发布。

## 输出校验与重试

模型返回的 JSON 会经过严格校验：`assistantMessage` 非空且不超过 4000 字；`readyToDraft=true` 时必须带 `plan`，且标题 1 至 80 字、描述与区域非空、验收标准至少 1 条（每条不超过 200 字）、建议悬赏大于 0 且不超过 100000。超出上限的验收标准与澄清只截断不报错；`readyToDraft=false` 时即便带了 `plan` 也会被丢弃，避免页面提前解锁草稿。

校验失败会自动重试一次（共 2 次尝试），并把失败原因作为修复指令追加到对话尾部，例如“上一次输出没有通过校验：AI 返回的草稿缺少任务标题，请重试。”。如果这一轮已经把文字发给页面，重试前会先发 `restart` 事件让页面清空半截回复；两次都失败才用流内 `error` 返回可直接展示的原因。超时与上游不可用不重试。

## 时间处理

模型不掌握当前时间，如果只靠对话内容推断“明天下午”这类相对时间，很容易输出已经过去的日期，导致创建任务时被领域规则拒绝（“截止时间必须晚于创建时间”）。为此后端做两层处理：

1. 后端在每次请求的 system prompt 中写入当前北京时间（UTC+8，含星期）和 UTC 时间，并要求模型：相对时间必须基于该时间换算；`deadline` 必须是晚于当前时间的未来时刻，按 ISO 8601 带时区偏移输出（如 `2026-09-12T15:00:00+08:00`）；不得使用训练数据中的年份。
2. `ParseDeadline` 会兜底纠正模型输出：解析失败、缺少时间或落在过去时，一律改为当前时间 + 1 天。模型未带时区偏移时按北京时间解释，避免结果随服务器时区变化。

兜底产生的截止时间会显示在发布预览中，用户确认才会发布。
