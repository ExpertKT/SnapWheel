# record-version.ps1
# SnapWheel 版本快照工具
#   用法：
#     .\record-version.ps1 -Note "本次更新说明"                 # 用源码里现有的版本号快照
#     .\record-version.ps1 -Version 0.1.2 -Note "新增 XX"        # 改版本号 -> 重新编译 -> 快照
#   作用：把当前 SnapWheel.cs / SnapWheel.exe / snapwheel.ico 复制到 versions\v<版本>\，
#         并写入 NOTES.md，同时更新 versions\README.md 的版本历史表。

param(
    [string]$Version = "",
    [string]$Note = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'SnapWheel.cs'
$exe  = Join-Path $root 'SnapWheel.exe'
$ico  = Join-Path $root 'snapwheel.ico'
$vers = Join-Path $root 'versions'

if (-not (Test-Path $src)) { throw "找不到源码：$src" }
if (-not (Test-Path $ico)) { throw "找不到图标：$ico" }

# 1) 读取源码里的版本号
$text = [System.IO.File]::ReadAllText($src, [System.Text.Encoding]::UTF8)
$m = [regex]::Match($text, '(?s)#else\s*\r?\n\s*public const string Version\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"')
if (-not $m.Success) { $m = [regex]::Match($text, 'Version\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"') }
if (-not $m.Success) { throw "源码里找不到 AppInfo.Version" }
$curVer = $m.Groups[1].Value

# 2) 如果要改版本号：改源码 + 重新编译
if ($Version -and $Version -ne $curVer) {
    # 源码里有两行版本号（#if NO_KEY 的 0.2.x / #else 的 0.3.x），只改 #else 那一行
    $rx = '(?s)(#else\s*\r?\n\s*public const string Version = ")[0-9]+\.[0-9]+\.[0-9]+(")'
    if ($text -notmatch $rx) { throw "源码结构变了：找不到 #else 下面的 Version" }
    $text = $text -replace $rx, ('${1}' + $Version + '${2}')
    [System.IO.File]::WriteAllText($src, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "[1/4] 版本号已改为 $Version，正在编译..."
    $csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    & $csc /nologo /optimize+ /target:winexe ("/win32icon:" + $ico) ("/out:" + $exe) $src
    if (-not (Test-Path $exe)) { throw "编译失败" }
    Write-Host "[1/4] 编译完成"
} else {
    $Version = $curVer
    Write-Host "[1/4] 使用现有版本号 $Version"
}

# 3) 快照到 versions\v<版本>
$out = Join-Path $vers ("v" + $Version)
if (Test-Path $out) {
    Write-Host "[2/4] 目录已存在，覆盖：$out"
} else {
    New-Item -ItemType Directory -Path $out | Out-Null
    Write-Host "[2/4] 新建目录：$out"
}
Copy-Item $src $out -Force
Copy-Item $exe $out -Force
Copy-Item $ico $out -Force

# 4) 写 NOTES.md
$today = Get-Date -Format 'yyyy-MM-dd'
if ([string]::IsNullOrWhiteSpace($Note)) { $Note = "（未填写说明）" }
$notes = @"
# SnapWheel v$Version

**日期**：$today

## 本次更新
$Note

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `SnapWheel.cs` —— 完整源码
- `snapwheel.ico` —— 图标

## 编译
``````
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe SnapWheel.cs
``````
"@
[System.IO.File]::WriteAllText((Join-Path $out 'NOTES.md'), $notes, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "[3/4] 说明已写入 NOTES.md"

# 5) 更新版本历史表
$readme = Join-Path $vers 'README.md'
if (Test-Path $readme) {
    $r = [System.IO.File]::ReadAllText($readme, [System.Text.Encoding]::UTF8)
    $row = "| v$Version | $today · $Note |"
    if ($r -notmatch [regex]::Escape("| v$Version |")) {
        $r = $r.TrimEnd() + "`r`n" + $row + "`r`n"
        [System.IO.File]::WriteAllText($readme, $r, (New-Object System.Text.UTF8Encoding($false)))
    }
    Write-Host "[4/4] 版本历史已更新"
}

Write-Host ""
Write-Host "完成！版本 v$Version 已保存到：$out"
Write-Host "里面包含：SnapWheel.exe / SnapWheel.cs / snapwheel.ico / NOTES.md"
