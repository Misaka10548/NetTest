using NetTest.Core.Enums;

namespace NetTest.Core.Configuration;

/// <summary>探针配置公共字段。</summary>
public abstract class ProbeConfiguration
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>同组串行与 UI 分组；空值时使用探针 ID。</summary>
    public string? GroupId { get; set; }

    public List<string> Tags { get; set; } = new();
    public List<string> PlanIds { get; set; } = new();

    public abstract ProbeType Type { get; }
}

public sealed class PingProbeConfiguration : ProbeConfiguration
{
    public override ProbeType Type => ProbeType.Ping;
    public string Target { get; set; } = "";
    public int PacketCount { get; set; } = 4;
    public int TimeoutMs { get; set; } = 3000;
    public int PayloadSize { get; set; } = 32;
}

public sealed class TracertProbeConfiguration : ProbeConfiguration
{
    public override ProbeType Type => ProbeType.Tracert;
    public string Target { get; set; } = "";
    public int MaxHops { get; set; } = 30;
    public int AttemptsPerHop { get; set; } = 3;
    public int TimeoutMs { get; set; } = 3000;
}

public enum DnsResolverMode
{
    SystemDirect,
    Custom,
}

public sealed class DnsResolverConfiguration
{
    public DnsResolverMode Mode { get; set; } = DnsResolverMode.SystemDirect;
    public List<string> Addresses { get; set; } = new();
}

public sealed class DnsProbeConfiguration : ProbeConfiguration
{
    public override ProbeType Type => ProbeType.Dns;
    public string QueryName { get; set; } = "";
    public List<string> RecordTypes { get; set; } = new() { "A", "AAAA" };
    public DnsResolverConfiguration Resolver { get; set; } = new();
    public int TimeoutMs { get; set; } = 5000;
}

public enum ProxyMode
{
    Direct,
    System,
    Custom,
}

public sealed class ProxyConfiguration
{
    public ProxyMode Mode { get; set; } = ProxyMode.Direct;
    public string? Url { get; set; }
    public string? Username { get; set; }

    /// <summary>敏感字段：不得进入 UI 回显、日志、导出或持久化快照。</summary>
    public string? Password { get; set; }
}

public sealed class HttpsProbeConfiguration : ProbeConfiguration
{
    public override ProbeType Type => ProbeType.Https;
    public string Url { get; set; } = "";
    public ProxyConfiguration Proxy { get; set; } = new();
    public int TimeoutMs { get; set; } = 30000;
    public int MaxRedirects { get; set; } = 5;
    public long MaxResponseBytes { get; set; } = 10 * 1024 * 1024;
    public bool AllowInvalidCertificate { get; set; }
}
