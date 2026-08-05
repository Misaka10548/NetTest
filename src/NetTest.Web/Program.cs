using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTest.Core;
using NetTest.Core.Configuration;
using NetTest.Core.Enums;
using NetTest.Core.Notifications;
using NetTest.Core.Scheduling;
using NetTest.Core.Storage;
using NetTest.Data;
using NetTest.Data.Persistence;
using NetTest.Web.Components;
using NetTest.Web.Services;
using Serilog;

// ============================================================
// 启动顺序（TechSpec 10.1）
// ============================================================

// 1. 单实例互斥锁
using Mutex instanceMutex = SingleInstance.TryAcquire(Paths.BaseDirectory, out bool singleInstance);
if (!singleInstance)
{
    Console.Error.WriteLine("NetTest 已经在运行（单实例限制）。");
    return 1;
}

// 2. 创建缺失目录
Directory.CreateDirectory(Paths.ConfigDirectory);
Directory.CreateDirectory(Paths.DataDirectory);
Directory.CreateDirectory(Paths.LogsDirectory);
Directory.CreateDirectory(Paths.BackupsDirectory);

// 3. 加载或创建配置
var configManager = new ConfigManager(Paths.ConfigFilePath, Paths.ConfigBackupPath, Paths.BaseDirectory);
try
{
    await configManager.InitializeAsync();
}
catch (ConfigValidationException ex)
{
    Console.Error.WriteLine("配置错误，无法启动：");
    foreach (ConfigError error in ex.Errors)
    {
        Console.Error.WriteLine($"  {error.Path}: {error.Message}");
    }

    return 1;
}

