using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using DnsClient;
using DnsClient.Protocol;
using NetTest.Core.Configuration;
using NetTest.Core.Enums;

namespace NetTest.Core.Probes;

/// <summary>
/// DNS 探针：使用 DnsClient 直接查询，禁用客户端缓存；
/// SystemDirect 读取活动网卡 DNS 服务器，Custom 使用配置地址（TechSpec 6.4）。
/// </summary>
public sealed class DnsProbe : IProbe
{
    public ProbeType Type => ProbeType.Dns;

    public async Task<ProbeMeasurement> ExecuteAsync(
        ProbeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var config = (DnsProbeConfiguration)context.Configuration.Configuration;

        NameServer[] nameServers = config.Resolver.Mode switch
        {
            DnsResolverMode.Custom => ParseNameServers(config.Resolver.Addresses),
            _ => GetSystemDnsServers().Select(address => new NameServer(address)).ToArray(),
        };

        if (nameServers.Length == 0)
        {
            var empty = new DnsMetricsV1 { Resolver = null, ResponseCode = "NoDnsServers" };
            return new ProbeMeasurement(
                true,
                ProbeOutcome.DnsError,
                null,
                ProbeMetrics.SchemaVersion,
                empty,
                "NoDnsServers",
                "未找到可用的系统 DNS 服务器。");
        }

        var lookup = new LookupClient(new LookupClientOptions(nameServers)
        {
            UseCache = false,
            UseTcpFallback = true,
            Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs),
        });

        var metrics = new DnsMetricsV1 { Resolver = nameServers[0].ToString() };
        var watch = Stopwatch.StartNew();
        bool cancelled = false;
        bool anySuccess = false;
        string? worstCode = null;

        try
        {
            foreach (string recordType in config.RecordTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                QueryType queryType = recordType switch
                {
                    "A" => QueryType.A,
                    "AAAA" => QueryType.AAAA,
                    "CNAME" => QueryType.CNAME,
                    "MX" => QueryType.MX,
                    _ => throw new InvalidOperationException($"不支持的记录类型 {recordType}。"),
                };

                IDnsQueryResponse response = await lookup.QueryAsync(
                    config.QueryName,
                    queryType,
                    QueryClass.IN,
                    cancellationToken);

                string code = response.Header.ResponseCode.ToString();
                worstCode ??= code;
                if (code != "NoError")
                {
                    worstCode = code;
                }

                if (response.Header.ResponseCode.ToString() == "NoError")
                {
                    anySuccess = true;
                    foreach (DnsResourceRecord answer in response.Answers)
                    {
                        switch (answer)
                        {
                            case ARecord a:
                                metrics.Answers.Add(new DnsAnswer { Type = "A", Value = a.Address.ToString(), TtlSeconds = a.TimeToLive });
                                break;
                            case AaaaRecord aaaa:
                                metrics.Answers.Add(new DnsAnswer { Type = "AAAA", Value = aaaa.Address.ToString(), TtlSeconds = aaaa.TimeToLive });
                                break;
                            case CNameRecord cname:
                                metrics.Answers.Add(new DnsAnswer { Type = "CNAME", Value = cname.CanonicalName.ToString().TrimEnd('.'), TtlSeconds = cname.TimeToLive });
                                metrics.CnameChain.Add(cname.CanonicalName.ToString().TrimEnd('.'));
                                break;
                            case MxRecord mx:
                                metrics.Answers.Add(new DnsAnswer { Type = "MX", Value = mx.Exchange.ToString().TrimEnd('.'), TtlSeconds = mx.TimeToLive });
                                break;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (DnsResponseException ex)
        {
            worstCode = ex.Code.ToString();
            metrics.ResponseCode = worstCode;
        }
        catch (Exception) when (worstCode is null)
        {
            worstCode ??= "Timeout";
        }

        watch.Stop();
        metrics.ElapsedMs = watch.ElapsedMilliseconds;
        metrics.ResponseCode ??= worstCode;

        ProbeOutcome outcome;
        string? errorCode = null;
        string? errorMessage = null;
        if (anySuccess)
        {
            outcome = ProbeOutcome.Success;
        }
        else if (worstCode == "Timeout")
        {
            outcome = ProbeOutcome.NetworkTimeout;
            errorCode = "Timeout";
            errorMessage = "DNS 查询超时。";
        }
        else
        {
            outcome = ProbeOutcome.DnsError;
            errorCode = worstCode;
            errorMessage = $"DNS 查询失败：{worstCode}";
        }

        return new ProbeMeasurement(
            !cancelled,
            outcome,
            metrics.ElapsedMs,
            ProbeMetrics.SchemaVersion,
            metrics,
            errorCode,
            errorMessage);
    }

    private static IPAddress[] GetSystemDnsServers()
    {
        var servers = new List<IPAddress>();
        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties = networkInterface.GetIPProperties();
            foreach (IPAddress dns in properties.DnsAddresses)
            {
                if (!servers.Contains(dns))
                {
                    servers.Add(dns);
                }
            }
        }

        return servers.ToArray();
    }

    /// <summary>
    /// 解析自定义解析器地址，支持 "ip" 或 "ip:port"（测试与非常规端口场景）。
    /// 配置验证仍只接受纯 IP 字面量。
    /// </summary>
    private static NameServer[] ParseNameServers(IReadOnlyList<string> addresses)
    {
        return addresses.Select(address =>
        {
            int lastColon = address.LastIndexOf(':');
            if (lastColon > 0
                && lastColon < address.Length - 1
                && int.TryParse(address[(lastColon + 1)..], out int port)
                && port > 0
                && port < 65536
                && IPAddress.TryParse(address[..lastColon], out IPAddress? ipWithPort))
            {
                return new NameServer(ipWithPort, port);
            }

            return new NameServer(IPAddress.Parse(address));
        }).ToArray();
    }
}