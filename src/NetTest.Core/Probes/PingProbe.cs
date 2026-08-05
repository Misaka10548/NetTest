using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using NetTest.Core.Configuration;
using NetTest.Core.Enums;

namespace NetTest.Core.Probes;

/// <summary>
/// Ping 探针：按序串行发送 packetCount 次，取消时保留已完成响应与部分统计（TechSpec 6.2）。
/// 使用执行上下文中的 ResolvedAddress（若提供）保证记录实际连接 IP。
/// </summary>
public sealed class PingProbe : IProbe
{
    public ProbeType Type => ProbeType.Ping;

    public async Task<ProbeMeasurement> ExecuteAsync(
        ProbeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var config = (PingProbeConfiguration)context.Configuration.Configuration;
        IPAddress target = context.ResolvedAddress ?? ResolveTarget(config.Target);

        byte[] buffer = new byte[config.PayloadSize > 0 ? config.PayloadSize : 32];
        var samples = new List<PingSample>();
        bool cancelled = false;
        bool unreachable = false;

        try
        {
            using var ping = new Ping();
            for (int sequence = 1; sequence <= config.PacketCount; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var watch = Stopwatch.StartNew();
                PingReply reply = await ping.SendPingAsync(
                    target,
                    TimeSpan.FromMilliseconds(config.TimeoutMs),
                    buffer,
                    cancellationToken: cancellationToken);
                watch.Stop();

                samples.Add(new PingSample
                {
                    Sequence = sequence,
                    Status = reply.Status == IPStatus.Success ? "Success" : reply.Status.ToString(),
                    RttMs = reply.Status == IPStatus.Success ? watch.ElapsedMilliseconds : null,
                });

                if (reply.Status != IPStatus.Success && reply.Status != IPStatus.TimedOut)
                {
                    unreachable = true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }

        List<long> rtts = samples
            .Where(s => s.RttMs is not null)
            .Select(s => s.RttMs!.Value)
            .ToList();

        var metrics = new PingMetricsV1
        {
            Sent = samples.Count,
            Received = rtts.Count,
            LossPercent = samples.Count > 0 ? (samples.Count - rtts.Count) * 100.0 / samples.Count : null,
            RttMinMs = rtts.Count > 0 ? rtts.Min() : null,
            RttAverageMs = rtts.Count > 0 ? rtts.Average() : null,
            RttMaxMs = rtts.Count > 0 ? rtts.Max() : null,
            JitterMs = ComputeJitter(rtts),
            Samples = samples,
        };

        ProbeOutcome outcome;
        string? errorCode = null;
        string? errorMessage = null;
        if (rtts.Count > 0)
        {
            outcome = ProbeOutcome.Success;
        }
        else if (unreachable)
        {
            outcome = ProbeOutcome.NetworkUnreachable;
            errorCode = "Unreachable";
            errorMessage = "目标不可达。";
        }
        else if (samples.Count > 0)
        {
            outcome = ProbeOutcome.NetworkTimeout;
            errorCode = "Timeout";
            errorMessage = "全部请求超时。";
        }
        else
        {
            outcome = ProbeOutcome.None;
        }

        return new ProbeMeasurement(
            !cancelled && samples.Count == config.PacketCount,
            outcome,
            metrics.RttMinMs,
            ProbeMetrics.SchemaVersion,
            metrics,
            errorCode,
            errorMessage);
    }

    private static double? ComputeJitter(List<long> rtts)
    {
        if (rtts.Count < 2)
        {
            return null;
        }

        long total = 0;
        for (int i = 1; i < rtts.Count; i++)
        {
            total += Math.Abs(rtts[i] - rtts[i - 1]);
        }

        return (double)total / (rtts.Count - 1);
    }

    private static IPAddress ResolveTarget(string target)
    {
        if (IPAddress.TryParse(target, out IPAddress? literal))
        {
            return literal;
        }

        IPAddress[] addresses = Dns.GetHostAddresses(target);
        return addresses.Length > 0 ? addresses[0] : IPAddress.Loopback;
    }
}
