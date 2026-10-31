# 文档链检查：所有 .md 里的相对链接必须指向存在的文件/目录。
# 用途：W4 的 CI 守卫之一；拆分/移动文档后先跑一遍，避免死链。
#
# 用法：
#   pwsh tools/check-doc-links.ps1           # 报告死链，退出码 = 死链数
#   pwsh tools/check-doc-links.ps1 -Quiet     # 只输出计数
param([switch]$Quiet)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

$files = Get-ChildItem $repo -Recurse -File -Filter *.md |
    Where-Object { $_.FullName -notmatch '\\.git\\|\\obj\\|\\bin\\|\\node_modules\\' }

$missing = @()
foreach ($f in $files) {
    $text = Get-Content $f.FullName -Raw
    foreach ($m in [regex]::Matches($text, '\]\(([^)\s]+)\)')) {
        $target = $m.Groups[1].Value
        # 跳过外链、锚点、任何 URI scheme（含 dsh-session: 这类自定义协议）
        if ($target -match '^[a-z][a-z0-9+.-]*:|^#' -or $target -match '^\.\.?$') { continue }
        # 跳过占位写法（无扩展名、无路径分隔符，例如文档示例里的 ](url)
        if ($target -notmatch '[./]') { continue }
        $path = ($target -split '#')[0]
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        # 去掉 GitHub 风格的行号后缀 :123
        $path = $path -replace ':\d+$', ''
        $base = Split-Path -Parent $f.FullName
        $resolved = Join-Path $base ($path -replace '%20', ' ')
        if (-not (Test-Path -LiteralPath $resolved)) {
            $missing += "{0}: {1}" -f $f.FullName.Replace("$repo\", ''), $target
        }
    }
}

if ($missing.Count -eq 0) {
    if (-not $Quiet) { Write-Host "[ok] $($files.Count) 个文档，无死链" }
    exit 0
}

Write-Host "[fail] 发现 $($missing.Count) 处死链："
$missing | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" }
exit ([Math]::Min($missing.Count, 200))
