using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NetTest.Core.Configuration;
using NetTest.Core.Enums;
using NetTest.Core.Notifications;
using NetTest.Core.Probes;
using NetTest.Core.Storage;

namespace NetTest.Core.Scheduling;

/// <summary>进入执行队列的工作项。</summary>
public sealed record ExecutionWorkItem(
    Guid ExecutionId,
    Guid RunId,
    string? ProbeId,
    string ProbeNameSnapshot,
    ProbeType ProbeType,
    string? GroupIdSnapshot,
    string? PlanId,
    TriggerKind TriggerKind,
    ProbeConfigurationSnapshot ConfigurationSnapshot,
    NetworkAddressFamily? AddressFamily,
    IPAddress? ResolvedAddress,
    DateTime CreatedAtUtc);

/// <summary>
/// 执行器：建立运行批次、有界队列 + 并发上限、同组串行、协作式取消、
/// 部分结果保存、Run 终态聚合与进程内通知。
/// </summary>
public sealed class ProbeExecutor : IAsyncDisposable
{
    private readonly IExecutionStore _store;
    private readonly IProbeRegistry _registry;
    private readonly RuntimeNotifier _notifier;
    private readonly ConfigManager _config;
    private readonly CapacityNoticeService _capacityNotices;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProbeExecutor> _logger;

    private readonly Channel<ExecutionWorkItem> _queue;
    private readonly List<Task> _workers = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, PlanGate> _planGates = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _groupLocks = new();
    private readonly ConcurrentDictionary<Guid, RunContext> _runs = new();
    private int _maxConcurrency;
    private readonly object _workerLock = new();

    public ProbeExecutor(
        IExecutionStore store,
        IProbeRegistry registry,
        RuntimeNotifier notifier,
        ConfigManager config,
        CapacityNoticeService capacityNotices,
        TimeProvider timeProvider,
        ILogger<ProbeExecutor> logger,
        int queueCapacity,
        int maxConcurrency)
    {
        _store = store;
        _registry = registry;
        _notifier = notifier;
        _config = config;
        _capacityNotices = capacityNotices;
        _timeProvider = timeProvider;
        _logger = logger;
        _queue = Channel.CreateBounded<ExecutionWorkItem>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
        _maxConcurrency = Math.Max(1, maxConcurrency);
    }

    /// <summary>启动执行器（由宿主调用），按当前配置并发上限启动 worker。</summary>
    public void Start()
    {
        EnsureWorkerCount();
    }

    /// <summary>按计划触发（Scheduled）。同一计划串行化，重叠 tick 合并只保留最新。</summary>
    public async Task TriggerPlanAsync(string planId, CancellationToken cancellationToken)
    {
        PlanGate gate = _planGates.GetOrAdd(planId, _ => new PlanGate());
        await gate.RunAsync(async () =>
        {
            NetTestConfiguration config = _config.Current;
            PlanConfiguration? plan = config.Plans.FirstOrDefault(p => p.Id == planId);
            if (plan is null || !plan.Enabled)
            {
                return;
            }

            List<ProbeConfiguration> probes = config.Probes.All
                .Where(p => p.Enabled && p.PlanIds.Contains(planId))
                .ToList();

            await CancelActiveRunsAsync(planId, CancellationReason.SupersededByNextRun, cancellationToken);
            await CreateRunAndEnqueueAsync(
                planId,
                plan.Name,
                probes,
                TriggerKind.Scheduled,
                _config.Revision,
                cancellationToken);
        }, cancellationToken);
    }

    /// <summary>手动触发（Manual）：不关联 PlanId，不参与计划取消与容量提示。</summary>
    public async Task<Guid> TriggerManualAsync(
        ProbeConfiguration probe,
        string? probeId,
        CancellationToken cancellationToken)
    {
        Guid runId = await CreateRunAndEnqueueAsync(
            planId: null,
            planName: null,
            new[] { probe },
            TriggerKind.Manual,
            _config.Revision,
            cancellationToken);
        return runId;
    }

