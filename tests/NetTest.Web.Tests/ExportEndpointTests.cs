using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTest.Core.Enums;
using NetTest.Data.Entities;
using NetTest.Data.Persistence;
using NetTest.Web.Services;

namespace NetTest.Web.Tests;

/// <summary>
/// /exports/history.csv 集成测试（TechSpec 7.5）。
/// 共享单一 WebApplicationFactory：Program 启动时获取基于程序目录的单实例互斥锁，
/// 同一测试进程只能成功启动一次宿主，因此认证切换等场景不在此自动覆盖。
/// </summary>
public sealed class ExportEndpointTests : IClassFixture<ExportEndpointFixture>
{
    private readonly ExportEndpointFixture _fixture;

    public ExportEndpointTests(ExportEndpointFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HistoryPage_PaginationUsesButtons_NotHashAnchors()
    {
        // 回归测试：分页链接曾用 <a href="#">，配合 <base href="/"> 解析为根路径，
        // 点击后被导航到 dashboard 导致无法翻页；必须渲染为 button（TechSpec 9.2 历史筛选）。
        DateTime now = DateTime.UtcNow;
        await _fixture.SeedExecutionsAsync("probe-pager", now.AddHours(-1), count: 60);

        using HttpResponseMessage response = await _fixture.Client.GetAsync("/history");
        response.EnsureSuccessStatusCode();

        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<button type=\"button\" class=\"page-link\"", html);
        Assert.DoesNotContain("<a class=\"page-link\" href=\"#\">", html);
    }

    [Fact]
    public async Task Export_ReturnsCsvHeaderAndEscapedMetricsJson()
    {
        DateTime now = DateTime.UtcNow;
        string probeId = "probe-esc";
        string metricsJson = "{\"a\":1,\"b\":\"x,y\"}";
        await _fixture.SeedExecutionAsync(probeId, now, metricsJson);

        using HttpResponseMessage response = await _fixture.Client.GetAsync(
            BuildUrl(now.AddMinutes(-5), now.AddMinutes(5)) + "&probeId=probe-esc");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/csv; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("nettest-history.csv", response.Content.Headers.ContentDisposition?.ToString());

        string body = await response.Content.ReadAsStringAsync();
        string[] lines = SplitLines(body);
        Assert.Equal(2, lines.Length);
        Assert.Equal(CsvFormatter.Header, lines[0]);
        Assert.Contains(probeId, lines[1]);
        // metricsJson 单列：逗号与引号被转义（RFC 4180 双写引号）。
        Assert.Contains("x,y\"\"}", lines[1]);
    }

