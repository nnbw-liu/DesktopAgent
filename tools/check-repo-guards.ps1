# 仓库守卫：体量、死链、脏文件、工作区清单、依赖边、契约同改。
# 本地可直接跑；CI 里由 .github/workflows/contracts.yml 调用。
#
# 用法：
#   pwsh tools/check-repo-guards.ps1                    # 常规检查
#   pwsh tools/check-repo-guards.ps1 -Base origin/master  # 额外检查契约是否同改（需要 git 历史）
param([string]$Base = "")

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo

$fail = @()
$ok = @()

function Check([string]$name, [scriptblock]$test, [string]$detail) {
    $issues = & $test
    if ($issues) {
        $script:fail += "[$name] $detail`n" + (($issues | ForEach-Object { "    - $_" }) -join "`n")
    }
    else {
        $script:ok += "[$name] $detail"
    }
}

# 1) 入口文档行数：根 AGENTS.md ≤100 行，其它 AGENTS.md ≤120 行
Check 'agents-size' {
    Get-ChildItem -Recurse -File -Filter AGENTS.md |
        Where-Object { $_.FullName -notmatch '\\.git\\|\\obj\\|\\bin\\' } |
        ForEach-Object {
            $limit = if ($_.DirectoryName -eq $repo) { 100 } else { 120 }
            $lines = (Get-Content $_.FullName).Count
            if ($lines -gt $limit) { "$($_.FullName.Replace("$repo\", ''))：$lines 行 > $limit" }
        }
} 'AGENTS.md 行数'

# 2) 必读文档体量
Check 'doc-size' {
    $issues = @()
    foreach ($rule in @(
            @{ Dir = 'README.md'; Max = 8; Pattern = $null },
            @{ Dir = 'docs/architecture'; Max = 60; Pattern = $null },
            @{ Dir = 'docs/guide'; Max = 30; Pattern = $null })) {
        if ($rule.Dir -eq 'README.md') {
            $f = Get-Item 'README.md' -ErrorAction SilentlyContinue
            if ($f -and $f.Length -gt ($rule.Max * 1KB)) { $issues += "README.md：$([math]::Round($f.Length/1KB,1)) KB > $($rule.Max) KB" }
            continue
        }
        Get-ChildItem $rule.Dir -File -Filter *.md -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -ne 'detail' } |
            ForEach-Object {
                if ($_.Length -gt ($rule.Max * 1KB)) { $issues += "$($_.FullName.Replace("$repo\", ''))：$([math]::Round($_.Length/1KB,1)) KB > $($rule.Max) KB" }
            }
    }
    $issues
} 'README ≤8KB / architecture ≤60KB / guide ≤30KB'

# 3) 死链
Check 'dead-links' {
    & (Join-Path $PSScriptRoot 'check-doc-links.ps1') -Quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { "有死链，运行 pwsh tools/check-doc-links.ps1 查看" }
} '相对链接可解析'

# 4) 脏文件不得入库
Check 'no-dirty-artifacts' {
    $tracked = git ls-files 'TestResults' '.tmp' 2>$null
    if ($tracked) { $tracked }
} 'TestResults / .tmp 未被提交'

