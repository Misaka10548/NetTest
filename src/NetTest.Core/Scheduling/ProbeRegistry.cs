using NetTest.Core.Enums;
using NetTest.Core.Probes;

namespace NetTest.Core.Scheduling;

/// <summary>按探针类型解析配置并选择对应 IProbe 实现的注册表。</summary>
public interface IProbeRegistry
{
    IProbe GetProbe(ProbeType type);
}

/// <summary>默认注册表：每种探针类型一个无状态单例。</summary>
public sealed class DefaultProbeRegistry : IProbeRegistry
{
    private readonly IReadOnlyDictionary<ProbeType, IProbe> _probes = new Dictionary<ProbeType, IProbe>
    {
        [ProbeType.Ping] = new PingProbe(),
        [ProbeType.Tracert] = new TracertProbe(),
        [ProbeType.Dns] = new DnsProbe(),
        [ProbeType.Https] = new HttpsProbe(),
    };

    public IProbe GetProbe(ProbeType type) => _probes[type];
}

/// <summary>
/// 同一计划下的异步 gate：串行化触发，合并等待期间重复到达的同计划 tick，只保留最新一次。
/// </summary>
internal sealed class PlanGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private int _pending;

    public async Task RunAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _pending);
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            int current = Interlocked.Exchange(ref _pending, 0);
            if (current > 0)
            {
                await action();
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