    [Fact]
    public async Task Export_MissingStart_ReturnsBadRequest()
    {
        using HttpResponseMessage response = await _fixture.Client.GetAsync(
            "/exports/history.csv?end=2026-01-01T00%3A00%3A00Z");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Export_EndBeforeStart_ReturnsBadRequest()
    {
        DateTime now = DateTime.UtcNow;
        using HttpResponseMessage response = await _fixture.Client.GetAsync(
            BuildUrl(now.AddHours(1), now));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Export_RangeExceedsRetentionDays_ReturnsBadRequest()
    {
        DateTime now = DateTime.UtcNow;
        // 测试配置 retentionDays=2，3 天窗口必须被拒绝（TechSpec 7.3）。
        using HttpResponseMessage response = await _fixture.Client.GetAsync(
            BuildUrl(now.AddDays(-3), now));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Export_InvalidEnumFilter_ReturnsBadRequest()
    {
        DateTime now = DateTime.UtcNow;
        using HttpResponseMessage response = await _fixture.Client.GetAsync(
            BuildUrl(now.AddMinutes(-5), now.AddMinutes(5)) + "&status=Bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Export_StreamsAllRowsAcrossBatches()
    {
        DateTime now = DateTime.UtcNow;
        string probeId = "probe-batch";
        // 1001 行强制跨过 endpoint 的 1000 行批次边界，验证游标循环完整输出。
        await _fixture.SeedExecutionsAsync(probeId, now.AddSeconds(-1000), count: 1001);

        using HttpResponseMessage response = await _fixture.Client.GetAsync(
            BuildUrl(now.AddMinutes(-30), now.AddMinutes(5)) + "&probeId=probe-batch");
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync();
        string[] lines = SplitLines(body);
        Assert.Equal(1002, lines.Length); // header + 1001 数据行
    }

    [Fact]
    public async Task Export_AppliesFilters()
    {
        DateTime now = DateTime.UtcNow;
        await _fixture.SeedExecutionAsync("probe-included", now.AddSeconds(-10));
        await _fixture.SeedExecutionAsync("probe-excluded", now.AddSeconds(-20));

        using HttpResponseMessage response = await _fixture.Client.GetAsync(
            BuildUrl(now.AddMinutes(-5), now.AddMinutes(5)) + "&probeId=probe-included");
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("probe-included", body);
        Assert.DoesNotContain("probe-excluded", body);
    }

    private static string BuildUrl(DateTime startUtc, DateTime endUtc)
    {
        return "/exports/history.csv?start=" + Uri.EscapeDataString(startUtc.ToString("O"))
            + "&end=" + Uri.EscapeDataString(endUtc.ToString("O"));
    }

    /// <summary>StreamWriter 在 Windows 输出 CRLF 行尾，统一拆行避免断言差异。</summary>
    private static string[] SplitLines(string body) => body.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// 共享 fixture：清理并预写测试输出目录下的 Config/nettest.json（无密码、retentionDays=2），
/// 创建唯一的 WebApplicationFactory，预建数据库 schema（factory 模式不执行 Program 的迁移段）。
/// </summary>
public sealed class ExportEndpointFixture : IDisposable
{
    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Client { get; }
    public string DatabasePath { get; }

    private readonly string _baseDir;
    private readonly IDbContextFactory<NetTestDbContext> _dbFactory;

    public ExportEndpointFixture()
    {
        _baseDir = AppContext.BaseDirectory;
        string configDir = Path.Combine(_baseDir, "Config");
        string dataDir = Path.Combine(_baseDir, "Data");

        DeleteIfExists(configDir);
        DeleteIfExists(dataDir);
        Directory.CreateDirectory(configDir);

        File.WriteAllText(
            Path.Combine(configDir, "nettest.json"),
            """
            {
              "schemaVersion": 1,
              "host": { "urls": ["http://127.0.0.1:5000"], "password": null },
              "storage": { "databasePath": "Data/nettest.db", "retentionDays": 2, "chartMaxPointsPerSeries": 2000 },
              "scheduler": { "maxConcurrency": 10, "queueCapacity": 256, "capacityWarningWindow": 10, "capacityWarningRatio": 0.6 },
              "logging": { "minimumLevel": "Information", "directory": "Data/Logs", "fileSizeLimitMiB": 10, "retainedDays": 14 },
              "plans": [ { "id": "default-five-minutes", "name": "默认五分钟计划", "cron": "*/5 * * * *", "enabled": false } ],
              "probes": { "ping": [], "tracert": [], "dns": [], "https": [] }
            }
            """,
            new UTF8Encoding(false));

        Factory = new WebApplicationFactory<Program>();
        Client = Factory.CreateClient();

        DatabasePath = Path.Combine(dataDir, "nettest.db");
        _dbFactory = CreateDbContextFactory(DatabasePath);
        using (NetTestDbContext context = _dbFactory.CreateDbContext())
        {
            DatabaseInitializer.InitializeAsync(context).GetAwaiter().GetResult();
        }
    }

    public async Task SeedExecutionAsync(string probeId, DateTime atUtc, string? metricsJson = null)
    {
        await using NetTestDbContext context = await _dbFactory.CreateDbContextAsync();
        var runId = Guid.NewGuid();
        context.ProbeRuns.Add(new ProbeRun
        {
            Id = runId,
            PlanId = "plan-1",
            PlanNameSnapshot = "计划一",
            TriggerKind = TriggerKind.Scheduled,
            ConfigurationRevision = "rev",
            Status = ExecutionStatus.Completed,
            CancellationReason = CancellationReason.None,
            StartedAtUtc = atUtc,
            CreatedAtUtc = atUtc,
        });
        context.ProbeExecutions.Add(new ProbeExecution
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            ProbeId = probeId,
            ProbeNameSnapshot = "探针",
            ProbeType = ProbeType.Ping,
            PlanId = "plan-1",
            TriggerKind = TriggerKind.Scheduled,
            AddressFamily = NetworkAddressFamily.IPv4,
            ConfigurationSchemaVersion = 1,
            ConfigurationSnapshotJson = "{}",
            Status = ExecutionStatus.Completed,
            Outcome = ProbeOutcome.Success,
            CancellationReason = CancellationReason.None,
            PrimaryLatencyMs = 10,
            MetricsSchemaVersion = 1,
            MetricsJson = metricsJson,
            StartedAtUtc = atUtc,
            CompletedAtUtc = atUtc,
            CreatedAtUtc = atUtc,
        });
        await context.SaveChangesAsync();
    }

    public async Task SeedExecutionsAsync(string probeId, DateTime firstAtUtc, int count)
    {
        await using NetTestDbContext context = await _dbFactory.CreateDbContextAsync();
        var runId = Guid.NewGuid();
        context.ProbeRuns.Add(new ProbeRun
        {
            Id = runId,
            PlanId = "plan-1",
            PlanNameSnapshot = "计划一",
            TriggerKind = TriggerKind.Scheduled,
            ConfigurationRevision = "rev",
            Status = ExecutionStatus.Completed,
            CancellationReason = CancellationReason.None,
            StartedAtUtc = firstAtUtc,
            CreatedAtUtc = firstAtUtc,
        });

        var executions = new List<ProbeExecution>();
        for (int i = 0; i < count; i++)
        {
            DateTime at = firstAtUtc.AddSeconds(i);
            executions.Add(new ProbeExecution
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                ProbeId = probeId,
                ProbeNameSnapshot = "探针",
                ProbeType = ProbeType.Ping,
                PlanId = "plan-1",
                TriggerKind = TriggerKind.Scheduled,
                AddressFamily = NetworkAddressFamily.IPv4,
                ConfigurationSchemaVersion = 1,
                ConfigurationSnapshotJson = "{}",
                Status = ExecutionStatus.Completed,
                Outcome = ProbeOutcome.Success,
                CancellationReason = CancellationReason.None,
                PrimaryLatencyMs = 10,
                MetricsSchemaVersion = 1,
                StartedAtUtc = at,
                CompletedAtUtc = at,
                CreatedAtUtc = at,
            });
        }

        context.ProbeExecutions.AddRange(executions);
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        Factory.Dispose();
        Client.Dispose();
        DeleteIfExists(Path.Combine(_baseDir, "Config"));
        DeleteIfExists(Path.Combine(_baseDir, "Data"));
    }

    private static IDbContextFactory<NetTestDbContext> CreateDbContextFactory(string databasePath)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddPooledDbContextFactory<NetTestDbContext>(options =>
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString())
                .AddInterceptors(new NetTestConnectionInterceptor()));
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<NetTestDbContext>>();
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // Serilog 可能仍持有 Data/Logs 文件句柄；清理失败不影响测试。
            }
        }
    }
}
