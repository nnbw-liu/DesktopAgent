namespace SDemo.DesktopAgent.Execution;

/// <summary>一条会话级直跑授权（内存，重启失效）。</summary>
public sealed record SessionGrant(string SessionId, string Prefix, string Command, DateTime GrantedAt);

/// <summary>
/// 会话级直跑授权：同一会话里相同「可执行文件 + 前两个参数」不再弹审批。
/// 高风险命令由 <see cref="CommandPolicy.IsHighRisk"/> 拒绝授权；授权只存内存。
/// </summary>
public sealed class SessionGrantStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Dictionary<string, SessionGrant>> _bySession =
        new(StringComparer.Ordinal);

    /// <summary>本会话是否已授权某个前缀。</summary>
    public bool IsGranted(string sessionId, string prefix)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(prefix))
        {
            return false;
        }

        lock (_sync)
        {
            return _bySession.TryGetValue(sessionId, out var grants)
                   && grants.ContainsKey(Normalize(prefix));
        }
    }

    /// <summary>记录授权；高风险命令返回 false，不写入。</summary>
    public bool Grant(string sessionId, string prefix, string command, bool highRisk)
    {
        if (highRisk || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(prefix))
        {
            return false;
        }

        lock (_sync)
        {
            if (!_bySession.TryGetValue(sessionId, out var grants))
            {
                grants = new Dictionary<string, SessionGrant>(StringComparer.OrdinalIgnoreCase);
                _bySession[sessionId] = grants;
            }

            grants[Normalize(prefix)] = new SessionGrant(sessionId, prefix, command, DateTime.Now);
            return true;
        }
    }

    /// <summary>撤销某个前缀；不存在时返回 false。</summary>
    public bool Revoke(string sessionId, string prefix)
    {
        lock (_sync)
        {
            return _bySession.TryGetValue(sessionId, out var grants)
                   && grants.Remove(Normalize(prefix));
        }
    }

    /// <summary>列出授权；<paramref name="sessionId"/> 为空时列出全部（设置页展示用）。</summary>
    public IReadOnlyList<SessionGrant> List(string? sessionId = null)
    {
        lock (_sync)
        {
            var values = string.IsNullOrWhiteSpace(sessionId)
                ? _bySession.Values.SelectMany(grants => grants.Values)
                : _bySession.TryGetValue(sessionId, out var grants)
                    ? grants.Values
                    : Enumerable.Empty<SessionGrant>();

            return values.OrderBy(grant => grant.GrantedAt).ToArray();
        }
    }

    /// <summary>清空某会话或全部（应用重启时由 DI 生命周期自然清空）。</summary>
    public void Clear(string? sessionId = null)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                _bySession.Clear();
            }
            else
            {
                _bySession.Remove(sessionId);
            }
        }
    }

    private static string Normalize(string prefix) => prefix.Trim();
}
