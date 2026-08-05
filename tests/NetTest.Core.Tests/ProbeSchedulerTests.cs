using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NetTest.Core.Configuration;
using NetTest.Core.Notifications;
using NetTest.Core.Scheduling;

namespace NetTest.Core.Tests;

/// <summary>
/// ProbeScheduler 回归测试：
/// 1. 多个计划同一时刻到期（整点同时命中的 */5 与 0 */1 * * *）必须全部触发，不允许永久饥饿；
/// 2. 配置保存后发布 ConfigurationChanged，新增计划立即重算触发，不等旧等待结束；
/// 3. 低频 Cron（间隔超过 Task.Delay 上限）分段等待不崩溃且能按时触发。
/// </summary>
public sealed class ProbeSchedulerTests
{
    private const string TwoPlanConfig = """
        {
          "schemaVersion": 1,
          "host": { "urls": ["http://127.0.0.1:5000"], "password": null },
          "storage": { "databasePath": "Data/nettest.db", "retentionDays": 90, "chartMaxPointsPerSeries": 2000 },
          "scheduler": { "maxConcurrency": 4, "queueCapacity": 256, "capacityWarningWindow": 10, "capacityWarningRatio": 0.6 },
          "logging": { "minimumLevel": "Information", "directory": "Data/Logs", "fileSizeLimitMiB": 10, "retainedDays": 14 },
          "plans": [
            { "id": "plan-a", "name": "A", "cron": "*/5 * * * *", "enabled": true },
            { "id": "plan-b", "name": "B", "cron": "0 */1 * * *", "enabled": true }
          ],
          "probes": {
            "ping": [
              { "id": "probe-a", "name": "probe-a", "enabled": true, "groupId": null, "tags": [], "planIds": ["plan-a"], "target": "1.1.1.1", "packetCount": 1, "timeoutMs": 1000, "payloadSize": 32 },
              { "id": "probe-b", "name": "probe-b", "enabled": true, "groupId": null, "tags": [], "planIds": ["plan-b"], "target": "1.1.1.1", "packetCount": 1, "timeoutMs": 1000, "payloadSize": 32 }
            ],
            "tracert": [],
            "dns": [],
            "https": []
          }
        }
        """;

    private const string SinglePlanConfig = """
        {
          "schemaVersion": 1,
          "host": { "urls": ["http://127.0.0.1:5000"], "password": null },
          "storage": { "databasePath": "Data/nettest.db", "retentionDays": 90, "chartMaxPointsPerSeries": 2000 },
          "scheduler": { "maxConcurrency": 4, "queueCapacity": 256, "capacityWarningWindow": 10, "capacityWarningRatio": 0.6 },
          "logging": { "minimumLevel": "Information", "directory": "Data/Logs", "fileSizeLimitMiB": 10, "retainedDays": 14 },
          "plans": [
            { "id": "plan-a", "name": "A", "cron": "*/5 * * * *", "enabled": true }
          ],
          "probes": {
            "ping": [
              { "id": "probe-a", "name": "probe-a", "enabled": true, "groupId": null, "tags": [], "planIds": ["plan-a"], "target": "1.1.1.1", "packetCount": 1, "timeoutMs": 1000, "payloadSize": 32 }
            ],
            "tracert": [],
            "dns": [],
            "https": []
          }
        }
        """;

    private const string YearlyPlanConfig = """
        {
          "schemaVersion": 1,
          "host": { "urls": ["http://127.0.0.1:5000"], "password": null },
          "storage": { "databasePath": "Data/nettest.db", "retentionDays": 90, "chartMaxPointsPerSeries": 2000 },
          "scheduler": { "maxConcurrency": 4, "queueCapacity": 256, "capacityWarningWindow": 10, "capacityWarningRatio": 0.6 },
          "logging": { "minimumLevel": "Information", "directory": "Data/Logs", "fileSizeLimitMiB": 10, "retainedDays": 14 },
          "plans": [
            { "id": "plan-a", "name": "A", "cron": "0 0 1 1 *", "enabled": true }
          ],
          "probes": {
            "ping": [
              { "id": "probe-a", "name": "probe-a", "enabled": true, "groupId": null, "tags": [], "planIds": ["plan-a"], "target": "1.1.1.1", "packetCount": 1, "timeoutMs": 1000, "payloadSize": 32 }
            ],
            "tracert": [],
            "dns": [],
            "https": []
          }
        }
        """;

