using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NetTest.Core.Configuration;
using NetTest.Core.Enums;
using NetTest.Core.Notifications;
using NetTest.Core.Storage;

namespace NetTest.Core.Scheduling;

/// <summary>
/// 容量提示：按计划统计最近 N 次 Scheduled Run 中因下一轮到达（SupersededByNextRun）未完整完成的
/// 比例，跨越阈值时写一条去重 Warning 并通知 UI，恢复时写 Information（TechSpec 5.5）。
/// 网络失败、手动运行、应用退出和配置修改不计入比例。
/// </summary>
public sealed class CapacityNoticeService
{
    private readonly INetTestQueries _queries;
    private readonly RuntimeNotifier _notifier;
    private readonly ConfigManager _config;
    private readonly ILogger<CapacityNoticeService> _logger;
    private readonly ConcurrentDictionary<string, CapacityNoticeState> _lastState = new();

    public CapacityNoticeService(
        INetTestQueries queries,
        RuntimeNotifier notifier,
        ConfigManager config,
        ILogger<CapacityNoticeService> logger)
    {
        _queries = queries;
        _notifier = notifier;
        _config = config;
        _logger = logger;
    }

    /// <summary>计划 Run 进入终态后调用。</summary>
    public async Task OnRunCompletedAsync(string planId, CancellationToken cancellationToken)
    {
        SchedulerConfiguration scheduler = _config.Current.Scheduler;
        await EvaluateAsync(planId, scheduler.CapacityWarningWindow, scheduler.CapacityWarningRatio, cancellationToken);
    }

    /// <summary>从数据库重新计算并返回当前状态（不写日志、不通知）。</summary>
    public async Task<CapacityNoticeState> EvaluateAsync(string planId, CancellationToken cancellationToken)
    {
        SchedulerConfiguration scheduler = _config.Current.Scheduler;
        return await ComputeStateAsync(planId, scheduler.CapacityWarningWindow, scheduler.CapacityWarningRatio, cancellationToken);
    }

    private async Task EvaluateAsync(string planId, int window, double ratio, CancellationToken cancellationToken)
    {
        CapacityNoticeState state = await ComputeStateAsync(planId, window, ratio, cancellationToken);
        if (_lastState.TryGetValue(planId, out CapacityNoticeState previous) && previous == state)
        {
            return;
        }

        _lastState[planId] = state;
        _notifier.PublishCapacityNoticeChanged(planId, state);

        switch (state)
        {
            case CapacityNoticeState.Active:
                _logger.LogWarning(
                    "计划 {PlanId} 最近 {Window} 次计划运行中，因下一轮到达而未完整完成的比例达到 {Ratio:P0}，建议调整计划周期或探针耗时。",
                    planId,
                    window,
                    ratio);
                break;
            case CapacityNoticeState.Inactive:
                _logger.LogInformation("计划 {PlanId} 的容量提示已恢复。", planId);
                break;
        }
    }

    private async Task<CapacityNoticeState> ComputeStateAsync(string planId, int window, double ratio, CancellationToken cancellationToken)
    {
        IReadOnlyList<RunStatusSample> samples = await _queries.GetRecentRunStatusesAsync(planId, window, cancellationToken);
        if (samples.Count < window)
        {
            return CapacityNoticeState.InsufficientData;
        }

        int affected = samples.Count(sample =>
            (sample.Status == ExecutionStatus.Incomplete || sample.Status == ExecutionStatus.Cancelled)
            && sample.CancellationReason == CancellationReason.SupersededByNextRun);

        double currentRatio = affected / (double)window;
        return currentRatio >= ratio ? CapacityNoticeState.Active : CapacityNoticeState.Inactive;
    }
}
