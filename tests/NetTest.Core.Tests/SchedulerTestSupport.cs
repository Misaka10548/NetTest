using System.Collections.Concurrent;
using NetTest.Core.Enums;
using NetTest.Core.Probes;
using NetTest.Core.Scheduling;
using NetTest.Core.Storage;

namespace NetTest.Core.Tests;

/// <summary>内存 IExecutionStore：记录创建的 Run，供调度测试断言触发行为。</summary>
public sealed class InMemoryExecutionStore : IExecutionStore
{
    public ConcurrentQueue<(Guid RunId, string? PlanId)> CreatedRuns { get; } = new();

    public Task CreateRunAsync(ProbeRunDraft run, IReadOnlyList<ProbeExecutionDraft> executions, CancellationToken ct)
    {
        CreatedRuns.Enqueue((run.RunId, run.PlanId));
        return Task.CompletedTask;
    }

    public Task MarkExecutionRunningAsync(Guid executionId, DateTime startedAtUtc, CancellationToken ct)
        => Task.CompletedTask;

    public Task CompleteExecutionAsync(Guid executionId, ProbeExecutionCompletion completion, CancellationToken ct)
        => Task.CompletedTask;

    public Task CompleteRunAsync(Guid runId, ProbeRunCompletion completion, CancellationToken ct)
        => Task.CompletedTask;

    public Task<IReadOnlyList<ActiveRun>> GetActiveRunsAsync(string planId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ActiveRun>>(Array.Empty<ActiveRun>());

    public Task RecoverInterruptedRunsAsync(DateTime recoveredAtUtc, CancellationToken ct)
        => Task.CompletedTask;

    public IReadOnlyList<string> CreatedPlanIds() => CreatedRuns
        .Select(r => r.PlanId)
        .Where(p => p is not null)
        .Cast<string>()
        .ToList();
}

/// <summary>空只读查询实现：容量提示等查询返回空样本。</summary>
public sealed class EmptyNetTestQueries : INetTestQueries
{
    public Task<HistoryPage> GetHistoryPageAsync(HistoryQuery query, CancellationToken ct)
        => Task.FromResult(new HistoryPage(0, Array.Empty<HistoryItem>()));

    public Task<HistoryExportBatch> GetHistoryExportBatchAsync(HistoryExportQuery query, CancellationToken ct)
        => Task.FromResult(new HistoryExportBatch(Array.Empty<HistoryItem>(), false));

    public Task<RunDetail?> GetRunDetailAsync(Guid runId, CancellationToken ct)
        => Task.FromResult<RunDetail?>(null);

    public Task<IReadOnlyList<TrendSeries>> GetTrendSeriesAsync(TrendQuery query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TrendSeries>>(Array.Empty<TrendSeries>());

    public Task<IReadOnlyList<RecentRunSummary>> GetRecentRunsAsync(string? planId, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RecentRunSummary>>(Array.Empty<RecentRunSummary>());

    public Task<IReadOnlyList<ProbeOverview>> GetProbeOverviewAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProbeOverview>>(Array.Empty<ProbeOverview>());

    public Task<IReadOnlyList<RunStatusSample>> GetRecentRunStatusesAsync(string planId, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RunStatusSample>>(Array.Empty<RunStatusSample>());
}

/// <summary>立即完成、不发网络请求的探针。</summary>
public sealed class ImmediateProbe : IProbe
{
    public ProbeType Type => ProbeType.Ping;

    public Task<ProbeMeasurement> ExecuteAsync(ProbeExecutionContext context, CancellationToken cancellationToken)
        => Task.FromResult(new ProbeMeasurement(
            IsComplete: true,
            ProbeOutcome.Success,
            PrimaryLatencyMs: 1,
            MetricsSchemaVersion: 1,
            Metrics: new { },
            ErrorCode: null,
            ErrorMessage: null));
}

public sealed class ImmediateProbeRegistry : IProbeRegistry
{
    public IProbe GetProbe(ProbeType type) => new ImmediateProbe();
}

/// <summary>暴露 protected ExecuteAsync，使测试可直接驱动调度循环。</summary>
public sealed class TestProbeScheduler : ProbeScheduler
{
    public TestProbeScheduler(
        Configuration.ConfigManager config,
        ProbeExecutor executor,
        Notifications.RuntimeNotifier notifier,
        TimeProvider timeProvider,
        Microsoft.Extensions.Logging.ILogger<ProbeScheduler> logger)
        : base(config, executor, notifier, timeProvider, logger)
    {
    }

    public Task RunAsync(CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);
}
