namespace SDemo.DesktopAgent.Execution;

/// <summary>自动续跑设置（P5 §3.2）。</summary>
public sealed class VerificationSettings
{
    public bool AutoVerifyAfterEdit { get; set; } = true;

    public int MaxAutoContinue { get; set; } = 1;
}

/// <summary>checkpoint 设置（P5 §3.3）。</summary>
public sealed class CheckpointSettings
{
    public bool Enabled { get; set; } = true;

    public int Keep { get; set; } = 20;
}