    [Fact]
    public async Task PlansDueAtSameTime_AllTriggered()
    {
        await using Harness h = await Harness.CreateAsync(TwoPlanConfig);
        await WaitForAsync(() => h.Time.TimerCount >= 1, TimeSpan.FromSeconds(10));

        // 越过本地整点 1 秒：plan-a（*/5）与 plan-b（0 */1 * * *）同时到期。
        h.Time.Advance(TimeSpan.FromSeconds(31));

        await WaitForAsync(
            () => h.Store.CreatedPlanIds().Contains("plan-a") && h.Store.CreatedPlanIds().Contains("plan-b"),
            TimeSpan.FromSeconds(10));

        Assert.Contains("plan-a", h.Store.CreatedPlanIds());
        Assert.Contains("plan-b", h.Store.CreatedPlanIds());
    }

    [Fact]
    public async Task ConfigChangePublished_NewPlanTriggeredBeforeOldNextOccurrence()
    {
        await using Harness h = await Harness.CreateAsync(SinglePlanConfig);
        await WaitForAsync(() => h.Time.TimerCount >= 1, TimeSpan.FromSeconds(10));

        h.Time.Advance(TimeSpan.FromSeconds(31));
        await WaitForAsync(() => h.Store.CreatedPlanIds().Contains("plan-a"), TimeSpan.FromSeconds(10));

        // 保存新配置：新增 plan-c（每分钟），并通过 RuntimeNotifier 发布配置变更。
        NetTestConfiguration loaded = JsonSerializer.Deserialize<NetTestConfiguration>(
            File.ReadAllText(h.ConfigPath), NetTestJson.Options)!;
        loaded.Plans.Add(new PlanConfiguration { Id = "plan-c", Name = "C", Cron = "*/1 * * * *", Enabled = true });
        loaded.Probes.Ping.Add(new PingProbeConfiguration
        {
            Id = "probe-c",
            Name = "probe-c",
            Enabled = true,
            GroupId = null,
            Tags = new List<string>(),
            PlanIds = new List<string> { "plan-c" },
            Target = "1.1.1.1",
            PacketCount = 1,
            TimeoutMs = 1000,
            PayloadSize = 32,
        });
        ConfigSaveResult result = await h.Config.SaveAsync(loaded, h.Config.Revision, CancellationToken.None);
        Assert.False(result.Conflict);
        h.Notifier.PublishConfigurationChanged(result.Revision);

        // 允许调度器处理信号并创建 plan-c 的等待 timer。
        await Task.Delay(100);

        // 小步推进（步进 + 让步），直到 plan-c 触发；plan-c 必须在 plan-a 的下一次
        // （起点后 5 分钟）之前触发，证明保存后立即重算生效。
        for (int i = 0; i < 20 && !h.Store.CreatedPlanIds().Contains("plan-c"); i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(50);
        }

        Assert.Contains("plan-c", h.Store.CreatedPlanIds());
        Assert.True(
            h.Time.GetUtcNow().UtcDateTime < h.StartUtc.AddMinutes(5),
            "plan-c 应在 plan-a 的下一次（起点后 5 分钟）之前触发，说明配置变更立即重算。");
    }

