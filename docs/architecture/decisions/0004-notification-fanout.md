# ADR-0004：多实例通知投递（原子认领 + Redis 发布/订阅扇出）

- 状态：已接受
- 日期：2026-09-12
- 相关：[系统架构](../system-architecture.md)、[领域模型](../domain-model.md)、[ADR-0003 运营可配置设置](0003-operator-configurable-settings.md)、[交接文档](../../development/handoff.md)

## 背景

通知此前是「单进程 Outbox 轮询」：`notifications` 表兼作 Outbox，`NotificationDispatcher` 每 2 秒扫描 `DispatchedAt IS NULL` 的记录，通过 SignalR 推给 `user:{userId:N}` 组，推送成功后写 `DispatchedAt`。单实例部署下这个闭环是完整的（失败重试、进程重启不丢、客户端重连靠 REST 补齐）。

一旦横向扩容就会出现两个问题：

1. **重复推送**：每个实例都会扫到同一批未派发记录，同一个用户如果只连在其中一个实例上，其它实例的推送是空推，但记录会被标记，语义混乱；
2. **推不到人**：通知由实例 A 产生，用户恰好连在实例 B 上，A 的本地分组里没有这个连接，实时推送直接丢失（只有等客户端重连拉 REST 才补上）。

SignalR 官方方案是 backplane 包 `Microsoft.AspNetCore.SignalR.StackExchangeRedis`，但本机 NuGet 离线环境取不到该包（`nuget.org` 不可达），无法依赖。

## 决策

1. **先认领，再推送**。`INotificationRepository.TryClaim(id, now)` 用一条条件更新完成认领：

   ```sql
   UPDATE notifications SET "DispatchedAt" = @p WHERE "Id" = @id AND "DispatchedAt" IS NULL
   ```

   只有影响 1 行的实例拿到推送权，多实例并发扫表不会重复推送。内存实现用同一把锁提供等价语义。原先「推送成功后再 `Save`」的写法已删除。

2. **失败必须回滚认领**。推送或广播失败时调用 `ReleaseDispatch(id)`（领域方法 `Notification.ReleaseDispatch()`）把记录放回 Outbox，下个周期重试。否则「认领成功但没发出去」的通知会被永久当成已派发，客户端永远等不到它。

3. **启用扇出时改为广播**。`INotificationFanout`（Application 层抽象）负责把认领到的通知广播出去，认领方不再直接推客户端：

   - `RedisNotificationFanout`（Infrastructure）用 StackExchange.Redis 3.1.3 的发布/订阅，频道固定为 `aitohuman:notifications:fanout`；
   - `NotificationFanoutSubscriber`（Api）在后台订阅该频道，收到消息后用**和本地推送完全相同**的信封转换逻辑推给自己身上的连接（`NotificationService.ToEnvelope` 的两个重载保证两条路径产出同一个信封）；
   - 发布者自己也在订阅者之列，因此「谁认领、谁推送」这件事对客户端不可见。

4. **频道名不做成配置项**。各实例一旦配成不同频道，通知会被认领后再也推不出去（认领已生效，不会重试）。需要隔离环境请用连接串里的库号或键前缀。

5. **连接串是部署级、开关是运营级**。`ConnectionStrings__Redis` 沿用 ADR-0003 的分层：部署基础设施不进配置表。运营只控制 `notifications.fanout.enabled`（分组「通知推送」，Bool，默认 `false`）。

6. **未启用或依赖不可用时退化为单实例**。没配连接串或开关为 false 时，派发方直接推本实例客户端，行为与没有扇出时一致。广播失败（Redis 抖动、还没起来）时先兜底推本实例在线客户端并打警告日志——本实例的用户不该因为内部基础设施抖动而收不到通知，其它实例的客户端仍能靠 REST 补齐；只有广播和本地推送都失败才回滚认领，2 秒后重试。

7. **订阅方持续等开关**。`NotificationFanoutSubscriber` 每 5 秒重新读一次开关：运营在后台打开扇出后自动接入，不需要重启进程；订阅意外断开也按同样间隔重连。

## 被拒绝的方案

- **引入官方 backplane 包**：离线环境取不到包，且它接管的是整个 SignalR 消息流；本仓库只需要「让通知跨实例可见」这一件事。
- **只加分布式锁、不做广播**：能解决重复推送，但解决不了「用户不在产生通知的实例上」，实时性依然丢。
- **把通知放到 Redis 队列/Stream 由各实例消费**：同一用户仍然只应收到一次推送，需要额外的路由信息（哪个实例持有连接）与消费组协调，复杂度远高于按用户组广播。
- **让 dispatcher 直接把通知发给所有实例的 HTTP 端点**：引入服务发现、鉴权与失败重试策略，且比 Redis 已有的发布/订阅更脆。
- **把频道路径做成运营配置**：配置不一致会造成静默丢推送（见决策 4）。

## 后果

- 通知投递变成 **at-most-once 的实时提示**：广播时若没有任何实例在订阅（例如所有实例都在重连），这条推送就丢了，但通知本身已经落库。因此 **事实状态始终以 `notifications` 表 + REST 收件箱为准**，客户端重连后重新拉取补齐；发布成功但订阅者数量为 0 时会打警告日志，让运维在日志里看得见。
- 新增一次 Redis 往返（发布 + 各实例各一次推送），换来的是「无论用户连在哪个实例都能实时收到」。未启用扇出时没有额外开销；Redis 不可用时会多一次失败的广播尝试，然后走本地推送。为了不让一条卡住的命令拖住整个派发周期，Redis 客户端的异步命令超时压到 2 秒、连接保持后台自动重连（`AbortOnConnectFail = false`）。
- 多实例部署不再需要「粘性会话」来保证通知送达；但其它依赖本机内存的能力（例如 Settings 快照的 15 秒轮询延迟）不受影响。
- 通知表成为唯一事实来源这一点没有改变；扇出只是把「谁推给客户端」从「产生通知的实例」改成「任何持有该用户连接的实例」。
- 缓存、分布式锁与限流仍未接入 Redis；本决策只覆盖通知投递。多实例部署仍需共享 PostgreSQL、`DataProtection__KeysPath` 与 Redis。
