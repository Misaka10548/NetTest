# NetTest 个人网络数据采集与可视化实施计划

## Summary

- 使用 .NET 10 LTS、Blazor Web App Interactive Server、EF Core/SQLite 构建单进程 Windows 便携应用。
- 产品定位为单用户、本机优先的数据采集与可视化工具；不包含告警通知、多用户、外部 REST API、IP 归属和 TCP 抓包。
- Web UI 直接维护 `Config/nettest.json`；配置不入库。SQLite 只保存运行、结果和必要配置快照。
- 程序手动启动，默认监听 `http://127.0.0.1:5000`，数据统一存放于程序目录的 `Data/`。

## Implementation Changes

### 配置与宿主

- JSON 顶层包含 `schemaVersion`、宿主配置、日志配置、计划列表，以及按 `ping/dns/https/tracert` 分类的探针列表；每个探针通过稳定 ID 引用一个或多个 `planIds`，并支持显示分组和标签。
- Web UI 保存配置时完整校验 ID、引用、Cron、地址和探针参数，通过同目录临时文件原子替换，并保留一份 `.bak`。
- 探针和计划修改立即重新加载；监听地址、密码和日志路径保存后提示重启。正在执行的任务继续使用启动时的配置快照。
- 使用基于程序目录的命名互斥锁限制单实例运行。
- 可选 `password` 启用单用户 Cookie 登录；未配置密码且监听非 loopback 时，在启动和配置重载时写一条去重 Warning。密码和代理凭据不得进入 UI 回显、日志或导出。

### 运行与调度

- `Plan` 与 `ProbeDefinition` 为多对多关系；同一探针被不同计划同时触发时分别执行并保留各自 `PlanId`。
- Cron 使用系统本地时区计算，所有时间以 UTC 入库；程序停机期间不补跑。
- 计划触发时先创建 Run 及所有 Pending Execution。不同计划互不取消，手动运行也不受计划取消影响。
- 同一计划下一周期到达时，取消上一轮并等待其保存部分结果后再启动新一轮：已开始项为 `Incomplete/SupersededByNextRun`，未开始项为 `Cancelled/SupersededByNextRun`。
- 正常退出时使用 `ApplicationExit` 标记运行中和排队项；异常退出后的遗留状态在下次启动时修正。
- 不同逻辑分组可并发，同组探针串行；全局使用有界队列和默认并发上限 10，所有探针支持协作式取消。
- 每个计划最近 10 次 Scheduled Run 中，因下一轮到达而未完整完成的 Run 达到 60% 时，在仪表盘和计划页显示提示并写一条去重 Warning；比例恢复后清除提示并写 Information。

### 探针行为

- IP 字面量只执行对应地址族；域名分别解析 A/AAAA，每族选择返回顺序中的第一个地址并记录实际 IP。无对应记录则不产生该族结果，有记录但连接失败则保存失败结果。
- Ping 默认 4 次、单次超时 3 秒，取消时保留已完成响应及部分统计。
- Tracert 默认最大 30 跳、每跳 3 次、单次超时 3 秒，取消时保留已完成 hops。
- DNS 使用 DnsClient 直接查询、禁用客户端缓存；默认读取活动网卡 DNS 服务器，也允许指定解析器，并记录实际响应服务器、记录类型和 CNAME 链。
- HTTPS 使用 GET、新建实际测量连接并保证 DNS/TCP/TLS/HTTP 阶段来自同一请求；域名连接指定 IP 时仍保留原域名作为 SNI/Host。
- HTTPS 支持 `Direct/System/Custom` 代理模式，最多 5 次重定向，总超时默认 30 秒，最大读取量默认 10 MiB。
- HTTPS 正文读取到 EOF 后丢弃，不保存内容；记录 DNS/TCP/TLS/首字节/完整读取耗时、状态码、最终 URI、重定向链、证书到期时间、接收字节数和是否达到读取上限。
- 网络响应超时属于一次已完成测量：`Completed + NetworkTimeout`，不得与调度取消混淆。

