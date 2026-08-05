# NetTest 技术规格文档

本文档将 [Plan.md](./Plan.md) 和 [SystemDesign.md](./SystemDesign.md) 中的决策固化为实现级契约。

## 1. 技术基线

### 1.1 运行环境

| 项目 | 规格 |
| --- | --- |
| Target Framework | `net10.0` |
| 发布目标 | Windows x64，self-contained portable directory publish |
| 语言 | C#，启用 nullable reference types 和 implicit usings |
| Web | ASP.NET Core Blazor Web App，Interactive Server render mode |
| 数据访问 | EF Core 10 + Microsoft.Data.Sqlite |
| 数据库 | SQLite，单文件位于 `Data/nettest.db` |
| 调度 | Cronos，标准五字段 Cron |
| DNS | DnsClient.NET |
| 日志 | Microsoft.Extensions.Logging + Serilog 控制台/滚动文件 Sink |
| 测试 | xUnit，ASP.NET Core TestServer，Playwright |

所有 Microsoft EF Core 包必须使用相同的 `10.0.x` 补丁版本。第三方包在首次实现时锁定兼容 .NET 10 的稳定版本，不使用浮动版本。

发布属性固定为 `RuntimeIdentifier=win-x64`、`SelfContained=true`、`PublishSingleFile=false`、`PublishTrimmed=false`。产物是可直接复制运行的目录，不要求目标机预装 .NET。

### 1.2 解决方案结构

```text
NetTest.slnx
|-- src/
|   |-- NetTest.Core/
|   |-- NetTest.Data/
|   `-- NetTest.Web/
|-- tests/
|   |-- NetTest.Core.Tests/
|   |-- NetTest.Data.Tests/
|   |-- NetTest.Web.Tests/
|   `-- NetTest.EndToEnd.Tests/
|-- Config/
|   `-- nettest.json
|-- Data/                         # 运行时创建并从源代码忽略
|-- Plan.md
|-- SystemDesign.md
`-- TechnicalSpecification.md
```

引用方向固定为：`Web -> Core`、`Web -> Data`、`Data -> Core`。Core 不引用 Data 或 Web。禁止增加 Core 与 Data 的循环引用，也不引入通用 Repository 抽象。

## 2. 配置规格

### 2.1 文件位置与编码

- 正式配置：`{AppContext.BaseDirectory}/Config/nettest.json`
- 备份配置：同目录 `nettest.json.bak`
- 临时文件：同目录随机名称 `.nettest.{guid}.tmp`
- 编码：UTF-8 without BOM
- 首次启动文件不存在时创建默认配置，然后继续启动。
- JSON 属性名使用 camelCase；读取时大小写敏感；未知属性视为验证错误，避免拼写错误被静默忽略。

### 2.2 完整配置结构

```json
{
  "schemaVersion": 1,
  "host": {
    "urls": ["http://127.0.0.1:5000"],
    "password": null
  },
  "storage": {
    "databasePath": "Data/nettest.db",
    "retentionDays": 90,
    "chartMaxPointsPerSeries": 2000
  },
  "scheduler": {
    "maxConcurrency": 10,
    "queueCapacity": 256,
    "capacityWarningWindow": 10,
    "capacityWarningRatio": 0.6
  },
  "logging": {
    "minimumLevel": "Information",
    "directory": "Data/Logs",
    "fileSizeLimitMiB": 10,
    "retainedDays": 14
  },
  "plans": [
    {
      "id": "default-five-minutes",
      "name": "默认五分钟计划",
      "cron": "*/5 * * * *",
      "enabled": true
    }
  ],
  "probes": {
    "ping": [],
    "tracert": [],
    "dns": [],
    "https": []
  }
}
```

### 2.3 公共探针字段

每个探针配置均包含以下字段：

| 字段 | 类型 | 规则 |
| --- | --- | --- |
| `id` | string | 全部探针类型之间全局唯一且不可变 |
| `name` | string | 1 至 100 个字符 |
| `enabled` | bool | 禁用后不参与新的计划运行 |
| `groupId` | string? | 用于同组串行和 UI 分组；空值时使用探针 ID |
| `tags` | string[] | 去重，每项 1 至 32 个字符，最多 16 项 |
| `planIds` | string[] | 至少一项，全部引用存在的计划 ID |

