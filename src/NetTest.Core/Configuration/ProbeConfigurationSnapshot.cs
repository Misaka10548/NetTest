using System.Text.Json;
using System.Text.Json.Nodes;
using NetTest.Core.Enums;

namespace NetTest.Core.Configuration;

/// <summary>
/// 探针配置快照：执行时冻结，随 Execution 持久化。
/// 序列化时剔除代理凭据（proxy.password），确保密码不进入结果库。
/// </summary>
public sealed record ProbeConfigurationSnapshot(
    ProbeType ProbeType,
    object Configuration)
{
    public string Serialize() => ProbeSnapshotSerializer.Serialize(this);
}

public static class ProbeSnapshotSerializer
{
    private const string TypeProperty = "probeType";
    private const string ConfigurationProperty = "configuration";

    public static string Serialize(ProbeConfigurationSnapshot snapshot)
    {
        JsonObject root = new()
        {
            [TypeProperty] = snapshot.ProbeType.ToString().ToLowerInvariant(),
            [ConfigurationProperty] = JsonSerializer.SerializeToNode(snapshot.Configuration, NetTestJson.PersistedOptions),
        };

        // 剔除代理密码：https 探针的 proxy.password 不进入持久化快照。
        if (root[ConfigurationProperty] is JsonObject config
            && config["proxy"] is JsonObject proxy)
        {
            proxy.Remove("password");
        }

        return root.ToJsonString(NetTestJson.PersistedOptions);
    }

    public static ProbeConfigurationSnapshot Deserialize(string json)
    {
        JsonObject root = JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("配置快照不是对象。");

        string typeValue = (root[TypeProperty] as JsonValue)?.GetValue<string>()
            ?? throw new JsonException("配置快照缺少 probeType。");
        ProbeType type = Enum.Parse<ProbeType>(typeValue, ignoreCase: true);

        JsonNode? configNode = root[ConfigurationProperty]
            ?? throw new JsonException("配置快照缺少 configuration。");

        object configuration = type switch
        {
            ProbeType.Ping => configNode.Deserialize<PingProbeConfiguration>(NetTestJson.PersistedOptions)
                ?? throw new JsonException("Ping 配置快照反序列化失败。"),
            ProbeType.Tracert => configNode.Deserialize<TracertProbeConfiguration>(NetTestJson.PersistedOptions)
                ?? throw new JsonException("Tracert 配置快照反序列化失败。"),
            ProbeType.Dns => configNode.Deserialize<DnsProbeConfiguration>(NetTestJson.PersistedOptions)
                ?? throw new JsonException("DNS 配置快照反序列化失败。"),
            ProbeType.Https => configNode.Deserialize<HttpsProbeConfiguration>(NetTestJson.PersistedOptions)
                ?? throw new JsonException("HTTPS 配置快照反序列化失败。"),
            _ => throw new JsonException($"未知探针类型 {typeValue}。"),
        };

        return new ProbeConfigurationSnapshot(type, configuration);
    }
}
