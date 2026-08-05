using NetTest.Core.Enums;

namespace NetTest.Data.Entities;

/// <summary>一次计划或手动触发产生的运行批次（TechSpec 4.2）。</summary>
public sealed class ProbeRun
{
    public Guid Id { get; set; }

    public string? PlanId { get; set; }

    public string? PlanNameSnapshot { get; set; }

    public TriggerKind TriggerKind { get; set; }

    /// <summary>触发时刻配置文件的 SHA-256 revision。</summary>
    public string ConfigurationRevision { get; set; } = "";

    public ExecutionStatus Status { get; set; }

    public CancellationReason CancellationReason { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public List<ProbeExecution> Executions { get; set; } = new();
}