ID 使用正则 `^[a-z0-9][a-z0-9._-]{0,63}$`。计划 ID 在计划集合内唯一，探针 ID 在四类探针的并集中唯一。配置项显示顺序就是数组顺序。

宿主和计划字段还必须满足：

- `host.urls` 包含 1 至 8 个互不重复的绝对 HTTP URL；禁止 user info、query 和 fragment，path 只能为 `/`。v1 不接受 HTTPS URL，TLS 由反向代理终止。
- `host.password` 为空表示禁用认证；非空时长度为 1 至 256 个字符。
- `storage.databasePath` 和 `logging.directory` 必须是位于 AppContext.BaseDirectory 下的相对路径，规范化后不得逃逸程序目录。
- Plan name 长度为 1 至 100 个字符；Cron 必须能以 Cronos Standard 五字段格式解析。
- `groupId` 为空或符合与 ID 相同的格式；同名 groupId 只表示串行和展示分组，不建立额外配置实体。
- 启用的 Plan 必须被至少一个启用探针引用；允许先创建未启用的空计划，再完成探针关联。

### 2.4 探针配置

#### Ping

```json
{
  "id": "cloudflare-ping",
  "name": "Cloudflare Ping",
  "enabled": true,
  "groupId": "cloudflare",
  "tags": ["public-dns"],
  "planIds": ["default-five-minutes"],
  "target": "1.1.1.1",
  "packetCount": 4,
  "timeoutMs": 3000,
  "payloadSize": 32
}
```

约束：`packetCount` 1..20，`timeoutMs` 100..60000，`payloadSize` 0..65500。

#### Tracert

```json
{
  "id": "example-route",
  "name": "Example Route",
  "enabled": true,
  "groupId": "example",
  "tags": [],
  "planIds": ["default-five-minutes"],
  "target": "example.com",
  "maxHops": 30,
  "attemptsPerHop": 3,
  "timeoutMs": 3000
}
```

约束：`maxHops` 1..64，`attemptsPerHop` 1..5，`timeoutMs` 100..60000。

#### DNS

```json
{
  "id": "example-dns",
  "name": "Example DNS",
  "enabled": true,
  "groupId": "example",
  "tags": [],
  "planIds": ["default-five-minutes"],
  "queryName": "example.com",
  "recordTypes": ["A", "AAAA"],
  "resolver": {
    "mode": "SystemDirect",
    "addresses": []
  },
  "timeoutMs": 5000
}
```

`recordTypes` 允许 `A/AAAA/CNAME/MX`，至少一项且去重。Resolver mode 为 `SystemDirect` 或 `Custom`；Custom 必须提供一个或多个 IP 字面量。客户端缓存固定禁用，不提供开启选项。

#### HTTPS

```json
{
  "id": "example-https",
  "name": "Example HTTPS",
  "enabled": true,
  "groupId": "example",
  "tags": [],
  "planIds": ["default-five-minutes"],
  "url": "https://example.com/",
  "proxy": {
    "mode": "Direct",
    "url": null,
    "username": null,
    "password": null
  },
  "timeoutMs": 30000,
  "maxRedirects": 5,
  "maxResponseBytes": 10485760,
  "allowInvalidCertificate": false
}
```

URL 必须是绝对 `https` URI，不接受嵌入式用户名或密码。Proxy mode 为 `Direct/System/Custom`；Custom 必须提供 HTTP 或 HTTPS 代理 URI。`timeoutMs` 1000..300000，`maxRedirects` 0..10，`maxResponseBytes` 1024..1073741824。

### 2.5 配置保存协议

Web UI 读取配置时获得内容和 `revision`，revision 是正式文件 UTF-8 字节的 SHA-256 十六进制值。保存请求必须携带原 revision：

1. 使用进程内异步互斥锁串行化保存。
2. 再次计算磁盘 revision；不一致时返回配置冲突，要求页面重新加载。
3. 将 UI DTO 与未回显的密码字段合并，完成全量验证。
4. 写入临时文件，调用 flush-to-disk。
5. 通过 `File.Replace(temp, target, backup)` 原子替换；目标不存在时使用同卷原子 move。
6. 重新加载并发布新配置快照。

