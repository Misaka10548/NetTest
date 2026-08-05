using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTest.Data.Persistence;

namespace NetTest.Data.Tests;

/// <summary>测试基础设施：独立临时目录 + 真实 SQLite 文件（验证 WAL 与迁移）。</summary>
internal static class TestDb
{
    public static (string Directory, string DatabasePath) CreateTemp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nettest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (dir, Path.Combine(dir, "nettest.db"));
    }

    public static IDbContextFactory<NetTestDbContext> CreateFactory(string databasePath)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddPooledDbContextFactory<NetTestDbContext>(options =>
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString())
                .AddInterceptors(new NetTestConnectionInterceptor()));
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<NetTestDbContext>>();
    }

    public static async Task InitializeAsync(IDbContextFactory<NetTestDbContext> factory)
    {
        await using NetTestDbContext context = await factory.CreateDbContextAsync();
        await DatabaseInitializer.InitializeAsync(context);
    }
}
