using Microsoft.EntityFrameworkCore;
using NetTest.Core.Enums;
using NetTest.Core.Storage;
using NetTest.Data.Entities;
using NetTest.Data.Persistence;

namespace NetTest.Data.Tests;

/// <summary>GetHistoryExportBatchAsync 游标分块导出测试（TechSpec 7.5 流式生成的数据层）。</summary>
public class HistoryExportQueryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly IDbContextFactory<NetTestDbContext> _factory;

    public HistoryExportQueryTests()
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

    private async Task SeedAsync(int executionCount, DateTime startUtc)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync();
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
            StartedAtUtc = startUtc,
            CreatedAtUtc = startUtc,
        });

        var executions = new List<ProbeExecution>();
        for (int i = 0; i < executionCount; i++)
        {
            // 每两个 Execution 共享同一 CreatedAtUtc，验证双键游标 (CreatedAtUtc, Id) 边界。
            DateTime created = startUtc.AddMinutes(i / 2);
            executions.Add(new ProbeExecution
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                ProbeId = "probe-1",
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
                StartedAtUtc = created,
                CompletedAtUtc = created,
                CreatedAtUtc = created,
            });
        }

        context.ProbeExecutions.AddRange(executions);
        await context.SaveChangesAsync();
    }

    private static async Task<List<HistoryItem>> DrainAllAsync(NetTestQueries queries, HistoryExportQuery first, CancellationToken ct)
    {
        var all = new List<HistoryItem>();
        HistoryExportQuery query = first;
        while (true)
        {
            HistoryExportBatch batch = await queries.GetHistoryExportBatchAsync(query, ct);
            all.AddRange(batch.Items);
            if (!batch.HasMore)
            {
                return all;
            }

            HistoryItem last = batch.Items[^1];
            query = query with
            {
                AfterCreatedAtUtc = last.CreatedAtUtc,
                AfterId = last.ExecutionId,
            };
        }
    }

    [Fact]
    public async Task Batches_MergeToSameResultAsSingleBatch()
    {
        await TestDb.InitializeAsync(_factory);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(executionCount: 25, start);

        var queries = new NetTestQueries(_factory);
        DateTime end = start.AddHours(1);

        List<HistoryItem> batched = await DrainAllAsync(
            queries,
            new HistoryExportQuery(start, end, null, null, null, null, null, null, null, BatchSize: 10),
            CancellationToken.None);
        HistoryExportBatch single = await queries.GetHistoryExportBatchAsync(
            new HistoryExportQuery(start, end, null, null, null, null, null, null, null, BatchSize: 1000),
            CancellationToken.None);

        Assert.Equal(25, batched.Count);
        Assert.Equal(25, single.Items.Count);
        Assert.False(single.HasMore);
        Assert.Equal(single.Items.Select(i => i.ExecutionId), batched.Select(i => i.ExecutionId));
    }

    [Fact]
    public async Task HasMore_IsTrueWhenMoreRowsExist()
    {
        await TestDb.InitializeAsync(_factory);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(executionCount: 25, start);

        var queries = new NetTestQueries(_factory);
        DateTime end = start.AddHours(1);

        HistoryExportBatch exact = await queries.GetHistoryExportBatchAsync(
            new HistoryExportQuery(start, end, null, null, null, null, null, null, null, BatchSize: 25),
            CancellationToken.None);
        HistoryExportBatch shortBatch = await queries.GetHistoryExportBatchAsync(
            new HistoryExportQuery(start, end, null, null, null, null, null, null, null, BatchSize: 24),
            CancellationToken.None);

        Assert.False(exact.HasMore);
        Assert.Equal(25, exact.Items.Count);
        Assert.True(shortBatch.HasMore);
        Assert.Equal(24, shortBatch.Items.Count);
    }

    [Fact]
    public async Task Cursor_DoesNotSkipOrDuplicateRowsWithSharedTimestamp()
    {
        await TestDb.InitializeAsync(_factory);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(executionCount: 6, start);

        var queries = new NetTestQueries(_factory);
        DateTime end = start.AddHours(1);

        // 6 条 Execution 只占用 3 个时间戳，BatchSize=1 强制每一批都落在同时间戳边界内。
        List<HistoryItem> items = await DrainAllAsync(
            queries,
            new HistoryExportQuery(start, end, null, null, null, null, null, null, null, BatchSize: 1),
            CancellationToken.None);

        Assert.Equal(6, items.Count);
        Assert.Equal(items.Count, items.Select(i => i.ExecutionId).Distinct().Count());
        // 与单批结果完全一致（含顺序）。
        HistoryExportBatch single = await queries.GetHistoryExportBatchAsync(
            new HistoryExportQuery(start, end, null, null, null, null, null, null, null, BatchSize: 1000),
            CancellationToken.None);
        Assert.Equal(single.Items.Select(i => i.ExecutionId), items.Select(i => i.ExecutionId));
    }

    [Fact]
    public async Task Filters_ApplyToExportBatches()
    {
        await TestDb.InitializeAsync(_factory);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(executionCount: 5, start);

        var queries = new NetTestQueries(_factory);
        DateTime end = start.AddHours(1);

        HistoryExportBatch none = await queries.GetHistoryExportBatchAsync(
            new HistoryExportQuery(start, end, "plan-unknown", null, null, null, null, null, null, BatchSize: 100),
            CancellationToken.None);
        HistoryExportBatch statusFiltered = await queries.GetHistoryExportBatchAsync(
            new HistoryExportQuery(start, end, null, null, null, ExecutionStatus.Pending, null, null, null, BatchSize: 100),
            CancellationToken.None);

        Assert.Empty(none.Items);
        Assert.False(none.HasMore);
        Assert.Empty(statusFiltered.Items);
    }
}
