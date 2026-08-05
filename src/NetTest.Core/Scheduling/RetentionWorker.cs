using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetTest.Core.Configuration;
using NetTest.Core.Storage;

namespace NetTest.Core.Scheduling;

/// <summary>
/// 保留清理：启动 5 分钟后首次运行，此后每 24 小时按保留期批量删除过期结果（TechSpec 9）。
/// </summary>
public sealed class RetentionWorker : BackgroundService
{
    private const int BatchSize = 1000;
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IRetentionStore _store;
    private readonly ConfigManager _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(
        IRetentionStore store,
        ConfigManager config,
        TimeProvider timeProvider,
        ILogger<RetentionWorker> logger)
    {
        _store = store;
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(FirstRunDelay, _timeProvider, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据保留清理失败，将在下一周期重试。");
            }

            await Task.Delay(Interval, _timeProvider, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        int retentionDays = _config.Current.Storage.RetentionDays;
        DateTime cutoffUtc = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-retentionDays);

        int total = 0;
        while (true)
        {
            int deleted = await _store.DeleteExpiredRunsAsync(cutoffUtc, BatchSize, cancellationToken);
            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            _logger.LogInformation("清理过期运行 {Count} 条（保留 {RetentionDays} 天）。", total, retentionDays);
        }
    }
}