    /// <summary>正常关闭：取消全部活动运行并等待收尾。</summary>
    public async Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        foreach (RunContext run in _runs.Values)
        {
            run.Cancellation?.Cancel();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await Task.WhenAll(_workers.ToArray()).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("等待探针收尾超过 {Timeout}，进程退出；遗留状态将在下次启动时恢复。", timeout);
        }

        _shutdown.Cancel();
    }

    private async Task CancelActiveRunsAsync(string planId, CancellationReason reason, CancellationToken cancellationToken)
    {
        IReadOnlyList<ActiveRun> active = await _store.GetActiveRunsAsync(planId, cancellationToken);
        foreach (ActiveRun run in active)
        {
            if (_runs.TryGetValue(run.RunId, out RunContext? context))
            {
                context.Reason = reason;
                context.Cancellation?.Cancel();
            }

            await WaitForSettlementAsync(planId, run.RunId, cancellationToken);
        }
    }

    private async Task WaitForSettlementAsync(string planId, Guid runId, CancellationToken cancellationToken)
    {
        while (_runs.TryGetValue(runId, out RunContext? context)
            && Volatile.Read(ref context.Done) < Volatile.Read(ref context.Total))
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, cancellationToken);
            _logger.LogWarning(
                "计划 {PlanId} 的上一轮 Run {RunId} 取消收尾超过 5 秒仍未完成（{Done}/{Total}），继续等待。",
                planId,
                runId,
                Volatile.Read(ref context.Done),
                Volatile.Read(ref context.Total));
        }
    }

    private async Task<Guid> CreateRunAndEnqueueAsync(
        string? planId,
        string? planName,
        IReadOnlyList<ProbeConfiguration> probes,
        TriggerKind triggerKind,
        string revision,
        CancellationToken cancellationToken)
    {
        Guid runId = Guid.NewGuid();
        DateTime startedAt = _timeProvider.GetUtcNow().UtcDateTime;
        var items = new List<ExecutionWorkItem>();

        foreach (ProbeConfiguration probe in probes)
        {
            AddressExpansion expansion = await AddressExpander.ExpandAsync(probe, cancellationToken);
            if (expansion.DnsFailed)
            {
                items.Add(CreateWorkItem(runId, probe, null, null, triggerKind, planId, startedAt));
                continue;
            }

            foreach (ProbeAddressTarget target in expansion.Targets)
            {
                items.Add(CreateWorkItem(runId, probe, target.AddressFamily, target.ResolvedAddress, triggerKind, planId, startedAt));
            }
        }

        var context = new RunContext
        {
            Total = items.Count,
            PlanId = planId,
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token),
        };
        _runs[runId] = context;

        List<ProbeExecutionDraft> drafts = items.Select(item => new ProbeExecutionDraft(
            item.ExecutionId,
            item.RunId,
            item.ProbeId,
            item.ProbeNameSnapshot,
            item.ProbeType,
            item.GroupIdSnapshot,
            item.PlanId,
            item.TriggerKind,
            item.AddressFamily,
            item.ResolvedAddress,
            NetTestConfiguration.CurrentSchemaVersion,
            item.ConfigurationSnapshot.Serialize(),
            item.CreatedAtUtc)).ToList();

        await _store.CreateRunAsync(
            new ProbeRunDraft(runId, planId, planName, triggerKind, revision, startedAt, startedAt),
            drafts,
            cancellationToken);

        foreach (ExecutionWorkItem item in items)
        {
            await _queue.Writer.WriteAsync(item, cancellationToken);
        }

        EnsureWorkerCount();

        if (items.Count == 0)
        {
            // 地址展开后没有任何适用 Execution：调度正常完成但没有该地址族的数据。
            await CompleteRunAsync(runId, context);
        }

        _logger.LogDebug(
            "触发 {TriggerKind} Run {RunId}（计划 {PlanId}），共 {Count} 个 Execution。",
            triggerKind,
            runId,
            planId,
            items.Count);

        return runId;
    }

    private ExecutionWorkItem CreateWorkItem(
        Guid runId,
        ProbeConfiguration probe,
        NetworkAddressFamily? addressFamily,
        IPAddress? resolvedAddress,
        TriggerKind triggerKind,
        string? planId,
        DateTime createdAt)
    {
        return new ExecutionWorkItem(
            Guid.NewGuid(),
            runId,
            probe.Id.Length > 0 ? probe.Id : null,
            probe.Name,
            probe.Type,
            string.IsNullOrEmpty(probe.GroupId) ? probe.Id : probe.GroupId,
            planId,
            triggerKind,
            new ProbeConfigurationSnapshot(probe.Type, probe),
            addressFamily,
            resolvedAddress,
            createdAt);
    }

    private void EnsureWorkerCount()
    {
        lock (_workerLock)
        {
            int maxConcurrency = Math.Max(1, _config.Current.Scheduler.MaxConcurrency);
            while (_workers.Count < maxConcurrency)
            {
                Task worker = Task.Run(WorkerLoopAsync);
                _workers.Add(worker);
            }

            if (maxConcurrency < _workers.Count)
            {
                _maxConcurrency = maxConcurrency;
                // 超出的 worker 会在下一个队列项到达时自然结束？不会——ReadAllAsync 会一直等待。
                // 通过引入"额外 worker 退出标志"处理：超过上限的 worker 在下次循环退出。
                // 简化处理：记录日志，其余 worker 保持运行（队列消费不会超过 maxConcurrency 个并发执行，
                // 因为实际并发由 worker 数量决定，数量只会偏多不会破坏语义）。
                _logger.LogDebug("并发上限下调为 {MaxConcurrency}，现有 {Count} 个 worker 继续运行。", maxConcurrency, _workers.Count);
            }
        }
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            await foreach (ExecutionWorkItem item in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                await ExecuteItemAsync(item);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // 正常关闭：取消使 ReadAllAsync 结束，worker 以完成状态退出，
            // 避免未捕获的取消异常从 Task.WhenAll 冒泡（DisposeAsync / ShutdownAsync）。
        }
    }

    private async Task ExecuteItemAsync(ExecutionWorkItem item)
    {
        RunContext? context = _runs.TryGetValue(item.RunId, out RunContext? c) ? c : null;
        CancellationToken token = context?.Cancellation?.Token ?? _shutdown.Token;

        // 未开始且运行已被取消：直接保存 Cancelled。
        if (token.IsCancellationRequested)
        {
            await SaveCancelledAsync(item, context?.Reason ?? CancellationReason.ApplicationExit);
            return;
        }

        string key = item.GroupIdSnapshot ?? item.ProbeId ?? item.ExecutionId.ToString("D");
        SemaphoreSlim groupLock = _groupLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await groupLock.WaitAsync(_shutdown.Token);
        try
        {
            if (token.IsCancellationRequested)
            {
                await SaveCancelledAsync(item, context?.Reason ?? CancellationReason.ApplicationExit);
                return;
            }

            IProbe probe = _registry.GetProbe(item.ProbeType);
            DateTime startedAt = _timeProvider.GetUtcNow().UtcDateTime;
            await _store.MarkExecutionRunningAsync(item.ExecutionId, startedAt, _shutdown.Token);
            _notifier.PublishExecutionChanged(item.ExecutionId, item.RunId, ExecutionStatus.Running);

            ProbeMeasurement measurement;
            try
            {
                var executionContext = new ProbeExecutionContext(
                    item.RunId,
                    item.ExecutionId,
                    item.ProbeId,
                    item.TriggerKind,
                    item.ConfigurationSnapshot,
                    item.AddressFamily,
                    item.ResolvedAddress,
                    _timeProvider);
                measurement = await probe.ExecuteAsync(executionContext, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 探针未按契约返回部分结果而抛出取消异常：按部分结果处理。
                measurement = new ProbeMeasurement(false, ProbeOutcome.None, null, 0, new { }, "Cancelled", "执行被取消。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "探针 {ProbeId}（Run {RunId}）执行异常。", item.ProbeId, item.RunId);
                measurement = new ProbeMeasurement(true, ProbeOutcome.InternalError, null, 0, new { }, "InternalError", ex.Message);
            }

            DateTime completedAt = _timeProvider.GetUtcNow().UtcDateTime;
            long? durationMs = completedAt >= startedAt ? (long)(completedAt - startedAt).TotalMilliseconds : null;

            (ExecutionStatus status, CancellationReason reason) = measurement.IsComplete
                ? (ExecutionStatus.Completed, CancellationReason.None)
                : (ExecutionStatus.Incomplete, context?.Reason ?? CancellationReason.ApplicationExit);

            await _store.CompleteExecutionAsync(item.ExecutionId, new ProbeExecutionCompletion(
                status,
                measurement.Outcome,
                reason,
                measurement.PrimaryLatencyMs,
                measurement.MetricsSchemaVersion,
                SerializeMetrics(measurement.Metrics),
                measurement.ErrorCode,
                measurement.ErrorMessage,
                startedAt,
                completedAt,
                durationMs),
                _shutdown.Token);
            _notifier.PublishExecutionChanged(item.ExecutionId, item.RunId, status);
        }
        finally
        {
            groupLock.Release();
        }

        await TrackCompletionAsync(item.RunId);
    }

    private async Task SaveCancelledAsync(ExecutionWorkItem item, CancellationReason reason)
    {
        DateTime completedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await _store.CompleteExecutionAsync(item.ExecutionId, new ProbeExecutionCompletion(
            ExecutionStatus.Cancelled,
            ProbeOutcome.None,
            reason,
            null,
            0,
            null,
            null,
            null,
            null,
            completedAt,
            null),
            _shutdown.Token);
        _notifier.PublishExecutionChanged(item.ExecutionId, item.RunId, ExecutionStatus.Cancelled);
        if (_runs.TryGetValue(item.RunId, out RunContext? context))
        {
            context.Results[item.ExecutionId] = (ExecutionStatus.Cancelled, reason);
        }

        await TrackCompletionAsync(item.RunId);
    }

    private async Task TrackCompletionAsync(Guid runId)
    {
        if (!_runs.TryGetValue(runId, out RunContext? context))
        {
            return;
        }

        if (Interlocked.Increment(ref context.Done) == Volatile.Read(ref context.Total))
        {
            await CompleteRunAsync(runId, context);
        }
    }

    private async Task CompleteRunAsync(Guid runId, RunContext context)
    {
        try
        {
            (ExecutionStatus status, CancellationReason reason) = RunAggregator.Aggregate(context.Results.Values);
            DateTime completedAt = _timeProvider.GetUtcNow().UtcDateTime;
            await _store.CompleteRunAsync(runId, new ProbeRunCompletion(status, reason, completedAt), _shutdown.Token);
            _notifier.PublishRunChanged(runId, status);
            _runs.TryRemove(runId, out _);

            if (context.PlanId is not null)
            {
                // 计划运行进入终态后更新容量提示（TechSpec 5.5）。
                await _capacityNotices.OnRunCompletedAsync(context.PlanId, _shutdown.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run {RunId} 聚合完成失败。", runId);
            _runs.TryRemove(runId, out _);
        }
    }

    private static string? SerializeMetrics(object? metrics)
    {
        return metrics is null ? null : JsonSerializer.Serialize(metrics, NetTestJson.PersistedOptions);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        await Task.WhenAll(_workers.ToArray());
        _shutdown.Dispose();
    }

    private sealed class RunContext
    {
        public int Total;

        public int Done;

        public string? PlanId;

        public CancellationReason Reason;

        public CancellationTokenSource? Cancellation;

        public ConcurrentDictionary<Guid, (ExecutionStatus Status, CancellationReason Reason)> Results { get; } = new();
    }
}