任何失败都删除临时文件并继续使用旧快照。配置错误使用结构化字段路径返回，例如 `probes.https[0].url`。

### 2.6 生效策略

- plans、probes、storage 和 scheduler 保存成功后热加载。
- host 和 logging 的变化被保存，但返回 `restartRequired=true`，当前进程不动态重绑端口或重建日志管道。
- 禁用或删除配置不取消活动 Run；活动 Run 使用其 ConfigSnapshot 完成。
- 修改 Cron 后丢弃旧的未来触发时间，基于保存完成时刻计算新配置的下一次触发。

## 3. 公共类型和接口

### 3.1 枚举

```csharp
public enum ProbeType { Ping, Tracert, Dns, Https }
public enum TriggerKind { Scheduled, Manual }
public enum ExecutionStatus { Pending, Running, Completed, Incomplete, Cancelled }
public enum CancellationReason { None, SupersededByNextRun, ApplicationExit }
public enum NetworkAddressFamily { IPv4, IPv6 }

public enum ProbeOutcome
{
    None,
    Success,
    NetworkTimeout,
    DnsError,
    ConnectionRefused,
    NetworkUnreachable,
    TlsError,
    HttpError,
    TargetNotReached,
    ResponseLimitExceeded,
    InternalError
}
```

`ProbeOutcome.None` 只允许用于 Pending、Running、Incomplete 和 Cancelled。Completed 必须具有非 None outcome。网络超时必须表示为 `Completed + NetworkTimeout`。

### 3.2 探针契约

```csharp
public interface IProbe
{
    ProbeType Type { get; }

    Task<ProbeMeasurement> ExecuteAsync(
        ProbeExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed record ProbeExecutionContext(
    Guid RunId,
    Guid ExecutionId,
    string? ProbeId,
    TriggerKind TriggerKind,
    ProbeConfigurationSnapshot Configuration,
    NetworkAddressFamily? AddressFamily,
    IPAddress? ResolvedAddress,
    TimeProvider TimeProvider);

public sealed record ProbeMeasurement(
    bool IsComplete,
    ProbeOutcome Outcome,
    long? PrimaryLatencyMs,
    int MetricsSchemaVersion,
    object Metrics,
    string? ErrorCode,
    string? ErrorMessage);
```

实现必须捕获由协作式取消导致的 `OperationCanceledException`，并使用本地累计状态返回 `IsComplete=false` 的 ProbeMeasurement。非预期异常由 ProbeExecutor 转换为 `Completed + InternalError`；不得让一个探针异常终止其他 Execution 或 BackgroundService。

### 3.3 持久化端口

```csharp
public interface IExecutionStore
{
    Task CreateRunAsync(ProbeRunDraft run, IReadOnlyList<ProbeExecutionDraft> executions, CancellationToken ct);
    Task MarkExecutionRunningAsync(Guid executionId, DateTime startedAtUtc, CancellationToken ct);
    Task CompleteExecutionAsync(Guid executionId, ProbeExecutionCompletion completion, CancellationToken ct);
    Task CompleteRunAsync(Guid runId, ProbeRunCompletion completion, CancellationToken ct);
    Task<IReadOnlyList<ActiveRun>> GetActiveRunsAsync(string planId, CancellationToken ct);
    Task RecoverInterruptedRunsAsync(DateTime recoveredAtUtc, CancellationToken ct);
}
```

所有更新方法必须执行条件更新，确保状态只按合法路径转换。例如 MarkExecutionRunning 只更新 Pending，CompleteExecution 只更新 Running。

### 3.4 进程内通知

RuntimeNotifier 发布 `RunChanged`、`ExecutionChanged`、`ConfigurationChanged` 和 `CapacityNoticeChanged`。通知只包含 ID 和变化类型，不携带完整指标。订阅者收到通知后重新查询应用服务；通知丢失不影响数据库真实性。

## 4. 数据库规格

### 4.1 SQLite 初始化

每个新连接执行：

```sql
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;
```

数据库初始化后设置 `PRAGMA journal_mode = WAL`。DbContext 通过 `AddPooledDbContextFactory` 注册；禁止在 Blazor circuit、并行任务或 BackgroundService 中长期持有 DbContext。

### 4.2 ProbeRuns

