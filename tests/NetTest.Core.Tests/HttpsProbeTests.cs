using System.Net;
using Microsoft.AspNetCore.Http;
using NetTest.Core.Configuration;
using NetTest.Core.Enums;
using NetTest.Core.Probes;

namespace NetTest.Core.Tests;

public class HttpsProbeTests : IAsyncLifetime
{
    private LocalHttpsServer? _server;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    private static ProbeExecutionContext CreateContext(HttpsProbeConfiguration config)
    {
        return new ProbeExecutionContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https-1",
            TriggerKind.Scheduled,
            new ProbeConfigurationSnapshot(ProbeType.Https, config),
            NetworkAddressFamily.IPv4,
            IPAddress.Loopback,
            TimeProvider.System);
    }

    private static HttpsProbeConfiguration CreateConfig(string url, long? maxResponseBytes = null, int maxRedirects = 5)
    {
        return new HttpsProbeConfiguration
        {
            Url = url,
            Proxy = new ProxyConfiguration { Mode = ProxyMode.Direct },
            TimeoutMs = 15000,
            MaxRedirects = maxRedirects,
            MaxResponseBytes = maxResponseBytes ?? 1024 * 1024,
            AllowInvalidCertificate = true,
        };
    }

    [Fact]
    public async Task Success_ReportsAllPhasesAndCertificate()
    {
        _server = new LocalHttpsServer(async context =>
        {
            await context.Response.WriteAsync("hello world");
        });

        var probe = new HttpsProbe();
        ProbeMeasurement measurement = await probe.ExecuteAsync(CreateContext(CreateConfig(_server.BaseUrl)), CancellationToken.None);

        Assert.True(measurement.IsComplete);
        Assert.Equal(ProbeOutcome.Success, measurement.Outcome);
        Assert.Equal(ProbeMetrics.SchemaVersion, measurement.MetricsSchemaVersion);

        var metrics = Assert.IsType<HttpsMetricsV1>(measurement.Metrics);
        Assert.Equal(200, metrics.StatusCode);
        Assert.NotNull(metrics.DnsMs);
        Assert.NotNull(metrics.TcpConnectMs);
        Assert.NotNull(metrics.TlsHandshakeMs);
        Assert.NotNull(metrics.TimeToFirstByteMs);
        Assert.NotNull(metrics.DownloadMs);
        Assert.NotNull(metrics.TotalMs);
        Assert.Equal(11, metrics.BytesRead);
        Assert.False(metrics.ResponseLimitReached);
        Assert.NotNull(metrics.CertificateExpiresAtUtc);
        Assert.True(metrics.CertificateInvalid); // allowInvalidCertificate=true 时标记
    }

    [Fact]
    public async Task Redirect_FollowsChainAndRecordsSteps()
    {
        _server = new LocalHttpsServer(async context =>
        {
            if (context.Request.Path == "/redirect")
            {
                context.Response.StatusCode = 302;
                context.Response.Headers.Location = "/final";
                return;
            }

            context.Response.StatusCode = 200;
            await context.Response.WriteAsync("final");
        });

        var probe = new HttpsProbe();
        string url = _server.BaseUrl.TrimEnd('/') + "/redirect";
        ProbeMeasurement measurement = await probe.ExecuteAsync(CreateContext(CreateConfig(url)), CancellationToken.None);

        Assert.True(measurement.IsComplete);
        Assert.Equal(ProbeOutcome.Success, measurement.Outcome);
        var metrics = Assert.IsType<HttpsMetricsV1>(measurement.Metrics);
        Assert.Single(metrics.Redirects);
        Assert.Equal(302, metrics.Redirects[0].StatusCode);
        Assert.Equal("/final", metrics.Redirects[0].Location);
        Assert.EndsWith("/final", metrics.FinalUri);
    }

    [Fact]
    public async Task TooManyRedirects_ReturnsHttpError()
    {
        _server = new LocalHttpsServer(async context =>
        {
            context.Response.StatusCode = 302;
            context.Response.Headers.Location = "/loop";
            await Task.CompletedTask;
        });

        var probe = new HttpsProbe();
        string url = _server.BaseUrl.TrimEnd('/') + "/loop";
        ProbeMeasurement measurement = await probe.ExecuteAsync(CreateContext(CreateConfig(url, maxRedirects: 0)), CancellationToken.None);

        Assert.True(measurement.IsComplete);
        Assert.Equal(ProbeOutcome.HttpError, measurement.Outcome);
        Assert.Equal("TooManyRedirects", measurement.ErrorCode);
    }

    [Fact]
    public async Task ResponseLimit_StopsReadingAndFlagsLimit()
    {
        _server = new LocalHttpsServer(async context =>
        {
            byte[] body = new byte[10 * 1024];
            await context.Response.Body.WriteAsync(body);
        });

        var probe = new HttpsProbe();
        ProbeMeasurement measurement = await probe.ExecuteAsync(
            CreateContext(CreateConfig(_server.BaseUrl, maxResponseBytes: 2048)),
            CancellationToken.None);

        Assert.True(measurement.IsComplete);
        Assert.Equal(ProbeOutcome.ResponseLimitExceeded, measurement.Outcome);
        var metrics = Assert.IsType<HttpsMetricsV1>(measurement.Metrics);
        Assert.True(metrics.ResponseLimitReached);
        Assert.True(metrics.BytesRead >= 2048);
    }

    [Fact]
    public async Task Http4xx_ReturnsHttpError()
    {
        _server = new LocalHttpsServer(context =>
        {
            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        });

        var probe = new HttpsProbe();
        ProbeMeasurement measurement = await probe.ExecuteAsync(CreateContext(CreateConfig(_server.BaseUrl)), CancellationToken.None);

        Assert.True(measurement.IsComplete);
        Assert.Equal(ProbeOutcome.HttpError, measurement.Outcome);
        Assert.Equal("Http404", measurement.ErrorCode);
    }

    [Fact]
    public async Task SchedulerCancellation_ReturnsPartialMetrics()
    {
        _server = new LocalHttpsServer(async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
        });

        var probe = new HttpsProbe();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        ProbeMeasurement measurement = await probe.ExecuteAsync(CreateContext(CreateConfig(_server.BaseUrl)), cts.Token);

        Assert.False(measurement.IsComplete);
        var metrics = Assert.IsType<HttpsMetricsV1>(measurement.Metrics);
        Assert.NotNull(metrics.TotalMs);
    }

    [Fact]
    public async Task UnreachablePort_ReturnsConnectionRefused()
    {
        // 找一个未监听端口
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var probe = new HttpsProbe();
        ProbeMeasurement measurement = await probe.ExecuteAsync(
            CreateContext(CreateConfig($"https://127.0.0.1:{port}/")),
            CancellationToken.None);

        Assert.True(measurement.IsComplete);
        Assert.Equal(ProbeOutcome.ConnectionRefused, measurement.Outcome);
    }

    [Fact]
    public async Task QueryInUrl_IsStrippedFromFinalUri()
    {
        _server = new LocalHttpsServer(async context =>
        {
            await context.Response.WriteAsync("ok");
        });

        var probe = new HttpsProbe();
        string url = _server.BaseUrl.TrimEnd('/') + "/probe?token=secret";
        ProbeMeasurement measurement = await probe.ExecuteAsync(CreateContext(CreateConfig(url)), CancellationToken.None);

        Assert.True(measurement.IsComplete);
        Assert.Equal(ProbeOutcome.Success, measurement.Outcome);
        var metrics = Assert.IsType<HttpsMetricsV1>(measurement.Metrics);
        // SystemDesign 10：敏感查询参数不得进入结果；finalUri 只保留 scheme/host/port/path。
        Assert.DoesNotContain("?", metrics.FinalUri);
        Assert.DoesNotContain("token", metrics.FinalUri);
        Assert.EndsWith("/probe", metrics.FinalUri);
    }

    [Fact]
    public async Task RedirectLocationWithQuery_IsStripped()
    {
        _server = new LocalHttpsServer(async context =>
        {
            if (context.Request.Path == "/redirect")
            {
                context.Response.StatusCode = 302;
                context.Response.Headers.Location = "/final?token=secret";
                return;
            }

            context.Response.StatusCode = 200;
            await context.Response.WriteAsync("final");
        });

        var probe = new HttpsProbe();
        string url = _server.BaseUrl.TrimEnd('/') + "/redirect";
        ProbeMeasurement measurement = await probe.ExecuteAsync(CreateContext(CreateConfig(url)), CancellationToken.None);

        Assert.True(measurement.IsComplete);
        Assert.Equal(ProbeOutcome.Success, measurement.Outcome);
        var metrics = Assert.IsType<HttpsMetricsV1>(measurement.Metrics);
        Assert.Single(metrics.Redirects);
        Assert.Equal("/final", metrics.Redirects[0].Location);
        Assert.DoesNotContain("?", metrics.Redirects[0].Location);
        Assert.EndsWith("/final", metrics.FinalUri);
        Assert.DoesNotContain("token", metrics.FinalUri);
    }
}