    [Fact]
    public async Task LowFrequencyCron_DoesNotCrashAndFiresOnSchedule()
    {
        // 起点：本地 2026-01-01 00:00 前 400 天 → 等待约 400 天，远超 Task.Delay 上限。
        DateTime utcStart = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 1, 1, 0, 0, 0), TimeZoneInfo.Local)
            .AddDays(-400);
        await using Harness h = await Harness.CreateAsync(YearlyPlanConfig, utcStart);
        await WaitForAsync(() => h.Time.TimerCount >= 1, TimeSpan.FromSeconds(10));

        // 跨多个 24 小时分段推进，调度器应持续等待且不崩溃。
        for (int i = 0; i < 4; i++)
        {
            h.Time.Advance(TimeSpan.FromHours(25));
            await Task.Delay(50);
        }

        Assert.False(h.SchedulerTask.IsFaulted, "低频 Cron 分段等待不应导致调度器崩溃。");

        // 继续推进到 2026-01-01 00:00:01（本地），计划应触发。
        DateTime targetUtc = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 1, 1, 0, 0, 1), TimeZoneInfo.Local);
        while (h.Time.GetUtcNow().UtcDateTime < targetUtc)
        {
            DateTime now = h.Time.GetUtcNow().UtcDateTime;
            TimeSpan remaining = targetUtc - now;
            h.Time.Advance(remaining > TimeSpan.FromHours(25) ? TimeSpan.FromHours(25) : remaining);
            await Task.Delay(50);
        }

        await WaitForAsync(() => h.Store.CreatedPlanIds().Contains("plan-a"), TimeSpan.FromSeconds(10));
        Assert.False(h.SchedulerTask.IsFaulted);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待条件在超时内未满足。");
            }

            await Task.Delay(20);
        }
    }

    private static DateTime NextLocalHourStartUtc()
    {
        DateTime localNow = TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.Local);
        DateTime localNextHour = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0).AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(localNextHour, TimeZoneInfo.Local);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly string _dir;

        public Harness(
            FakeTimeProvider time,
            ConfigManager config,
            RuntimeNotifier notifier,
            InMemoryExecutionStore store,
            ProbeExecutor executor,
            TestProbeScheduler scheduler,
            Task schedulerTask,
            string dir,
            string configPath,
            DateTime startUtc)
        {
            Time = time;
            Config = config;
            Notifier = notifier;
            Store = store;
            Executor = executor;
            Scheduler = scheduler;
            SchedulerTask = schedulerTask;
            _dir = dir;
            ConfigPath = configPath;
            StartUtc = startUtc;
        }

        public FakeTimeProvider Time { get; }

        public ConfigManager Config { get; }

        public RuntimeNotifier Notifier { get; }

        public InMemoryExecutionStore Store { get; }

        public ProbeExecutor Executor { get; }

        public TestProbeScheduler Scheduler { get; }

        public Task SchedulerTask { get; set; } = Task.CompletedTask;

        public string ConfigPath { get; }

        public DateTime StartUtc { get; }

        public static async Task<Harness> CreateAsync(string configJson, DateTime? startUtc = null)
        {
            string dir = Path.Combine(Path.GetTempPath(), "nettest-scheduler-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string configPath = Path.Combine(dir, "nettest.json");
            await File.WriteAllTextAsync(configPath, configJson, new UTF8Encoding(false));

            var config = new ConfigManager(configPath, configPath + ".bak", dir);
            await config.InitializeAsync();

            DateTime effectiveStartUtc = startUtc ?? NextLocalHourStartUtc().AddSeconds(-30);
            var time = new FakeTimeProvider(new DateTimeOffset(effectiveStartUtc));

            var notifier = new RuntimeNotifier();
            var store = new InMemoryExecutionStore();
            var capacity = new CapacityNoticeService(
                new EmptyNetTestQueries(),
                notifier,
                config,
                NullLogger<CapacityNoticeService>.Instance);
            var executor = new ProbeExecutor(
                store,
                new ImmediateProbeRegistry(),
                notifier,
                config,
                capacity,
                time,
                NullLogger<ProbeExecutor>.Instance,
                queueCapacity: 256,
                maxConcurrency: 4);
            executor.Start();
            var scheduler = new TestProbeScheduler(
                config,
                executor,
                notifier,
                time,
                NullLogger<ProbeScheduler>.Instance);

            var harness = new Harness(
                time,
                config,
                notifier,
                store,
                executor,
                scheduler,
                Task.CompletedTask,
                dir,
                configPath,
                effectiveStartUtc);
            harness.SchedulerTask = scheduler.RunAsync(harness._cts.Token);
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await SchedulerTask;
            }
            catch (OperationCanceledException)
            {
            }

            await Executor.DisposeAsync();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