| 列 | SQLite 类型 | 约束 |
| --- | --- | --- |
| `Id` | TEXT | PK，Guid `D` 格式 |
| `PlanId` | TEXT | nullable，手动运行允许为空 |
| `PlanNameSnapshot` | TEXT | nullable |
| `TriggerKind` | INTEGER | not null |
| `ConfigurationRevision` | TEXT | not null，SHA-256 |
| `Status` | INTEGER | not null |
| `CancellationReason` | INTEGER | not null |
| `StartedAtUtc` | TEXT | not null，ISO 8601 UTC |
| `CompletedAtUtc` | TEXT | nullable |
| `CreatedAtUtc` | TEXT | not null |

索引：

- `IX_ProbeRuns_PlanId_StartedAtUtc`：`PlanId, StartedAtUtc DESC`
- `IX_ProbeRuns_Status_StartedAtUtc`：`Status, StartedAtUtc DESC`
- `IX_ProbeRuns_TriggerKind_StartedAtUtc`：`TriggerKind, StartedAtUtc DESC`

### 4.3 ProbeExecutions

| 列 | SQLite 类型 | 约束 |
| --- | --- | --- |
| `Id` | TEXT | PK，Guid `D` 格式 |
| `RunId` | TEXT | FK -> ProbeRuns，cascade delete |
| `ProbeId` | TEXT | nullable，临时手动探针允许为空 |
| `ProbeNameSnapshot` | TEXT | not null |
| `ProbeType` | INTEGER | not null |
| `GroupIdSnapshot` | TEXT | nullable |
| `PlanId` | TEXT | nullable，历史查询冗余列 |
| `TriggerKind` | INTEGER | not null，历史查询冗余列 |
| `AddressFamily` | INTEGER | nullable，DNS 或解析失败允许为空 |
| `ResolvedAddress` | TEXT | nullable |
| `ConfigurationSchemaVersion` | INTEGER | not null |
| `ConfigurationSnapshotJson` | TEXT | not null |
| `Status` | INTEGER | not null |
| `Outcome` | INTEGER | not null |
| `CancellationReason` | INTEGER | not null |
| `PrimaryLatencyMs` | INTEGER | nullable |
| `MetricsSchemaVersion` | INTEGER | not null |
| `MetricsJson` | TEXT | nullable |
| `ErrorCode` | TEXT | nullable，最大 100 字符 |
| `ErrorMessage` | TEXT | nullable，最大 2000 字符 |
| `StartedAtUtc` | TEXT | nullable |
| `CompletedAtUtc` | TEXT | nullable |
| `DurationMs` | INTEGER | nullable |
| `CreatedAtUtc` | TEXT | not null |

索引：

- `IX_ProbeExecutions_RunId`
- `IX_ProbeExecutions_ProbeId_CompletedAtUtc`：`ProbeId, CompletedAtUtc DESC`
- `IX_ProbeExecutions_PlanId_CompletedAtUtc`：`PlanId, CompletedAtUtc DESC`
- `IX_ProbeExecutions_Status_CompletedAtUtc`：`Status, CompletedAtUtc DESC`
- `IX_ProbeExecutions_TriggerKind_CompletedAtUtc`：`TriggerKind, CompletedAtUtc DESC`

配置和 Metrics JSON 在写入前使用 System.Text.Json 统一 camelCase 序列化。历史查询不得依赖 JSON 字段完成常用筛选；常用维度必须使用独立列。

### 4.4 状态聚合

Run 的终态按以下顺序计算：

1. 所有 Execution 为 Completed：Run 为 Completed，reason None。
2. 任一 Execution 为 Incomplete：Run 为 Incomplete，采用该轮统一取消原因。
3. 没有 Incomplete 且至少一个 Completed、至少一个 Cancelled：Run 为 Incomplete。
4. 所有 Execution 为 Cancelled：Run 为 Cancelled。
5. 地址展开后没有任何适用 Execution：Run 为 Completed，表示调度正常完成但没有该地址族的数据。

网络结果是否成功不影响 Run 的完整性。例如全部 HTTPS 均得到 NetworkTimeout 时，Run 仍为 Completed。

### 4.5 启动恢复

启动迁移完成后，在调度器启动前执行一次事务性恢复：

