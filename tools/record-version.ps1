<#
    SnapWheel 发布新版本

    它做四件事：
      1) 改源码里的版本号（完整版 / 无万能键版，两条线各一个号）
      2) 编译两条线
      3) 快照到 versions\v<版本>\（exe + 源码 + 图标 + 说明）
      4) 更新 versions\README.md 的历史表

    用法：
        双击 tools\发布新版本.bat        （会问你版本号和说明）
        或命令行：
        .\tools\record-version.ps1 -Version 0.4.7 -NoKeyVersion 0.2.17 -Note "改了什么"

    注意：只做本地归档，不会推到 GitHub。
#>
param(
    [string]$Version = "",
    [string]$NoKeyVersion = "",
    [string]$Note = ""
)

$ErrorActionPreference = 'Stop'

# 脚本在 tools\ 下，项目根目录是它的上一级（也兼容直接放在根目录）
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = $scriptDir
if (-not (Test-Path (Join-Path $root 'src'))) { $root = Split-Path -Parent $scriptDir }

# 0.4.9 起源码在 src\ 下（WheelForm 是 5 个 partial）；版本号写在 src\00-AppInfo.cs
$codeDir  = Join-Path $root 'src'
$appInfo  = Join-Path $codeDir '00-AppInfo.cs'
$sources  = @(Get-ChildItem $codeDir -Filter *.cs -File | Sort-Object Name | ForEach-Object { $_.FullName })
$ico  = Join-Path $root 'snapwheel.ico'
$exe  = Join-Path $root 'SnapWheel.exe'
$vers = Join-Path $root 'versions'

function Info($s) { Write-Host $s -ForegroundColor Cyan }
function Ok($s)   { Write-Host "  [OK] $s" -ForegroundColor Green }
function Bad($s)  { Write-Host "  [X]  $s" -ForegroundColor Red }

if ($sources.Count -eq 0) { Bad "找不到源码: $codeDir"; exit 1 }
if (-not (Test-Path $ico)) { Bad "找不到图标: $ico"; exit 1 }

$csc = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { Bad "找不到 csc.exe（需要 .NET Framework 4.x）"; exit 1 }

# ---- 读源码里两条线当前的版本号 ----
$text = [System.IO.File]::ReadAllText($appInfo, [System.Text.Encoding]::UTF8)
$rxNoKey = [regex]::Match($text, '(#if NO_KEY\s*\r?\n\s*public const string Version = ")([0-9]+\.[0-9]+\.[0-9]+)(")')
$rxFull  = [regex]::Match($text, '(#else\s*\r?\n\s*public const string Version = ")([0-9]+\.[0-9]+\.[0-9]+)(")')
if (-not $rxNoKey.Success -or -not $rxFull.Success) { Bad "源码版本号格式变了，找不到 #if NO_KEY / #else 两行"; exit 1 }

$curFull  = $rxFull.Groups[2].Value
$curNoKey = $rxNoKey.Groups[2].Value

# 没给新版本号就沿用当前（等于只重新归档，不改号）
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $curFull }
if ([string]::IsNullOrWhiteSpace($NoKeyVersion)) {
    $seg = $Version.Split('.')
    $fp = [int]$seg[2]
    $guess = if ([int]$seg[1] -ge 4) { $fp + 10 } else { $fp }   # 0.4.7 -> 0.2.17；0.3.9 -> 0.2.9
    $NoKeyVersion = "0.2.$guess"
}
if ([string]::IsNullOrWhiteSpace($Note)) { $Note = "（未填写说明）" }

Info ""
Info "SnapWheel 发布新版本"
Info "  完整版      $curFull  ->  $Version"
Info "  无万能键版   $curNoKey ->  $NoKeyVersion"
Info "  说明        $Note"
Info ""

# ---- 1) 改源码里的两行版本号 ----
$text = $text.Replace($rxFull.Value,  $rxFull.Groups[1].Value  + $Version      + $rxFull.Groups[3].Value)
$text = $text.Replace($rxNoKey.Value, $rxNoKey.Groups[1].Value + $NoKeyVersion + $rxNoKey.Groups[3].Value)
[System.IO.File]::WriteAllText($appInfo, $text, (New-Object System.Text.UTF8Encoding($false)))
Ok "源码版本号已更新"

# ---- 2) 编译两条线 ----
function Build($outExe, $define, $label) {
    $a = @('/nologo', '/optimize+', '/target:winexe', "/win32icon:$ico", "/out:$outExe") + $sources
    if ($define) { $a = @('/nologo', '/optimize+', "/define:$define", '/target:winexe', "/win32icon:$ico", "/out:$outExe") + $sources }
    $log = & $csc @a 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $outExe)) {
        Bad "$label 编译失败"
        $log | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
        exit 1
    }
    Ok ("{0,-14} {1,10:N0} 字节" -f $label, (Get-Item $outExe).Length)
}

