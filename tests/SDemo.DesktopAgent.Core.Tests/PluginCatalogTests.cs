using SDemo.DesktopAgent.Plugins;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>M0 骨架：扫描 / 校验 / 排序 / 去重 / 降级。</summary>
public sealed class PluginCatalogTests
{
    [Fact]
    public void 合法最小清单_得到可用插件()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("demo", Fixtures.Minimal(), ("skills/main.md", Fixtures.Skill));

        var result = temp.Scan();

        Assert.Empty(result.Failures);
        var plugin = Assert.Single(result.Usable);
        Assert.Equal("demo", plugin.Id);
        Assert.Equal("演示", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(PluginSource.Local, plugin.Source);
        Assert.Equal(PluginState.Available, plugin.State);
        Assert.False(plugin.HasErrors);
        Assert.Equal("demo", plugin.DirectoryName);

        var capability = Assert.Single(plugin.Capabilities);
        Assert.True(capability.IsUsable);
        Assert.Equal("演示", capability.Name);
        Assert.Equal("演示", capability.SkillName);
        Assert.Equal("一句话说明。", capability.SkillDescription);
        Assert.Contains("按规则处理", capability.SkillContent);
        Assert.False(capability.AllowGlobalSkills);
        Assert.False(capability.AllowHistory);
        Assert.Equal(PluginOutputFormat.Text, capability.OutputFormat);

        var mount = Assert.Single(plugin.Mounts);
        Assert.True(mount.IsUsable);
        Assert.Equal(PluginSurface.Composer, mount.Surface);
        Assert.Equal(PluginInput.Composer, mount.Input);
        Assert.Equal(100, mount.Order);
        Assert.True(mount.Show);
        Assert.Equal("demo/composer", mount.Key);
        Assert.Same(capability, mount.Capability);
    }

    [Fact]
    public void 入口默认输入按surface推断()
    {
        using var temp = new TempPlugins();
        var manifest = """
            {
              "id": "demo",
              "name": "演示",
              "capabilities": [ { "id": "main", "name": "演示", "skill": "skills/main.md" } ],
              "mounts": [
                { "id": "sidebar", "surface": "sidebar", "capability": "main" },
                { "id": "composer", "surface": "composer", "capability": "main" },
                { "id": "bubble", "surface": "message", "capability": "main" }
              ]
            }
            """;
        temp.WriteLocal("demo", manifest, ("skills/main.md", Fixtures.Skill));

        var result = temp.Scan();

        var plugin = Assert.Single(result.Usable);
        Assert.Equal(PluginInput.Composer, plugin.Mounts.Single(item => item.Id == "sidebar").Input);
        Assert.Equal(PluginInput.Composer, plugin.Mounts.Single(item => item.Id == "composer").Input);
        Assert.Equal(PluginInput.Message, plugin.Mounts.Single(item => item.Id == "bubble").Input);
        Assert.Single(result.UsableMounts(PluginSurface.Sidebar));
        Assert.Single(result.UsableMounts(PluginSurface.Message));
    }

    [Fact]
    public void 缺少skill文件_只有该能力不可用()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("demo", Fixtures.Minimal(skill: "skills/missing.md"));

        var result = temp.Scan();

        var plugin = Assert.Single(result.Plugins);
        Assert.False(plugin.IsUsable);
        Assert.Empty(result.Usable);