- Running Execution -> `Incomplete/ApplicationExit`
- Pending Execution -> `Cancelled/ApplicationExit`
- 包含上述 Execution 的非终态 Run 按聚合规则更新，CompletedAtUtc 使用恢复时刻

硬崩溃前尚未写入的内存指标不可恢复，不执行猜测性补写。

## 5. 调度与执行算法

### 5.1 Cron

- 使用 `CronExpression.Parse(value, CronFormat.Standard)`，只接受五字段表达式。
- 使用 `TimeZoneInfo.Local` 计算下一次执行时间。
- 调度内部等待基于 `TimeProvider`，使测试可使用 FakeTimeProvider。
- 启动和配置重载只计算当前时刻之后的下一次，不根据 LastRunAt 补跑。

### 5.2 调度循环

每个启用计划维护一个 next occurrence 和一个按 PlanId 建立的异步 gate。到期事件执行：

1. 获取当前配置快照和 revision。
2. 进入 Plan gate，合并等待期间重复到达的同计划 tick，只保留最新一次。
3. 若存在活动 Run，取消其 linked CancellationToken，并等待其 ProbeExecutor 完成状态收尾。
4. 展开计划关联的所有启用探针及地址族。
5. 创建新 Run 和全部 Pending Execution。
6. 将 Execution 按配置顺序提交有界队列。
7. 释放 Plan gate；Run 自身由执行器异步完成。
8. 立即计算下一次 occurrence。

若一个探针未响应取消，执行器每 5 秒写一次 Warning，但继续等待，不并行启动同计划的新 Run。网络调用必须有自身超时，避免永久等待。

### 5.3 地址展开

- IP 字面量生成一个对应 IPv4 或 IPv6 的 Execution。
- 直连探针的域名在 Run 创建前分别查询 A 和 AAAA，每族按响应顺序选择第一个地址。
- 某一族没有记录时，不为该族创建 Execution。
- 整体解析失败或超时时，创建一个 AddressFamily/ResolvedAddress 为空的 Execution，并以 `Completed + DnsError` 保存。
- HTTPS Direct 模式地址展开取得的 DNS 用时作为该执行的 DNS 阶段；实际 TCP 连接必须使用已选择 IP。
- HTTPS System/Custom 代理模式不展开目标地址族，只创建一个 AddressFamily/ResolvedAddress 为空的 Execution；目标 DNS 由代理负责，dnsMs 为 null，TCP/TLS 阶段描述代理链路。
- DNS 探针自身不执行上述地址展开，其查询类型由 recordTypes 决定。

### 5.4 队列与分组

- 使用 bounded Channel，容量默认 256，FullMode 为 Wait。
- worker 数量等于 maxConcurrency。
- Execution 执行前按 `groupId ?? probeId ?? executionId` 获取 keyed asynchronous lock；完成或取消后释放。
- 同一探针被不同计划触发时仍是不同 Execution，但相同 groupId 会自然串行。
- 手动运行进入同一队列和并发上限，但不参与计划取消比例。

### 5.5 容量提示计算

在每次 Scheduled Run 进入终态后查询该 PlanId 最近 N 次 Scheduled Run，N 默认为 10：

- 样本不足 N 次：状态为 InsufficientData，不显示提示。
- 受影响 Run：状态为 Incomplete 或 Cancelled，且 reason 为 SupersededByNextRun。
- ratio = 受影响 Run 数 / N。
- ratio >= 0.6：Active；否则 Inactive。
- Inactive -> Active 写一条 Warning 并通知 UI。
- Active -> Inactive 写一条 Information 并通知 UI。
- 同一状态持续期间不重复写阈值日志。

提示状态可由数据库重新计算，不新增持久化告警表。

## 6. 探针算法与指标

### 6.1 通用计时和错误

- 持久化时间使用 `TimeProvider.GetUtcNow()`。
- 持续时间使用 `Stopwatch.GetTimestamp()` 或 TimeProvider timestamp，不使用墙上时钟相减。
- ErrorCode 使用稳定机器码，ErrorMessage 使用简短用户可读文本，不保存完整异常堆栈；堆栈仅写系统日志。
- 取消产生部分结果时 Outcome 保持已知结果或 None，由 ExecutionStatus 表达不完整。

