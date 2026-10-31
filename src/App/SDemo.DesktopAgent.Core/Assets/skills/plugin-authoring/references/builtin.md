# 新增内置插件（C# 仓库路径）

> 只适用于 **SDemo.DesktopAgent 仓库维护者**：把插件随应用一起发布。
> 面向最终用户的自制插件走 `references/declarative.md`，不要碰 C#。

内置插件与本地插件**共用同一套清单格式和校验规则**，区别只是内容来源：清单与技能作为
**嵌入资源**编译进各自的类库，运行时由 `EmbeddedPluginPackage` 读取，`source = "builtin"`。
这样加载器从第一天就被内置插件验证，Android 也能拿到内置插件。

## 1. 新建类库

```text
src/Plugins/SDemo.DesktopAgent.Plugin.<名字>/
├── SDemo.DesktopAgent.Plugin.<名字>.csproj
├── plugin.json
├── <名字>Plugin.cs
└── skills/
    └── main.md
```

`csproj` 只引用插件契约（`ArchitectureTests.内置插件只依赖插件契约` 会检查这一点）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>SDemo.DesktopAgent.Plugins.<名字></RootNamespace>
    <AssemblyName>SDemo.DesktopAgent.Plugin.<名字></AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\SDemo.DesktopAgent.Plugins.Abstractions\SDemo.DesktopAgent.Plugins.Abstractions.csproj" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="plugin.json" LogicalName="plugin.json" />
    <EmbeddedResource Include="skills\*.md" LogicalName="skills/%(Filename)%(Extension)" />
  </ItemGroup>
</Project>
```

引用其它工程会被架构测试拒绝；UI / Core 也不允许出现具体插件的 `id`。

## 2. 清单与技能

与本地插件完全一样（见 `references/declarative.md`），注意：

- `plugin.json` 的 `id` 必须与类库里的插件 id 一致；
- 技能路径写包内相对路径，例如 `skills/main.md`；
- 内置插件的 `id` 是公开契约：用户会用同 id 的本地插件覆盖它，改名等于破坏兼容。

## 3. 插件类

继承 `EmbeddedPluginPackage`，三行即可（泛型样板已经收敛在基类里）：

```csharp
namespace SDemo.DesktopAgent.Plugins.MyPlugin;

/// <summary>内置我的插件：声明式清单 + 技能，作为嵌入资源随程序集发布。</summary>
public sealed class MyPlugin()
    : EmbeddedPluginPackage("my-plugin", typeof(MyPlugin).Assembly);
```

## 4. 注册进组合根

`src/App/SDemo.DesktopAgent.Core/Composition/AgentCoreServiceCollectionExtensions.cs`：

```csharp
using SDemo.DesktopAgent.Plugins.MyPlugin;

// 内置插件：每个插件一个类库，清单与技能作为嵌入资源随程序集发布。
services.AddSingleton<IPluginPackage, TranslatePlugin>();
services.AddSingleton<IPluginPackage, SummarizePlugin>();
services.AddSingleton<IPluginPackage, QuickAskPlugin>();
services.AddSingleton<IPluginPackage, MyPlugin>();   // ← 新增
```

`PluginCatalog` 会自动把内置包排在本地插件前面，并在同 id 时让本地覆盖内置。

## 5. 测试与文档

- `tests/SDemo.DesktopAgent.Core.Tests/BuiltinPluginTests.cs` 是内置插件的验收习惯：
  扫描内置包（`new PluginCatalog([new MyPlugin()], 临时空目录)`），断言无失败、
  capability 可用、入口数量、参数与图标；
- 新增清单或技能后跑 `dotnet test tests/SDemo.DesktopAgent.Core.Tests`；
- 在 `docs/features/plugin-authoring.md` 与 `docs/architecture/detail/plugin-system.md`
  的示例里补一节，方便用户理解这个内置插件能做什么；
- 不要改 `PluginValidator` 来「放行」自己的清单：规则对本地插件同样生效。

## 6. 完成清单

- [ ] 类库只依赖 `SDemo.DesktopAgent.Plugins.Abstractions`；
- [ ] `plugin.json` 与 `skills/*.md` 都是嵌入资源，`LogicalName` 正确；
- [ ] 插件已在 `AgentCoreServiceCollectionExtensions` 注册；
- [ ] `BuiltinPluginTests` 覆盖新插件（或新增等价用例）；
- [ ] 本地同 id 覆盖仍然生效（`PluginCatalog` 的合并逻辑没有改坏）。
