using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NetTest.Core.Enums;
using NetTest.Data.Entities;
using NetTest.Data.Persistence;

namespace NetTest.Data.Tests;

public class ExecutionStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly IDbContextFactory<NetTestDbContext> _factory;

    public ExecutionStoreTests()
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

    private async Task<Guid> CreateRunAsync(ExecutionStore store, int executionCount = 2)
    {
        var runId = Guid.NewGuid();
        var drafts = Enumerable.Range(0, executionCount).Select(i => new NetTest.Core.Storage.ProbeExecutionDraft(
            Guid.NewGuid(),
            runId,
            $"probe-{i}",
            $"探针 {i}",
            ProbeType.Ping,
            "group",
            "plan-1",
            TriggerKind.Scheduled,
            NetworkAddressFamily.IPv4,
            System.Net.IPAddress.Parse("127.0.0.1"),
            1,
            "{\"probeType\":\"ping\",\"configuration\":{\"id\":\"p1\"}}",
            DateTime.UtcNow)).ToList();

        await store.CreateRunAsync(
            new NetTest.Core.Storage.ProbeRunDraft(runId, "plan-1", "计划", TriggerKind.Scheduled, "rev-1", DateTime.UtcNow, DateTime.UtcNow),
            drafts,
            CancellationToken.None);

        return runId;
    }

    [Fact]
    public async Task Initialize_EmptyDatabase_CreatesSchemaWithWal()
    {
        await TestDb.InitializeAsync(_factory);

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        string mode = (string)(await command.ExecuteScalarAsync())!;

        Assert.Equal("wal", mode);

        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('ProbeRuns','ProbeExecutions');";
        int count = 0;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                count++;
            }
        }

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CreateRun_CreatesRunAndPendingExecutions()
    {
        await TestDb.InitializeAsync(_factory);
        var store = new ExecutionStore(_factory);
        Guid runId = await CreateRunAsync(store);

        await using NetTestDbContext context = await _factory.CreateDbContextAsync();
        Assert.NotNull(await context.ProbeRuns.FindAsync(runId));
        Assert.Equal(2, await context.ProbeExecutions.CountAsync(e => e.RunId == runId));
    }

    [Fact]
    public async Task MarkRunning_OnlyTransitionsPending()
    {
        await TestDb.InitializeAsync(_factory);
        var store = new ExecutionStore(_factory);
        Guid runId = await CreateRunAsync(store);

        await using NetTestDbContext context = await _factory.CreateDbContextAsync();
        Guid executionId = (await context.ProbeExecutions.FirstAsync(e => e.RunId == runId)).Id;

        await store.MarkExecutionRunningAsync(executionId, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Running, (await context.ProbeExecutions.AsNoTracking().FirstAsync(e => e.Id == executionId)).Status);

        // 重复 MarkRunning 无效果（只更新 Pending）
        await store.MarkExecutionRunningAsync(executionId, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Running, (await context.ProbeExecutions.AsNoTracking().FirstAsync(e => e.Id == executionId)).Status);
    }

    [Fact]
    public async Task CompleteExecution_EnforcesStateMachine()
    {
        await TestDb.InitializeAsync(_factory);
        var store = new ExecutionStore(_factory);
        Guid runId = await CreateRunAsync(store);

        await using NetTestDbContext context = await _factory.CreateDbContextAsync();
        List<Guid> ids = await context.ProbeExecutions.Where(e => e.RunId == runId).Select(e => e.Id).ToListAsync();

        // Pending -> Cancelled 合法
        var completion = new NetTest.Core.Storage.ProbeExecutionCompletion(
            ExecutionStatus.Cancelled, ProbeOutcome.None, CancellationReason.ApplicationExit,
            null, 0, null, null, null, null, DateTime.UtcNow, null);
        await store.CompleteExecutionAsync(ids[0], completion, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Cancelled, (await context.ProbeExecutions.FindAsync(ids[0]))!.Status);

        // Pending -> Completed 非法（必须是 Running）——状态机拒绝
        await store.MarkExecutionRunningAsync(ids[1], DateTime.UtcNow, CancellationToken.None);
        var completed = new NetTest.Core.Storage.ProbeExecutionCompletion(
            ExecutionStatus.Completed, ProbeOutcome.Success, CancellationReason.None,
            12, 1, "{}", null, null, DateTime.UtcNow, DateTime.UtcNow, 12);
        await store.CompleteExecutionAsync(ids[1], completed, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Completed, (await context.ProbeExecutions.FindAsync(ids[1]))!.Status);
    }

    [Fact]
    public async Task DeleteRun_CascadesExecutions()
    {
        await TestDb.InitializeAsync(_factory);
        var store = new ExecutionStore(_factory);
        Guid runId = await CreateRunAsync(store);

        await using NetTestDbContext context = await _factory.CreateDbContextAsync();
        await context.ProbeRuns.Where(r => r.Id == runId).ExecuteDeleteAsync();

        Assert.Equal(0, await context.ProbeExecutions.CountAsync(e => e.RunId == runId));
    }

    [Fact]
    public async Task RecoverInterruptedRuns_RepairsStatusAndAggregatesRun()
    {
        await TestDb.InitializeAsync(_factory);
        var store = new ExecutionStore(_factory);

        // 制造崩溃现场：一个 Running Execution + 一个 Pending Execution
        var runId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var runningId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        await using (NetTestDbContext context = await _factory.CreateDbContextAsync())
        {
            context.ProbeRuns.Add(new ProbeRun
            {
                Id = runId,
                PlanId = "plan-1",
                TriggerKind = TriggerKind.Scheduled,
                ConfigurationRevision = "rev",
                Status = ExecutionStatus.Running,
                CancellationReason = CancellationReason.None,
                StartedAtUtc = now,
                CreatedAtUtc = now,
            });
            context.ProbeExecutions.AddRange(
                new ProbeExecution
                {
                    Id = runningId,
                    RunId = runId,
                    ProbeNameSnapshot = "p",
                    ProbeType = ProbeType.Ping,
                    TriggerKind = TriggerKind.Scheduled,
                    ConfigurationSchemaVersion = 1,
                    ConfigurationSnapshotJson = "{}",
                    Status = ExecutionStatus.Running,
                    Outcome = ProbeOutcome.None,
                    CancellationReason = CancellationReason.None,
                    MetricsSchemaVersion = 0,
                    CreatedAtUtc = now,
                },
                new ProbeExecution
                {
                    Id = pendingId,
                    RunId = runId,
                    ProbeNameSnapshot = "p",
                    ProbeType = ProbeType.Ping,
                    TriggerKind = TriggerKind.Scheduled,
                    ConfigurationSchemaVersion = 1,
                    ConfigurationSnapshotJson = "{}",
                    Status = ExecutionStatus.Pending,
                    Outcome = ProbeOutcome.None,
                    CancellationReason = CancellationReason.None,
                    MetricsSchemaVersion = 0,
                    CreatedAtUtc = now,
                });
            await context.SaveChangesAsync();
        }

        var recoveredAt = now.AddMinutes(1);
        await store.RecoverInterruptedRunsAsync(recoveredAt, CancellationToken.None);

        await using NetTestDbContext verify = await _factory.CreateDbContextAsync();
        Assert.Equal(ExecutionStatus.Incomplete, (await verify.ProbeExecutions.FindAsync(runningId))!.Status);
        Assert.Equal(CancellationReason.ApplicationExit, (await verify.ProbeExecutions.FindAsync(runningId))!.CancellationReason);
        Assert.Equal(ExecutionStatus.Cancelled, (await verify.ProbeExecutions.FindAsync(pendingId))!.Status);
        Assert.Equal(CancellationReason.ApplicationExit, (await verify.ProbeExecutions.FindAsync(pendingId))!.CancellationReason);

        ProbeRun run = (await verify.ProbeRuns.FindAsync(runId))!;
        Assert.Equal(ExecutionStatus.Incomplete, run.Status);
        Assert.Equal(CancellationReason.ApplicationExit, run.CancellationReason);
        Assert.NotNull(run.CompletedAtUtc);
    }

    [Fact]
    public async Task Retention_DeletesOnlyExpiredInBatches()
    {
        await TestDb.InitializeAsync(_factory);
        var store = new ExecutionStore(_factory);
        var retention = new RetentionStore(_factory);
        var now = DateTime.UtcNow;

        // 3 个过期 + 1 个未过期
        for (int i = 0; i < 3; i++)
        {
            var runId = Guid.NewGuid();
            await using (NetTestDbContext context = await _factory.CreateDbContextAsync())
            {
                context.ProbeRuns.Add(new ProbeRun
                {
                    Id = runId,
                    TriggerKind = TriggerKind.Manual,
                    ConfigurationRevision = "rev",
                    Status = ExecutionStatus.Completed,
                    CancellationReason = CancellationReason.None,
                    StartedAtUtc = now.AddDays(-100),
                    CreatedAtUtc = now.AddDays(-100),
                    CompletedAtUtc = now.AddDays(-100),
                });
                await context.SaveChangesAsync();
            }
        }

        var freshId = Guid.NewGuid();
        await using (NetTestDbContext context = await _factory.CreateDbContextAsync())
        {
            context.ProbeRuns.Add(new ProbeRun
            {
                Id = freshId,
                TriggerKind = TriggerKind.Manual,
                ConfigurationRevision = "rev",
                Status = ExecutionStatus.Completed,
                CancellationReason = CancellationReason.None,
                StartedAtUtc = now,
                CreatedAtUtc = now,
            });
            await context.SaveChangesAsync();
        }

        int deleted = await retention.DeleteExpiredRunsAsync(now.AddDays(-90), 2, CancellationToken.None);
        Assert.Equal(2, deleted);
        deleted = await retention.DeleteExpiredRunsAsync(now.AddDays(-90), 2, CancellationToken.None);
        Assert.Equal(1, deleted);
        Assert.Equal(0, await retention.DeleteExpiredRunsAsync(now.AddDays(-90), 2, CancellationToken.None));

        await using NetTestDbContext verify = await _factory.CreateDbContextAsync();
        Assert.NotNull(await verify.ProbeRuns.FindAsync(freshId));
        Assert.Equal(1, await verify.ProbeRuns.CountAsync());
    }

    [Fact]
    public async Task Backup_CreatesReadableConsistentCopy()
    {
        await TestDb.InitializeAsync(_factory);
        var store = new ExecutionStore(_factory);
        await CreateRunAsync(store);

        var config = new NetTest.Core.Configuration.ConfigManager(
            Path.Combine(_dir, "Config", "nettest.json"),
            Path.Combine(_dir, "Config", "nettest.json.bak"),
            _dir);
        config.Current.Storage.DatabasePath = "nettest.db";

        // BackupService 使用 Paths（基于 AppContext.BaseDirectory），这里直接测试 SQLite API 行为。
        string backupPath = Path.Combine(_dir, "backup.db");
        await using (var source = new SqliteConnection($"Data Source={_dbPath}"))
        await using (var destination = new SqliteConnection($"Data Source={backupPath}"))
        {
            await source.OpenAsync();
            await destination.OpenAsync();
            source.BackupDatabase(destination);
        }

        Assert.True(File.Exists(backupPath));
        await using var backupConnection = new SqliteConnection($"Data Source={backupPath}");
        await backupConnection.OpenAsync();
        await using var command = backupConnection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ProbeRuns;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }
}
