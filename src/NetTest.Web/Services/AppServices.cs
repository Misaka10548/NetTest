using NetTest.Core.Configuration;
using NetTest.Core.Notifications;
using NetTest.Core.Scheduling;
using NetTest.Core.Storage;

namespace NetTest.Web.Services;

/// <summary>Web UI 应用服务：配置读取/保存协议、手动触发、容量提示查询。</summary>
public sealed class AppServices
{
    private readonly ConfigManager _config;
    private readonly ProbeExecutor _executor;
    private readonly CapacityNoticeService _capacityNotices;
    private readonly RuntimeNotifier _notifier;
    private readonly ILogger<AppServices> _logger;

    public AppServices(
        ConfigManager config,
        ProbeExecutor executor,
        CapacityNoticeService capacityNotices,
        RuntimeNotifier notifier,
        ILogger<AppServices> logger)
    {
        _config = config;
        _executor = executor;
        _capacityNotices = capacityNotices;
        _notifier = notifier;
        _logger = logger;
    }

    public NetTestConfiguration Configuration => _config.Current;

    public string Revision => _config.Revision;

    /// <summary>
    /// 保存配置：合并未回显密码 + revision 校验 + 原子替换（TechSpec 2.5）。
    /// 冲突时返回 Conflict=true 要求页面重新加载。
    /// </summary>
    public async Task<ConfigSaveResult> SaveConfigurationAsync(
        NetTestConfiguration incoming,
        CancellationToken cancellationToken = default)
    {
        SecretMerger.MergeSecrets(_config.Current, incoming);
        ConfigSaveResult result = await _config.SaveAsync(incoming, _config.Revision, cancellationToken);
        if (!result.Conflict)
        {
            // 通知调度器等订阅者立即丢弃旧触发时间并重算（TechSpec 2.6），
            // 保证新增/修改/删除计划保存后立即生效，而不是等当前等待结束。
            _notifier.PublishConfigurationChanged(result.Revision);
            if (!result.RestartRequired)
            {
                _logger.LogInformation("配置已保存（revision {Revision}）。", result.Revision);
            }
        }

        return result;
    }

    /// <summary>手动触发：已配置探针按 ProbeId，临时探针 ProbeId 为 null。</summary>
    public async Task<Guid> TriggerManualAsync(
        ProbeConfiguration probe,
        string? probeId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("手动检测触发：{ProbeId}。", probeId ?? probe.Name);
        return await _executor.TriggerManualAsync(probe, probeId, cancellationToken);
    }

    public async Task<CapacityNoticeState> GetCapacityNoticeAsync(string planId, CancellationToken cancellationToken = default)
        => await _capacityNotices.EvaluateAsync(planId, cancellationToken);
}
