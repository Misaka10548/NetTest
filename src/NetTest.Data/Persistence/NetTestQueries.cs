using Microsoft.EntityFrameworkCore;
using NetTest.Core.Enums;
using NetTest.Core.Storage;

namespace NetTest.Data.Persistence;

/// <summary>INetTestQueries 的 EF Core 实现：历史、详情、趋势、仪表盘与容量样本（TechSpec 7.3）。</summary>
public sealed class NetTestQueries : INetTestQueries
{
    private readonly IDbContextFactory<NetTestDbContext> _factory;

    public NetTestQueries(IDbContextFactory<NetTestDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<HistoryPage> GetHistoryPageAsync(HistoryQuery query, CancellationToken ct)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync(ct);

        IQueryable<Entities.ProbeExecution> baseQuery = ApplyHistoryFilters(
            context.ProbeExecutions,
            query.StartUtc,
            query.EndUtc,
            query.PlanId,
            query.ProbeId,
            query.AddressFamily,
            query.Status,
            query.TriggerKind);

        int total = await baseQuery.CountAsync(ct);

        List<Entities.ProbeExecution> rows = await baseQuery
            .OrderByDescending(e => e.CreatedAtUtc)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        Dictionary<Guid, string?> runPlanNames = rows.Count > 0
            ? await context.ProbeRuns
                .Where(r => rows.Select(row => row.RunId).Contains(r.Id))
                .Select(r => new { r.Id, r.PlanNameSnapshot })
                .ToDictionaryAsync(r => r.Id, r => r.PlanNameSnapshot, ct)
            : new Dictionary<Guid, string?>();

        var items = rows.Select(row => new HistoryItem(
            row.Id,
            row.RunId,
            row.ProbeId,
            row.ProbeNameSnapshot,
            row.ProbeType,
            row.PlanId,
            runPlanNames.GetValueOrDefault(row.RunId),
            row.TriggerKind,
            row.AddressFamily,
            row.Status,
            row.Outcome,
            row.CancellationReason,
            row.PrimaryLatencyMs,
            row.StartedAtUtc,
            row.CompletedAtUtc,
            row.DurationMs,
            row.CreatedAtUtc,
            row.ErrorCode,
            row.MetricsJson)).ToList();

        return new HistoryPage(total, items);
    }

    public async Task<HistoryExportBatch> GetHistoryExportBatchAsync(HistoryExportQuery query, CancellationToken ct)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync(ct);

        IQueryable<Entities.ProbeExecution> baseQuery = ApplyHistoryFilters(
            context.ProbeExecutions,
            query.StartUtc,
            query.EndUtc,
            query.PlanId,
            query.ProbeId,
            query.AddressFamily,
            query.Status,
            query.TriggerKind);

        if (query.AfterCreatedAtUtc is not null && query.AfterId is not null)
        {
            DateTime afterCreatedAtUtc = query.AfterCreatedAtUtc.Value;
            Guid afterId = query.AfterId.Value;
            baseQuery = baseQuery.Where(e => e.CreatedAtUtc < afterCreatedAtUtc
                || (e.CreatedAtUtc == afterCreatedAtUtc && e.Id < afterId));
        }

        List<Entities.ProbeExecution> rows = await baseQuery
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .Take(query.BatchSize + 1)
            .ToListAsync(ct);

        bool hasMore = rows.Count > query.BatchSize;
        List<Entities.ProbeExecution> pageRows = hasMore ? rows.Take(query.BatchSize).ToList() : rows;

        Dictionary<Guid, string?> runPlanNames = pageRows.Count > 0
            ? await context.ProbeRuns
                .Where(r => pageRows.Select(row => row.RunId).Contains(r.Id))
                .Select(r => new { r.Id, r.PlanNameSnapshot })
                .ToDictionaryAsync(r => r.Id, r => r.PlanNameSnapshot, ct)
            : new Dictionary<Guid, string?>();

        var items = pageRows.Select(row => new HistoryItem(
            row.Id,
            row.RunId,
            row.ProbeId,
            row.ProbeNameSnapshot,
            row.ProbeType,
            row.PlanId,
            runPlanNames.GetValueOrDefault(row.RunId),
            row.TriggerKind,
            row.AddressFamily,
            row.Status,
            row.Outcome,
            row.CancellationReason,
            row.PrimaryLatencyMs,
            row.StartedAtUtc,
            row.CompletedAtUtc,
            row.DurationMs,
            row.CreatedAtUtc,
            row.ErrorCode,
            row.MetricsJson)).ToList();

