# R1.1：8K 单文件任务 A–E 双跑（Qwen3.5-4B / Compact / 8K）
#
# 为什么用独立工作区：P5 的任务会直接改 src / tests，不能在本仓库里跑。
# P5_ISOLATE=1 会在每个任务开始前把 src / tests 还原到快照，避免 A 改坏的文件拖垮 D/E。
#
# 用法：
#   pwsh tools/r1-validate/run-8k.ps1                # 准备一次，跑 2 轮
#   pwsh tools/r1-validate/run-8k.ps1 -Rounds 1      # 只跑 1 轮
#   pwsh tools/r1-validate/run-8k.ps1 -Reset         # 重新复制并构建工作区
param(
    [string]$Workspace = "",
    [int]$Rounds = 2,
    [switch]$Reset
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# 工作区与日志一律放仓外：基准会复制整仓并产生大量文件，塞回工作区会污染
# agent 的搜索范围与 token 预算。可用 P5_SCRATCH_ROOT 指定位置。
$scratch = if ($env:P5_SCRATCH_ROOT) { $env:P5_SCRATCH_ROOT } else { Join-Path (Split-Path -Parent $repo) '_scratch' }
if (-not (Test-Path $scratch)) { New-Item -ItemType Directory -Path $scratch -Force | Out-Null }

if ([string]::IsNullOrWhiteSpace($Workspace)) {
    $Workspace = Join-Path $scratch 'r1-8k-workspace'
}

function Prepare-Workspace {
    Write-Host "准备 8K 工作区：$Workspace"

    if (Test-Path $Workspace) {
        Remove-Item $Workspace -Recurse -Force
    }

    robocopy $repo $Workspace /E /NFL /NDL /NJH /NJS `
        /XD .git .vs bin obj TestResults .tmp docs eval .github | Out-Null

    Push-Location $Workspace
    try {
        dotnet restore SDemo.DesktopAgent.Wpf.slnf --nologo | Out-Null
        # slnf 不含测试工程，验证命令要用 Core.Tests，必须单独还原。
        dotnet restore tests/SDemo.DesktopAgent.Core.Tests/SDemo.DesktopAgent.Core.Tests.csproj --nologo | Out-Null
        dotnet build SDemo.DesktopAgent.Wpf.slnf -c Release --no-restore --nologo -v q | Out-Null
        dotnet build tests/SDemo.DesktopAgent.Core.Tests/SDemo.DesktopAgent.Core.Tests.csproj `
            -c Release --no-restore --nologo -v q | Out-Null
    }
    finally {
        Pop-Location
    }
}

if ($Reset -or -not (Test-Path (Join-Path $Workspace 'SDemo.DesktopAgent.sln'))) {
    Prepare-Workspace
}

# 运行器先构建一次：后面每轮都用 --no-build 直接跑。
dotnet build (Join-Path $repo 'tools/p5-bench/P5Bench.csproj') -c Release --nologo -v q | Out-Null


$env:P5_PROVIDER = 'local'
$env:P5_MODEL = 'qwen3.5-4b'
$env:P5_MODE = 'Compact'
$env:P5_PROFILE = '8K'
$env:P5_MAX_CALLS = '15'
$env:P5_PROMPT = 'auto'
$env:P5_REPAIR = '1'
$env:P5_ISOLATE = '1'
# 4B 偶尔会退化成不结束的生成；单轮 240s 超时按失败处理，避免整轮评测挂死。
$env:P5_TIMEOUT = '240'

for ($round = 1; $round -le $Rounds; $round++) {
    $out = Join-Path $repo "eval/results/r1/8k-round$round"
    New-Item -ItemType Directory -Path $out -Force | Out-Null

    $env:P5_TOOL_LOG = Join-Path $scratch "r1-8k-round$round-tools.log"
    $console = Join-Path $scratch "r1-8k-round$round-console.log"

    Write-Host "`n=== 8K Round $round ==="
    dotnet run --project (Join-Path $repo 'tools/p5-bench/P5Bench.csproj') -c Release --no-build `
        -- $Workspace 2>&1 | Tee-Object -FilePath $console

    $results = Join-Path (Split-Path -Parent $Workspace) 'p5-bench-results.json'
    if (Test-Path $results) {
        Copy-Item $results (Join-Path $out 'p5-bench-results.json') -Force
        Write-Host "结果已归档：$out"
    }
    else {
        Write-Warning "没有找到 $results；本轮结果未归档。"
    }
}
