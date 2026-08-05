using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using NetTest.Core.Configuration;
using NetTest.Core.Enums;

namespace NetTest.Core.Probes;

/// <summary>
/// Tracert 探针：从 TTL 1 开始逐跳探测，每跳 attemptsPerHop 次；到达目标即结束；
/// 取消时保留已完成 hops（TechSpec 6.3）。
/// </summary>
public sealed class TracertProbe : IProbe
{
    public ProbeType Type => ProbeType.Tracert;

    public async Task<ProbeMeasurement> ExecuteAsync(
        ProbeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var config = (TracertProbeConfiguration)context.Configuration.Configuration;
        IPAddress? targetAddress = context.ResolvedAddress;
        string target = targetAddress?.ToString() ?? config.Target;

        if (targetAddress is null && IPAddress.TryParse(target, out IPAddress? literal))
        {
            targetAddress = literal;
        }

        var metrics = new TracertMetricsV1();
        bool cancelled = false;
        bool reached = false;

        try
        {
            using var ping = new Ping();
            for (int hop = 1; hop <= config.MaxHops; hop++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var hopMetrics = new TracertHop { Index = hop };
                for (int attempt = 0; attempt < config.AttemptsPerHop; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var options = new PingOptions { Ttl = hop };
                    var watch = Stopwatch.StartNew();
                    PingReply reply = await ping.SendPingAsync(
                        targetAddress ?? IPAddress.Parse(target),
                        TimeSpan.FromMilliseconds(config.TimeoutMs),
                        new byte[32],
                        options,
                        cancellationToken: cancellationToken);
                    watch.Stop();

                    if (reply.Status == IPStatus.Success)
                    {
                        hopMetrics.Address ??= reply.Address?.ToString();
                        hopMetrics.Attempts.Add(watch.ElapsedMilliseconds);

                        if (targetAddress is not null && reply.Address is not null && reply.Address.Equals(targetAddress))
                        {
                            reached = true;
                        }
                    }
                    else
                    {
                        hopMetrics.Attempts.Add(null);
                    }
                }

                metrics.Hops.Add(hopMetrics);
                if (reached)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }

        metrics.ReachedTarget = reached;
        metrics.TotalHops = metrics.Hops.Count;

        ProbeOutcome outcome;
        string? errorCode = null;
        string? errorMessage = null;
        if (reached)
        {
            outcome = ProbeOutcome.Success;
        }
        else if (!cancelled)
        {
            outcome = ProbeOutcome.TargetNotReached;
            errorCode = "TargetNotReached";
            errorMessage = $"达到最大跳数 {config.MaxHops} 仍未到达目标。";
        }
        else
        {
            outcome = ProbeOutcome.None;
        }

        return new ProbeMeasurement(
            !cancelled && reached,
            outcome,
            null,
            ProbeMetrics.SchemaVersion,
            metrics,
            errorCode,
            errorMessage);
    }
}