### 6.2 Ping Metrics v1

```json
{
  "sent": 4,
  "received": 3,
  "lossPercent": 25.0,
  "rttMinMs": 12,
  "rttAverageMs": 15.3,
  "rttMaxMs": 19,
  "jitterMs": 2.5,
  "samples": [
    { "sequence": 1, "status": "Success", "rttMs": 12 }
  ]
}
```

- 按序串行发送 packetCount 次，成功响应之间不增加额外延时。
- jitter 是相邻成功样本 RTT 绝对差的平均值；不足两个成功样本时为 null。
- 至少一个成功响应：Outcome Success，丢包率作为指标体现。
- 全部请求达到网络超时：Outcome NetworkTimeout。
- 系统返回不可达或拒绝状态：映射 NetworkUnreachable 或对应 ErrorCode。

### 6.3 Tracert Metrics v1

```json
{
  "reachedTarget": true,
  "totalHops": 8,
  "hops": [
    {
      "index": 1,
      "address": "192.168.1.1",
      "attempts": [1, 1, null]
    }
  ]
}
```

- 从 TTL 1 开始，每跳串行执行 attemptsPerHop 次 ICMP 请求。
- 单次无响应在 attempts 中保存 null，不终止路径。
- 到达目标后立即结束，Outcome Success。
- 达到 maxHops 仍未到达：Outcome TargetNotReached。
- 取消时保留已完成 hop；正在测量但未完成全部 attempts 的 hop 也保留已有 attempts，并使 ProbeMeasurement.IsComplete=false。

### 6.4 DNS Metrics v1

```json
{
  "resolver": "192.168.1.1",
  "responseCode": "NoError",
  "elapsedMs": 23,
  "answers": [
    { "type": "A", "value": "93.184.216.34", "ttlSeconds": 300 }
  ],
  "cnameChain": []
}
```

- SystemDirect 从活动且启用的网络适配器读取 DNS 服务器，保持系统给出的优先顺序，由 DnsClient 直接查询。
- DnsClient cache 固定关闭；UDP 截断时允许库自动回退 TCP。
- 每种 recordType 依配置顺序执行，并合并到一个 Execution Metrics。
- 至少一个查询得到 NoError 响应：Outcome Success；NXDOMAIN、SERVFAIL、超时等映射为 DnsError 或 NetworkTimeout，并保存响应码。
- 记录最终实际响应 resolver，不声称该值代表系统缓存行为。

### 6.5 HTTPS Metrics v1

```json
{
  "dnsMs": 12,
  "tcpConnectMs": 18,
  "tlsHandshakeMs": 35,
  "timeToFirstByteMs": 61,
  "downloadMs": 22,
  "totalMs": 95,
  "statusCode": 200,
  "finalUri": "https://example.com/",
  "redirects": [],
  "certificateExpiresAtUtc": "2027-01-01T00:00:00Z",
  "bytesRead": 12543,
  "responseLimitReached": false
}
```

- Direct 模式为每个目标地址族创建独立 HttpClient/SocketsHttpHandler，完成后立即释放；代理模式只创建一个执行实例。
- Direct 模式预解析结果用于 DNS 阶段和 ConnectCallback；ConnectCallback 只连接指定 IP，TLS target host 仍为 URI host。
- System/Custom 代理模式使用 SocketsHttpHandler 原生代理能力，dnsMs 为 null，连接阶段反映到代理及隧道的实际开销，不声称代表目标地址族。
- TCP 完成时记录时间点；使用 SocketsHttpHandler 的连接/明文流回调或同一请求诊断事件记录 TLS 完成时间，不允许另建探测连接代替真实 HTTP 连接。
- 请求使用 GET 和 `HttpCompletionOption.ResponseHeadersRead`，然后以固定缓冲区读取到 EOF并丢弃正文。
- 不发送内容编码偏好，不启用自动解压缩；bytesRead 表示实际响应流读取字节数。
- 自动重定向关闭，由探针显式处理最多 maxRedirects 次，记录每跳状态和 Location；相对 Location 按当前 URI 解析。
- 响应仍要求重定向但已达到 maxRedirects 时返回 Completed + HttpError，ErrorCode 为 `TooManyRedirects`。
- 总 timeout 覆盖解析、连接、TLS、全部重定向和正文读取。
- 读取达到 maxResponseBytes：停止读取并返回 Completed + ResponseLimitExceeded，同时保存已有阶段和字节数据。
- HTTP 4xx/5xx：Completed + HttpError；2xx/3xx 最终响应：Success；TLS 校验失败：TlsError。
- allowInvalidCertificate 只跳过证书验证，不改变握手计时；结果必须记录证书无效标志。

