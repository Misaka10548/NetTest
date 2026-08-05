using System.Net;
using DnsClient;
using NetTest.Core.Configuration;
using NetTest.Core.Enums;

namespace NetTest.Core.Scheduling;

/// <summary>展开出的执行目标：地址族 + 实际地址（代理模式或 DNS 探针为空）。</summary>
public sealed record ProbeAddressTarget(NetworkAddressFamily? AddressFamily, IPAddress? ResolvedAddress);

/// <summary>地址展开结果。DnsFailed=true 时整体解析失败/超时，直接保存为 Completed + DnsError。</summary>
public sealed record AddressExpansion(
    IReadOnlyList<ProbeAddressTarget> Targets,
    bool DnsFailed,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// 地址展开（TechSpec 5.3）：IP 字面量只生成对应地址族的 Execution；域名分别解析 A/AAAA，
/// 每族按响应顺序选择第一个地址；无记录不产生该族结果；整体失败/超时标记 DnsFailed。
/// HTTPS 代理模式与 DNS 探针自身不展开地址族。
/// </summary>
public static class AddressExpander
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(5);

    public static async Task<AddressExpansion> ExpandAsync(
        ProbeConfiguration probe,
        CancellationToken cancellationToken)
    {
        switch (probe)
        {
            case DnsProbeConfiguration:
                return new AddressExpansion([new ProbeAddressTarget(null, null)], false, null, null);

            case HttpsProbeConfiguration https when https.Proxy.Mode != ProxyMode.Direct:
                return new AddressExpansion([new ProbeAddressTarget(null, null)], false, null, null);
        }

        string target = probe switch
        {
            PingProbeConfiguration ping => ping.Target,
            TracertProbeConfiguration tracert => tracert.Target,
            HttpsProbeConfiguration https => new Uri(https.Url).Host,
            _ => throw new InvalidOperationException($"不支持地址展开的探针类型：{probe.GetType().Name}"),
        };

        if (IPAddress.TryParse(target, out IPAddress? literal))
        {
            NetworkAddressFamily family = literal.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? NetworkAddressFamily.IPv4
                : NetworkAddressFamily.IPv6;
            return new AddressExpansion([new ProbeAddressTarget(family, literal)], false, null, null);
        }

        try
        {
            var lookup = new LookupClient(new LookupClientOptions
            {
                UseCache = false,
                UseTcpFallback = true,
                Timeout = ResolveTimeout,
            });

            var targets = new List<ProbeAddressTarget>(2);
            Task<IDnsQueryResponse> aTask = lookup.QueryAsync(target, QueryType.A, QueryClass.IN, cancellationToken);
            Task<IDnsQueryResponse> aaaaTask = lookup.QueryAsync(target, QueryType.AAAA, QueryClass.IN, cancellationToken);
            await Task.WhenAll(aTask, aaaaTask).WaitAsync(ResolveTimeout, cancellationToken);

            IDnsQueryResponse aResponse = aTask.Result;
            IDnsQueryResponse aaaaResponse = aaaaTask.Result;

            AddFirstRecord(aResponse, NetworkAddressFamily.IPv4, targets);
            AddFirstRecord(aaaaResponse, NetworkAddressFamily.IPv6, targets);

            return new AddressExpansion(targets, false, null, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AddressExpansion([], true, "DnsTimeout", $"解析 {target} 超时。");
        }
        catch (Exception ex)
        {
            return new AddressExpansion([], true, "DnsError", $"解析 {target} 失败：{ex.Message}");
        }
    }

    private static void AddFirstRecord(IDnsQueryResponse response, NetworkAddressFamily family, List<ProbeAddressTarget> targets)
    {
        if (response.Answers is null)
        {
            return;
        }

        foreach (DnsClient.Protocol.DnsResourceRecord answer in response.Answers)
        {
            if (answer is DnsClient.Protocol.ARecord aRecord)
            {
                targets.Add(new ProbeAddressTarget(family, aRecord.Address));
                return;
            }

            if (answer is DnsClient.Protocol.AaaaRecord aaaaRecord)
            {
                targets.Add(new ProbeAddressTarget(family, aaaaRecord.Address));
                return;
            }
        }
    }
}
