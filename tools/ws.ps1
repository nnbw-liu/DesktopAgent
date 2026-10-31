# 工作区入口工具：列出、查看某个工作区、给独立 worktree 建议。
#
# 用法：
#   pwsh tools/ws.ps1                  # 列出全部工作区
#   pwsh tools/ws.ps1 agent            # 查看 agent 工作区（路径/命令/只读区）
#   pwsh tools/ws.ps1 agent -Json      # 机器可读输出
#   pwsh tools/ws.ps1 planner -Sparse  # 打印独立 worktree + sparse-checkout 命令
param(
    [Parameter(Position = 0)][string]$Id,
    [switch]$Json,
    [switch]$Sparse
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$all = Get-ChildItem (Join-Path $repo 'workspaces') -File -Filter *.json |
    ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json }

if ([string]::IsNullOrWhiteSpace($Id)) {
    $all |
        Sort-Object @{ Expression = { if ($_.status -eq 'planned') { 1 } else { 0 } } }, id |
        Select-Object id, status, title, entry |
        Format-Table -AutoSize
    Write-Host "用法：pwsh tools/ws.ps1 <id>    例：pwsh tools/ws.ps1 agent"
    return
}

$w = $all | Where-Object { $_.id -eq $Id }
if (-not $w) {
    Write-Host "没有工作区 '$Id'。可选：$($all.id -join ', ')"
    exit 1
}

if ($Json) { $w | ConvertTo-Json -Depth 5; return }

Write-Host "工作区：$($w.title)   [$($w.status)]"
Write-Host "入口：  $($w.entry)"
Write-Host "路径：  $($w.paths -join ', ')"
if ($w.readOnlyPaths) { Write-Host "只读：  $($w.readOnlyPaths -join ', ')" }
Write-Host "构建：  $($w.build)"
Write-Host "测试：  $($w.test)"
if ($w.deny) { Write-Host "禁读：  $($w.deny -join ', ')" }

if ($w.status -eq 'planned') {
    Write-Host "`n注意：目录尚未创建。先读入口里的「开工前先做三件事」。"
}

if ($Sparse) {
    Write-Host "`n# 在仓外建独立工作区（重型隔离，仅在并行/长任务时用）："
    Write-Host "git worktree add ..\ws-$($w.id) -b agent/$($w.id)"
    Write-Host "git -C ..\ws-$($w.id) sparse-checkout set --cone $($w.paths -join ' ') docs/architecture workspaces"
}
