<#
  sync-readme-facts.ps1 —— 把 README 里"会随构建变"的数字，同步成实测值。

  为什么需要它（不是洁癖，是真出过）：
    README 里有两类数字是**从构建结果推出来的**，但一直靠人手改：
      · 版本徽章   version-v0.9.x
      · 体积徽章    exe-NNN%20KB，以及正文里的「一个 NNN KB 的 exe」
    实测已经漂过一次：徽章写着 343 KB，实际构建出来是 346 KB；
    仓库简介里更是写着 274 KB，那还是好几个版本以前的数。

    这正是反例 #1（度量与绘制同源）的同一个形状 —— **同一个事实写在了两个地方，
    它们迟早不一致**。解法也一样：只留一个来源（构建产物），另一处由程序同步。

  用法：
    powershell -File tools\sync-readme-facts.ps1 -Exe build\SnapWheel.exe -Version 0.9.5
    （build.ps1 -Package 会自动调它）

  只改它认识的那几种写法；找不到就跳过，绝不猜。
#>
param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [Parameter(Mandatory=$true)][string]$Version
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$readme = Join-Path $root 'README.md'

if (-not (Test-Path $Exe))    { Write-Host "  [i] 找不到 $Exe，跳过 README 同步"; exit 0 }
if (-not (Test-Path $readme)) { Write-Host "  [i] 找不到 README.md，跳过"; exit 0 }

$kb = [int][Math]::Round((Get-Item $Exe).Length / 1024.0)
$text = [System.IO.File]::ReadAllText($readme, [System.Text.Encoding]::UTF8)
$orig = $text
$changes = @()

function Swap([string]$t, [string]$pattern, [string]$replacement, [string]$label) {
    $m = [regex]::Matches($t, $pattern)
    if ($m.Count -gt 0) {
        $script:changes += ("{0} × {1}" -f $label, $m.Count)
        return [regex]::Replace($t, $pattern, $replacement)
    }
    return $t
}

# ① 版本徽章
$text = Swap $text 'version-v\d+\.\d+\.\d+-blue' ("version-v" + $Version + "-blue") '版本徽章'
# ② 体积徽章
$text = Swap $text 'exe-\d+%20KB-lightgrey' ("exe-" + $kb + "%20KB-lightgrey") '体积徽章'
# ③ 正文里的「一个 NNN KB 的 exe」（容忍中间有没有空格、有没有「约」）
$text = Swap $text '一个约?\s*\d+\s*KB\s*的\s*exe' ("一个 " + $kb + " KB 的 exe") '正文体积'

if ($text -eq $orig) {
    Write-Host "  [OK] README 同步    v$Version / $kb KB（本来就是最新的）" -ForegroundColor Green
    exit 0
}

[System.IO.File]::WriteAllText($readme, $text, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("  [OK] README 同步    v{0} / {1} KB  改动：{2}" -f $Version, $kb, ($changes -join '、')) -ForegroundColor Green
exit 0