# 5) 工作区清单（W3 落地后自动生效）
Check 'workspaces' {
    if (-not (Test-Path 'workspaces')) { return @() }
    $issues = @()
    Get-ChildItem 'workspaces' -File -Filter *.json | ForEach-Object {
        try { $m = Get-Content $_.FullName -Raw | ConvertFrom-Json } catch { $issues += "$($_.Name)：JSON 解析失败"; return }
        foreach ($k in 'id', 'title', 'status', 'entry', 'paths', 'build', 'test') {
            if (-not $m.$k) { $issues += "$($_.Name)：缺字段 $k" }
        }
        if ($m.id -ne $_.BaseName) { $issues += "$($_.Name)：id 与文件名不一致（$($m.id)）" }
        if ($m.status -and $m.status -notin @('active', 'planned')) { $issues += "$($_.Name)：status 只能是 active / planned" }
        if ($m.entry) {
            if (-not (Test-Path $m.entry)) {
                $issues += "$($_.Name)：entry 不存在 $($m.entry)"
            }
            else {
                $lines = (Get-Content $m.entry).Count
                if ($lines -gt 120) { $issues += "$($m.entry)：$lines 行 > 120" }
            }
        }
        # 规划中的工作区目录还不存在，跳过路径校验
        if ($m.status -eq 'active') {
            foreach ($p in @($m.paths)) { if ($p -and -not (Test-Path $p)) { $issues += "$($_.Name)：paths 不存在 $p" } }
            foreach ($p in @($m.readOnlyPaths)) { if ($p -and -not (Test-Path $p)) { $issues += "$($_.Name)：readOnlyPaths 不存在 $p" } }
        }
    }
    $issues
} 'workspaces/*.json：字段齐全、入口存在、active 的路径有效'

# 6) 依赖边（arch-guard 的种子；Apps / Shell 建立后自动收紧）
Check 'dependency-edges' {
    $issues = @()
    Get-ChildItem 'src' -Recurse -File -Filter *.csproj | ForEach-Object {
        $proj = $_
        $rel = $proj.FullName.Replace("$repo\", '') -replace '\\', '/'
        $xml = [xml](Get-Content $proj.FullName -Raw)
        foreach ($ref in $xml.Project.ProjectReference) {
            $inc = $ref.Include
            if (-not $inc) { continue }
            $target = ($inc -replace '\\', '/') -replace '^.*?/', ''
            # 统一成 src/ 下的相对描述
            $resolved = (Resolve-Path (Join-Path $proj.DirectoryName $inc) -ErrorAction SilentlyContinue)
            if (-not $resolved) { continue }
            $t = $resolved.Path.Replace("$repo\", '') -replace '\\', '/'
            $isApp = $rel -match '^src/Apps/([^/]+)/'
            $tIsApp = $t -match '^src/Apps/([^/]+)/'
            if ($isApp -and $tIsApp) {
                $a = $Matches[1]; $b = ($t -split '/')[2]
                if ($a -ne $b) { $issues += "$rel → $t（应用之间禁止互相引用）" }
            }
            if ($isApp -and ($t -match '^src/App/|^src/Agents/|^src/Shell/')) {
                if ($t -notmatch '^src/Shell/Contracts/') { $issues += "$rel → $t（应用不得依赖 Agent / Shell 实现）" }
            }
            if ($rel -match '^src/Shell/' -and ($t -match '^src/App/|^src/Agents/|^src/Apps/')) {
                $issues += "$rel → $t（宿主只依赖 Shell.Contracts）"
            }
            if (($rel -match '^src/App/|^src/Agents/') -and ($t -match '^src/Apps/|^src/Shell/')) {
                $issues += "$rel → $t（Agent 不得依赖产品应用）"
            }
        }
    }
    $issues
} '产品间零引用'

# 7) 契约同改（需要 -Base）
if ($Base) {
    Check 'contract-cochange' {
        $changed = git diff --name-only "$Base...HEAD" 2>$null
        if (-not $changed) { return @() }
        $sharedChanged = $changed | Where-Object { $_ -match '^src/Shared/' }
        $docsChanged = $changed | Where-Object { $_ -match '^docs/architecture/' }
        if ($sharedChanged -and -not $docsChanged) {
            "改了 src/Shared（" + ($sharedChanged -join ', ') + "），但没有同时更新 docs/architecture/"
        }
    } "契约改动同 PR 更新文档（base=$Base）"
}

Pop-Location

$ok | ForEach-Object { Write-Host "[ok]   $_" }
if ($fail.Count -gt 0) {
    Write-Host ''
    $fail | ForEach-Object { Write-Host "[FAIL] $_" }
    Write-Host "`n共 $($fail.Count) 项未通过"
    exit 1
}
Write-Host "`n全部通过（$($ok.Count) 项）"
exit 0
