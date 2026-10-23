# P7 外部仓库基准

## 锁定仓库

- 仓库：`commandlineparser/commandline`
- Commit：`b0b0ec9f5470bacc0823c013a599ba3b4e115900`（tag `v2.9.1`，2023-03-07）
- 本地镜像：`F:\AI\Code\SDemo\p7-bench\commandline`
- 基线：`dotnet build CommandLine.sln` 通过；`netcoreapp3.1` 684 个测试全部通过（冷构建约 29s，测试约 0.3s）。

任务与结果都绑定该 commit。任务规格冻结后不再改；如需改，必须记录原因并重跑两轮。

## 目录

```text
eval/
  repo.json               仓库、commit、基线记录
  configs/
    compact-8k.json       Qwen3.5-4B / Compact / 8K / 15 calls
    full-32k.json         Qwen3-8B / Full / 32K / 15 calls（/no_think）
  tasks/T01..T08.md       8 个冻结任务（需求 + 验收命令 + 隐藏测试）
  tests/                  隐藏验收测试，只在验收阶段注入工作区
  results/                Round 1 / Round 2 产物
```

## 运行

先加载模型（LM Studio）：

```powershell
lms unload --all
lms load qwen3.5-4b --context-length 8192 --gpu max -y
# 32K：
# lms unload --all
# lms load "qwen/qwen3-8b" --context-length 32768 --gpu max -y
```

再跑：

```powershell
dotnet run --project tools/AgentEval -c Release -- `
  --repo F:\AI\Code\SDemo\p7-bench\commandline `
  --commit b0b0ec9f5470bacc0823c013a599ba3b4e115900 `
  --config eval/configs/full-32k.json `
  --tasks eval/tasks `
  --out eval/results/round1/full-32k
```

运行器会：

1. 从镜像创建一个临时 git worktree（不改镜像）；
2. 每个任务前 `git reset --hard` + `git clean -fd`；
3. 跑 Agent，记录输出、工具轨迹、步数、耗时；
4. 在注入隐藏测试前捕获 `git diff`；
5. 注入隐藏测试，执行任务里的 `acceptance` 命令；
6. 写 `transcript.json`、`changes.diff`、`acceptance.log`、`verdict.json`，最后写 `summary.json`。

`--only T01,T02` 可只跑指定任务。`--keep-work` 保留 worktree 便于手工复核。

Round 1 / Round 2 的实际数字与结论见 [p7-report.md](../docs/plans/p7-report.md)；结果目录说明见 [results/README.md](results/README.md)。

修复后的运行器在回灌判定前会临时注入隐藏测试，验收完立即清掉，避免“没改文件但既有测试绿”被误判为通过；不要再用 `round1/full-32k-invalid-repair` 的数据做基线。

## 任务与隐藏测试

| 任务 | 类型 | 验收重点 |
| --- | --- | --- |
| T01 | feature | `ParserSettings.MaximumDisplayWidth` 拒绝非正值 |
| T02 | feature | `ParserResult<T>.IsParsed` 反映解析结果 |
| T03 | bugfix | `HelpText.MaximumDisplayWidth` 拒绝非正值 |
| T04 | bugfix | 含转义引号的参数经 `FormatCommandLine` + `SplitArgs` 后不拆散 |
| T05 | tests | 补 3 个 `PreferShortName` 单测 |
| T06 | tests | 补 3 个 `SplitArgs` 边界单测 |
| T07 | refactor | 抽取 `NeedsQuotes`，行为不变 |
| T08 | mixed | 增加 `UnParserSettings.WithSkipDefaultOnly()` 并验证 |

隐藏测试在 `eval/tests/`，只在验收阶段复制到 `tests/CommandLine.Tests/Unit/AgentEval/`，模型运行期间不可见。

## 已知边界

- 8B 必须 `/no_think`；Qwen3 默认 thinking 会吃光输出。
- LM Studio 的 `--context-length` 在当前版本可能被模型默认配置覆盖，需用 `lms ps` 确认实际 CONTEXT；评测配置里的 8K/32K 是 **Agent 侧预算**，不等于加载上下文。
- `FullMaxToolCalls` 是为 P7 新增的评测可调项，产品默认仍为 8。
- 外部仓库测试只跑 `netcoreapp3.1`，`net461` 仅参与构建。
