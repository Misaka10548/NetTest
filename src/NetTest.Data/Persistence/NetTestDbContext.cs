using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NetTest.Data.Entities;

namespace NetTest.Data.Persistence;

/// <summary>EF Core 数据库上下文：表、索引、值转换器（TechSpec 4.1/4.2/4.3）。</summary>
public sealed class NetTestDbContext : DbContext
{
    public NetTestDbContext(DbContextOptions<NetTestDbContext> options)
        : base(options)
    {
    }

    public DbSet<ProbeRun> ProbeRuns => Set<ProbeRun>();

    public DbSet<ProbeExecution> ProbeExecutions => Set<ProbeExecution>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureRun(modelBuilder.Entity<ProbeRun>());
        ConfigureExecution(modelBuilder.Entity<ProbeExecution>());
    }

    private static void ConfigureRun(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<ProbeRun> run)
    {
        run.ToTable("ProbeRuns");
        run.HasKey(r => r.Id);
        run.Property(r => r.Id).HasConversion(GuidConverter);
        run.Property(r => r.ConfigurationRevision).IsRequired();
        run.Property(r => r.StartedAtUtc).HasConversion(UtcConverter);
        run.Property(r => r.CompletedAtUtc).HasConversion(UtcConverter);
        run.Property(r => r.CreatedAtUtc).HasConversion(UtcConverter);
        run.HasMany(r => r.Executions)
            .WithOne(e => e.Run)
            .HasForeignKey(e => e.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        run.HasIndex(r => new { r.PlanId, r.StartedAtUtc }).IsDescending(false, true);
        run.HasIndex(r => new { r.Status, r.StartedAtUtc }).IsDescending(false, true);
        run.HasIndex(r => new { r.TriggerKind, r.StartedAtUtc }).IsDescending(false, true);
    }

    private static void ConfigureExecution(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<ProbeExecution> execution)
    {
        execution.ToTable("ProbeExecutions");
        execution.HasKey(e => e.Id);
        execution.Property(e => e.Id).HasConversion(GuidConverter);
        execution.Property(e => e.ProbeNameSnapshot).IsRequired();
        execution.Property(e => e.ConfigurationSnapshotJson).IsRequired();
        execution.Property(e => e.ErrorCode).HasMaxLength(100);
        execution.Property(e => e.ErrorMessage).HasMaxLength(2000);
        execution.Property(e => e.StartedAtUtc).HasConversion(UtcConverter);
        execution.Property(e => e.CompletedAtUtc).HasConversion(UtcConverter);
        execution.Property(e => e.CreatedAtUtc).HasConversion(UtcConverter);

        execution.HasIndex(e => e.RunId);
        execution.HasIndex(e => new { e.ProbeId, e.CompletedAtUtc }).IsDescending(false, true);
        execution.HasIndex(e => new { e.PlanId, e.CompletedAtUtc }).IsDescending(false, true);
        execution.HasIndex(e => new { e.Status, e.CompletedAtUtc }).IsDescending(false, true);
        execution.HasIndex(e => new { e.TriggerKind, e.CompletedAtUtc }).IsDescending(false, true);
    }

    private static readonly ValueConverter<Guid, string> GuidConverter = new(
        v => v.ToString("D"),
        v => Guid.ParseExact(v, "D"));

    private static readonly ValueConverter<DateTime, string> UtcConverter = new(
        v => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        v => DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}

/// <summary>每个新连接执行 SQLite pragma（foreign_keys、busy_timeout）。</summary>
public sealed class NetTestConnectionInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }
}

/// <summary>数据库初始化：WAL 模式 + 迁移（TechSpec 4.1/10.1）。</summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(NetTestDbContext context, CancellationToken cancellationToken = default)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        await context.Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>设计时工厂（dotnet ef migrations）。</summary>
public sealed class NetTestDesignTimeFactory : IDesignTimeDbContextFactory<NetTestDbContext>
{
    public NetTestDbContext CreateDbContext(string[] args)
    {
        string databasePath = Environment.GetEnvironmentVariable("NETTEST_DESIGN_DB")
            ?? "Data/nettest.db";
        string directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        Directory.CreateDirectory(directory);

        var options = new DbContextOptionsBuilder<NetTestDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString())
            .AddInterceptors(new NetTestConnectionInterceptor())
            .Options;
        return new NetTestDbContext(options);
    }
}
