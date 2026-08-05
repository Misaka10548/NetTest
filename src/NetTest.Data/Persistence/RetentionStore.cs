using Microsoft.EntityFrameworkCore;
using NetTest.Core.Storage;

namespace NetTest.Data.Persistence;

/// <summary>按保留期批量删除过期 Run（cascade 删除 Execution），每批独立提交（TechSpec 9）。</summary>
public sealed class RetentionStore : IRetentionStore
{
    private readonly IDbContextFactory<NetTestDbContext> _factory;

    public RetentionStore(IDbContextFactory<NetTestDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<int> DeleteExpiredRunsAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        await using NetTestDbContext context = await _factory.CreateDbContextAsync(ct);

        List<Guid> expired = await context.ProbeRuns
            .Where(r => r.CreatedAtUtc < cutoffUtc)
            .OrderBy(r => r.CreatedAtUtc)
            .Take(batchSize)
            .Select(r => r.Id)
            .ToListAsync(ct);

        if (expired.Count == 0)
        {
            return 0;
        }

        // 数据库级联删除 Executions（外键 ON DELETE CASCADE + PRAGMA foreign_keys=ON）。
        int deleted = await context.ProbeRuns
            .Where(r => expired.Contains(r.Id))
            .ExecuteDeleteAsync(ct);
        return deleted;
    }
}