$tmpExe = Join-Path $env:TEMP 'SnapWheel-nokey-build.exe'
Build $exe    $null    '完整版'
Build $tmpExe 'NO_KEY' '无万能键版'

# ---- 3) 快照到 versions\ ----
function Snapshot($ver, $exePath, $label) {
    $out = Join-Path $vers ("v" + $ver)
    if (Test-Path $out) { Ok "$label：覆盖 versions\v$ver" }
    else { New-Item -ItemType Directory -Path $out | Out-Null; Ok "$label：新建 versions\v$ver" }
    # 归档：src\ 原样一份（真正的结构） + 合并成一个单文件 SnapWheel.cs（方便直接编译/看全貌）。
    # 合并时必须把各文件的 using 抽到最前面去重 —— using 不允许出现在 namespace 块之后。
    Copy-Item $codeDir $out -Recurse -Force
    $usings = New-Object System.Collections.Generic.List[string]
    $bodies = New-Object System.Collections.Generic.List[string]
    foreach ($s in $sources) {
        $ln = [System.IO.File]::ReadAllText($s, [System.Text.Encoding]::UTF8) -split "`n"
        $i = 0
        while ($i -lt $ln.Count -and ($ln[$i] -match '^using ' -or $ln[$i].Trim() -eq '')) {
            $u = $ln[$i].TrimEnd()
            if ($ln[$i] -match '^using ' -and -not $usings.Contains($u)) { $usings.Add($u) }
            $i++
        }
        if ($i -lt $ln.Count) { $bodies.Add((($ln[$i..($ln.Count - 1)]) -join "`n")) }
    }
    $merged = ($usings -join "`n") + "`n`n" + ($bodies -join "`n")
    [System.IO.File]::WriteAllText((Join-Path $out 'SnapWheel.cs'), $merged, (New-Object System.Text.UTF8Encoding($false)))
    Copy-Item $exePath (Join-Path $out 'SnapWheel.exe') -Force
    Copy-Item $ico $out -Force
    $today = Get-Date -Format 'yyyy-MM-dd'
    $define = if ($label -eq '无万能键版') { '/define:NO_KEY ' } else { '' }
    $n = @"
# SnapWheel v$ver

**日期**：$today

## 本次更新
$Note

## 文件
- ``SnapWheel.exe`` —— 可执行文件（绿色免安装）
- ``src\`` —— 完整源码（0.4.9 起按类型拆分；WheelForm 是 5 个 partial）
- ``SnapWheel.cs`` —— 同一份源码合并成的单文件（方便直接编译/搜索）
- ``snapwheel.ico`` —— 图标

## 编译
``````
csc /nologo /optimize+ $define`/target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe src\*.cs
``````
"@
    $n = $n.Replace('$define`/target', "$define/target")
    [System.IO.File]::WriteAllText((Join-Path $out 'NOTES.md'), $n, (New-Object System.Text.UTF8Encoding($false)))
}

Snapshot $Version $exe '完整版'
Snapshot $NoKeyVersion $tmpExe '无万能键版'
Remove-Item $tmpExe -Force -ErrorAction SilentlyContinue

# ---- 4) 更新 versions\README.md 的历史表 ----
$rm = Join-Path $vers 'README.md'
if (Test-Path $rm) {
    $r = [System.IO.File]::ReadAllText($rm, [System.Text.Encoding]::UTF8)
    $today = Get-Date -Format 'yyyy-MM-dd'
    $rows = @()
    if ($r -notmatch [regex]::Escape("| v$Version |"))      { $rows += "| v$Version | $today | $Note |" }
    if ($r -notmatch [regex]::Escape("| v$NoKeyVersion |")) { $rows += "| v$NoKeyVersion | $today | **（无万能键线）** $Note |" }
    if ($rows.Count -gt 0) {
        $r = $r.TrimEnd() + "`r`n" + ($rows -join "`r`n") + "`r`n"
        [System.IO.File]::WriteAllText($rm, $r, (New-Object System.Text.UTF8Encoding($false)))
        Ok "版本历史已更新（$($rows.Count) 行）"
    } else { Ok "版本历史里已有这两个版本号，跳过" }
}

Info ""
Ok "完成：v$Version（完整版）/ v$NoKeyVersion（无万能键版）已归档"
Info ""
Info "接下来（推送 / 发 Release 前先跟人确认）：" -ForegroundColor Yellow
Info "  git add -A ; git commit -m `"v$Version`" ; git push" -ForegroundColor DarkGray
Info "  .\tools\build.ps1 -Package      打分发 zip" -ForegroundColor DarkGray
Info ""
