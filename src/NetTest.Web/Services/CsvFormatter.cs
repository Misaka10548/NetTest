using NetTest.Core.Storage;

namespace NetTest.Web.Services;

/// <summary>
/// CSV 导出格式器：RFC 4180 转义、公式注入防护与行序列化（TechSpec 7.5）。
/// MetricsJson 作为转义后的单列输出。
/// </summary>
internal static class CsvFormatter
{
    public const string Header =
        "runId,probeId,probeName,probeType,planId,triggerKind,addressFamily,status,outcome," +
        "cancellationReason,primaryLatencyMs,startedAtUtc,completedAtUtc,durationMs,errorCode,metricsJson";

    public static string FormatRow(HistoryItem item)
    {
        return string.Join(",",
            Escape(item.RunId.ToString("D")),
            Escape(item.ProbeId),
            Escape(item.ProbeNameSnapshot),
            Escape(item.ProbeType.ToString()),
            Escape(item.PlanId),
            Escape(item.TriggerKind.ToString()),
            Escape(item.AddressFamily?.ToString()),
            Escape(item.Status.ToString()),
            Escape(item.Outcome.ToString()),
            Escape(item.CancellationReason.ToString()),
            Escape(item.PrimaryLatencyMs?.ToString()),
            Escape(item.StartedAtUtc?.ToString("O")),
            Escape(item.CompletedAtUtc?.ToString("O")),
            Escape(item.DurationMs?.ToString()),
            Escape(item.ErrorCode),
            Escape(item.MetricsJson));
    }

    /// <summary>
    /// 转义逗号/引号/换行/CR，并防御 CSV 公式注入：以 =、+、-、@ 开头的值前置单引号，
    /// 避免 Excel 将其解释为公式（探针名等用户可控值）。
    /// </summary>
    public static string? Escape(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.StartsWith('=') || value.StartsWith('+') || value.StartsWith('-') || value.StartsWith('@'))
        {
            value = "'" + value;
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
