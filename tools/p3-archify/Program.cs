using System.Text.Json;
using SDemo.DesktopAgent.Services;

// P3 安装探针：
// 1) 选上游 Archify 3.2.0 仓库根或技能包目录；
// 2) 安装到技能库，写入 DesktopAgent 适配 SKILL.md；
// 3) 检查上游正文、许可 / NOTICE / 第三方声明与 catalog 索引。
//
// 用法：dotnet run --project tools/p3-archify -c Release
// 可选：--source <仓库根或 archify/> --skills-root <技能库目录>

try
{
    var options = ProbeOptions.Parse(args);
    var root = Path.GetFullPath(Environment.CurrentDirectory);
    var source = Path.GetFullPath(options.Source ?? Path.Combine(root, ".tmp", "archify-repo"));
    var skillsRoot = Path.GetFullPath(options.SkillsRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SDemo", "DesktopAgent", "skills"));

    var catalog = new SkillCatalog(skillsRoot);
    var installer = new ArchifyInstaller(catalog);

    Console.WriteLine($"上游目录：{source}");
    Console.WriteLine($"技能库：{skillsRoot}");

    var result = await installer.InstallAsync(source);

    if (!result.Success)
    {
        Console.Error.WriteLine($"安装失败：{result.Message}");
        return 1;
    }

    Console.WriteLine(result.Message);
    Console.WriteLine($"目标：{result.TargetRoot}");

    var target = result.TargetRoot!;
    Require(File.Exists(Path.Combine(target, "SKILL.md")), "缺少适配 SKILL.md。");
    Require(File.Exists(Path.Combine(target, "references", "upstream-SKILL.md")), "缺少上游正文。");
    Require(File.Exists(Path.Combine(target, "LICENSE")), "缺少 LICENSE。");
    Require(File.Exists(Path.Combine(target, "NOTICE")), "缺少 NOTICE。");
    Require(File.Exists(Path.Combine(target, "THIRD_PARTY_NOTICES.md")), "缺少第三方声明。");

    var adapter = catalog.ReadFile("archify", "SKILL.md");
    Require(adapter.IsOk, "适配 SKILL.md 读不到。");
    Require(adapter.Content!.Contains("DesktopAgent 适配", StringComparison.Ordinal), "适配入口没有写 DesktopAgent 说明。");

    var upstream = catalog.ReadFile("archify", "references/upstream-SKILL.md");
    Require(upstream.IsOk, "上游正文读不到。");
    Require(upstream.Bytes > 15_000, $"上游正文过短：{upstream.Bytes} B");

    var skill = catalog.List().SingleOrDefault(item => item.Name == "archify");
    Require(skill is not null, "catalog 没有索引到 archify。");
    Require(skill!.Kind == SkillKind.Package, "archify 不是目录包。");
    Require(skill.Version == "3.2.0", $"版本不是 3.2.0：{skill.Version}");

    var script = catalog.ResolveScript("archify", "bin/archify.mjs");
    Require(script.IsOk, $"脚本解析失败：{script.Status}");

    var report = new
    {
        probe = "archify-p3",
        timestamp = DateTimeOffset.Now.ToString("O"),
        source,
        skillsRoot,
        result.TargetRoot,
        result.Version,
        result.Files,
        result.Bytes,
        adapterBytes = adapter.Bytes,
        upstreamBytes = upstream.Bytes,
        scriptSha256 = script.Target!.Sha256,
        indexed = new
        {
            skill.Name,
            skill.Version,
            skill.Source,
            kind = skill.Kind.ToString()
        },
        licenses = new[] { "LICENSE", "NOTICE", "LICENSE-MIT", "thirdPartyNotices" }
            .Where(name => File.Exists(Path.Combine(target, name == "thirdPartyNotices" ? "THIRD_PARTY_NOTICES.md" : name))),
        pass = true
    };

    var reportPath = Path.Combine(skillsRoot, "p3-probe-report.json");
    File.WriteAllText(
        reportPath,
        JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));

    Console.WriteLine($"适配入口：{adapter.Bytes} B；上游正文：{upstream.Bytes} B");
    Console.WriteLine($"脚本 sha256：{script.Target.Sha256}");
    Console.WriteLine($"报告：{reportPath}");
    Console.WriteLine("P3 安装检查通过。");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"P3 安装检查失败：{ex.Message}");
    Console.Error.WriteLine(ex);
    return 1;
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed record ProbeOptions(string? Source, string? SkillsRoot)
{
    public static ProbeOptions Parse(string[] args)
    {
        string? Read(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        return new ProbeOptions(Read("--source"), Read("--skills-root"));
    }
}
