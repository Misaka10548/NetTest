using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTest.Core.Storage;
using NetTest.Data.Persistence;

namespace NetTest.Data;

/// <summary>组合根：注册 DbContextFactory、连接拦截器与全部持久化端口实现。</summary>
public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddNetTestData(this IServiceCollection services, string databasePath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        services.AddPooledDbContextFactory<NetTestDbContext>(options =>
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString())
                .AddInterceptors(new NetTestConnectionInterceptor()));

        services.AddSingleton<IExecutionStore, ExecutionStore>();
        services.AddSingleton<INetTestQueries, NetTestQueries>();
        services.AddSingleton<IRetentionStore, RetentionStore>();
        services.AddSingleton<IBackupService, BackupService>();

        return services;
    }
}
