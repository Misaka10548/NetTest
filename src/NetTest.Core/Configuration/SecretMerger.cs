namespace NetTest.Core.Configuration;

/// <summary>
/// UI DTO 与未回显的密码字段合并。规则：<c>null</c> 表示未修改（保留旧值），
/// 非 null（含空字符串）表示应用新值；空字符串表示清除/禁用。
/// </summary>
public static class SecretMerger
{
    public static void MergeSecrets(NetTestConfiguration current, NetTestConfiguration incoming)
    {
        if (incoming.Host.Password is null)
        {
            incoming.Host.Password = current.Host.Password;
        }

        var currentByProbeId = current.Probes.Https
            .Where(p => p.Id.Length > 0)
            .ToDictionary(p => p.Id, StringComparer.Ordinal);

        foreach (HttpsProbeConfiguration https in incoming.Probes.Https)
        {
            if (https.Proxy.Password is null
                && currentByProbeId.TryGetValue(https.Id, out HttpsProbeConfiguration? currentProbe))
            {
                https.Proxy.Password = currentProbe.Proxy.Password;
            }
        }
    }
}
