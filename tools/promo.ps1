# promo.ps1 -- 一键重做全部宣传物料（五套）。
#
# 为什么要脚本：以前这五套是**手敲五条命令**生成的，输入输出路径散在五个 .cs 里，
# 漏跑一套、跑错目录都发生过。而且物料是 **gitignore 的**，出了问题没法从 git 找回来，
# 只能重跑 —— 那就更该有一条固定的路子。
#
# 产物全部落在  宣传物料-1.0\  （仓库根目录，已 gitignore）
#
# 用法：  .\tools\promo.ps1
#         .\tools\promo.ps1 -Only 九宫格     只重做一套（九宫格/中文竖版/英文竖版/竖版GIF/演示GIF）

param(
    [string]$Only = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root '宣传物料-1.0'
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wr   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll'
$tmp  = [System.IO.Path]::GetTempPath()

function Want([string]$name) { return ($Only -eq '') -or ($Only -eq $name) }

Write-Host "宣传物料生成" -ForegroundColor Cyan
Write-Host ("  产物目录  " + $out)
Write-Host ""

# 先把 build\SnapWheel.exe 编出来（promo9 / 竖版都要读它的真实体积写文案）
& (Join-Path $PSScriptRoot 'build.ps1') | Out-Null
if (-not (Test-Path (Join-Path $root 'build\SnapWheel.exe'))) { throw '没有 build\SnapWheel.exe' }

New-Item -ItemType Directory -Force -Path $out | Out-Null

function Compile([string]$file, [string]$main, [string]$exe) {
    $p = Join-Path $tmp $exe
    $src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
    $args = @('/nologo', '/target:exe', "/main:$main", "/out:$p", "/r:$wr") + $src + (Join-Path $root "tests\$file")
    $r = & $csc @args 2>&1
    $err = $r | Select-String ': error'
    if ($err) { throw ("编译 $file 失败：`n" + ($err | Out-String)) }
    return $p
}

# ---- 先出 UI 截图（后面四套都吃它）----
if (Want '九宫格' -or Want '中文竖版' -or Want '英文竖版' -or Want '竖版GIF') {
    Write-Host '  [1/6] UI 截图（中文）' -ForegroundColor Yellow
    $ui = Compile 'ui-shot.cs' 'SnapWheel.UiShot' 'ui.exe'
    $uiDir = Join-Path $tmp 'snapwheel_ui'
    & $ui | Out-Null
    Write-Host '  [2/6] UI 截图（英文）'
    # ⚠️ 参数顺序：**第一个是输出目录、第二个才是语言**。
    # 写成 `& $ui en` 会把语言当成目录名，在仓库根目录建出一个 en\ 来（第一次就是这么错的）。
    & $ui $uiDir 'en' | Out-Null
}

# ---- 1. 朋友圈九宫格 ----
if (Want '九宫格') {
    Write-Host '  [3/6] 朋友圈九宫格' -ForegroundColor Yellow
    $d = Join-Path $out '1-朋友圈九宫格'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $e = Compile 'promo9.cs' 'SnapWheel.Promo9' 'p9.exe'
    & $e $d
}

# ---- 2. 中文竖版 ----
if (Want '中文竖版') {
    Write-Host '  [4/6] 中文竖版' -ForegroundColor Yellow
    $d = Join-Path $out '2-中文竖版'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $e = Compile 'promo-vertical.cs' 'SnapWheel.PromoV' 'pv.exe'
    & $e $d
}

# ---- 3. 英文竖版 ----
if (Want '英文竖版') {
    Write-Host '  [5/6] 英文竖版' -ForegroundColor Yellow
    $d = Join-Path $out '3-英文竖版'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $e = Compile 'promo-vertical-en.cs' 'SnapWheel.PromoVEn' 'pve.exe'
    & $e $d
}

# ---- 4. 竖版 GIF ----
if (Want '竖版GIF') {
    Write-Host '  [6/6] 竖版 GIF' -ForegroundColor Yellow
    $d = Join-Path $out '4-竖版GIF'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $e = Compile 'promo-vertical-gif.cs' 'SnapWheel.PromoVGif' 'pvg.exe'
    & $e
    foreach ($f in @('图1.gif', '图1-分镜.png')) {
        $s = Join-Path (Join-Path $tmp 'snapwheel_vertical') $f
        if (Test-Path $s) { Copy-Item $s $d -Force }
    }
}

# ---- 5. 演示 GIF ----
if (Want '演示GIF') {
    Write-Host '  [6/6] 演示 GIF' -ForegroundColor Yellow
    $d = Join-Path $out '5-演示GIF'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $e = Compile 'demo-gif.cs' 'SnapWheel.DemoGif' 'dg.exe'
    Push-Location $root
    try { & $e } finally { Pop-Location }
    $s = Join-Path $root 'docs\demo.gif'
    if (Test-Path $s) { Copy-Item $s $d -Force }
}

Write-Host ''
Write-Host '产物：' -ForegroundColor Cyan
Get-ChildItem $out -Recurse -File | ForEach-Object {
    '  ' + $_.FullName.Replace($out + '\', '') + '   ' + [int]($_.Length / 1024) + 'KB'
}