Metrics 中尚未发生或无法从当前传输模式测得的阶段使用 null；特别是代理模式的 dnsMs、连接建立前失败时的 TLS/TTFB，以及读取正文前失败时的 downloadMs。

## 7. Web 与认证规格

### 7.1 路由

| Route | 页面 |
| --- | --- |
| `/` | 仪表盘 |
| `/probes` | 探针列表和分类筛选 |
| `/probes/{type}/{id}` | 探针编辑 |
| `/plans` | 计划管理 |
| `/manual` | 手动检测 |
| `/history` | 历史筛选 |
| `/runs/{id}` | 运行详情 |
| `/settings` | 宿主、存储、调度和日志设置 |
| `/login` | 可选密码登录 |

### 7.2 认证

- host.password 为 null 或空字符串时不启用认证。
- 配置了 password 后，除 `/login` 和静态资源外的所有页面及 endpoint 都要求 Cookie 认证。
- 登录使用固定时间字节比较；认证失败返回统一消息，不记录密码。
- Cookie 设置 HttpOnly、SameSite=Lax；在 HTTPS 请求上设置 Secure。
- 没有多用户、注册、找回密码和角色系统。
- 非 loopback URL 且无密码时，在启动和配置重载时分别调用去重日志器；不阻止启动。

### 7.3 UI 查询

- 所有历史查询必须包含 start/end UTC，最大时间范围为 retentionDays。
- 默认 TriggerKind 过滤为 Scheduled；“包含手动检测”后包含 Manual。
- 趋势序列键为 `ProbeId + AddressFamily`；PlanId 是筛选维度而非默认拆分维度。
- 原始点数不超过 chartMaxPointsPerSeries 时直接返回。
- 超过上限时按 `ceil(range / maxPoints)` 形成等宽时间桶；完整结果计算 count/min/avg/max，部分结果只计算 partialCount 并在桶中心显示特殊标记，不参与完整指标聚合。
- 无数据返回空集合和查询范围，不推断停机原因。

### 7.4 手动检测

- 可以选择已配置探针，也可以提交临时探针参数。
- 已配置探针保存 ProbeId；临时探针 ProbeId 为 null，始终保存完整配置快照。
- Manual Run 持久化并进入同一执行队列，不关联 PlanId，不参与计划重叠取消和容量提示。
- 默认历史趋势排除 Manual，运行详情始终可访问。

### 7.5 最小 HTTP endpoint

- `GET /exports/history.csv`：按与历史页相同的必填时间范围和筛选导出，MetricsJson 作为转义后的单列；响应流式生成。
- `POST /system/database-backup`：通过 SQLite Backup API 创建一致性副本到 `Data/Backups/` 并返回下载结果。
- 配置密码时两个 endpoint 均要求认证；POST 使用 antiforgery token。
- 不提供探针、计划或历史 CRUD REST API。

## 8. 日志规格

### 8.1 输出

- Console Sink：供手动启动窗口查看。
- File Sink：`Data/Logs/nettest-.log`，按天和 10 MiB 大小滚动，保留 14 天。
- 默认文本格式包含时间、级别、事件 ID、消息、RunId、PlanId、ProbeId 和 Exception。

### 8.2 级别

| Level | 事件 |
| --- | --- |
| Debug | 单个探针正常开始/完成、队列活动、趋势查询细节 |
| Information | 启动、配置加载、计划完成、恢复、容量提示恢复、清理结果 |
| Warning | 配置保存失败、数据库 busy 重试、取消收尾过慢、容量阈值跨越、非本地无密码 |
| Error | 数据库写入失败、迁移失败、配置无法启动、未处理异常 |

NetworkTimeout、DnsError、TlsError、HttpError 等网络结果只入数据库；除非伴随系统异常，否则不写 Warning。

### 8.3 敏感信息过滤

