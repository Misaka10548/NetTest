using System.Net;
using NetTest.Core.Enums;

namespace NetTest.Core.Storage;

/// <summary>新建 Run 所需数据。</summary>
public sealed record ProbeRunDraft(
    Guid RunId,
    string? PlanId,
    string? PlanNameSnapshot,
    TriggerKind TriggerKind,
    string ConfigurationRevision,
    DateTime StartedAtUtc,
    DateTime CreatedAtUtc);

/// <summary>新建 Execution 所需数据。配置快照在执行创建时序列化冻结。</summary>
public sealed record ProbeExecutionDraft(
    Guid Id,
    Guid RunId,
    string? ProbeId,
    string ProbeNameSnapshot,
    ProbeType ProbeType,
    string? GroupIdSnapshot,
    string? PlanId,
    TriggerKind TriggerKind,
    NetworkAddressFamily? AddressFamily,
    IPAddress? ResolvedAddress,
    int ConfigurationSchemaVersion,
    string ConfigurationSnapshotJson,
    DateTime CreatedAtUtc);

/// <summary>Execution 终态写入数据。</summary>
public sealed record ProbeExecutionCompletion(
    ExecutionStatus Status,
    ProbeOutcome Outcome,
    CancellationReason CancellationReason,
    long? PrimaryLatencyMs,
    int MetricsSchemaVersion,
    string? MetricsJson,
    string? ErrorCode,
    string? ErrorMessage,
    DateTime? StartedAtUtc,
    DateTime CompletedAtUtc,
    long? DurationMs);

/// <summary>Run 终态写入数据。</summary>
public sealed record ProbeRunCompletion(
    ExecutionStatus Status,
    CancellationReason CancellationReason,
    DateTime CompletedAtUtc);

/// <summary>同计划下尚未进入终态的 Run。</summary>
public sealed record ActiveRun(
    Guid RunId,
    ExecutionStatus Status,
    CancellationReason CancellationReason,
    DateTime StartedAtUtc);

/// <summary>
/// 持久化端口，定义在 Core，由 NetTest.Data 实现。
/// 所有更新方法必须执行条件更新，确保状态只按合法路径转换。
/// </summary>
public interface IExecutionStore
{
    Task CreateRunAsync(ProbeRunDraft run, IReadOnlyList<ProbeExecutionDraft> executions, CancellationToken ct);

    Task MarkExecutionRunningAsync(Guid executionId, DateTime startedAtUtc, CancellationToken ct);

    Task CompleteExecutionAsync(Guid executionId, ProbeExecutionCompletion completion, CancellationToken ct);

    Task CompleteRunAsync(Guid runId, ProbeRunCompletion completion, CancellationToken ct);

    Task<IReadOnlyList<ActiveRun>> GetActiveRunsAsync(string planId, CancellationToken ct);

    Task RecoverInterruptedRunsAsync(DateTime recoveredAtUtc, CancellationToken ct);
}
