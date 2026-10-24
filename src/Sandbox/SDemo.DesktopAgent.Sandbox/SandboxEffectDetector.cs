namespace SDemo.DesktopAgent.Sandbox;

/// <summary>
/// 从脚本内容和变更集里推断副作用类别。这是**启发式提示**，不是安全分析：
/// 关键词只用来在界面上提醒「这脚本会关机 / 会联网」，绝不作为自动放行的依据。
/// </summary>
public static class SandboxEffectDetector
{
    /// <summary>命中即视为系统级操作：关机、注册表、服务、计划任务、提权、系统设置。</summary>
    private static readonly string[] SystemMarkers =
    [
        "shutdown",
        "restart-computer",
        "stop-computer",
        "bcdedit",
        "reg add",
        "reg delete",
        "regedit",
        "new-service",
        "set-service",
        "stop-service",
        "sc create",
        "sc.exe create",
        "schtasks",
        "netsh",
        "runas",
        "-verb runas",
        "takeown",
        "icacls",
        "set-executionpolicy",
        "add-mppreference",
        "wmic",
        "get-wmiobject",
        "disable-windowsoptionalfeature",
        "enable-windowsoptionalfeature"
    ];

    /// <summary>命中即视为联网意图（沙箱本身无网络，但值得提示）。</summary>
    private static readonly string[] NetworkMarkers =
    [
        "invoke-webrequest",
        "invoke-restmethod",
        "downloadstring",
        "downloadfile",
        "new-object system.net.webclient",
        "system.net.http",
        "httpclient",
        "webclient",
        "test-netconnection",
        "resolve-dnsname",
        "curl ",
        "wget ",
        "git clone",
        "npm install",
        "npm i ",
        "pnpm install",
        "pnpm add",
        "pip install",
        "dotnet restore",
        "dotnet tool",
        "winget",
        "choco install"
    ];

    /// <summary>一次判定的最大提示条数（再多也只是噪音）。</summary>
    public const int MaxSignals = 5;

    public static SandboxEffectReport Detect(string? script, IReadOnlyList<SandboxChange> changes)
    {
        var effects = new List<SandboxEffect>();
        var signals = new List<string>();

        if (changes.Count > 0)
        {
            effects.Add(SandboxEffect.FileChanges);
        }

        var text = script ?? string.Empty;

        if (Match(text, SystemMarkers, signals) && !effects.Contains(SandboxEffect.SystemLevel))
        {
            effects.Add(SandboxEffect.SystemLevel);
        }

        if (Match(text, NetworkMarkers, signals) && !effects.Contains(SandboxEffect.Network))
        {
            effects.Add(SandboxEffect.Network);
        }

        return new SandboxEffectReport(effects, signals);
    }

    private static bool Match(string script, IReadOnlyList<string> markers, List<string> signals)
    {
        if (script.Length == 0)
        {
            return false;
        }

        var hit = false;

        foreach (var marker in markers)
        {
            if (!script.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            hit = true;

            if (signals.Count < MaxSignals && !signals.Contains(marker, StringComparer.OrdinalIgnoreCase))
            {
                signals.Add(marker);
            }
        }

        return hit;
    }
}
