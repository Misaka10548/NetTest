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
public class ProbeScheduler : BackgroundService
{
    private readonly ConfigManager _config;
    private readonly ProbeExecutor _executor;
    private readonly RuntimeNotifier _notifier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProbeScheduler> _logger;
    private TaskCompletionSource _reloadSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>单次 Task.Delay 上限：低频 Cron 的等待分段进行，避免超过约 49.7 天上限抛异常。</summary>
    private static readonly TimeSpan MaxDelayChunk = TimeSpan.FromHours(24);

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

                List<(string PlanId, DateTime NextUtc)> due = ComputeDue(config, utcNow);

                // 已到期（含上一轮触发耗时期间刚到期）的计划全部触发，而不是只取最早一个：
                // 多个计划同一时刻到期时，若只触发最早一个，其余会因 GetNextOccurrence
                // 只返回严格未来而被推迟到下一周期；若存在一个周期更短且整点/半点都命中的
                // 计划（如 */5），被推迟的计划将永远轮不到（永久饥饿）。
                if (due.Any(d => d.NextUtc <= utcNow))
                {
                    foreach (string planId in due.Where(d => d.NextUtc <= utcNow).Select(d => d.PlanId))
                    {
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

                    // 触发耗时可能让其他计划刚到期，重算后继续触发。
                    continue;
                }

                if (due.Count == 0)
                {
                    await WaitForReloadOrStopAsync(stoppingToken);
                    continue;
                }

                DateTime nextUtc = due.MinBy(d => d.NextUtc).NextUtc;
                TimeSpan wait = nextUtc - utcNow;
                if (wait < TimeSpan.Zero)
                {
                    wait = TimeSpan.Zero;
                }

                Task reload = _reloadSignal.Task;
                if (!await WaitUntilAsync(wait, reload, stoppingToken))
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

                DateTime afterWait = _timeProvider.GetUtcNow().UtcDateTime;
                foreach (string planId in due.Where(d => d.NextUtc <= afterWait).Select(d => d.PlanId))
                {
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
        }
        finally
        {
            _notifier.ConfigurationChanged -= OnConfigurationChanged;
        }
    }

    /// <summary>计算所有启用计划的下一次执行时间（UTC，Cron 按系统本地时区解释）。</summary>
    private List<(string PlanId, DateTime NextUtc)> ComputeDue(NetTestConfiguration config, DateTime utcNow)
    {
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

        return due;
    }

    /// <summary>
    /// 等待指定时长，期间可被配置重载信号打断。返回 true 表示等待完成，false 表示 reload 先完成。
    /// 等待按 MaxDelayChunk 分段，避免低频 Cron（间隔超过 Task.Delay 上限约 49.7 天）抛异常。
    /// </summary>
    private async Task<bool> WaitUntilAsync(TimeSpan wait, Task reload, CancellationToken stoppingToken)
    {
        while (wait > MaxDelayChunk)
        {
            Task chunk = Task.Delay(MaxDelayChunk, _timeProvider, stoppingToken);
            if (await Task.WhenAny(chunk, reload) == reload)
            {
                return false;
            }

            wait -= MaxDelayChunk;
        }

        Task last = Task.Delay(wait, _timeProvider, stoppingToken);
        return await Task.WhenAny(last, reload) == last;
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
