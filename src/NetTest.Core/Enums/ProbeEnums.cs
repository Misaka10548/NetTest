namespace NetTest.Core.Enums;

/// <summary>探针类型。</summary>
public enum ProbeType
{
    Ping,
    Tracert,
    Dns,
    Https,
}

/// <summary>Run/Execution 的触发来源。</summary>
public enum TriggerKind
{
    Scheduled,
    Manual,
}

/// <summary>执行状态：描述调度/执行是否完整，与网络测量结果（ProbeOutcome）严格分离。</summary>
public enum ExecutionStatus
{
    Pending,
    Running,
    Completed,
    Incomplete,
    Cancelled,
}

/// <summary>取消原因。None 表示未发生调度或生命周期取消。</summary>
public enum CancellationReason
{
    None,
    SupersededByNextRun,
    ApplicationExit,
}

/// <summary>地址族。</summary>
public enum NetworkAddressFamily
{
    IPv4,
    IPv6,
}

/// <summary>测量结果类别：描述网络测量得到什么结论。</summary>
public enum ProbeOutcome
{
    None,
    Success,
    NetworkTimeout,
    DnsError,
    ConnectionRefused,
    NetworkUnreachable,
    TlsError,
    HttpError,
    TargetNotReached,
    ResponseLimitExceeded,
    InternalError,
}
