using System.Security.Cryptography;
using System.Text;

namespace NetTest.Core.Scheduling;

/// <summary>基于程序目录的命名互斥锁，限制单实例运行。</summary>
public static class SingleInstance
{
    /// <summary>
    /// 尝试获取单实例互斥锁。acquired=false 表示已有实例在运行。
    /// 互斥锁名称由规范化程序目录哈希派生，同目录只允许一个进程。
    /// </summary>
    public static Mutex TryAcquire(string baseDirectory, out bool acquired)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        string name = $"Local\\NetTest-{Convert.ToHexString(hash)[..16]}";
        return new Mutex(initiallyOwned: true, name, out acquired);
    }
}
