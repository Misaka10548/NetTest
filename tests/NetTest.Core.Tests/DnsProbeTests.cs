using System.Net;
using NetTest.Core.Configuration;
using NetTest.Core.Enums;
using NetTest.Core.Probes;

namespace NetTest.Core.Tests;

public class DnsProbeTests : IDisposable
{
    private readonly LocalDnsServer _server;

    public DnsProbeTests()
    {
        _server = new LocalDnsServer();
        _server.Records["test.example"] = IPAddress.Parse("93.184.216.34");
        _server.NxDomains.Add("missing.example");
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    private static ProbeExecutionContext CreateContext(DnsProbeConfiguration config)
    {
        return new ProbeExecutionContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "dns-1",
            TriggerKind.Scheduled,
            new ProbeConfigurationSnapshot(ProbeType.Dns, config),
            null,
            null,
            TimeProvider.System);
    }

    private DnsProbeConfiguration CreateConfig(string queryName, params string[] recordTypes)
    {
        return new DnsProbeConfiguration
        {
            QueryName = queryName,
            RecordTypes = recordTypes.ToList(),
            Resolver = new DnsResolverConfiguration
            {
                Mode = DnsResolverMode.Custom,
                Addresses = new List<string> { $"127.0.0.1:{_server.Port}" },
            },
            TimeoutMs = 3000,
        };
    }

    [Fact]
    public async Task CustomResolver_ReturnsAnswers()
    {
        var probe = new DnsProbe();
        ProbeMeasurement measurement = await probe.ExecuteAsync(CreateContext(CreateConfig("test.example", "A")), CancellationToken.None);

        Assert.True(measurement.IsComplete);
        Assert.Equal(ProbeOutcome.Success, measurement.Outcome);
        var metrics = Assert.IsType<DnsMetricsV1>(measurement.Metrics);
        Assert.Contains(metrics.Answers, a => a.Type == "A" && a.Value == "93.184.216.34");
        Assert.Equal("NoError", metrics.ResponseCode);
        Assert.NotNull(metrics.ElapsedMs);
    }

    [Fact]
    public async Task NxDomain_ReturnsDnsError()
    {
        var probe = new DnsProbe();
        ProbeMeasurement measurement = await probe.ExecuteAsync(CreateContext(CreateConfig("missing.example", "A")), CancellationToken.None);

        Assert.True(measurement.IsComplete);
        Assert.Equal(ProbeOutcome.DnsError, measurement.Outcome);
        Assert.Equal("NotExistentDomain", measurement.ErrorCode);
    }

    [Fact]
    public async Task CacheIsDisabled_TwoExecutionsBothHitServer()
    {
        var probe = new DnsProbe();
        DnsProbeConfiguration config = CreateConfig("test.example", "A");

        await probe.ExecuteAsync(CreateContext(config), CancellationToken.None);
        await probe.ExecuteAsync(CreateContext(config), CancellationToken.None);

        Assert.Equal(2, _server.QueryCount);
    }

    [Fact]
    public async Task UnreachableResolver_ReturnsNetworkTimeout()
    {
        var probe = new DnsProbe();
        DnsProbeConfiguration config = CreateConfig("test.example", "A");
        // 指向一个无服务端口（ICMP 端口不可达 → 连接级 DnsError）
        config.Resolver.Addresses[0] = "127.0.0.1:1";
        config.TimeoutMs = 800;

        ProbeMeasurement measurement = await probe.ExecuteAsync(CreateContext(config), CancellationToken.None);

        Assert.True(measurement.IsComplete);
        Assert.Equal(ProbeOutcome.DnsError, measurement.Outcome);
    }
}
