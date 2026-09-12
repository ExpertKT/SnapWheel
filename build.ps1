<#
    SnapWheel 一键构建

    用法（在项目根目录执行）：
        .\build.ps1               编译两条产品线到 build\
        .\build.ps1 -Test         编完顺便跑全部测试
        .\build.ps1 -Package      再打包成分发 zip（含使用说明）
        .\build.ps1 -Clean        先清空 build\ 再编

    两条产品线来自同一份 SnapWheel.cs：
        完整版     普通编译            -> SnapWheel.exe          含万能键
        无万能键版 csc /define:NO_KEY  -> SnapWheel-nokey.exe    不含万能键
#>
param(
    [switch]$Test,
    [switch]$Package,
    [switch]$Clean,
    [string]$OutDir = "build"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'SnapWheel.cs'
$ico  = Join-Path $root 'snapwheel.ico'
$out  = Join-Path $root $OutDir

function Info($s) { Write-Host $s -ForegroundColor Cyan }
function Ok($s)   { Write-Host "  [OK] $s" -ForegroundColor Green }
function Bad($s)  { Write-Host "  [X]  $s" -ForegroundColor Red }

if (-not (Test-Path $src)) { Bad "找不到源码: $src"; exit 1 }
if (-not (Test-Path $ico)) { Bad "找不到图标: $ico"; exit 1 }

# ---- 找 csc.exe（.NET Framework 自带的编译器，无需装 Visual Studio）----
function Get-Csc {
    $cands = @(
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    )
    foreach ($c in $cands) { if (Test-Path $c) { return $c } }
    throw "找不到 csc.exe（需要 .NET Framework 4.x）"
}
$csc = Get-Csc

# ---- 从源码里读两条线各自的版本号 ----
$text = [System.IO.File]::ReadAllText($src, [System.Text.Encoding]::UTF8)
$mNoKey = [regex]::Match($text, '#if NO_KEY\s*\r?\n\s*public const string Version = "([0-9]+\.[0-9]+\.[0-9]+)"')
$mFull  = [regex]::Match($text, '#else\s*\r?\n\s*public const string Version = "([0-9]+\.[0-9]+\.[0-9]+)"')
$vFull  = if ($mFull.Success)  { $mFull.Groups[1].Value }  else { "?" }
$vNoKey = if ($mNoKey.Success) { $mNoKey.Groups[1].Value } else { "?" }

if ($Clean -and (Test-Path $out)) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

Info ""
Info "SnapWheel 构建"
Info "  源码      $src"
Info "  编译器    $csc"
Info "  完整版    v$vFull"
Info "  无万能键版 v$vNoKey"
Info "  输出      $out"
Info ""

# ---- 编译两条线 ----
function Invoke-Build($define, $exeName, $label) {
    $target = Join-Path $out $exeName
    $args = @('/nologo', '/optimize+', '/target:winexe', "/win32icon:$ico", "/out:$target", $src)
    if ($define) { $args = @('/nologo', '/optimize+', "/define:$define", '/target:winexe', "/win32icon:$ico", "/out:$target", $src) }
    $log = & $csc @args 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $target)) {
        Bad "$label 编译失败"
        $log | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
        exit 1
    }
    $warn = ($log | Where-Object { $_ -match ': warning' }).Count
    $size = (Get-Item $target).Length
    Ok ("{0,-14} {1,-24} {2,8:N0} 字节   警告 {3}" -f $label, $exeName, $size, $warn)
}

Info "编译："
Invoke-Build $null    'SnapWheel.exe'       '完整版'
Invoke-Build 'NO_KEY' 'SnapWheel-nokey.exe' '无万能键版'

# ---- 跑测试 ----
if ($Test) {
    Info ""
    Info "测试："
    function Run-Test($name, $file, $main, $define) {
        $exe = Join-Path $out ("_t_" + [System.IO.Path]::GetFileNameWithoutExtension($file) + ".exe")
        $a = @('/nologo', '/target:exe')
        if ($define) { $a += "/define:$define" }
        if ($main)   { $a += "/main:$main" }
        $a += @("/out:$exe")
        if ($main -or $define) { $a += $src }
        $a += (Join-Path $root "tests\$file")
        & $csc @a 2>&1 | Where-Object { $_ -match ': error' } | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
        if (-not (Test-Path $exe)) { Bad "$name 编译失败"; return }
        $o = & $exe 2>&1
        $last = ($o | Where-Object { $_ -match '通过|ALL PASS|FAILURES' } | Select-Object -Last 1)
        $failed = ($o | Where-Object { $_ -match 'FAIL' }).Count
        if ($failed -eq 0) { Ok ("{0,-22} {1}" -f $name, $last) } else { Bad ("{0,-22} {1}（有 {2} 处 FAIL）" -f $name, $last, $failed) }
        Remove-Item $exe -Force -ErrorAction SilentlyContinue
    }
    Run-Test '缩放几何'      'resize-geometry-test.cs' $null $null
    Run-Test '图片格式/导入' 'io-test.cs'               'SnapWheel.IoTest' $null
    Run-Test '绘制/风格/DPI' 'render-smoke.cs'          'SnapWheel.RenderSmoke' $null
    Run-Test '绘制（无万能键）' 'render-smoke.cs'        'SnapWheel.RenderSmoke' 'NO_KEY'
    Write-Host "  （拖放测试会模拟鼠标真的拖拽，需要时手动跑：见 README）" -ForegroundColor DarkGray
}

# ---- 打包 ----
if ($Package) {
    Info ""
    Info "打包："
    $note = Join-Path $root 'dist\使用说明.txt'
    foreach ($pair in @(@{n='SnapWheel'; v=$vFull; t='完整版'; tag='full'}, @{n='SnapWheel-nokey'; v=$vNoKey; t='无万能键版'; tag='nokey'})) {
        $stage = Join-Path $out ("_stage_" + $pair.n)
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
        New-Item -ItemType Directory -Path $stage | Out-Null
        Copy-Item (Join-Path $out ($pair.n + '.exe')) (Join-Path $stage 'SnapWheel.exe') -Force
        if (Test-Path $note) { Copy-Item $note $stage -Force }
        Copy-Item (Join-Path $root 'README.md') $stage -Force
        $zip = Join-Path $out ("SnapWheel-v{0}-{1}.zip" -f $pair.v, $pair.tag)
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
        Remove-Item $stage -Recurse -Force
        Ok ("{0,-14} v{1,-8} {2}" -f $pair.t, $pair.v, [System.IO.Path]::GetFileName($zip))
    }
}

Info ""
Ok "完成，产物在 $out"
Info ""
