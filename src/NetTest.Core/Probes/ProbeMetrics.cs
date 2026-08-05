namespace NetTest.Core.Probes;

/// <summary>Metrics v1 模型（TechSpec 6.2-6.5）。camelCase 序列化。未发生阶段为 null。</summary>
public static class ProbeMetrics
{
    public const int SchemaVersion = 1;
}

public sealed class PingMetricsV1
{
    public int Sent { get; set; }

    public int Received { get; set; }

    public double? LossPercent { get; set; }

    public long? RttMinMs { get; set; }

    public double? RttAverageMs { get; set; }

    public long? RttMaxMs { get; set; }

    public double? JitterMs { get; set; }

    public List<PingSample> Samples { get; set; } = new();
}

public sealed class PingSample
{
    public int Sequence { get; set; }

    public string Status { get; set; } = "";

    public long? RttMs { get; set; }
}

public sealed class TracertMetricsV1
{
    public bool ReachedTarget { get; set; }

    public int TotalHops { get; set; }

    public List<TracertHop> Hops { get; set; } = new();
}

public sealed class TracertHop
{
    public int Index { get; set; }

    public string? Address { get; set; }

    /// <summary>每次尝试的 RTT；null 表示无响应。</summary>
    public List<long?> Attempts { get; set; } = new();
}

public sealed class DnsMetricsV1
{
    public string? Resolver { get; set; }

    public string? ResponseCode { get; set; }

    public long? ElapsedMs { get; set; }

    public List<DnsAnswer> Answers { get; set; } = new();

    public List<string> CnameChain { get; set; } = new();
}

public sealed class DnsAnswer
{
    public string Type { get; set; } = "";

    public string Value { get; set; } = "";

    public long TtlSeconds { get; set; }
}

public sealed class HttpsMetricsV1
{
    public long? DnsMs { get; set; }

    public long? TcpConnectMs { get; set; }

    public long? TlsHandshakeMs { get; set; }

    public long? TimeToFirstByteMs { get; set; }

    public long? DownloadMs { get; set; }

    public long? TotalMs { get; set; }

    public int? StatusCode { get; set; }

    public string? FinalUri { get; set; }

    public List<RedirectStep> Redirects { get; set; } = new();

    public DateTime? CertificateExpiresAtUtc { get; set; }

    public long? BytesRead { get; set; }

    public bool ResponseLimitReached { get; set; }

    public bool CertificateInvalid { get; set; }
}

public sealed class RedirectStep
{
    public int StatusCode { get; set; }

    public string? Location { get; set; }
}
