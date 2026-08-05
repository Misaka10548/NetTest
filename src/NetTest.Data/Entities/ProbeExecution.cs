using NetTest.Core.Enums;

namespace NetTest.Data.Entities;

/// <summary>单次探针 × 地址族执行（TechSpec 4.3）。可更新的状态记录。</summary>
public sealed class ProbeExecution
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public string? ProbeId { get; set; }

    public string ProbeNameSnapshot { get; set; } = "";

    public ProbeType ProbeType { get; set; }

    public string? GroupIdSnapshot { get; set; }

    /// <summary>历史查询冗余列。</summary>
    public string? PlanId { get; set; }

    /// <summary>历史查询冗余列。</summary>
    public TriggerKind TriggerKind { get; set; }

    public NetworkAddressFamily? AddressFamily { get; set; }

    public string? ResolvedAddress { get; set; }

    public int ConfigurationSchemaVersion { get; set; }

    public string ConfigurationSnapshotJson { get; set; } = "";

    public ExecutionStatus Status { get; set; }

    public ProbeOutcome Outcome { get; set; }

    public CancellationReason CancellationReason { get; set; }

    public long? PrimaryLatencyMs { get; set; }

    public int MetricsSchemaVersion { get; set; }

    public string? MetricsJson { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public long? DurationMs { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ProbeRun? Run { get; set; }
}
