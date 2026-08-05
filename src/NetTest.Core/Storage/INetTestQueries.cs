using NetTest.Core.Enums;

namespace NetTest.Core.Storage;

/// <summary>历史查询：必须包含 start/end UTC，最大时间范围由 retentionDays 约束。</summary>
public sealed record HistoryQuery(
    DateTime StartUtc,
    DateTime EndUtc,
    string? PlanId,
    string? ProbeId,
    NetworkAddressFamily? AddressFamily,
    ExecutionStatus? Status,
    TriggerKind? TriggerKind,
    int Page,
    int PageSize);

public sealed record HistoryPage(int TotalCount, IReadOnlyList<HistoryItem> Items);

/// <summary>
/// 导出查询：与 HistoryQuery 相同的筛选维度，但使用 (CreatedAtUtc, Id) 双键游标
/// 分批读取，支撑 CSV 导出流式生成（TechSpec 7.5）。
/// </summary>
public sealed record HistoryExportQuery(
    DateTime StartUtc,
    DateTime EndUtc,
    string? PlanId,
    string? ProbeId,
    NetworkAddressFamily? AddressFamily,
    ExecutionStatus? Status,
    TriggerKind? TriggerKind,
    DateTime? AfterCreatedAtUtc,
    Guid? AfterId,
    int BatchSize);

/// <summary>导出批次：HasMore 为 true 时用最后一条的 CreatedAtUtc/Id 作为下一批游标。</summary>
public sealed record HistoryExportBatch(IReadOnlyList<HistoryItem> Items, bool HasMore);

/// <summary>历史页行：Execution 级别，支持探针、地址族、状态、来源筛选。</summary>
public sealed record HistoryItem(
    Guid ExecutionId,
    Guid RunId,
    string? ProbeId,
    string ProbeNameSnapshot,
    ProbeType ProbeType,
    string? PlanId,
    string? PlanNameSnapshot,
    TriggerKind TriggerKind,
    NetworkAddressFamily? AddressFamily,
    ExecutionStatus Status,
    ProbeOutcome Outcome,
    CancellationReason CancellationReason,
    long? PrimaryLatencyMs,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    long? DurationMs,
    DateTime CreatedAtUtc,
    string? ErrorCode,
    string? MetricsJson);

public sealed record RunDetail(
    Guid RunId,
    string? PlanId,
    string? PlanNameSnapshot,
    TriggerKind TriggerKind,
    string ConfigurationRevision,
    ExecutionStatus Status,
    CancellationReason CancellationReason,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime CreatedAtUtc,
    IReadOnlyList<ExecutionDetail> Executions);

public sealed record ExecutionDetail(
    Guid ExecutionId,
    string? ProbeId,
    string ProbeNameSnapshot,
    ProbeType ProbeType,
    string? GroupIdSnapshot,
    string? PlanId,
    TriggerKind TriggerKind,
    NetworkAddressFamily? AddressFamily,
    string? ResolvedAddress,
    int ConfigurationSchemaVersion,
    string ConfigurationSnapshotJson,
    ExecutionStatus Status,
    ProbeOutcome Outcome,
    CancellationReason CancellationReason,
    long? PrimaryLatencyMs,
    int MetricsSchemaVersion,
    string? MetricsJson,
    string? ErrorCode,
    string? ErrorMessage,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    long? DurationMs,
    DateTime CreatedAtUtc);

/// <summary>趋势查询。maxPoints 为序列上限，超出时服务端按时间桶聚合。</summary>
public sealed record TrendQuery(
    DateTime StartUtc,
    DateTime EndUtc,
    string ProbeId,
    NetworkAddressFamily? AddressFamily,
    bool IncludeManual,
    int MaxPoints);

public sealed record TrendSeries(
    string ProbeId,
    NetworkAddressFamily? AddressFamily,
    IReadOnlyList<TrendPoint> Points);

/// <summary>趋势点。完整结果参与 count/min/avg/max；部分结果只计 partialCount，不参与完整指标聚合。</summary>
public sealed record TrendPoint(
    DateTime TimeUtc,
    int CompleteCount,
    int PartialCount,
    long? MinMs,
    double? AverageMs,
    long? MaxMs);

public sealed record RecentRunSummary(
    Guid RunId,
    string? PlanId,
    string? PlanNameSnapshot,
    TriggerKind TriggerKind,
    ExecutionStatus Status,
    CancellationReason CancellationReason,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    int CompletedExecutions,
    int TotalExecutions);

public sealed record ProbeOverview(
    string ProbeId,
    string ProbeNameSnapshot,
    ProbeType ProbeType,
    string? GroupIdSnapshot,
    ExecutionStatus? LatestStatus,
    ProbeOutcome? LatestOutcome,
    DateTime? LatestCompletedAtUtc,
    long? LatestPrimaryLatencyMs);

/// <summary>容量提示所需的最近 Run 状态样本。</summary>
public sealed record RunStatusSample(
    Guid RunId,
    ExecutionStatus Status,
    CancellationReason CancellationReason);

/// <summary>只读查询端口：历史、趋势、仪表盘数据。定义在 Core，由 NetTest.Data 实现。</summary>
public interface INetTestQueries
{
    Task<HistoryPage> GetHistoryPageAsync(HistoryQuery query, CancellationToken ct);

    /// <summary>按 (CreatedAtUtc, Id) 游标分批读取导出行；游标为 null 时从最新开始。</summary>
    Task<HistoryExportBatch> GetHistoryExportBatchAsync(HistoryExportQuery query, CancellationToken ct);

    Task<RunDetail?> GetRunDetailAsync(Guid runId, CancellationToken ct);

    Task<IReadOnlyList<TrendSeries>> GetTrendSeriesAsync(TrendQuery query, CancellationToken ct);

    Task<IReadOnlyList<RecentRunSummary>> GetRecentRunsAsync(string? planId, int limit, CancellationToken ct);

    Task<IReadOnlyList<ProbeOverview>> GetProbeOverviewAsync(CancellationToken ct);

    Task<IReadOnlyList<RunStatusSample>> GetRecentRunStatusesAsync(string planId, int limit, CancellationToken ct);
}
