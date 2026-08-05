using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetTest.Core.Configuration;

/// <summary>统一的 System.Text.Json 序列化选项。</summary>
public static class NetTestJson
{
    /// <summary>配置文件选项：camelCase、大小写敏感、未知属性视为错误、null 不写出。</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Metrics/快照选项：紧凑输出，仅用于持久化。</summary>
    public static JsonSerializerOptions PersistedOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
