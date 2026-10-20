# 内置插件目录

这里是**随应用发布**的内置插件，构建时会被复制到输出目录的 `plugins\` 下，
运行时由 `PluginCatalog` 从 `AppContext.BaseDirectory\plugins` 只读加载（`source = "builtin"`）。

规则与用户插件完全相同，只是位置不同：

```text
plugins/
└── translate/
    ├── plugin.json      ← 必需
    └── skills/
        └── translate.md ← 必需（capabilities[].skill 指向它）
```

- 用户插件目录（可写、可覆盖内置插件）：
  - Windows：`%APPDATA%\SDemo\DesktopAgent\plugins\`
  - Android：`<AppDataDirectory>/plugins/`
- 同 `id` 时**后加载的本地插件覆盖内置插件**，设置页会把内置那条标成「已覆盖」。
- 扫描只认 `plugins/<目录>/plugin.json`，其它文件（包括本 README）会被忽略。

清单格式、参数、占位符与校验规则见 [docs/plugin-system.md](../docs/plugin-system.md)。

> M0 阶段这里只有说明文件；内置「翻译」插件在 M5 放进 `translate/`，即可验证内置加载这条接缝。
