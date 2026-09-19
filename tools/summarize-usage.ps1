<#
  summarize-usage.ps1 —— 把本地使用统计变成一张能做决定的表。

  背景：这个项目几个"往哪走"的方向都是靠想定的，然后被真实数据否掉
  （我提议"把搬运做到极致"，而日志显示传递模式用户自己用了 12 次就再没打开）。
  与其再猜第二轮，不如让程序如实记一周，用这张表说话。

  用法：
    powershell -File tools\summarize-usage.ps1
    powershell -File tools\summarize-usage.ps1 -Path "C:\...\usage-log.tsv"
#>
param([string]$Path = "")

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($Path)) {
    $Path = Join-Path $env:APPDATA 'SnapWheel\usage-log.tsv'
}
if (-not (Test-Path $Path)) {
    Write-Host "没有统计文件：$Path" -ForegroundColor Yellow
    Write-Host "先在托盘右键打开「记录本地使用统计（只在本机）」，用几天再看。" -ForegroundColor Yellow
    exit 0
}

$rows = @()
foreach ($line in [System.IO.File]::ReadAllLines($Path, [System.Text.Encoding]::UTF8)) {
    if ($line.Length -eq 0 -or $line[0] -eq '#') { continue }
    $p = $line.Split("`t")
    if ($p.Length -lt 2) { continue }
    $detail = if ($p.Length -ge 3) { $p[2] } else { "" }
    $rows += [pscustomobject]@{ T = $p[0]; Ev = $p[1]; D = $detail }
}

if ($rows.Count -eq 0) { Write-Host "统计文件里还没有数据。" -ForegroundColor Yellow; exit 0 }

$first = $rows[0].T
$last  = $rows[$rows.Count - 1].T
Write-Host ""
Write-Host "SnapWheel 本地使用统计" -ForegroundColor Cyan
Write-Host ("  文件   {0}" -f $Path)
Write-Host ("  区间   {0}  →  {1}   共 {2} 条" -f $first, $last, $rows.Count)
Write-Host ""

# ---- ① 每天多少事 ----
Write-Host "【每天】" -ForegroundColor Cyan
$rows | Group-Object { $_.T.Substring(0, 10) } | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0}   {1,4} 条" -f $_.Name, $_.Count)
}
Write-Host ""

# ---- ② 功能用得怎么样 ----
Write-Host "【功能】用得多的排前面；从没用过的也列出来（那才是重点）" -ForegroundColor Cyan
$all = @('Shot','Shot.Cancel','DragOut','DropIn','ClipboardIn','Import','SaveAs','Pin',
         'LongShot.Start','Ocr','Translate','Carry.Start','Carry.Drop','UndoDelete')
foreach ($e in $all) {
    $g = @($rows | Where-Object { $_.Ev -eq $e })
    if ($g.Count -eq 0) {
        Write-Host ("  {0,-16} {1,5}   从未使用" -f $e, 0) -ForegroundColor DarkGray
    } else {
        $l = $g[$g.Count - 1].T
        $color = if ($g.Count -ge 10) { 'Green' } else { 'Gray' }
        Write-Host ("  {0,-16} {1,5}   最后 {2}" -f $e, $g.Count, $l) -ForegroundColor $color
    }
}
Write-Host ""

# ---- ③ 截图之后环上有几张（这是"环到底是什么"的直接证据）----
$ri = @($rows | Where-Object { $_.Ev -eq 'RingItems' })
if ($ri.Count -gt 0) {
    Write-Host "【截图之后，环上有几张图】" -ForegroundColor Cyan
    $ri | Group-Object { $_.D } | Sort-Object { [int]$_.Name } | ForEach-Object {
        $pct = 100.0 * $_.Count / $ri.Count
        $bar = '#' * [int][Math]::Round($pct / 4)
        Write-Host ("  {0,3} 张  {1,4} 次  {2,5:F0}%  {3}" -f $_.Name, $_.Count, $pct, $bar)
    }
    $nums = $ri | ForEach-Object { [int]$_.D }
    Write-Host ("  平均 {0:F1} 张   最多 {1} 张" -f (($nums | Measure-Object -Average).Average), ($nums | Measure-Object -Maximum).Maximum)
    Write-Host ""
}

# ---- ④ 标注里到底用了哪些工具 ----
$sh = @($rows | Where-Object { $_.Ev -eq 'Shot' -and $_.D -match '=' })
if ($sh.Count -gt 0) {
    Write-Host "【标注用到的工具】" -ForegroundColor Cyan
    $c = @{}
    foreach ($r in $sh) {
        foreach ($tk in ($r.D -split '\s+')) {
            if ($tk -match '^(\w+)=(\d+)$') {
                $k = $matches[1]; $v = [int]$matches[2]
                if ($k -eq '标注') { continue }        # 这是"一共几个图元"，不是工具名
                if (-not $c.ContainsKey($k)) { $c[$k] = 0 }
                $c[$k] += $v
            }
        }
    }
    if ($c.Count -eq 0) {
        Write-Host "  还没有带标注的截图（全是白板截图）" -ForegroundColor DarkGray
    } else {
        $c.GetEnumerator() | Sort-Object { -$_.Value } | ForEach-Object {
            Write-Host ("  {0,-12} {1,4} 次" -f $_.Key, $_.Value)
        }
    }
    $noAnnot = @($sh | Where-Object { $_.D -match '标注=0(\s|$)' }).Count
    Write-Host ("  其中没加任何标注的：{0} / {1}" -f $noAnnot, $sh.Count)
    Write-Host ""
}

# ---- ⑤ 取消率：高说明这一步有摩擦 ----
$shot = @($rows | Where-Object { $_.Ev -eq 'Shot' }).Count
$cancel = @($rows | Where-Object { $_.Ev -eq 'Shot.Cancel' }).Count
if ($shot + $cancel -gt 0) {
    Write-Host "【截图取消率】" -ForegroundColor Cyan
    Write-Host ("  确认 {0} 次 / 取消 {1} 次  →  取消占 {2:F0}%" -f $shot, $cancel, (100.0 * $cancel / ($shot + $cancel)))
    Write-Host ("  （偏高说明「打开浮层 → 放弃」这一段有摩擦）")
    Write-Host ""
}
