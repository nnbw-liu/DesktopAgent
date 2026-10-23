# CI（GitHub Actions）

> 文件：`.github/workflows/ci.yml`　状态：已接入，等待首次 GitHub 运行验证。

## 1. 为什么要 CI

在 CI 之前，Core 616 个测试、WPF 构建和 Sandbox 214 个测试都只在本机跑；M1–M4 连续四周的改动没有自动回归门禁。CI 的目标是：

1. 每个 PR 必须构建通过 + Core 测试全绿；
2. master 合并后跑一次 Sandbox 测试（非阻塞，托管 runner 不保证 AppContainer 可用）；
3. 不把“本机能跑”当成“合并后还能跑”。

## 2. 工作流

| Job | 触发 | 内容 | 是否阻塞 |
| --- | --- | --- | --- |
| `build-and-test` | PR / push master / 手动 | `dotnet restore` + `build SDemo.DesktopAgent.Wpf.slnf -c Release` + Core 测试 + TRX 产物 | ✅ 必过 |
| `sandbox-tests` | push master / 手动 | `dotnet test tests/SDemo.DesktopAgent.Sandbox.Tests -c Release` | ⚠️ 非阻塞（`continue-on-error`） |

设计取舍：

- **Windows runner**：WPF / `net10.0-windows10.0.19041.0` 只能在 Windows 上构建；
- **Release**：和实际发布一致，也能暴露 Debug-only 的编译问题；
- **Sandbox 非阻塞**：AppContainer / Windows Sandbox 在 GitHub 托管 runner 上不保证可用；稳定后迁到 self-hosted 再改为必过；
- **NuGet 缓存**：按 `*.csproj` / `*.sln` / `*.slnf` / `Directory.Build.props` 的 hash 缓存 `~/.nuget/packages`；
- **并发取消**：同一 ref 的新 push 会取消旧运行，避免排队。

## 3. 本地等价命令

```powershell
dotnet restore SDemo.DesktopAgent.Wpf.slnf
dotnet build SDemo.DesktopAgent.Wpf.slnf -c Release --no-restore
dotnet test tests/SDemo.DesktopAgent.Core.Tests -c Release
dotnet test tests/SDemo.DesktopAgent.Sandbox.Tests -c Release
```

本机实测（2026-10-23）：

- Release 构建：0 警告 / 0 错误；
- Core：632 通过 / 0 失败（CI 接入时为 616，P1/P2 新增 16 个）；
- Sandbox：214 通过 / 1 跳过。

## 4. 后续

1. 在 GitHub 上给 master 开分支保护，要求 `Build + Core tests` 必过；
2. 首次 PR 运行后，确认 `actions/cache` 命中、TRX 产物可下载；
3. Sandbox 测试稳定后，迁到带 Windows 容器的 self-hosted runner，去掉 `continue-on-error`；
4. 可选：加 `dotnet format --verify-no-changes`，但先统一现有格式，避免一次引入大量噪音。
