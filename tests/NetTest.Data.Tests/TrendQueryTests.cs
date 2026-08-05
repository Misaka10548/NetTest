using Microsoft.EntityFrameworkCore;
using NetTest.Core.Enums;
using NetTest.Core.Storage;
using NetTest.Data.Entities;
using NetTest.Data.Persistence;

namespace NetTest.Data.Tests;

public class TrendQueryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly IDbContextFactory<NetTestDbContext> _factory;

    public TrendQueryTests()
    {
        (_dir, _dbPath) = TestDb.CreateTemp();
        _factory = TestDb.CreateFactory(_dbPath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task SeedAsync(
        int scheduledCount,
        int manualCount,
        int partialCount,
        DateTime startUtc,
        long latencyMs)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var runId = Guid.NewGuid();
        context.ProbeRuns.Add(new ProbeRun
        {
            Id = runId,
            TriggerKind = TriggerKind.Scheduled,
            ConfigurationRevision = "rev",
            Status = ExecutionStatus.Completed,
            CancellationReason = CancellationReason.None,
            StartedAtUtc = now,
            CreatedAtUtc = now,
        });

        var executions = new List<ProbeExecution>();
        AddRange(scheduledCount, TriggerKind.Scheduled, ExecutionStatus.Completed, ProbeOutcome.Success);
        AddRange(manualCount, TriggerKind.Manual, ExecutionStatus.Completed, ProbeOutcome.Success);
        AddRange(partialCount, TriggerKind.Scheduled, ExecutionStatus.Incomplete, ProbeOutcome.None);

        void AddRange(int count, TriggerKind trigger, ExecutionStatus status, ProbeOutcome outcome)
        {
            for (int i = 0; i < count; i++)
            {
                executions.Add(new ProbeExecution
                {
                    Id = Guid.NewGuid(),
                    RunId = runId,
                    ProbeId = "probe-1",
                    ProbeNameSnapshot = "探针",
                    ProbeType = ProbeType.Ping,
                    PlanId = "plan-1",
                    TriggerKind = trigger,
                    AddressFamily = NetworkAddressFamily.IPv4,
                    ConfigurationSchemaVersion = 1,
                    ConfigurationSnapshotJson = "{}",
                    Status = status,
                    Outcome = outcome,
                    CancellationReason = status == ExecutionStatus.Incomplete ? CancellationReason.SupersededByNextRun : CancellationReason.None,
                    PrimaryLatencyMs = status == ExecutionStatus.Completed ? latencyMs : null,
                    MetricsSchemaVersion = status == ExecutionStatus.Completed ? 1 : 0,
                    StartedAtUtc = startUtc.AddMinutes(i),
                    CompletedAtUtc = startUtc.AddMinutes(i),
                    CreatedAtUtc = startUtc.AddMinutes(i),
                });
            }
        }

        context.ProbeExecutions.AddRange(executions);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Trend_UnderMaxPoints_ReturnsRawPoints()
    {
        await TestDb.InitializeAsync(_factory);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(scheduledCount: 3, manualCount: 0, partialCount: 0, start, latencyMs: 10);

        var queries = new NetTestQueries(_factory);
        IReadOnlyList<TrendSeries> series = await queries.GetTrendSeriesAsync(
            new TrendQuery(start, start.AddHours(1), "probe-1", NetworkAddressFamily.IPv4, IncludeManual: false, MaxPoints: 2000),
            CancellationToken.None);

        TrendSeries trend = Assert.Single(series);
        Assert.Equal(3, trend.Points.Count);
        Assert.All(trend.Points, p => Assert.Equal(1, p.CompleteCount));
        Assert.All(trend.Points, p => Assert.Equal(10, p.MinMs));
    }

    [Fact]
    public async Task Trend_ExcludesManualByDefault()
    {
        await TestDb.InitializeAsync(_factory);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(scheduledCount: 2, manualCount: 2, partialCount: 0, start, latencyMs: 10);

        var queries = new NetTestQueries(_factory);
        IReadOnlyList<TrendSeries> excluded = await queries.GetTrendSeriesAsync(
            new TrendQuery(start, start.AddHours(1), "probe-1", null, IncludeManual: false, MaxPoints: 2000),
            CancellationToken.None);
        Assert.Equal(2, Assert.Single(excluded).Points.Count);

        IReadOnlyList<TrendSeries> included = await queries.GetTrendSeriesAsync(
            new TrendQuery(start, start.AddHours(1), "probe-1", null, IncludeManual: true, MaxPoints: 2000),
            CancellationToken.None);
        Assert.Equal(4, Assert.Single(included).Points.Count);
    }

    [Fact]
    public async Task Trend_PartialResults_DoNotParticipateInAggregation()
    {
        await TestDb.InitializeAsync(_factory);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(scheduledCount: 3, manualCount: 0, partialCount: 2, start, latencyMs: 20);

        var queries = new NetTestQueries(_factory);
        IReadOnlyList<TrendSeries> series = await queries.GetTrendSeriesAsync(
            new TrendQuery(start, start.AddHours(1), "probe-1", null, IncludeManual: false, MaxPoints: 2000),
            CancellationToken.None);

        // 原始模式：3 个完整点 + 2 个部分点
        TrendSeries trend = Assert.Single(series);
        Assert.Equal(5, trend.Points.Count);
        Assert.Equal(2, trend.Points.Count(p => p.PartialCount == 1));
        Assert.Equal(3, trend.Points.Count(p => p.CompleteCount == 1));
    }

    [Fact]
    public async Task Trend_OverMaxPoints_AggregatesIntoTimeBuckets()
    {
        await TestDb.InitializeAsync(_factory);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(scheduledCount: 10, manualCount: 0, partialCount: 0, start, latencyMs: 10);

        var queries = new NetTestQueries(_factory);
        // 10 个点超过 MaxPoints=4：聚合为等宽桶
        IReadOnlyList<TrendSeries> series = await queries.GetTrendSeriesAsync(
            new TrendQuery(start, start.AddMinutes(10), "probe-1", null, IncludeManual: false, MaxPoints: 4),
            CancellationToken.None);

        TrendSeries trend = Assert.Single(series);
        Assert.NotEmpty(trend.Points);
        int totalComplete = trend.Points.Sum(p => p.CompleteCount);
        Assert.Equal(10, totalComplete);
        Assert.All(trend.Points.Where(p => p.CompleteCount > 0), p =>
        {
            Assert.NotNull(p.MinMs);
            Assert.Equal(10, p.MinMs);
            Assert.Equal(10, p.MaxMs);
        });
    }
}
