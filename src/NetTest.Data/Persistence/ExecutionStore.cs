using Microsoft.EntityFrameworkCore;
using NetTest.Core.Enums;
using NetTest.Core.Storage;

namespace NetTest.Data.Persistence;

/// <summary>
/// IExecutionStore 的 EF Core 实现。所有更新方法执行条件更新，
/// 确保状态只按 TechSpec 6.4 状态机规定的合法路径转换。
/// </summary>
public sealed class ExecutionStore : IExecutionStore
{
    private readonly IDbContextFactory<NetTestDbContext> _factory;

    public ExecutionStore(IDbContextFactory<NetTestDbContext> factory)
    {
        _factory = factory;
    }

    public async Task CreateRunAsync(ProbeRunDraft run, IReadOnlyList<ProbeExecutionDraft> executions, CancellationToken ct)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        try
        {
            context.ProbeRuns.Add(new Entities.ProbeRun
            {
                Id = run.RunId,
                PlanId = run.PlanId,
                PlanNameSnapshot = run.PlanNameSnapshot,
                TriggerKind = run.TriggerKind,
                ConfigurationRevision = run.ConfigurationRevision,
                Status = ExecutionStatus.Running,
                CancellationReason = CancellationReason.None,
                StartedAtUtc = run.StartedAtUtc,
                CreatedAtUtc = run.CreatedAtUtc,
            });

            foreach (ProbeExecutionDraft execution in executions)
            {
                context.ProbeExecutions.Add(new Entities.ProbeExecution
                {
                    Id = execution.Id,
                    RunId = execution.RunId,
                    ProbeId = execution.ProbeId,
                    ProbeNameSnapshot = execution.ProbeNameSnapshot,
                    ProbeType = execution.ProbeType,
                    GroupIdSnapshot = execution.GroupIdSnapshot,
                    PlanId = execution.PlanId,
                    TriggerKind = execution.TriggerKind,
                    AddressFamily = execution.AddressFamily,
                    ResolvedAddress = execution.ResolvedAddress?.ToString(),
                    ConfigurationSchemaVersion = execution.ConfigurationSchemaVersion,
                    ConfigurationSnapshotJson = execution.ConfigurationSnapshotJson,
                    Status = ExecutionStatus.Pending,
                    Outcome = ProbeOutcome.None,
                    CancellationReason = CancellationReason.None,
                    MetricsSchemaVersion = 0,
                    CreatedAtUtc = execution.CreatedAtUtc,
                });
            }

            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task MarkExecutionRunningAsync(Guid executionId, DateTime startedAtUtc, CancellationToken ct)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync(ct);
        await context.ProbeExecutions
            .Where(e => e.Id == executionId && e.Status == ExecutionStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.Status, ExecutionStatus.Running)
                    .SetProperty(e => e.StartedAtUtc, startedAtUtc),
                ct);
    }

    public async Task CompleteExecutionAsync(Guid executionId, ProbeExecutionCompletion completion, CancellationToken ct)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync(ct);

        // 状态机合法路径：Pending -> Cancelled；Running -> Completed/Incomplete。
        ExecutionStatus expectedFrom = completion.Status switch
        {
            ExecutionStatus.Completed or ExecutionStatus.Incomplete => ExecutionStatus.Running,
            ExecutionStatus.Cancelled => ExecutionStatus.Pending,
            _ => (ExecutionStatus)(-1),
        };

        if (expectedFrom == (ExecutionStatus)(-1))
        {
            throw new ArgumentOutOfRangeException(nameof(completion), $"非法终态 {completion.Status}。");
        }

        await context.ProbeExecutions
            .Where(e => e.Id == executionId && e.Status == expectedFrom)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.Status, completion.Status)
                    .SetProperty(e => e.Outcome, completion.Outcome)
                    .SetProperty(e => e.CancellationReason, completion.CancellationReason)
                    .SetProperty(e => e.PrimaryLatencyMs, completion.PrimaryLatencyMs)
                    .SetProperty(e => e.MetricsSchemaVersion, completion.MetricsSchemaVersion)
                    .SetProperty(e => e.MetricsJson, completion.MetricsJson)
                    .SetProperty(e => e.ErrorCode, completion.ErrorCode)
                    .SetProperty(e => e.ErrorMessage, completion.ErrorMessage)
                    .SetProperty(e => e.StartedAtUtc, completion.StartedAtUtc)
                    .SetProperty(e => e.CompletedAtUtc, completion.CompletedAtUtc)
                    .SetProperty(e => e.DurationMs, completion.DurationMs),
                ct);
    }

    public async Task CompleteRunAsync(Guid runId, ProbeRunCompletion completion, CancellationToken ct)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync(ct);
        await context.ProbeRuns
            .Where(r => r.Id == runId && r.Status == ExecutionStatus.Running)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.Status, completion.Status)
                    .SetProperty(r => r.CancellationReason, completion.CancellationReason)
                    .SetProperty(r => r.CompletedAtUtc, completion.CompletedAtUtc),
                ct);
    }

    public async Task<IReadOnlyList<ActiveRun>> GetActiveRunsAsync(string planId, CancellationToken ct)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync(ct);
        return await context.ProbeRuns
            .Where(r => r.PlanId == planId && r.Status == ExecutionStatus.Running)
            .OrderBy(r => r.StartedAtUtc)
            .Select(r => new ActiveRun(r.Id, r.Status, r.CancellationReason, r.StartedAtUtc))
            .ToListAsync(ct);
    }

    public async Task RecoverInterruptedRunsAsync(DateTime recoveredAtUtc, CancellationToken ct)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        try
        {
            List<Guid> affectedRunIds = await context.ProbeExecutions
                .Where(e => e.Status == ExecutionStatus.Running || e.Status == ExecutionStatus.Pending)
                .Select(e => e.RunId)
                .Distinct()
                .ToListAsync(ct);

            await context.ProbeExecutions
                .Where(e => e.Status == ExecutionStatus.Running)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(e => e.Status, ExecutionStatus.Incomplete)
                        .SetProperty(e => e.CancellationReason, CancellationReason.ApplicationExit)
                        .SetProperty(e => e.CompletedAtUtc, recoveredAtUtc),
                    ct);

            await context.ProbeExecutions
                .Where(e => e.Status == ExecutionStatus.Pending)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(e => e.Status, ExecutionStatus.Cancelled)
                        .SetProperty(e => e.CancellationReason, CancellationReason.ApplicationExit)
                        .SetProperty(e => e.CompletedAtUtc, recoveredAtUtc),
                    ct);

            foreach (Guid runId in affectedRunIds)
            {
                var statuses = await context.ProbeExecutions
                    .Where(e => e.RunId == runId)
                    .Select(e => new { e.Status, e.CancellationReason })
                    .ToListAsync(ct);

                (ExecutionStatus status, CancellationReason reason) =
                    NetTest.Core.Scheduling.RunAggregator.Aggregate(
                        statuses.Select(s => (s.Status, s.CancellationReason)));

                await context.ProbeRuns
                    .Where(r => r.Id == runId && r.Status == ExecutionStatus.Running)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(r => r.Status, status)
                            .SetProperty(r => r.CancellationReason, reason)
                            .SetProperty(r => r.CompletedAtUtc, recoveredAtUtc),
                        ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