### 存储、日志与 UI

- 数据库使用 `ProbeRuns` 和 `ProbeExecutions`。Execution 保存 RunId、ProbeId、PlanId、触发来源、地址族、实际地址、配置快照、状态、结果类别、取消原因、时间、部分指标 JSON 和 `MetricsSchemaVersion`。
- Execution 状态为 `Pending/Running/Completed/Incomplete/Cancelled`；结果类别独立表示成功、网络超时、DNS/TLS/HTTP/内部错误。
- 为探针时间、计划时间和状态查询建立索引；使用 `IDbContextFactory`、WAL、busy timeout 和短事务，启动时执行迁移。
- 原始结果默认保留 90 天并每日批量清理；图表单序列最多返回 2,000 点，超出时服务端按时间桶聚合。
- 手动检测结果同样持久化并标记 `Manual`，默认不进入趋势图；用户可开启“包含手动检测”。
- 部分结果在趋势图使用不同标记，保留在历史详情中，但默认不参与均值、分位数等汇总。
- UI 包含仪表盘、分类探针配置、计划管理、手动检测、历史与运行详情；支持 1 小时、24 小时、7 天和自定义时间范围，以及 Plan、探针、地址族和执行状态筛选。
- 不创建自定义 SignalR Hub 或 CRUD REST API；Blazor 组件直接调用应用服务，通过进程内通知刷新。仅为导出和数据库备份保留最小 HTTP endpoint。
- 日志输出到控制台和 `Data/Logs/`，按日期及 10 MiB 文件大小滚动，默认保留 14 天；记录 RunId、PlanId、ProbeId、触发来源和取消链路。网络探测失败作为结果数据，不按系统 Warning 刷屏。

## Public Contracts

- `IProbe.ExecuteAsync(ProbeExecutionContext, CancellationToken)` 必须在协作式取消时返回包含部分指标的 `ProbeMeasurement`，而不是丢失已完成步骤。
- 公共枚举固定为 `TriggerKind`、`ExecutionStatus`、`ProbeOutcome`、`CancellationReason` 和地址族。
- `ProbeExecutor` 负责状态转换、部分结果持久化和进程内通知；持久化接口定义在 Core，Data 实现，保持 Core -> Data 的依赖反转。
- JSON 配置和 Metrics JSON 均带独立 schema version；删除或修改当前配置不影响历史结果解释。

## Test Plan

- 配置测试：分类结构、重复 ID、断裂引用、非法 Cron/地址、原子替换、备份恢复、宿主配置重启提示。
- 调度测试：无停机补跑、多计划独立执行、同计划取消、部分结果保存、应用退出恢复、最近 10 次 60% 提示及恢复去重。
- 探针测试：使用本地可控 DNS/HTTP 服务验证无缓存解析、双栈选择、SNI、代理、重定向、完整读取、大小限制、网络超时和取消部分数据。
- 数据测试：SQLite 迁移、WAL 并发写、遗留 Running 状态恢复、90 天清理、索引查询和图表降采样。
- UI 测试：配置 CRUD 实际更新 JSON、登录保护、非本地无密码提示、手动结果默认排除、部分结果标记、计划筛选和实时刷新。
- 验收要求：第二实例无法启动；配置写入失败不破坏旧文件；计划取消不丢失已完成测量；异常退出后不存在永久 Running 状态；密码和代理凭据不出现在任何日志或导出中。

## Assumptions

- 第一版仅保证 Windows x64 便携运行，依赖系统网络路由选择，不提供网卡绑定。
- 默认历史保留 90 天、日志保留 14 天、并发上限 10、取消提示窗口 10 次且阈值 60%，均可在 JSON/Web UI 中调整。
- 可选密码仅用于简单访问控制；HTTP 下不承诺抵抗局域网窃听，需要更高安全性时由用户使用 HTTPS 反向代理终止 TLS。
- 强制结束或断电只能恢复已落库数据；不为保存每个内存中的瞬时测量而进行高频 SQLite 写入。
