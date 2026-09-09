# API 设计约定

## 1. 基础约定

- 协议：HTTPS
- 风格：REST/JSON；实时事件使用 SignalR
- 前缀：`/api/v1`
- JSON 字段：`camelCase`
- 时间：ISO 8601，包含时区；后端保存 UTC
- 标识符：推荐 UUID/ULID，作为字符串传输
- 金额：JSON 数字加币种字段，不使用浮点运算

API 契约通过 ASP.NET Core OpenAPI 输出，并生成 TypeScript 客户端。破坏性变更通过新版本或兼容迁移发布。

## 2. 资源示例

```text
POST   /api/v1/auth/register
POST   /api/v1/auth/login
POST   /api/v1/auth/refresh

POST   /api/v1/conversations
POST   /api/v1/conversations/{conversationId}/messages
POST   /api/v1/conversations/{conversationId}/task-drafts
PUT    /api/v1/task-drafts/{draftId}
POST   /api/v1/task-drafts/{draftId}/confirm

GET    /api/v1/tasks
GET    /api/v1/tasks/{taskId}
POST   /api/v1/tasks/{taskId}/publish
POST   /api/v1/tasks/{taskId}/cancel

POST   /api/v1/tasks/{taskId}/offers
GET    /api/v1/tasks/{taskId}/offers
POST   /api/v1/offers/{offerId}/accept
POST   /api/v1/offers/{offerId}/withdraw

GET    /api/v1/orders/{orderId}
POST   /api/v1/orders/{orderId}/accept
POST   /api/v1/orders/{orderId}/start-travel
POST   /api/v1/orders/{orderId}/start-work
POST   /api/v1/orders/{orderId}/submit
POST   /api/v1/orders/{orderId}/approve
POST   /api/v1/orders/{orderId}/reject
POST   /api/v1/orders/{orderId}/disputes
```

命令型子资源表达业务动作，避免允许客户端通过通用 PATCH 任意设置状态。

## 3. 成功响应

单资源直接返回资源或命令结果，HTTP 状态码表达结果，不额外固定包装一层 `code = 0`：

```json
{
  "id": "01J...",
  "status": "published",
  "createdAt": "2026-09-09T08:00:00Z"
}
```

创建返回 `201 Created` 和 `Location`；无响应体的成功操作返回 `204 No Content`。

## 4. 错误响应

采用 RFC 9457 Problem Details：

```json
{
  "type": "https://docs.example.com/problems/invalid-order-transition",
  "title": "Order state transition is not allowed",
  "status": 409,
  "detail": "An awaiting-review order cannot start work.",
  "instance": "/api/v1/orders/01J.../start-work",
  "traceId": "00-abcd...",
  "errors": {
    "status": ["Expected accepted or enRoute."]
  }
}
```

建议状态码：

- `400` 请求格式或字段错误。
- `401` 未认证。
- `403` 已认证但无权限。
- `404` 资源不存在，必要时也用于避免泄露资源存在性。
- `409` 状态冲突、并发冲突或幂等键冲突。
- `422` 请求语法正确但不符合业务规则。
- `429` 超过速率限制。

## 5. 分页和过滤

列表优先使用游标分页：

```text
GET /api/v1/tasks?category=pickup&district=chaoyang&limit=20&cursor=...
```

```json
{
  "items": [],
  "nextCursor": "...",
  "hasMore": false
}
```

排序字段采用白名单，不能将客户端字段直接拼接为 SQL。

## 6. 认证与授权

- Access Token 短时有效，Refresh Token 轮换并可撤销。
- 每个端点同时检查角色和资源关系，如任务所有者、订单参与者。
- SignalR 连接使用相同认证体系，并在每个 Hub 方法中授权。
- 管理接口使用独立策略和更严格审计。

具体 Token 存储方式在身份方案实现前通过安全评审确定。

## 7. 幂等与并发

- 创建任务、提交报价、选择报价、状态转换和未来支付接口支持 `Idempotency-Key`。
- 更新资源返回 ETag 或版本字段；冲突返回 `409`。
- 客户端超时后可以使用相同幂等键安全重试。

## 8. 文件上传

推荐三步流程：

1. 客户端请求上传授权并声明文件类型、大小和用途。
2. 客户端使用短时效签名 URL 上传至私有对象存储。
3. 客户端提交文件 ID，服务端确认存在且安全扫描通过后关联订单。

服务端不信任扩展名，应检查 MIME、文件签名、大小和图片元数据；必要时重新编码图片并移除 EXIF。

## 9. SignalR 事件

事件名称使用过去式和版本化载荷，例如：

- `task.offerSubmitted`
- `order.statusChanged`
- `order.messageCreated`
- `notification.created`

实时事件只是刷新提示，客户端断线重连后必须通过 REST 获取事实状态，不能只依赖事件恢复数据。