// 4. 初始化日志（Serilog：控制台 + Data/Logs 滚动文件）
LoggingConfiguration logging = configManager.Current.Logging;
string logDirectory = Paths.ResolveUnderBase(logging.Directory) ?? Paths.LogsDirectory;
Directory.CreateDirectory(logDirectory);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(ParseLogLevel(logging.MinimumLevel))
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(logDirectory, "nettest-.log"),
        rollingInterval: RollingInterval.Day,
        rollOnFileSizeLimit: true,
        fileSizeLimitBytes: logging.FileSizeLimitMiB * 1024L * 1024L,
        retainedFileCountLimit: logging.RetainedDays,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Logger.Information("NetTest 启动（版本 0.1.0）。");

    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog();

    // ============================================================
    // 服务注册
    // ============================================================
    bool passwordEnabled = !string.IsNullOrEmpty(configManager.Current.Host.Password);
    string databasePath = Paths.ResolveUnderBase(configManager.Current.Storage.DatabasePath)
        ?? throw new InvalidOperationException("数据库路径无效。");

    builder.Services.AddNetTestData(databasePath);
    builder.Services.AddSingleton(configManager);
    builder.Services.AddSingleton<RuntimeNotifier>();
    builder.Services.AddSingleton<CapacityNoticeService>();
    builder.Services.AddSingleton<ProbeExecutor>(sp => new ProbeExecutor(
        sp.GetRequiredService<IExecutionStore>(),
        new DefaultProbeRegistry(),
        sp.GetRequiredService<RuntimeNotifier>(),
        configManager,
        sp.GetRequiredService<CapacityNoticeService>(),
        TimeProvider.System,
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProbeExecutor>>(),
        configManager.Current.Scheduler.QueueCapacity,
        configManager.Current.Scheduler.MaxConcurrency));
    builder.Services.AddHostedService<ProbeScheduler>();
    builder.Services.AddHostedService<RetentionWorker>();
    builder.Services.AddSingleton<AppServices>();
    builder.Services.AddHttpContextAccessor();

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
        });
    builder.Services.AddAuthorization();

    if (!passwordEnabled && !IsLoopbackOnly(configManager.Current.Host.Urls))
    {
        // 非本地监听且无密码：允许启动，写去重 Warning（TechSpec 10/SystemDesign 10）。
        Log.Logger.Warning("监听非回环地址且未配置密码，局域网内可被直接访问。");
    }

    var app = builder.Build();

    // 5. 数据库迁移 + pragma 初始化
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NetTestDbContext>>();
        await using NetTestDbContext context = await factory.CreateDbContextAsync();
        await DatabaseInitializer.InitializeAsync(context);

        // 7. 恢复遗留 Run/Execution
        var store = scope.ServiceProvider.GetRequiredService<IExecutionStore>();
        await store.RecoverInterruptedRunsAsync(DateTime.UtcNow, CancellationToken.None);
    }

    // ============================================================
    // HTTP 管道
    // ============================================================
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
    }

    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();
    app.MapStaticAssets();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    // ============================================================
    // 最小 endpoint：登录、导出、数据库备份（TechSpec 7.5）
    // ============================================================
    var antiforgery = app.Services.GetRequiredService<IAntiforgery>();

    app.MapPost("/login", async (HttpContext http, IFormCollection form) =>
    {
        string? password = form["password"].ToString();
        string? configuredPassword = configManager.Current.Host.Password;
        bool valid = password is not null
            && configuredPassword is not null
            && FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(configuredPassword));

        if (valid)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, "user"),
                new(ClaimTypes.Role, "user"),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Redirect("/");
        }

        return Results.Redirect("/login?error=1");
    });

    app.MapGet("/logout", async (HttpContext http) =>
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login");
    });

    var exportEndpoint = app.MapGet("/exports/history.csv", async (
        HttpContext http,
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] string? planId,
        [FromQuery] string? probeId,
        [FromQuery] string? addressFamily,
        [FromQuery] string? status,
        [FromQuery] string? triggerKind,
        INetTestQueries queries,
        CancellationToken ct) =>
    {
        DateTime startUtc = start.ToUniversalTime();
        DateTime endUtc = end.ToUniversalTime();

        // TechSpec 7.3：所有历史查询必须包含 start/end UTC，最大时间范围为 retentionDays。
        if (endUtc < startUtc)
        {
            return Results.BadRequest("结束时间必须不早于开始时间。");
        }

        int retentionDays = configManager.Current.Storage.RetentionDays;
        if (endUtc - startUtc > TimeSpan.FromDays(retentionDays))
        {
            return Results.BadRequest($"查询时间范围不能超过保留期 {retentionDays} 天。");
        }

        if (!TryParseOptionalEnum(addressFamily, out NetworkAddressFamily? family)
            || !TryParseOptionalEnum(status, out ExecutionStatus? executionStatus)
            || !TryParseOptionalEnum(triggerKind, out TriggerKind? trigger))
        {
            return Results.BadRequest("筛选参数值无效。");
        }
        // 流式生成（TechSpec 7.5）：按 (CreatedAtUtc, Id) 游标分批读取，边读边写响应体。
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = "attachment; filename=\"nettest-history.csv\"";
        await using var writer = new StreamWriter(http.Response.Body, new UTF8Encoding(false));
        await writer.WriteLineAsync(CsvFormatter.Header);

        DateTime? afterCreatedAtUtc = null;
        Guid? afterId = null;
        const int batchSize = 1000;

        while (true)
        {
            HistoryExportBatch batch = await queries.GetHistoryExportBatchAsync(
                new HistoryExportQuery(
                    startUtc,
                    endUtc,
                    string.IsNullOrEmpty(planId) ? null : planId,
                    string.IsNullOrEmpty(probeId) ? null : probeId,
                    family,
                    executionStatus,
                    trigger,
                    afterCreatedAtUtc,
                    afterId,
                    batchSize),
                ct);

            foreach (HistoryItem item in batch.Items)
            {
                await writer.WriteLineAsync(CsvFormatter.FormatRow(item));
            }

            if (!batch.HasMore)
            {
                break;
            }

            HistoryItem last = batch.Items[^1];
            afterCreatedAtUtc = last.CreatedAtUtc;
            afterId = last.ExecutionId;
        }

        return Results.Empty;
    });

    var backupEndpoint = app.MapPost("/system/database-backup", async (
        HttpContext http,
        IBackupService backupService) =>
    {
        try
        {
            BackupResult backup = await backupService.CreateBackupAsync(http.RequestAborted);
            return Results.File(backup.FilePath, "application/octet-stream", Path.GetFileName(backup.FilePath));
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "数据库备份失败。");
            return Results.Problem("数据库备份失败。", statusCode: 500);
        }
    });

    if (passwordEnabled)
    {
        exportEndpoint.RequireAuthorization();
        backupEndpoint.RequireAuthorization();
    }

    // ============================================================
    // 启动后台服务：执行器、调度器、保留 worker
    // ============================================================
    var executor = app.Services.GetRequiredService<ProbeExecutor>();
    executor.Start();

    await app.StartAsync();

    // ============================================================
    // 关闭顺序（TechSpec 10.2）
    // ============================================================
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    using var shutdownSignal = new ManualResetEventSlim();
    lifetime.ApplicationStopping.Register(shutdownSignal.Set);
    await Task.Run(shutdownSignal.Wait);

    Log.Logger.Information("NetTest 正在关闭：取消活动运行并等待收尾。");
    await executor.ShutdownAsync(TimeSpan.FromSeconds(35), CancellationToken.None);
    await app.StopAsync(TimeSpan.FromSeconds(10));

    return 0;
}
catch (Exception ex)
{
    Log.Logger.Error(ex, "NetTest 异常退出。");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

// ============================================================
// 辅助
// ============================================================

static bool IsLoopbackOnly(IReadOnlyList<string> urls)
{
    return urls.All(url =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && (uri.Host == "127.0.0.1" || uri.Host == "localhost" || uri.Host == "::1"));
}

static bool FixedTimeEquals(byte[] left, byte[] right)
{
    if (left.Length != right.Length)
    {
        return false;
    }

    return CryptographicOperations.FixedTimeEquals(left, right);
}

static Serilog.Events.LogEventLevel ParseLogLevel(string level)
{
    return level.ToLowerInvariant() switch
    {
        "trace" => Serilog.Events.LogEventLevel.Verbose,
        "debug" => Serilog.Events.LogEventLevel.Debug,
        "warning" => Serilog.Events.LogEventLevel.Warning,
        "error" => Serilog.Events.LogEventLevel.Error,
        "critical" => Serilog.Events.LogEventLevel.Fatal,
        _ => Serilog.Events.LogEventLevel.Information,
    };
}

static bool TryParseOptionalEnum<TEnum>(string? raw, out TEnum? value) where TEnum : struct, Enum
{
    if (string.IsNullOrEmpty(raw))
    {
        value = null;
        return true;
    }

    if (Enum.TryParse(raw, ignoreCase: true, out TEnum parsed))
    {
        value = parsed;
        return true;
    }

    value = null;
    return false;
}

/// <summary>供 WebApplicationFactory 集成测试定位入口类型。</summary>
public partial class Program
{
}
