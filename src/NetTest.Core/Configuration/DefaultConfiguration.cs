namespace NetTest.Core.Configuration;

/// <summary>首次启动时的默认配置，结构与 TechSpec 2.2 一致。</summary>
public static class DefaultConfiguration
{
    public static NetTestConfiguration Create() => new()
    {
        SchemaVersion = NetTestConfiguration.CurrentSchemaVersion,
        Host = new HostConfiguration
        {
            Urls = new List<string> { "http://127.0.0.1:5000" },
            Password = null,
        },
        Storage = new StorageConfiguration
        {
            DatabasePath = "Data/nettest.db",
            RetentionDays = 90,
            ChartMaxPointsPerSeries = 2000,
        },
        Scheduler = new SchedulerConfiguration
        {
            MaxConcurrency = 10,
            QueueCapacity = 256,
            CapacityWarningWindow = 10,
            CapacityWarningRatio = 0.6,
        },
        Logging = new LoggingConfiguration
        {
            MinimumLevel = "Information",
            Directory = "Data/Logs",
            FileSizeLimitMiB = 10,
            RetainedDays = 14,
        },
        Plans = new List<PlanConfiguration>
        {
            new()
            {
                Id = "default-five-minutes",
                Name = "默认五分钟计划",
                Cron = "*/5 * * * *",
                Enabled = false,
            },
        },
        Probes = new ProbeCollection(),
    };
}
