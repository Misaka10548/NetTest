using System.Net;
using NetTest.Core.Configuration;
using NetTest.Core.Enums;

namespace NetTest.Core.Probes;

/// <summary>
/// 探针执行上下文。配置快照在执行开始时冻结，不受后续配置修改影响。
/// </summary>
public sealed record ProbeExecutionContext(
    Guid RunId,
    Guid ExecutionId,
    string? ProbeId,
    TriggerKind TriggerKind,
    ProbeConfigurationSnapshot Configuration,
    NetworkAddressFamily? AddressFamily,
    IPAddress? ResolvedAddress,
    TimeProvider TimeProvider);

/// <summary>
/// 测量结果。协作式取消时必须返回包含部分指标的结果（IsComplete=false），不得丢失已完成步骤。
/// </summary>
public sealed record ProbeMeasurement(
    bool IsComplete,
    ProbeOutcome Outcome,
    long? PrimaryLatencyMs,
    int MetricsSchemaVersion,
    object Metrics,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>探针契约：按类型实现具体测量。</summary>
public interface IProbe
{
    ProbeType Type { get; }

    Task<ProbeMeasurement> ExecuteAsync(
        ProbeExecutionContext context,
        CancellationToken cancellationToken);
}
