using System.Net;
using System.Text.RegularExpressions;
using NetTest.Core.Enums;
using Cronos;

namespace NetTest.Core.Configuration;

/// <summary>
/// 配置完整校验。错误使用结构化字段路径，例如 <c>probes.https[0].url</c>。
/// </summary>
public static class ConfigValidator
{
    private static readonly Regex IdRegex = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedRecordTypes = new(StringComparer.Ordinal) { "A", "AAAA", "CNAME", "MX" };

    private static readonly HashSet<string> AllowedLogLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Trace", "Debug", "Information", "Warning", "Error", "Critical",
    };

    public static void Validate(NetTestConfiguration config, string baseDirectory)
    {
        var errors = new List<ConfigError>();
        ValidateGlobal(config, errors);
        ValidateHost(config.Host, errors);
        ValidateStorage(config.Storage, baseDirectory, errors);
        ValidateScheduler(config.Scheduler, errors);
        ValidateLogging(config.Logging, baseDirectory, errors);
        ValidatePlans(config, errors);
        ValidateProbes(config, errors);
        ValidatePlanReferences(config, errors);

        if (errors.Count > 0)
        {
            throw new ConfigValidationException(errors);
        }
    }

    private static void ValidateGlobal(NetTestConfiguration config, List<ConfigError> errors)
    {
        if (config.SchemaVersion != NetTestConfiguration.CurrentSchemaVersion)
        {
            errors.Add(new("schemaVersion", $"不支持的配置版本 {config.SchemaVersion}，当前仅支持 {NetTestConfiguration.CurrentSchemaVersion}。"));
        }
    }

    private static void ValidateHost(HostConfiguration host, List<ConfigError> errors)
    {
        if (host.Urls.Count is < 1 or > 8)
        {
            errors.Add(new("host.urls", "必须包含 1 至 8 个监听地址。"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < host.Urls.Count; i++)
        {
            string url = host.Urls[i];
            string path = $"host.urls[{i}]";
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || uri.Scheme != Uri.UriSchemeHttp)
            {
                errors.Add(new(path, "必须是绝对 HTTP URL（v1 不接受 HTTPS，TLS 由反向代理终止）。"));
                continue;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                errors.Add(new(path, "禁止包含 user info（用户名/密码）。"));
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                errors.Add(new(path, "禁止包含 query 或 fragment。"));
            }

            if (uri.AbsolutePath != "/")
            {
                errors.Add(new(path, "path 只能为 /。"));
            }

            if (!seen.Add(url))
            {
                errors.Add(new(path, "监听地址重复。"));
            }
        }

        if (host.Password is not null && (host.Password.Length is < 1 or > 256))
        {
            errors.Add(new("host.password", "密码长度必须为 1 至 256 个字符。"));
        }
    }

    private static void ValidateStorage(StorageConfiguration storage, string baseDirectory, List<ConfigError> errors)
    {
        if (storage.RetentionDays is < 1 or > 3650)
        {
            errors.Add(new("storage.retentionDays", "必须为 1 至 3650。"));
        }

        if (storage.ChartMaxPointsPerSeries < 1)
        {
            errors.Add(new("storage.chartMaxPointsPerSeries", "必须大于等于 1。"));
        }

        ValidateRelativePath("storage.databasePath", storage.DatabasePath, baseDirectory, errors);
    }

    private static void ValidateScheduler(SchedulerConfiguration scheduler, List<ConfigError> errors)
    {
        if (scheduler.MaxConcurrency < 1)
        {
            errors.Add(new("scheduler.maxConcurrency", "必须大于等于 1。"));
        }

        if (scheduler.QueueCapacity < 1)
        {
            errors.Add(new("scheduler.queueCapacity", "必须大于等于 1。"));
        }

        if (scheduler.CapacityWarningWindow < 1)
        {
            errors.Add(new("scheduler.capacityWarningWindow", "必须大于等于 1。"));
        }

        if (scheduler.CapacityWarningRatio is <= 0 or > 1)
        {
            errors.Add(new("scheduler.capacityWarningRatio", "必须大于 0 且小于等于 1。"));
        }
    }

    private static void ValidateLogging(LoggingConfiguration logging, string baseDirectory, List<ConfigError> errors)
    {
        if (!AllowedLogLevels.Contains(logging.MinimumLevel))
        {
            errors.Add(new("logging.minimumLevel", $"必须是 Trace/Debug/Information/Warning/Error/Critical 之一。"));
        }

        if (logging.FileSizeLimitMiB < 1)
        {
            errors.Add(new("logging.fileSizeLimitMiB", "必须大于等于 1。"));
        }

        if (logging.RetainedDays < 1)
        {
            errors.Add(new("logging.retainedDays", "必须大于等于 1。"));
        }

        ValidateRelativePath("logging.directory", logging.Directory, baseDirectory, errors);
    }

    private static void ValidateRelativePath(string fieldPath, string value, string baseDirectory, List<ConfigError> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            errors.Add(new(fieldPath, "必须是相对于程序目录的相对路径。"));
            return;
        }

        string full = Path.GetFullPath(Path.Combine(baseDirectory, value));
        string normalizedBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        bool underBase = full.Equals(normalizedBase, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(normalizedBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!underBase)
        {
            errors.Add(new(fieldPath, "规范化后不得逃逸程序目录。"));
        }
    }

    private static void ValidatePlans(NetTestConfiguration config, List<ConfigError> errors)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < config.Plans.Count; i++)
        {
            PlanConfiguration plan = config.Plans[i];
            string path = $"plans[{i}]";

            if (!IsValidId(plan.Id))
            {
                errors.Add(new($"{path}.id", "ID 必须匹配 ^[a-z0-9][a-z0-9._-]{0,63}$。"));
            }
            else if (!ids.Add(plan.Id))
            {
                errors.Add(new($"{path}.id", $"计划 ID \"{plan.Id}\" 重复。"));
            }

            if (plan.Name.Length is < 1 or > 100)
            {
                errors.Add(new($"{path}.name", "名称长度必须为 1 至 100 个字符。"));
            }

            if (string.IsNullOrWhiteSpace(plan.Cron))
            {
                errors.Add(new($"{path}.cron", "Cron 不能为空。"));
            }
            else
            {
                try
                {
                    _ = CronExpression.Parse(plan.Cron, CronFormat.Standard);
                }
                catch (CronFormatException)
                {
                    errors.Add(new($"{path}.cron", "Cron 必须是有效的标准五字段表达式。"));
                }
            }
        }
    }

    private static void ValidateProbes(NetTestConfiguration config, List<ConfigError> errors)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var planIds = config.Plans.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        ValidateProbeList(config.Probes.Ping, "ping", ids, planIds, errors);
        ValidateProbeList(config.Probes.Tracert, "tracert", ids, planIds, errors);
        ValidateProbeList(config.Probes.Dns, "dns", ids, planIds, errors);
        ValidateProbeList(config.Probes.Https, "https", ids, planIds, errors);
    }

    private static void ValidateProbeList(
        IReadOnlyList<ProbeConfiguration> probes,
        string typeName,
        HashSet<string> ids,
        HashSet<string> planIds,
        List<ConfigError> errors)
    {
        for (int i = 0; i < probes.Count; i++)
        {
            ProbeConfiguration probe = probes[i];
            string path = $"probes.{typeName}[{i}]";
            ValidateCommonFields(probe, path, ids, planIds, errors);

            switch (probe)
            {
                case PingProbeConfiguration ping:
                    ValidatePing(ping, path, errors);
                    break;
                case TracertProbeConfiguration tracert:
                    ValidateTracert(tracert, path, errors);
                    break;
                case DnsProbeConfiguration dns:
                    ValidateDns(dns, path, errors);
                    break;
                case HttpsProbeConfiguration https:
                    ValidateHttps(https, path, errors);
                    break;
            }
        }
    }

    private static void ValidateCommonFields(
        ProbeConfiguration probe,
        string path,
        HashSet<string> ids,
        HashSet<string> planIds,
        List<ConfigError> errors)
    {
        if (!IsValidId(probe.Id))
        {
            errors.Add(new($"{path}.id", "ID 必须匹配 ^[a-z0-9][a-z0-9._-]{0,63}$。"));
        }
        else if (!ids.Add(probe.Id))
        {
            errors.Add(new($"{path}.id", $"探针 ID \"{probe.Id}\" 在全部探针类型中重复。"));
        }

        if (probe.Name.Length is < 1 or > 100)
        {
            errors.Add(new($"{path}.name", "名称长度必须为 1 至 100 个字符。"));
        }

        if (probe.GroupId is not null && probe.GroupId.Length > 0 && !IsValidId(probe.GroupId))
        {
            errors.Add(new($"{path}.groupId", "groupId 必须匹配 ^[a-z0-9][a-z0-9._-]{0,63}$ 或为空。"));
        }

        var seenTags = new HashSet<string>(StringComparer.Ordinal);
        if (probe.Tags.Count > 16)
        {
            errors.Add(new($"{path}.tags", "标签最多 16 项。"));
        }

        for (int t = 0; t < probe.Tags.Count; t++)
        {
            string tag = probe.Tags[t];
            if (tag.Length is < 1 or > 32)
            {
                errors.Add(new($"{path}.tags[{t}]", "每项标签长度必须为 1 至 32 个字符。"));
            }

            if (!seenTags.Add(tag))
            {
                errors.Add(new($"{path}.tags[{t}]", "标签重复。"));
            }
        }

        if (probe.PlanIds.Count < 1)
        {
            errors.Add(new($"{path}.planIds", "至少引用一个计划。"));
        }

        var seenPlans = new HashSet<string>(StringComparer.Ordinal);
        for (int p = 0; p < probe.PlanIds.Count; p++)
        {
            string planId = probe.PlanIds[p];
            if (!seenPlans.Add(planId))
            {
                errors.Add(new($"{path}.planIds[{p}]", "计划引用重复。"));
            }

            if (!planIds.Contains(planId))
            {
                errors.Add(new($"{path}.planIds[{p}]", $"引用了不存在的计划 ID \"{planId}\"。"));
            }
        }
    }

    private static void ValidateTarget(string target, string path, List<ConfigError> errors)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            errors.Add(new(path, "目标不能为空。"));
            return;
        }

        if (IPAddress.TryParse(target, out _))
        {
            return;
        }

        if (target.Length > 253
            || target.Any(char.IsWhiteSpace)
            || target.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            errors.Add(new(path, "目标必须是有效的 IP 字面量或域名。"));
        }
    }

    private static void ValidatePing(PingProbeConfiguration ping, string path, List<ConfigError> errors)
    {
        ValidateTarget(ping.Target, $"{path}.target", errors);

        if (ping.PacketCount is < 1 or > 20)
        {
            errors.Add(new($"{path}.packetCount", "必须为 1 至 20。"));
        }

        if (ping.TimeoutMs is < 100 or > 60000)
        {
            errors.Add(new($"{path}.timeoutMs", "必须为 100 至 60000。"));
        }

        if (ping.PayloadSize is < 0 or > 65500)
        {
            errors.Add(new($"{path}.payloadSize", "必须为 0 至 65500。"));
        }
    }

    private static void ValidateTracert(TracertProbeConfiguration tracert, string path, List<ConfigError> errors)
    {
        ValidateTarget(tracert.Target, $"{path}.target", errors);

        if (tracert.MaxHops is < 1 or > 64)
        {
            errors.Add(new($"{path}.maxHops", "必须为 1 至 64。"));
        }

        if (tracert.AttemptsPerHop is < 1 or > 5)
        {
            errors.Add(new($"{path}.attemptsPerHop", "必须为 1 至 5。"));
        }

        if (tracert.TimeoutMs is < 100 or > 60000)
        {
            errors.Add(new($"{path}.timeoutMs", "必须为 100 至 60000。"));
        }
    }

    private static void ValidateDns(DnsProbeConfiguration dns, string path, List<ConfigError> errors)
    {
        if (string.IsNullOrWhiteSpace(dns.QueryName) || dns.QueryName.Length > 253)
        {
            errors.Add(new($"{path}.queryName", "查询名称必须是有效的域名。"));
        }

        if (dns.RecordTypes.Count < 1)
        {
            errors.Add(new($"{path}.recordTypes", "至少选择一项记录类型。"));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < dns.RecordTypes.Count; i++)
        {
            string type = dns.RecordTypes[i];
            if (!AllowedRecordTypes.Contains(type))
            {
                errors.Add(new($"{path}.recordTypes[{i}]", "允许 A/AAAA/CNAME/MX。"));
            }

            if (!seen.Add(type))
            {
                errors.Add(new($"{path}.recordTypes[{i}]", "记录类型重复。"));
            }
        }

        if (dns.Resolver.Mode == DnsResolverMode.Custom)
        {
            if (dns.Resolver.Addresses.Count < 1)
            {
                errors.Add(new($"{path}.resolver.addresses", "Custom 模式必须提供一个或多个解析器地址。"));
            }

            for (int i = 0; i < dns.Resolver.Addresses.Count; i++)
            {
                if (!IPAddress.TryParse(dns.Resolver.Addresses[i], out _))
                {
                    errors.Add(new($"{path}.resolver.addresses[{i}]", "必须是 IP 字面量。"));
                }
            }
        }

        if (dns.TimeoutMs is < 100 or > 60000)
        {
            errors.Add(new($"{path}.timeoutMs", "必须为 100 至 60000。"));
        }
    }

    private static void ValidateHttps(HttpsProbeConfiguration https, string path, List<ConfigError> errors)
    {
        if (!Uri.TryCreate(https.Url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add(new($"{path}.url", "必须是绝对 HTTPS URL。"));
        }
        else if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errors.Add(new($"{path}.url", "禁止在 URL 中嵌入用户名或密码。"));
        }

        if (https.Proxy.Mode == ProxyMode.Custom)
        {
            if (string.IsNullOrWhiteSpace(https.Proxy.Url)
                || !Uri.TryCreate(https.Proxy.Url, UriKind.Absolute, out Uri? proxyUri)
                || (proxyUri.Scheme != Uri.UriSchemeHttp && proxyUri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add(new($"{path}.proxy.url", "Custom 模式必须提供 HTTP 或 HTTPS 代理 URI。"));
            }
        }

        if (https.TimeoutMs is < 1000 or > 300000)
        {
            errors.Add(new($"{path}.timeoutMs", "必须为 1000 至 300000。"));
        }

        if (https.MaxRedirects is < 0 or > 10)
        {
            errors.Add(new($"{path}.maxRedirects", "必须为 0 至 10。"));
        }

        if (https.MaxResponseBytes is < 1024 or > 1073741824)
        {
            errors.Add(new($"{path}.maxResponseBytes", "必须为 1024 至 1073741824。"));
        }
    }

    private static void ValidatePlanReferences(NetTestConfiguration config, List<ConfigError> errors)
    {
        var probes = config.Probes.All.ToList();
        foreach (PlanConfiguration plan in config.Plans.Where(p => p.Enabled))
        {
            bool referenced = probes.Any(p => p.Enabled && p.PlanIds.Contains(plan.Id));
            if (!referenced)
            {
                errors.Add(new($"plans", $"启用的计划 \"{plan.Id}\" 必须被至少一个启用探针引用。"));
            }
        }
    }

    private static bool IsValidId(string id) => id.Length is >= 1 and <= 64 && IdRegex.IsMatch(id);
}
