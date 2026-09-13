# 备份、恢复与演练

这份文档是 AIToHuman 的**恢复手册**：出事时按它把数据捞回来，平时按它定期演练一次。
结论先写在最前面——**没恢复过的备份等于没有备份**，所以本文的每一步都在本机真实执行过一遍，
执行结果记在最后一节。

## 1. 到底要备份哪几样东西

| 资产 | 存在哪 | 丢了会怎样 | 备份方式 |
| --- | --- | --- | --- |
| 业务数据 | PostgreSQL（`tasks`/`orders`/`conversations`/`notifications`/`evidence`/`ledger_entries`/`risk_*` 等，共 29 个迁移建出的表） | 任务、订单、通知、资金流水、审计全部丢失 | `pg_dump -Fc`（自定义格式，可并行恢复、可选择性恢复） |
| 凭证文件 | 私有对象存储（`storage.provider=s3`）或本机目录（`storage.provider=local`） | 凭证元数据还在、文件打不开 | 对象存储：`mc mirror` 到备份目录/另一个桶；本机目录：直接拷目录 |
| **Data Protection 密钥环** | 部署环境目录（`DataProtection__KeysPath`） | **`system_settings` 里加密的机密（模型 API Key、S3 密钥、扫描服务密钥）解不出来**，接口会在读配置时报错 | 与数据库**一起**备份，两者必须成对恢复 |
| 部署配置 | 环境变量（`ConnectionStrings__*`、`Admin__*`、`Cors__*`、`VolcengineAI__*`…） | 进程起不来或行为变样 | 由部署流程管理（本文不涉及），但恢复时要用**同一份** |

一张表一句话：**库 + 文件 + 密钥环 + 配置，四样缺一不可**。其中密钥环最容易被漏掉，
而它的后果最隐蔽——数据都在，只是配置页打不开。

## 2. 两个指标

- **RPO（能丢多少数据）**：等于备份周期。按小时级 dump 就是小时级，因此生产上建议
  「每天一次全量 + 每小时一次 WAL 归档（`archive_mode`）」。当前仓库里的开发环境没有自动备份任务，
  生产必须由部署流程补上。
- **RTO（多久能恢复）**：本次演练的实测值——开发库 86 KB dump，`pg_restore` 秒级完成，
  从零到「应用起来并通过就绪检查」不到一分钟。生产量级下主要成本是 dump 传输与索引重建时间。

## 3. 恢复演练（本机实测可执行的完整步骤）

前置：PostgreSQL 客户端工具在 `E:\develop\sql\PostgreSql\bin`（`pg_dump`/`pg_restore`/`psql`），
对象存储客户端 `mc` 在 `E:\develop\Tools\MinIo\mc.exe`。

```powershell
# 0) 凭据只放进环境变量，别写进命令行历史里的脚本文本
$env:PGPASSWORD = '<postgres 密码>'
$pg = 'E:\develop\sql\PostgreSql\bin'

# 1) 全量备份（自定义格式：可并行、可只恢复某张表）
& "$pg\pg_dump.exe" -h localhost -p 5432 -U postgres -d aitohuman_risk_check -Fc -f .\backup\risk_check.dump

# 2) 恢复到一个**新库**：永远不要在出事的原库上直接恢复，先恢复出来核对
& "$pg\psql.exe" -h localhost -p 5432 -U postgres -d postgres -c "CREATE DATABASE aitohuman_restore_check;"
& "$pg\pg_restore.exe" -h localhost -p 5432 -U postgres -d aitohuman_restore_check --no-owner --no-privileges .\backup\risk_check.dump

# 3) 行数核对：把源库与恢复库的关键表逐一对齐（个数不一致就别急着切流量）
foreach ($t in 'users','tasks','orders','task_applications','risk_decision_entries','task_risk_appeals','ledger_entries','address_access_entries','system_settings') {
  $a = (& "$pg\psql.exe" -h localhost -p 5432 -U postgres -d aitohuman_risk_check   -At -c "select count(*) from $t;").Trim()
  $b = (& "$pg\psql.exe" -h localhost -p 5432 -U postgres -d aitohuman_restore_check -At -c "select count(*) from $t;").Trim()
  "{0,-28} {1} -> {2} {3}" -f $t, $a, $b, $(if ($a -eq $b) { 'OK' } else { 'MISMATCH' })
}

# 4) 迁移是否完整（两边都应等于迁移总数，当前 29）
'select count(*) from "__EFMigrationsHistory";' | Set-Content .\backup\count.sql -Encoding UTF8
& "$pg\psql.exe" -h localhost -p 5432 -U postgres -d aitohuman_restore_check -At -f .\backup\count.sql

# 5) 让应用连恢复库跑一遍：能起来、能读数据，才算真的恢复成功
$env:ConnectionStrings__Postgres = 'Host=localhost;Port=5432;Database=aitohuman_restore_check;Username=postgres;Password=<密码>'
dotnet run --project backend\AIToHuman.Api --no-launch-profile
# 另开一个终端：
#   GET /health/ready                     -> 200，postgres 项显示"没有待应用的迁移"
#   POST /api/v1/auth/login（真实账号）    -> 200（用户与密码散列都在）
#   GET /api/v1/admin/settings             -> 200 且键数正确（这条同时验证密钥环恢复成功）
#   GET /api/v1/admin/risk/stats?days=30   -> 判定总数与备份前一致
#   GET /api/v1/tasks?limit=5              -> 大厅能出数据

# 6) 对象存储：镜像到本机目录（备份），再回灌进一个新桶（恢复），比对对象数与内容哈希
$mc = 'E:\develop\Tools\MinIo\mc.exe'
& $mc alias set drill http://127.0.0.1:9000 <accessKey> <secretKey>
& $mc mirror --quiet drill/aitohuman-evidence .\backup\evidence-mirror
& $mc mb --ignore-existing drill/aitohuman-evidence-restore
& $mc mirror --quiet --overwrite .\backup\evidence-mirror drill/aitohuman-evidence-restore
(& $mc ls --recursive drill/aitohuman-evidence-restore | Measure-Object).Count

# 7) 收尾：删掉演练库与临时桶，避免和真实环境混淆
& "$pg\psql.exe" -h localhost -p 5432 -U postgres -d postgres -c "DROP DATABASE IF EXISTS aitohuman_restore_check;"
& $mc rb --force drill/aitohuman-evidence-restore
```

