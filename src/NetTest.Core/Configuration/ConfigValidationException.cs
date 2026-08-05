namespace NetTest.Core.Configuration;

/// <summary>配置验证错误：结构化字段路径 + 消息。</summary>
public sealed record ConfigError(string Path, string Message);

/// <summary>配置验证失败时抛出，携带全部字段错误。调用方应保留当前有效配置。</summary>
public sealed class ConfigValidationException : Exception
{
    public ConfigValidationException(IReadOnlyList<ConfigError> errors)
        : base($"配置验证失败，共 {errors.Count} 个错误。")
    {
        Errors = errors;
    }

    public IReadOnlyList<ConfigError> Errors { get; }
}
