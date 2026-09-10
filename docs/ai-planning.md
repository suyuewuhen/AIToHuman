# AI 需求规划配置

AIToHuman 使用火山引擎 Ark 的 OpenAI 兼容接口，将自然语言需求整理为结构化任务草稿。

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
- TimeoutSeconds：`45`（允许范围 5 至 120 秒）
- Endpoint：`POST /api/v1/ai/plan`

超过配置时间时接口返回 `504 Gateway Timeout`，无法连接上游时返回 `502 Bad Gateway`。前端会自动回退到本地规则建议；如果模型经常超过 45 秒，应先检查网络、模型名称和火山引擎端点，再按需通过 `VolcengineAI__TimeoutSeconds` 调整上限。

接口只返回结构化草稿，不会自动发布任务。用户仍需确认地点、时间、验收标准和固定悬赏后再发布。