### 恢复时的顺序

1. **先停写流量**（把实例从负载均衡摘掉，或直接停实例）：恢复期间产生的写入会丢。
2. 恢复密钥环与部署配置（没有它们，应用连配置都读不出来）。
3. 恢复数据库，跑上面的行数核对与迁移数核对。
4. 让**一个新实例**连恢复库并通过 `/health/ready`（这就是第 5 步的作用：不靠肉眼，靠探针）。
5. 恢复对象存储（`mc mirror` 回灌）。
6. 放流量进来，先做一次人工冒烟：登录、看大厅、看一条订单的资金流水。

### 三个容易踩的坑（本机演练时确认过）

- **密钥环不备份 = 配置页 500**：`system_settings` 里的机密是 Data Protection 密文（`dp1:` 前缀），
  换一台机器又没有同一份密钥环时解不开。演练时我们用的是同一份密钥环，所以
  `GET /api/v1/admin/settings` 才能返回 30 个键、7 个分组。
- **`__EFMigrationsHistory` 必须带双引号查**：表名是大小写混合的，`select count(*) from __EFMigrationsHistory`
  会被 Postgres 折成小写并报"关系不存在"（演练时确实踩到了）。用文件喂给 `psql -f` 最省事，
  免去 PowerShell 吞掉双引号的麻烦。
- **空桶会让演练"假通过"**：开发环境的 `aitohuman-evidence` 桶当时是空的，
  `mc mirror` 两边都是 0 个对象、看起来"一致"。所以演练时要先放两个探针对象，
  确认镜像与回灌真的搬了东西（本次演练就是放探针后做的，并比对了 SHA-256）。

## 4. 演练记录（2026-09-13，本机真实执行）

- **PostgreSQL**：`pg_dump -Fc` 产出 86,138 字节；恢复到新库 `aitohuman_restore_check` 成功（`pg_restore` 退出码 0）。
  逐表核对**全部一致**：`users` 45、`tasks` 87、`orders` 16、`task_applications` 22、
  `risk_decision_entries` 13、`task_risk_appeals` 6、`ledger_entries` 16、`address_access_entries` 4、
  `system_settings` 3（行数不一致的表：0 张）；`__EFMigrationsHistory` 两边都是 29 行。
- **应用层验证**：用恢复库启动 API → `/health/ready` 返回 `healthy`（postgres 项："已应用的迁移 29 个，没有待应用的迁移"）；
  真实账号登录 `200`；`GET /api/v1/admin/settings` 返回 30 个键 / 7 个分组（证明密钥环可用）；
  `GET /api/v1/admin/risk/stats?days=30` 返回判定总数 13（拦 6、放行 7、2 条规则命中）；
  大厅列表返回 5 条且 `hasMore=true`。
- **对象存储**：放 2 个探针对象 → `mc mirror` 到本机目录（2 个文件）→ 回灌到临时桶（2 个对象）→
  抽查对象的 SHA-256 与镜像文件一致；收尾时删掉探针对象与临时桶（桶回到 0 个对象）。
- **清理**：演练库已 `DROP`，临时桶已删除，开发库未受影响。

## 5. 定期演练清单

- [ ] 备份文件真的生成了、大小不是 0、且落在**另一台机器或另一个存储**上（同盘同机不算备份）。
- [ ] 恢复到一个**新库**（不要动原库），逐表行数一致、迁移数一致。
- [ ] 用恢复库把 API 起起来，`/health/ready` 为 `healthy`。
- [ ] 用真实账号登录并把运营配置页打开一次（这一步验证的是密钥环，不只是数据库）。
- [ ] 对象存储镜像能回灌，抽查一个对象的哈希一致。
- [ ] 记录本次 RTO（从开始恢复到放流量）与备份时间点，写进值班记录。
- [ ] 演练完成后删除临时库与临时桶，别把演练数据留在生产旁边。

相关：就绪探针的语义见 [交接文档](../development/handoff.md) 第 9、11 节；
部署级配置项见第 7 节与 [ADR-0003](../architecture/decisions/0003-operator-configurable-settings.md)。
