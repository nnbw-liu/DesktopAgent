namespace SDemo.DesktopAgent.SandboxHost;

/// <summary>
/// 沙箱内 console helper（S0 骨架）。
/// 真正的 <c>run --job &lt;path&gt;</c> 协议处理在 S2 实现：读 job.json → 按 mode 分发 → 原子写 result.json。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "run" && args[1] == "--job")
        {
            Console.Error.WriteLine("SandboxHost 尚未实现作业执行（S2 阶段）。");
            return 3;
        }

        Console.Error.WriteLine("Usage: SandboxHost run --job <path>");
        return 2;
    }
}