        var capability = Assert.Single(plugin.Capabilities);
        Assert.False(capability.IsUsable);
        var issue = Assert.Single(capability.Issues);
        Assert.True(issue.IsError);
        Assert.Contains("skill 文件不存在", issue.Message);
        Assert.Equal("plugin.json", issue.File);
        Assert.Equal("skill", issue.Path);
    }

    [Fact]
    public void skill路径穿越_被拒绝()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("demo", Fixtures.Minimal(skill: "../../secret.md"));
        File.WriteAllText(Path.Combine(temp.Root, "secret.md"), "secret");

        var result = temp.Scan();

        var capability = Assert.Single(Assert.Single(result.Plugins).Capabilities);
        Assert.False(capability.IsUsable);
        Assert.Contains("越界", Assert.Single(capability.Issues).Message);
    }

    [Fact]
    public void skill绝对路径_被拒绝()
    {
        using var temp = new TempPlugins();
        var absolute = Path.Combine(temp.Root, "secret.md");
        File.WriteAllText(absolute, "secret");
        // JSON 里不能出现裸反斜杠，换成 / 依然是绝对路径。
        temp.WriteLocal("demo", Fixtures.Minimal(skill: absolute.Replace('\\', '/')));

        var result = temp.Scan();

        var capability = Assert.Single(Assert.Single(result.Plugins).Capabilities);
        Assert.False(capability.IsUsable);
        Assert.Contains("越界", Assert.Single(capability.Issues).Message);
    }

    [Fact]
    public void 未定义占位符_能力不可用并带行号()
    {
        using var temp = new TempPlugins();
        var skill = """
            ---
            name: 演示
            version: 1.0
            ---

            请把文本交给 {{nope}} 处理。
            """;
        temp.WriteLocal("demo", Fixtures.Minimal(), ("skills/main.md", skill));

        var result = temp.Scan();

        var capability = Assert.Single(Assert.Single(result.Plugins).Capabilities);
        Assert.False(capability.IsUsable);

        var issue = Assert.Single(capability.Issues);
        Assert.True(issue.IsError);
        Assert.Equal("skills/main.md", issue.File);
        Assert.Equal(6, issue.Line);
        Assert.Contains("{{nope}}", issue.Message);
    }

    [Fact]
    public void 已定义占位符与label写法_通过校验()
    {
        using var temp = new TempPlugins();
        var manifest = """
            {
              "id": "translate",
              "name": "翻译",
              "capabilities": [
                {
                  "id": "translate",
                  "name": "翻译",
                  "skill": "skills/translate.md",
                  "parameters": [
                    {
                      "key": "target",
                      "label": "目标语言",
                      "kind": "select",
                      "defaultFrom": "system-language",
                      "fallback": "en",
                      "options": [
                        { "value": "ja", "label": "日本語" },
                        { "value": "en", "label": "English" }
                      ]
                    }
                  ]
                }
              ],
              "mounts": [
                { "id": "composer", "surface": "composer", "capability": "translate" }
              ]
            }
            """;
        var skill = """
            ---
            name: 翻译
            ---

            把文本翻译成 {{target.label}}（{{target}}）。
            """;
        temp.WriteLocal("translate", manifest, ("skills/translate.md", skill));

        var result = temp.Scan();

        var plugin = Assert.Single(result.Usable);
        var capability = Assert.Single(plugin.Capabilities);
        Assert.True(capability.IsUsable);
        Assert.Empty(capability.Issues);

        var parameter = Assert.Single(capability.Parameters);
        Assert.Equal("目标语言", parameter.Label);
        Assert.Equal(2, parameter.Options.Count);
        Assert.Equal("system-language", parameter.DefaultFrom);
        Assert.Equal("en", parameter.Fallback);
        Assert.True(parameter.Required);
        Assert.Equal("日本語", parameter.ResolveLabel("ja"));
    }

    [Fact]
    public void 重复id_本地覆盖内置()
    {
        using var temp = new TempPlugins();
        temp.WriteBuiltin("translate", Fixtures.Minimal("translate", "翻译"), ("skills/main.md", Fixtures.Skill));
        temp.WriteLocal("my-translate", Fixtures.Minimal("translate", "翻译（改）"), ("skills/main.md", Fixtures.Skill));

        var result = temp.Scan();

        var usable = Assert.Single(result.Usable);
        Assert.Equal(PluginSource.Local, usable.Source);
        Assert.Equal("翻译（改）", usable.Name);

        var builtinEntry = Assert.Single(result.Plugins, plugin => plugin.Source == PluginSource.Builtin);
        Assert.True(builtinEntry.IsOverridden);
        Assert.False(builtinEntry.IsUsable);
    }

    [Fact]
    public void 同源重复id_保留先加载的_另一个标重复()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("a", Fixtures.Minimal("dup", "A"), ("skills/main.md", Fixtures.Skill));
        temp.WriteLocal("b", Fixtures.Minimal("dup", "B"), ("skills/main.md", Fixtures.Skill));

        var result = temp.Scan();

        Assert.Single(result.Usable);

        var duplicate = Assert.Single(result.Plugins, plugin => plugin.State == PluginState.Duplicate);
        Assert.Equal("重复", duplicate.StateText);
        Assert.False(duplicate.IsUsable);
        Assert.Contains(duplicate.Issues, issue => issue.Message.Contains("重复", StringComparison.Ordinal));
    }

    [Fact]
    public void 清单语法错误_只影响该插件()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("good", Fixtures.Minimal(), ("skills/main.md", Fixtures.Skill));
        temp.WriteLocal("bad", """
            {
              "id": "bad,
              "name": "坏插件"
            }
            """);

        var result = temp.Scan();

        Assert.Single(result.Usable);

        var failure = Assert.Single(result.Failures);
        Assert.Equal(PluginSource.Local, failure.Source);
        var issue = Assert.Single(failure.Issues);
        Assert.True(issue.IsError);
        Assert.Equal("plugin.json", issue.File);
        Assert.NotNull(issue.Line);
        Assert.Contains("无法解析", issue.Message);
        Assert.Contains("第", issue.ToDisplayString());
    }

    [Fact]
    public void 清单超过512KB_被拒绝()
    {
        using var temp = new TempPlugins();
        var padding = new string('x', 520 * 1024);
        var json = $$"""
            {
              "id": "big",
              "name": "大插件",
              "padding": "{{padding}}",
              "capabilities": [ { "id": "main", "name": "大插件", "skill": "skills/main.md" } ],
              "mounts": [ { "id": "composer", "surface": "composer", "capability": "main" } ]
            }
            """;
        temp.WriteLocal("big", json, ("skills/main.md", Fixtures.Skill));

        var result = temp.Scan();

        var failure = Assert.Single(result.Failures);
        Assert.Contains("512 KB", Assert.Single(failure.Issues).Message);
        Assert.Empty(result.Usable);
    }

    [Fact]
    public void skill超过256KB_能力不可用()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("demo", Fixtures.Minimal(), ("skills/main.md", new string('x', 300 * 1024)));

        var result = temp.Scan();

        var capability = Assert.Single(Assert.Single(result.Plugins).Capabilities);
        Assert.False(capability.IsUsable);
        Assert.Contains("256 KB", Assert.Single(capability.Issues).Message);
    }

    [Fact]
    public void 清单允许注释尾逗号且字段名大小写不敏感()
    {
        using var temp = new TempPlugins();
        var json = """
            {
              // 配置文件是手写的，允许注释
              "ID": "demo",
              "Name": "演示",
              "capabilities": [
                { "id": "main", "name": "演示", "skill": "skills/main.md", },
              ],
              "mounts": [
                { "id": "composer", "surface": "composer", "capability": "main", },
              ],
            }
            """;
        temp.WriteLocal("demo", json, ("skills/main.md", Fixtures.Skill));

        var result = temp.Scan();

        Assert.Empty(result.Failures);
        Assert.Equal("demo", Assert.Single(result.Usable).Id);
    }

    [Fact]
    public void 未知字段只记警告_不阻断加载()
    {
        using var temp = new TempPlugins();
        var json = """
            {
              "id": "demo",
              "name": "演示",
              "extra": true,
              "capabilities": [
                { "id": "main", "name": "演示", "skill": "skills/main.md", "descrption": "拼错了" }
              ],
              "mounts": [ { "id": "composer", "surface": "composer", "capability": "main" } ]
            }
            """;
        temp.WriteLocal("demo", json, ("skills/main.md", Fixtures.Skill));

        var result = temp.Scan();

        var plugin = Assert.Single(result.Usable);
        Assert.Contains(plugin.Issues, issue =>
            issue.Severity == PluginIssueSeverity.Warning
            && issue.Message.Contains("未知字段", StringComparison.Ordinal));
        Assert.Contains(plugin.Issues, issue => issue.Path == "capabilities[0].descrption");
    }

    [Fact]
    public void minHostVersion过高_插件被禁用但仍在列表里()
    {
        using var temp = new TempPlugins();
        var json = """
            {
              "id": "future",
              "name": "未来插件",
              "minHostVersion": "1.5.0",
              "capabilities": [ { "id": "main", "name": "未来", "skill": "skills/main.md" } ],
              "mounts": [ { "id": "composer", "surface": "composer", "capability": "main" } ]
            }
            """;
        temp.WriteLocal("future", json, ("skills/main.md", Fixtures.Skill));

        var result = temp.Scan(new PluginHostVersion(1, 0, 0));

        var plugin = Assert.Single(result.Plugins);
        Assert.Equal(PluginState.Disabled, plugin.State);
        Assert.Contains("1.5.0", plugin.DisabledReason);
        Assert.Equal("已禁用", plugin.StateText);
        Assert.Empty(result.Usable);
    }

    [Fact]
    public void mount指向不存在的能力_该入口不可用_能力仍可用()
    {
        using var temp = new TempPlugins();
        var json = """
            {
              "id": "demo",
              "name": "演示",
              "capabilities": [ { "id": "main", "name": "演示", "skill": "skills/main.md" } ],
              "mounts": [ { "id": "bubble", "surface": "message", "capability": "other" } ]
            }
            """;
        temp.WriteLocal("demo", json, ("skills/main.md", Fixtures.Skill));

        var result = temp.Scan();

        var plugin = Assert.Single(result.Plugins);
        Assert.False(plugin.IsUsable);
        Assert.Empty(result.Usable);
        Assert.True(Assert.Single(plugin.Capabilities).IsUsable);

        var mount = Assert.Single(plugin.Mounts);
        Assert.False(mount.IsUsable);
        Assert.Contains("不存在", Assert.Single(mount.Issues).Message);
    }

    [Fact]
    public void 参数kind非法或select缺options_能力不可用()
    {
        using var temp = new TempPlugins();
        var json = """
            {
              "id": "demo",
              "name": "演示",
              "capabilities": [
                {
                  "id": "main",
                  "name": "演示",
                  "skill": "skills/main.md",
                  "parameters": [
                    { "key": "tone", "label": "语气", "kind": "dropdown" },
                    { "key": "target", "label": "目标语言", "kind": "select" },
                    { "key": "level", "label": "级别", "kind": "number", "default": "abc" }
                  ]
                }
              ],
              "mounts": [ { "id": "composer", "surface": "composer", "capability": "main" } ]
            }
            """;
        temp.WriteLocal("demo", json, ("skills/main.md", Fixtures.Skill));

        var result = temp.Scan();

        var capability = Assert.Single(Assert.Single(result.Plugins).Capabilities);
        Assert.False(capability.IsUsable);
        Assert.Contains(capability.Issues, issue => issue.Message.Contains("kind 必须是", StringComparison.Ordinal));
        Assert.Contains(capability.Issues, issue => issue.Message.Contains("必须提供 options", StringComparison.Ordinal));
        Assert.Contains(capability.Issues, issue => issue.Message.Contains("不是合法数字", StringComparison.Ordinal));
    }

    [Fact]
    public void 图标含非法字符_只丢图标不丢插件()
    {
        using var temp = new TempPlugins();
        var json = """
            {
              "id": "demo",
              "name": "演示",
              "icon": { "paths": ["M3 5 L8 9", "M1 1 <script>alert(1)</script>"], "text": "演" },
              "capabilities": [ { "id": "main", "name": "演示", "skill": "skills/main.md" } ],
              "mounts": [ { "id": "composer", "surface": "composer", "capability": "main" } ]
            }
            """;
        temp.WriteLocal("demo", json, ("skills/main.md", Fixtures.Skill));

        var result = temp.Scan();

        var plugin = Assert.Single(result.Usable);
        Assert.NotNull(plugin.Icon);
        Assert.Equal("演", plugin.Icon!.Text);
        Assert.Single(plugin.Icon.Paths);
        Assert.Contains(plugin.Issues, issue => issue.Message.Contains("非法字符", StringComparison.Ordinal));
    }

    [Fact]
    public void 空目录与不存在的目录_不报错()
    {
        using var temp = new TempPlugins();

        var empty = temp.Scan();
        Assert.True(empty.IsEmpty);
        Assert.Empty(empty.Failures);

        var catalog = new PluginCatalog(
            Path.Combine(temp.Root, "missing-builtin"),
            Path.Combine(temp.Root, "missing-local"));
        var missing = catalog.Scan();
        Assert.True(missing.IsEmpty);
        Assert.Empty(missing.Failures);
    }

    [Fact]
    public void 没有pluginjson的子目录_被跳过()
    {
        using var temp = new TempPlugins();
        var notes = Path.Combine(temp.LocalRoot, "notes");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "readme.txt"), "不是插件");

        var result = temp.Scan();

        Assert.True(result.IsEmpty);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void 同一个目录扫描两次结果稳定_并且会触发Changed()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("demo", Fixtures.Minimal(), ("skills/main.md", Fixtures.Skill));

        var catalog = temp.CreateCatalog();
        var changes = 0;
        catalog.Changed += (_, _) => changes++;

        var first = catalog.Scan();
        var second = catalog.Scan();

        Assert.Equal("demo", Assert.Single(first.Usable).Id);
        Assert.Equal("demo", Assert.Single(second.Usable).Id);
        Assert.Equal(2, changes);
        Assert.Same(second, catalog.Result);
    }

    [Fact]
    public void FindMount按组合键查找()
    {
        using var temp = new TempPlugins();
        temp.WriteLocal("demo", Fixtures.Minimal(), ("skills/main.md", Fixtures.Skill));

        var result = temp.Scan();

        Assert.NotNull(result.FindMount("demo/composer"));
        Assert.Equal("demo", result.Find("demo")?.Id);
        Assert.Null(result.Find("nope"));
        Assert.Null(result.FindMount("demo/missing"));
        Assert.Null(result.FindMount("demo"));
        Assert.Null(result.FindMount(null));
        Assert.Null(result.FindMount("/composer"));
    }

    [Fact]
    public void 内置与本地按来源排序()
    {
        using var temp = new TempPlugins();
        temp.WriteBuiltin("a-builtin", Fixtures.Minimal("builtin", "内置的"), ("skills/main.md", Fixtures.Skill));
        temp.WriteLocal("b-local", Fixtures.Minimal("local", "本地的"), ("skills/main.md", Fixtures.Skill));

        var result = temp.Scan();

        Assert.Equal(
            new[] { PluginSource.Builtin, PluginSource.Local },
            result.Plugins.Select(plugin => plugin.Source).ToArray());
    }
}
