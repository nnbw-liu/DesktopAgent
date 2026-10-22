namespace SDemo.DesktopAgent.Sandbox;

/// <summary>
/// Windows Sandbox 一次作业的宿主侧描述。只描述 job 目录与脚本，不包含用户可控的绝对路径：
/// 目录来自 <c>SandboxDirectory</c> 的布局，脚本名由宿主生成。
/// </summary>
public sealed record WindowsSandboxJobSpec
{
    /// <summary>job 根目录（宿主侧），用于校验其余目录都在它下面。</summary>
    public required string JobRoot { get; init; }

    /// <summary>宿主侧 input 目录，映射为沙箱内 <c>C:\job\input</c>（只读）。</summary>
    public required string InputDirectory { get; init; }

    /// <summary>宿主侧 work 目录，映射为沙箱内 <c>C:\job\work</c>（可写）。</summary>
    public required string WorkDirectory { get; init; }

    /// <summary>宿主侧 output 目录，映射为沙箱内 <c>C:\job\output</c>（可写）。</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>input 下要执行的用户脚本文件名。</summary>
    public string ScriptFileName { get; init; } = "script.ps1";

    /// <summary>沙箱内的工作目录，相对 work；<c>.</c> 表示 work 根。</summary>
    public string WorkingDirectory { get; init; } = ".";

    /// <summary>分配给沙箱虚拟机的内存（MB）。</summary>
    public int MemoryMB { get; init; } = 4096;

    /// <summary>启动虚拟机额外宽限秒数；超时 = 脚本超时 + 这个值。</summary>
    public int StartupGraceSeconds { get; init; } = 60;
}

/// <summary>
/// Windows Sandbox 自检结果。<see cref="Launched"/> / <see cref="PowerShellOk"/> / <see cref="NoNetwork"/>
/// 只有全部为真，运行器的 Verified 才置为真，PowerShell 工具才允许启用。
/// </summary>
public sealed record WindowsSandboxSelfTestReport(
    bool Launched,
    bool PowerShellOk,
    bool NoNetwork,
    TimeSpan Duration,
    string? Detail)
{
    public bool AllPassed => Launched && PowerShellOk && NoNetwork;
}
