using Microsoft.Data.Sqlite;
using NetTest.Core;
using NetTest.Core.Configuration;
using NetTest.Core.Storage;

namespace NetTest.Data.Persistence;

/// <summary>通过 SQLite 在线备份 API 创建一致性数据库副本（TechSpec 7.5/9）。</summary>
public sealed class BackupService : IBackupService
{
    private readonly ConfigManager _config;

    public BackupService(ConfigManager config)
    {
        _config = config;
    }

    public async Task<BackupResult> CreateBackupAsync(CancellationToken ct)
    {
        string? databasePath = Paths.ResolveUnderBase(_config.Current.Storage.DatabasePath)
            ?? throw new InvalidOperationException("数据库路径无效。");

        Directory.CreateDirectory(Paths.BackupsDirectory);
        string backupPath = Path.Combine(
            Paths.BackupsDirectory,
            $"nettest-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");

        await using var source = new SqliteConnection($"Data Source={databasePath}");
        await source.OpenAsync(ct);
        await using var destination = new SqliteConnection($"Data Source={backupPath}");
        await destination.OpenAsync(ct);
        source.BackupDatabase(destination);

        long size = new FileInfo(backupPath).Length;
        return new BackupResult(backupPath, size);
    }
}
