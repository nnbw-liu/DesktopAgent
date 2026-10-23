# 内循环提速（P4）

> 目标：改代码后的「构建 → 测试」不再每次都等沙箱全量复制。
> 默认仍走沙箱；直跑是显式打开、白名单、逐次审批、可审计的快车道。
> 计划与验收：[plans/p4-inner-loop.md](plans/p4-inner-loop.md) · 实测报告：[plans/p4-report.md](plans/p4-report.md)

## 1. 两种执行模式

```csharp
public enum ExecutionMode
{
    SandboxFirst,       // 默认：命令进沙箱
    AllowlistDirect     // 白名单命令可在宿主直跑
}
```

- `SandboxFirst` + `DirectRunEnabled=false` 是默认值；两个条件同时满足才允许直跑。
- `run_powershell` 永远走沙箱，不受直跑设置影响。
- 直跑开启后，设置页用红字说明：**不受沙箱网络隔离，可访问网络与真实工作区**。
- 直跑只影响 `run_command`；文件读写仍走 P3 的读前写 / 审批。

## 2. `run_command`

```csharp
run_command(command, args = "", workingDirectory = ".", timeoutSeconds = 0, purpose = "")
```

- `command` 只能是不带路径的可执行文件名，例如 `dotnet`；拒绝 `C:\...\dotnet.exe`、`cmd /c`、`powershell -Command`。
- 默认白名单：`dotnet`、`npm`、`pnpm`、`node`、`git`、`python`、`cargo`、`go`，可在设置页修改。
- `args` 按空白 / 引号切分，直接放进 `ProcessStartInfo.ArgumentList`，**从不拼 shell 字符串**。
- 拒绝参数里的 `& | > < ; $ ( ) \` ^ %`、换行、`..`、绝对路径 / UNC；单条参数总长 ≤ 4KB。
- `git` 只允许 `status` / `diff` / `log` / `show` / `rev-parse`；`git reset` / `git clean` 永远逐次审批。
- 工作目录必须是工作区内已存在的相对路径，junction / 符号链接拒绝。
- 超时：默认 120s；构建 / 测试 600s；模型传入值被夹在 1–900s。
- 输出：stdout / stderr 各按设置上限（默认 12K 字符）截断，带截断标记。
- 退出码非 0 也返回输出，不抛异常；模型可据此修正。

不满足策略时返回 `ERROR: ...`，并提示可改用 `run_powershell` 进沙箱。

## 3. 直跑执行器

`DirectProcessRunner`：

- `UseShellExecute=false`、`CreateNoWindow=true`；
- 加入 Job Object（进程数 / 内存 / UI 限制），超时或取消时终止整棵进程树；
- 环境变量白名单：`PATH`、`PATHEXT`、`TEMP`、`TMP`、`USERPROFILE`、`HOME`、`NUGET_PACKAGES`、`DOTNET_*`、`SystemRoot`、`SystemDrive`、`ComSpec`；其余不继承；
- 输出异步读取，避免管道阻塞；
- 取消时抛 `OperationCanceledException`，工具层转成「已取消；进程树已终止」。

## 4. 审批与会话授权

- 默认每次直跑都弹审批；安全命令可点「本会话允许」。
- 授权键是「可执行文件 + 前两个参数」：例如 `dotnet build Demo.sln`；同前缀后续直接执行。
- 高风险命令（`rm` / `del` / `format` / `rd` / `git reset` / `git clean` 等）**不显示**会话授权按钮。
- 授权只存内存（`SessionGrantStore`），应用重启全部失效；设置页列出当前授权并可单个撤销。
- 每次执行都写 `ToolAuditEntry`：`mode=direct`、`commandSha256`、`workingDirectory`、退出码、耗时、`approval=ApproveOnce/SessionGrant/Deny`。

## 5. 构建 / 测试结果

`run_command` 根据 `purpose` 或命令本身识别 `build` / `test`：

- `dotnet build`：解析 `N Error(s)` / `N Warning(s)`（兼容中文「N 个错误 / 个警告」），并提取 `error CS...` 行作为 `TopIssues`。
- `dotnet test`：解析 `Passed:` / `Failed:`（兼容中文「通过 / 失败」），提取 `[FAIL]` 用例名。
- 解析失败时 `Errors = -1`，仍保留原始输出尾部，不会把失败误判成成功。

结果写入 `ChatSession.LastVerification`，并通过 `LastVerificationJson` 落库；`WorkspaceStateProvider` 把它渲染进状态包：

```text
## 验证
dotnet build Demo.sln → 成功（0 errors / 2 warnings，8.4s，10:45）
dotnet test Demo.sln → 失败（12 passed / 1 failed：OptionsTests.RejectsEmptyEndpoint，10:47）
```

状态包截断时验证块永远保留。

## 6. 工作区命令与预 restore

- 工作区新增 `BuildCommand` / `TestCommand`；设置页「内循环」可按当前工作区填写、自动探测并保存。
- 探测规则：根目录有 `.sln` → `dotnet build <sln> -v minimal`；只有 `.csproj` → 对应项目；`package.json` → `npm/pnpm run build` / `test`。
- `workspace_info` 返回已保存的构建 / 测试命令，模型可直接用于 `run_command`。
- 「预热包缓存」按钮在宿主执行一次固定的 `dotnet restore`（用户点击即授权），**不开启直跑模式**；restore 后沙箱内构建可加 `--no-restore`。

## 7. 边界与未实现

- 直跑白名单里的 `npm` / `pnpm` 在 Windows 上是 `.cmd`，当前 `DirectProcessRunner` 只解析 `.exe`；实际使用建议 `dotnet`，或显式配置可执行文件包装。计划里的 npm 支持仍需补 `.cmd` 启动策略。
- NuGet 缓存对 AppContainer 的只读 ACL 映射未实现；当前是「宿主预 restore + 沙箱 `--no-restore`」，不是完整的离线包映射。
- 热工作区（`warmWorkspace.enabled`）未实现，仍每次全量复制；配置字段已预留。
- 不做交互式终端、命令输出流式进聊天、git 写操作、远程 / 容器执行。
- 直跑默认关闭；没有直跑时仍可用沙箱 + 预 restore，只是更慢。

## 8. 验证

```powershell
dotnet test tests/SDemo.DesktopAgent.Core.Tests/SDemo.DesktopAgent.Core.Tests.csproj
dotnet test tests/SDemo.DesktopAgent.Sandbox.Tests/SDemo.DesktopAgent.Sandbox.Tests.csproj
dotnet build SDemo.DesktopAgent.Wpf.slnf
dotnet run --project tools/p4-loop -c Release -- F:\AI\Code\SDemo\DesktopAgent
```
