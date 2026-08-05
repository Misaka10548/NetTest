namespace NetTest.Core;

/// <summary>程序目录布局与路径解析。所有数据基于 AppContext.BaseDirectory。</summary>
public static class Paths
{
    /// <summary>规范化的程序基目录（末尾无分隔符）。</summary>
    public static string BaseDirectory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));

    public static string ConfigDirectory => Path.Combine(BaseDirectory, "Config");
    public static string ConfigFilePath => Path.Combine(ConfigDirectory, "nettest.json");
    public static string ConfigBackupPath => Path.Combine(ConfigDirectory, "nettest.json.bak");
    public static string DataDirectory => Path.Combine(BaseDirectory, "Data");
    public static string LogsDirectory => Path.Combine(DataDirectory, "Logs");
    public static string BackupsDirectory => Path.Combine(DataDirectory, "Backups");

    /// <summary>
    /// 将配置中的相对路径解析为基目录下的绝对路径；规范化后逃逸基目录时返回 null。
    /// </summary>
    public static string? ResolveUnderBase(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        string full = Path.GetFullPath(Path.Combine(BaseDirectory, relativePath));
        if (!IsUnderBase(full))
        {
            return null;
        }

        return full;
    }

    public static bool IsUnderBase(string fullPath)
    {
        string baseDir = BaseDirectory;
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        return candidate.Equals(baseDir, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