日志 enrich 之前统一过滤：host.password、proxy.password、Authorization、Cookie、Proxy-Authorization 和 URI query。URL 日志只保留 scheme、host、port 和 path；不记录响应正文或请求正文。

## 9. 数据保留与备份

- RetentionWorker 在启动 5 分钟后首次运行，此后每 24 小时运行。
- 每批最多删除 1,000 个早于 UTC cutoff 的 ProbeRuns；通过 cascade 删除 Execution，循环至无匹配行，每批独立提交。
- retentionDays 最小 1、最大 3650；修改后下一次 worker 周期生效。
- 日志保留由滚动 Sink 管理。
- 数据库备份使用 SQLite 在线备份 API，不直接复制活动的 db/wal/shm 文件。
- 发布包不得包含或覆盖用户的 `Config/`、`Data/`。

## 10. 启动与关闭顺序

### 10.1 启动

1. 根据规范化 AppContext.BaseDirectory 生成命名 mutex 并尝试获取。
2. 创建缺失的 Config、Data、Logs、Backups 目录。
3. 加载或创建配置；不可恢复的启动配置错误写控制台并退出非零码。
4. 初始化日志。
5. 注册服务并创建 WebApplication。
6. 执行数据库迁移和 SQLite pragma 初始化。
7. 恢复遗留 Run/Execution。
8. 启动 Web host、RetentionWorker 和 ProbeScheduler。

### 10.2 关闭

1. 停止产生新的计划触发。
2. 以 ApplicationExit 取消活动计划和手动运行。
3. 等待探针返回部分数据并完成数据库写入，最长等待 35 秒。
4. 超过等待时间时记录 Error 后退出；下次启动执行遗留状态恢复。
5. 释放 DbContext、日志管道和命名 mutex。

## 11. 测试与验收

### 11.1 单元测试

- 所有配置字段边界、未知字段、重复 ID、断裂 planIds、revision 冲突和密码字段保留。
- 状态转换矩阵和 Run 聚合规则。
- Cron 下一次时间、停机不补跑和配置重载重算。
- 最近 10 次 60% 容量提示的跨越、保持和恢复。
- Ping/Tracert 部分指标计算、jitter 和结果映射。
- 趋势时间桶和部分结果排除规则。

### 11.2 集成测试

- 原子配置替换失败时正式文件保持不变且备份可恢复。
- SQLite migration、WAL、busy timeout、条件状态更新、cascade 和批量保留清理。
- 同一计划重叠时旧 Run 取消完成后才创建新 Run。
- 不同计划引用同一探针时分别产生 Run/Execution。
- 正常退出与模拟崩溃后的状态恢复。
- 本地 DNS 服务器验证直接查询和无客户端缓存。
- 本地 HTTP/TLS/代理服务器验证 SNI、重定向、完整读取、大小上限、超时和无正文持久化。

### 11.3 UI 与端到端测试

- 首次启动默认配置、配置 CRUD、冲突提示和重启提示。
- 密码开启/关闭、受保护页面和 endpoint、非本地无密码 Warning。
- Manual 默认不进入趋势，开启后显示。
- IPv4/IPv6 分序列、部分结果特殊标记、Plan 筛选和 2,000 点降采样。
- 容量提示出现、去重和自动恢复。
- 第二实例拒绝启动且第一实例继续运行。

### 11.4 完成标准

- 所有项目在 Windows x64 使用 .NET 10 SDK 构建，无 nullable warning。
- 自动化测试不依赖公网；需要真实网络的测试单独分类且默认跳过。
- 配置或数据库写入失败不会导致旧配置损坏或永久 Running 状态。
- 任何日志、CSV、备份响应元数据和错误页面均不泄露密码或代理凭据。
- 计划取消后已完成的 Ping 响应、Tracert hops 和 HTTPS 阶段仍可从运行详情查看。

## 12. 已知限制

- 系统只观察运行主机的默认路由，不能指定网卡。
- 每个地址族只测量一个 DNS 返回地址，不覆盖同族全部负载均衡节点。
- 硬崩溃可能丢失尚未写入数据库的当前探针部分指标。
- 可选密码在纯 HTTP 上不提供传输保密性。
- 单进程和 SQLite 设计不支持共享目录多实例或分布式调度。
