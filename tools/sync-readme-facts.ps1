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
# ④ 状态徽章（v1.0 摘 BETA 时加）：0.x 是 BETA，1.x 起是正式版。
# 由版本号推出来，不写死 —— 写死的话下一个大版本又会漂（这个文件存在的理由就是防漂）。
$majorVer = 0
try { $majorVer = [int]($Version.Split('.')[0]) } catch { $majorVer = 0 }
$statusWord = if ($majorVer -ge 1) { 'stable-brightgreen' } else { 'BETA-orange' }
$text = Swap $text 'status-[A-Za-z0-9.]+-(orange|brightgreen|green|yellow|blue)' ("status-" + $statusWord) '状态徽章'

$readmeChanged = ($text -ne $orig)
if ($readmeChanged) { [System.IO.File]::WriteAllText($readme, $text, (New-Object System.Text.UTF8Encoding($false))) }

# ⑤ 包里那份 使用说明.txt 的第一行版本号。
# 同一个道理：它是**从构建结果推出来的**，写死在文件里迟早和 exe 对不上
# （实测已经发生过一次：文档停在 v0.5.1，而包里的 exe 已经是 v1.1.0）。
# 这是"同一个事实写在两个地方"的又一个形状 —— 让它由构建同步。
$manualChanged = $false
$manual = Join-Path $root 'dist\使用说明.txt'
if (Test-Path $manual) {
    $mt = [System.IO.File]::ReadAllText($manual, [System.Text.Encoding]::UTF8)
    $mt2 = [regex]::Replace($mt, '(?m)^SnapWheel 快照轮环\s+v\d+\.\d+\.\d+', ("SnapWheel 快照轮环  v" + $Version), 1)
    if ($mt2 -ne $mt) {
        [System.IO.File]::WriteAllText($manual, $mt2, (New-Object System.Text.UTF8Encoding($false)))
        $manualChanged = $true
    }
}

if (-not $readmeChanged -and -not $manualChanged) {
    Write-Host "  [OK] README 同步    v$Version / $kb KB（本来就是最新的）" -ForegroundColor Green
    exit 0
}
$what = @()
if ($readmeChanged) { $what += ($changes -join '、') }
if ($manualChanged) { $what += '使用说明版本号' }
Write-Host ("  [OK] README 同步    v{0} / {1} KB  改动：{2}" -f $Version, $kb, ($what -join '、')) -ForegroundColor Green
exit 0
