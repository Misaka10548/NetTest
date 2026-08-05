using NetTest.Core.Enums;

namespace NetTest.Core.Notifications;

/// <summary>容量提示状态。</summary>
public enum CapacityNoticeState
{
    /// <summary>样本不足 N 次，不显示提示。</summary>
    InsufficientData,

    /// <summary>受影响比例达到阈值，显示提示。</summary>
    Active,

    /// <summary>比例恢复，不显示提示。</summary>
    Inactive,
}

public sealed record RunChangedNotification(Guid RunId, ExecutionStatus Status);

public sealed record ExecutionChangedNotification(Guid ExecutionId, Guid RunId, ExecutionStatus Status);

public sealed record ConfigurationChangedNotification(string Revision);

public sealed record CapacityNoticeChangedNotification(string PlanId, CapacityNoticeState State);

/// <summary>
/// 进程内通知。只包含 ID 和变化类型，不携带完整指标。
/// 订阅者收到通知后重新查询应用服务；通知丢失不影响数据库真实性。
/// </summary>
public sealed class RuntimeNotifier
{
    public event EventHandler<RunChangedNotification>? RunChanged;

    public event EventHandler<ExecutionChangedNotification>? ExecutionChanged;

    public event EventHandler<ConfigurationChangedNotification>? ConfigurationChanged;

    public event EventHandler<CapacityNoticeChangedNotification>? CapacityNoticeChanged;

    public void PublishRunChanged(Guid runId, ExecutionStatus status)
        => RunChanged?.Invoke(this, new RunChangedNotification(runId, status));

    public void PublishExecutionChanged(Guid executionId, Guid runId, ExecutionStatus status)
        => ExecutionChanged?.Invoke(this, new ExecutionChangedNotification(executionId, runId, status));

    public void PublishConfigurationChanged(string revision)
        => ConfigurationChanged?.Invoke(this, new ConfigurationChangedNotification(revision));

    public void PublishCapacityNoticeChanged(string planId, CapacityNoticeState state)
        => CapacityNoticeChanged?.Invoke(this, new CapacityNoticeChangedNotification(planId, state));
}
