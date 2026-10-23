# P7 结果目录

- `round1/full-32k/`：Qwen3-8B，32K Full，Round 1 基线（修复前）。
- `round1/compact-8k/`：Qwen3.5-4B，8K Compact，Round 1 基线（修复前）。
- `round1/full-32k-invalid-repair/`：早期运行器在“回灌判定”时没有注入隐藏测试，会把“没改文件但既有测试绿”误判为通过；仅保留作排障记录，**不作为基线数据**。
- `round2/full-32k/`：同一 commit / 任务 / 配置，应用 Top 3 修复后的重跑。
- `round2/compact-8k/`：同上，8K Compact。
- `round1/deepseek-32k/`、`round2/deepseek-32k/`：云端 DeepSeek V4 Pro，32K Full，同配置双跑（7/8、7/8）。
- `cloud-smoke/`：云端 T01 冒烟，用于确认 provider/Key/工具调用链路。

每个任务目录：

```text
T01/
  transcript.json   输出、工具轨迹、步数、耗时、验收结果
  changes.diff      模型改动（在注入隐藏测试前捕获）
  acceptance.log    验收命令与输出
  verdict.json      成功判定、失败分类、原因
```

汇总：`summary.json`（通过数、每个任务结果）。

注意：仓库根 `.gitignore` 的 `*.log` 会忽略 `acceptance.log`；本目录的日志已用 `git add -f` 归档，后续新增日志需要同样处理，或只提交 JSON/diff。
