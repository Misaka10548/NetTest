using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetTest.Core.Configuration;
using NetTest.Core.Notifications;

namespace NetTest.Core.Scheduling;

/// <summary>
/// 计划调度器：每个启用计划基于系统本地时区计算下一次执行时间，到点触发 ProbeExecutor；
/// 停机期间不补跑；配置重载后基于保存完成时刻重算下一次触发（TechSpec 5.1/5.2/2.6）。
/// </summary>
public sealed class ProbeScheduler : BackgroundService
{
    private readonly ConfigManager _config;
    private readonly ProbeExecutor _executor;
    private readonly RuntimeNotifier _notifier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProbeScheduler> _logger;
    private TaskCompletionSource _reloadSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProbeScheduler(
        ConfigManager config,
        ProbeExecutor executor,
        RuntimeNotifier notifier,
        TimeProvider timeProvider,
        ILogger<ProbeScheduler> logger)
    {
        _config = config;
        _executor = executor;
        _notifier = notifier;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _notifier.ConfigurationChanged += OnConfigurationChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                NetTestConfiguration config = _config.Current;
                DateTime utcNow = _timeProvider.GetUtcNow().UtcDateTime;

                var due = new List<(string PlanId, DateTime NextUtc)>();
                foreach (PlanConfiguration plan in config.Plans.Where(p => p.Enabled))
                {
                    try
                    {
                        CronExpression cron = CronExpression.Parse(plan.Cron, CronFormat.Standard);
                        DateTime? next = cron.GetNextOccurrence(utcNow, TimeZoneInfo.Local);
                        if (next is not null)
                        {
                            due.Add((plan.Id, next.Value));
                        }
                    }
                    catch (CronFormatException ex)
                    {
                        // 配置已通过验证，此处仅为防御。
                        _logger.LogWarning(ex, "计划 {PlanId} 的 Cron 无效，已跳过。", plan.Id);
                    }
                }

                if (due.Count == 0)
                {
                    await WaitForReloadOrStopAsync(stoppingToken);
                    continue;
                }

                (string planId, DateTime nextUtc) = due.MinBy(d => d.NextUtc);
                TimeSpan wait = nextUtc - utcNow;
                if (wait < TimeSpan.Zero)
                {
                    wait = TimeSpan.Zero;
                }

                Task delay = Task.Delay(wait, _timeProvider, stoppingToken);
                Task reload = _reloadSignal.Task;
                Task completed = await Task.WhenAny(delay, reload);

                if (completed == reload)
                {
                    // 配置已变化：丢弃旧未来触发时间，从保存完成时刻重算。
                    _reloadSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _logger.LogDebug("配置已重载，重新计算计划触发时间。");
                    continue;
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await _executor.TriggerPlanAsync(planId, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "计划 {PlanId} 触发失败。", planId);
                }
            }
        }
        finally
        {
            _notifier.ConfigurationChanged -= OnConfigurationChanged;
        }
    }

    private void OnConfigurationChanged(object? sender, ConfigurationChangedNotification e)
        => _reloadSignal.TrySetResult();

    private async Task WaitForReloadOrStopAsync(CancellationToken stoppingToken)
    {
        Task reload = _reloadSignal.Task;
        Task completed = await Task.WhenAny(reload, Task.Delay(TimeSpan.FromMinutes(1), _timeProvider, stoppingToken));
        if (completed == reload)
        {
            _reloadSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
