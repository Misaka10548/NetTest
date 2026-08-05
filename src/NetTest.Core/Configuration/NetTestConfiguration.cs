namespace NetTest.Core.Configuration;

/// <summary>配置根模型。camelCase 序列化，未知属性视为验证错误。</summary>
public sealed class NetTestConfiguration
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public HostConfiguration Host { get; set; } = new();
    public StorageConfiguration Storage { get; set; } = new();
    public SchedulerConfiguration Scheduler { get; set; } = new();
    public LoggingConfiguration Logging { get; set; } = new();
    public List<PlanConfiguration> Plans { get; set; } = new();
    public ProbeCollection Probes { get; set; } = new();
}

/// <summary>四类探针列表，显示顺序即数组顺序。</summary>
public sealed class ProbeCollection
{
    public List<PingProbeConfiguration> Ping { get; set; } = new();
    public List<TracertProbeConfiguration> Tracert { get; set; } = new();
    public List<DnsProbeConfiguration> Dns { get; set; } = new();
    public List<HttpsProbeConfiguration> Https { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<ProbeConfiguration> All => Ping.Cast<ProbeConfiguration>()
        .Concat(Tracert)
        .Concat(Dns)
        .Concat(Https);
}

/// <summary>宿主设置。保存后需要重启生效。</summary>
public sealed class HostConfiguration
{
    public List<string> Urls { get; set; } = new() { "http://127.0.0.1:5000" };

    /// <summary>null/空字符串表示禁用认证；非空时启用单用户 Cookie 登录。</summary>
    public string? Password { get; set; }
}

public sealed class StorageConfiguration
{
    public string DatabasePath { get; set; } = "Data/nettest.db";
    public int RetentionDays { get; set; } = 90;
    public int ChartMaxPointsPerSeries { get; set; } = 2000;
}

public sealed class SchedulerConfiguration
{
    public int MaxConcurrency { get; set; } = 10;
    public int QueueCapacity { get; set; } = 256;
    public int CapacityWarningWindow { get; set; } = 10;
    public double CapacityWarningRatio { get; set; } = 0.6;
}

public sealed class LoggingConfiguration
{
    public string MinimumLevel { get; set; } = "Information";
    public string Directory { get; set; } = "Data/Logs";
    public int FileSizeLimitMiB { get; set; } = 10;
    public int RetainedDays { get; set; } = 14;
}

public sealed class PlanConfiguration
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Cron { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
