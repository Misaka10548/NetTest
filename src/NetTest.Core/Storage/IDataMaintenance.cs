namespace NetTest.Core.Storage;

/// <summary>数据维护端口：按保留期批量清理过期 Run（级联删除 Execution），由 NetTest.Data 实现。</summary>
public interface IRetentionStore
{
    /// <summary>删除早于 cutoff 的 ProbeRuns，每批最多 batchSize 条，返回本批删除数。</summary>
    Task<int> DeleteExpiredRunsAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct);
}

/// <summary>数据库备份结果。</summary>
public sealed record BackupResult(string FilePath, long SizeBytes);

/// <summary>备份端口：使用 SQLite 在线备份 API 创建一致性副本，由 NetTest.Data 实现。</summary>
public interface IBackupService
{
    Task<BackupResult> CreateBackupAsync(CancellationToken ct);
}