        return new HistoryExportBatch(items, hasMore);
    }

    private static IQueryable<Entities.ProbeExecution> ApplyHistoryFilters(
        IQueryable<Entities.ProbeExecution> source,
        DateTime startUtc,
        DateTime endUtc,
        string? planId,
        string? probeId,
        NetworkAddressFamily? addressFamily,
        ExecutionStatus? status,
        TriggerKind? triggerKind)
    {
        IQueryable<Entities.ProbeExecution> query = source
            .Where(e => e.CreatedAtUtc >= startUtc && e.CreatedAtUtc <= endUtc);

        if (planId is not null)
        {
            query = query.Where(e => e.PlanId == planId);
        }

        if (probeId is not null)
        {
            query = query.Where(e => e.ProbeId == probeId);
        }

        if (addressFamily is not null)
        {
            query = query.Where(e => e.AddressFamily == addressFamily);
        }

        if (status is not null)
        {
            query = query.Where(e => e.Status == status);
        }

        if (triggerKind is not null)
        {
            query = query.Where(e => e.TriggerKind == triggerKind);
        }

        return query;
    }

    public async Task<RunDetail?> GetRunDetailAsync(Guid runId, CancellationToken ct)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync(ct);
        Entities.ProbeRun? run = await context.ProbeRuns
            .Include(r => r.Executions)
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return null;
        }

        var executions = run.Executions
            .OrderBy(e => e.CreatedAtUtc)
            .Select(e => new ExecutionDetail(
                e.Id,
                e.ProbeId,
                e.ProbeNameSnapshot,
                e.ProbeType,
                e.GroupIdSnapshot,
                e.PlanId,
                e.TriggerKind,
                e.AddressFamily,
                e.ResolvedAddress,
                e.ConfigurationSchemaVersion,
                e.ConfigurationSnapshotJson,
                e.Status,
                e.Outcome,
                e.CancellationReason,
                e.PrimaryLatencyMs,
                e.MetricsSchemaVersion,
                e.MetricsJson,
                e.ErrorCode,
                e.ErrorMessage,
                e.StartedAtUtc,
                e.CompletedAtUtc,
                e.DurationMs,
                e.CreatedAtUtc))
            .ToList();

        return new RunDetail(
            run.Id,
            run.PlanId,
            run.PlanNameSnapshot,
            run.TriggerKind,
            run.ConfigurationRevision,
            run.Status,
            run.CancellationReason,
            run.StartedAtUtc,
            run.CompletedAtUtc,
            run.CreatedAtUtc,
            executions);
    }

    public async Task<IReadOnlyList<TrendSeries>> GetTrendSeriesAsync(TrendQuery query, CancellationToken ct)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync(ct);

        IQueryable<Entities.ProbeExecution> baseQuery = context.ProbeExecutions
            .Where(e => e.ProbeId == query.ProbeId)
            .Where(e => e.CompletedAtUtc >= query.StartUtc && e.CompletedAtUtc <= query.EndUtc);

        if (query.AddressFamily is not null)
        {
            baseQuery = baseQuery.Where(e => e.AddressFamily == query.AddressFamily);
        }

        if (!query.IncludeManual)
        {
            baseQuery = baseQuery.Where(e => e.TriggerKind == TriggerKind.Scheduled);
        }

        int rawCount = await baseQuery.CountAsync(ct);
        if (rawCount == 0)
        {
            return [new TrendSeries(query.ProbeId, query.AddressFamily, [])];
        }

        if (rawCount <= query.MaxPoints)
        {
            List<Entities.ProbeExecution> raw = await baseQuery
                .OrderBy(e => e.CompletedAtUtc)
                .ToListAsync(ct);

            var points = raw.Select(e =>
            {
                bool complete = e.Status == ExecutionStatus.Completed;
                return new TrendPoint(
                    e.CompletedAtUtc!.Value,
                    complete ? 1 : 0,
                    complete ? 0 : 1,
                    complete ? e.PrimaryLatencyMs : null,
                    complete && e.PrimaryLatencyMs is not null ? (double)e.PrimaryLatencyMs.Value : null,
                    complete ? e.PrimaryLatencyMs : null);
            }).ToList();

            return [new TrendSeries(query.ProbeId, query.AddressFamily, points)];
        }

        // 超过上限：服务端按等宽时间桶聚合（完整结果算 count/min/avg/max，部分结果只计 partialCount）。
        var (points2, _) = await AggregateByBucketAsync(baseQuery, query.StartUtc, query.EndUtc, query.MaxPoints, ct);
        return [new TrendSeries(query.ProbeId, query.AddressFamily, points2)];
    }

    private static async Task<(List<TrendPoint> Points, DateTime EndUtc)> AggregateByBucketAsync(
        IQueryable<Entities.ProbeExecution> query,
        DateTime startUtc,
        DateTime endUtc,
        int maxPoints,
        CancellationToken ct)
    {
        List<Entities.ProbeExecution> rows = await query
            .Select(e => new Entities.ProbeExecution
            {
                Id = e.Id,
                Status = e.Status,
                PrimaryLatencyMs = e.PrimaryLatencyMs,
                CompletedAtUtc = e.CompletedAtUtc,
            })
            .ToListAsync(ct);

        long rangeTicks = endUtc.Ticks - startUtc.Ticks;
        int bucketCount = (int)Math.Ceiling(rangeTicks / (double)maxPoints);
        long bucketWidthTicks = Math.Max(1, rangeTicks / bucketCount);

        var buckets = new SortedDictionary<long, List<Entities.ProbeExecution>>();
        foreach (Entities.ProbeExecution row in rows)
        {
            long index = (row.CompletedAtUtc!.Value.Ticks - startUtc.Ticks) / bucketWidthTicks;
            if (!buckets.TryGetValue(index, out List<Entities.ProbeExecution>? list))
            {
                list = new List<Entities.ProbeExecution>();
                buckets[index] = list;
            }

            list.Add(row);
        }

        var points = new List<TrendPoint>();
        foreach ((long index, List<Entities.ProbeExecution> group) in buckets)
        {
            long bucketStartTicks = startUtc.Ticks + index * bucketWidthTicks;
            DateTime center = new(bucketStartTicks + bucketWidthTicks / 2, DateTimeKind.Utc);

            var complete = group.Where(e => e.Status == ExecutionStatus.Completed && e.PrimaryLatencyMs is not null)
                .Select(e => e.PrimaryLatencyMs!.Value)
                .ToList();
            int partialCount = group.Count(e => e.Status != ExecutionStatus.Completed);

            points.Add(new TrendPoint(
                center,
                complete.Count,
                partialCount,
                complete.Count > 0 ? complete.Min() : null,
                complete.Count > 0 ? complete.Average() : null,
                complete.Count > 0 ? complete.Max() : null));
        }

        return (points, endUtc);
    }

    public async Task<IReadOnlyList<RecentRunSummary>> GetRecentRunsAsync(string? planId, int limit, CancellationToken ct)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync(ct);

        IQueryable<Entities.ProbeRun> query = context.ProbeRuns;
        if (planId is not null)
        {
            query = query.Where(r => r.PlanId == planId);
        }

        return await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(limit)
            .Select(r => new RecentRunSummary(
                r.Id,
                r.PlanId,
                r.PlanNameSnapshot,
                r.TriggerKind,
                r.Status,
                r.CancellationReason,
                r.StartedAtUtc,
                r.CompletedAtUtc,
                r.Executions.Count(e => e.Status == ExecutionStatus.Completed),
                r.Executions.Count))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ProbeOverview>> GetProbeOverviewAsync(CancellationToken ct)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync(ct);

        List<Entities.ProbeExecution> latestPerProbe = await context.ProbeExecutions
            .Where(e => e.ProbeId != null)
            .GroupBy(e => e.ProbeId!)
            .Select(g => g.OrderByDescending(e => e.CreatedAtUtc).First())
            .ToListAsync(ct);

        return latestPerProbe
            .Select(e => new ProbeOverview(
                e.ProbeId!,
                e.ProbeNameSnapshot,
                e.ProbeType,
                e.GroupIdSnapshot,
                e.Status,
                e.Outcome,
                e.CompletedAtUtc,
                e.PrimaryLatencyMs))
            .OrderBy(o => o.ProbeId, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<RunStatusSample>> GetRecentRunStatusesAsync(string planId, int limit, CancellationToken ct)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync(ct);
        return await context.ProbeRuns
            .Where(r => r.PlanId == planId && r.TriggerKind == TriggerKind.Scheduled)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(limit)
            .Select(r => new RunStatusSample(r.Id, r.Status, r.CancellationReason))
            .ToListAsync(ct);
    }
}
