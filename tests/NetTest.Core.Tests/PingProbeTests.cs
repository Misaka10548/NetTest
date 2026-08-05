using System.Net;
using NetTest.Core.Configuration;
using NetTest.Core.Enums;
using NetTest.Core.Probes;

namespace NetTest.Core.Tests;

/// <summary>
/// Ping 探针测试。ICMP 回环在 Windows 上无需管理员权限，但部分受限环境可能禁用，
/// 因此归入 RequiresNetwork 分类（TechSpec 11.4：需要系统能力的测试单独分类且默认跳过）。
/// </summary>
[Trait("Category", "RequiresNetwork")]
public class PingProbeTests
{
    private static ProbeExecutionContext CreateContext(PingProbeConfiguration config)
    {
        return new ProbeExecutionContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "ping-1",
            TriggerKind.Scheduled,
            new ProbeConfigurationSnapshot(ProbeType.Ping, config),
            NetworkAddressFamily.IPv4,
            IPAddress.Loopback,
            TimeProvider.System);
    }

    [Fact]
    public async Task Loopback_SucceedsWithStats()
    {
        var probe = new PingProbe();
        var config = new PingProbeConfiguration
        {
            Target = "127.0.0.1",
            PacketCount = 4,
            TimeoutMs = 3000,
            PayloadSize = 32,
        };

        ProbeMeasurement measurement = await probe.ExecuteAsync(CreateContext(config), CancellationToken.None);

        Assert.True(measurement.IsComplete);
        Assert.Equal(ProbeOutcome.Success, measurement.Outcome);
        var metrics = Assert.IsType<PingMetricsV1>(measurement.Metrics);
        Assert.Equal(4, metrics.Sent);
        Assert.Equal(4, metrics.Received);
        Assert.Equal(0, metrics.LossPercent);
        Assert.NotNull(metrics.RttMinMs);
        Assert.NotNull(metrics.RttAverageMs);
        Assert.NotNull(metrics.RttMaxMs);
        Assert.NotNull(metrics.JitterMs); // 4 个成功样本 → jitter 可计算
        Assert.Equal(4, metrics.Samples.Count);
    }
}
