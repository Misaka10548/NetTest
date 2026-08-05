using System.Security.Cryptography;
using System.Text;

namespace NetTest.Core.Configuration;

/// <summary>配置保存结果。</summary>
public sealed record ConfigSaveResult(bool Conflict, bool RestartRequired, string Revision);

/// <summary>
/// 配置加载、验证、原子保存与 revision 管理。revision 是正式文件 UTF-8 字节的 SHA-256 十六进制值。
/// </summary>
public sealed class ConfigManager
{
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private NetTestConfiguration _current;
    private string _revision = "";

    public ConfigManager(string configFilePath, string backupFilePath, string baseDirectory)
    {
        ConfigFilePath = configFilePath;
        BackupFilePath = backupFilePath;
        BaseDirectory = baseDirectory;
        _current = new NetTestConfiguration();
    }

    public string ConfigFilePath { get; }

    public string BackupFilePath { get; }

    public string BaseDirectory { get; }

    public NetTestConfiguration Current
    {
        get
        {
            lock (_current)
            {
                return _current;
            }
        }
    }

    public string Revision
    {
        get
        {
            lock (_current)
            {
                return _revision;
            }
        }
    }

    /// <summary>
    /// 加载配置；文件不存在时创建默认配置并写入，然后继续。启动级验证失败抛 ConfigValidationException。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(ConfigFilePath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(ConfigFilePath))
        {
            NetTestConfiguration defaults = DefaultConfiguration.Create();
            await SaveFreshAsync(defaults, cancellationToken);
            return;
        }

        await LoadFromDiskAsync(cancellationToken);
    }

    /// <summary>
    /// 按 2.5 保存协议保存配置：revision 校验、完整验证、临时文件 + flush + 原子替换 + .bak。
    /// 验证失败抛 ConfigValidationException；revision 冲突返回 Conflict=true。
    /// </summary>
    public async Task<ConfigSaveResult> SaveAsync(
        NetTestConfiguration config,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken);
        string? tempPath = null;
        try
        {
            string currentOnDisk = await ComputeRevisionAsync(ConfigFilePath, cancellationToken);
            if (!string.Equals(currentOnDisk, expectedRevision, StringComparison.Ordinal))
            {
                return new ConfigSaveResult(Conflict: true, RestartRequired: false, Revision: currentOnDisk);
            }

            ConfigValidator.Validate(config, BaseDirectory);

            string hostAndLoggingBefore = HostAndLoggingFingerprint(_current);
            tempPath = Path.Combine(
                Path.GetDirectoryName(ConfigFilePath)!,
                $".nettest.{Guid.NewGuid():N}.tmp");
            await WriteFileAsync(tempPath, config, cancellationToken);
            FlushToDisk(tempPath);

            if (File.Exists(ConfigFilePath))
            {
                File.Replace(tempPath, ConfigFilePath, BackupFilePath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, ConfigFilePath);
            }

            tempPath = null; // 已移动/替换，不再清理
            await LoadFromDiskAsync(cancellationToken);
            string hostAndLoggingAfter = HostAndLoggingFingerprint(_current);

            return new ConfigSaveResult(
                Conflict: false,
                RestartRequired: !string.Equals(hostAndLoggingBefore, hostAndLoggingAfter, StringComparison.Ordinal),
                Revision: _revision);
        }
        finally
        {
            if (tempPath is not null && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // 清理失败不掩盖原始结果
                }
            }

            _saveLock.Release();
        }
    }

    private async Task SaveFreshAsync(NetTestConfiguration config, CancellationToken cancellationToken)
    {
        ConfigValidator.Validate(config, BaseDirectory);
        string tempPath = Path.Combine(Path.GetDirectoryName(ConfigFilePath)!, $".nettest.{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteFileAsync(tempPath, config, cancellationToken);
            FlushToDisk(tempPath);
            File.Move(tempPath, ConfigFilePath);
            await LoadFromDiskAsync(cancellationToken);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private async Task LoadFromDiskAsync(CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(ConfigFilePath, cancellationToken);
        string json = Encoding.UTF8.GetString(bytes);
        NetTestConfiguration loaded;
        try
        {
            loaded = System.Text.Json.JsonSerializer.Deserialize<NetTestConfiguration>(json, NetTestJson.Options)
                ?? throw new ConfigValidationException(new[] { new ConfigError("", "配置文件为空。") });
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ConfigValidationException(new[] { new ConfigError("", $"配置文件解析失败：{ex.Message}") });
        }

        ConfigValidator.Validate(loaded, BaseDirectory);

        lock (_current)
        {
            _current = loaded;
            _revision = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
    }

    private static async Task WriteFileAsync(string path, NetTestConfiguration config, CancellationToken cancellationToken)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(config, NetTestJson.Options);
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }

    private static void FlushToDisk(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<string> ComputeRevisionAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return "";
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string HostAndLoggingFingerprint(NetTestConfiguration config)
    {
        string passwordMarker = config.Host.Password is null ? "none" : "set";
        return string.Join("\n", config.Host.Urls) + "\n" + passwordMarker
            + "\n" + config.Logging.MinimumLevel
            + "\n" + config.Logging.Directory
            + "\n" + config.Logging.FileSizeLimitMiB
            + "\n" + config.Logging.RetainedDays;
    }
}
