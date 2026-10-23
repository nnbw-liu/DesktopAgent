using System.Security.Cryptography;

namespace SDemo.DesktopAgent.Editing;

/// <summary>一次「读过」记录：路径、读取时的 SHA-256、时间。</summary>
public sealed record ReadRecord(string RelativePath, string Hash, DateTimeOffset ReadAt);

/// <summary>
/// 读前写注册表：按会话记录模型读过哪些文件、读时的内容 hash。
/// 重启后清空，等价于「必须重新读」。
/// </summary>
public interface IFileReadRegistry
{
    void RecordRead(string sessionId, string relativePath, string hash);

    ReadRecord? Find(string sessionId, string relativePath);

    void Clear(string sessionId);
}

/// <inheritdoc />
public sealed class FileReadRegistry : IFileReadRegistry
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(2);

    private readonly object _sync = new();
    private readonly Dictionary<string, Dictionary<string, ReadRecord>> _sessions =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;

    public FileReadRegistry(TimeProvider? time = null, TimeSpan? ttl = null)
    {
        _time = time ?? TimeProvider.System;
        _ttl = ttl ?? DefaultTtl;
    }

    public void RecordRead(string sessionId, string relativePath, string hash)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var key = Normalize(relativePath);
        var now = _time.GetUtcNow();

        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var bucket))
            {
                bucket = new Dictionary<string, ReadRecord>(StringComparer.OrdinalIgnoreCase);
                _sessions[sessionId] = bucket;
            }

            bucket[key] = new ReadRecord(key, hash, now);
            RemoveExpired(bucket, now);
        }
    }

    public ReadRecord? Find(string sessionId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var key = Normalize(relativePath);
        var now = _time.GetUtcNow();

        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var bucket)
                || !bucket.TryGetValue(key, out var record))
            {
                return null;
            }

            if (now - record.ReadAt > _ttl)
            {
                bucket.Remove(key);
                return null;
            }

            return record;
        }
    }

    public void Clear(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_sync)
        {
            _sessions.Remove(sessionId);
        }
    }

    /// <summary>文件内容的 SHA-256（小写十六进制）；文件不存在返回 null。</summary>
    public static string? HashFile(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return null;
        }

        using var stream = File.OpenRead(fullPath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string HashText(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private void RemoveExpired(Dictionary<string, ReadRecord> bucket, DateTimeOffset now)
    {
        if (bucket.Count < 32)
        {
            return;
        }

        foreach (var pair in bucket.Where(pair => now - pair.Value.ReadAt > _ttl).ToArray())
        {
            bucket.Remove(pair.Key);
        }
    }

    /// <summary>
    /// 与 WorkspacePath.Resolve 对齐：统一斜杠、去掉重复分隔符与 "." 段。
    /// 差一个 "./" 就会让「读前写」误判成没读过。
    /// </summary>
    private static string Normalize(string path) =>
        string.Join(
            '/',
            path.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment != "."));
}
