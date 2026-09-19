<#
    SnapWheel 一键构建（build.ps1）

    用法（双击 tools\一键编译.bat 最省事；命令行见下）：
        .\tools\build.ps1               编译两条产品线到 build\
        .\tools\build.ps1 -Test         编完顺便跑全部测试
        .\tools\build.ps1 -Package      再打包成分发 zip（含使用说明）
        .\tools\build.ps1 -Clean        先清空 build\ 再编

    两条产品线来自同一份源码（src\ 下 19 个 .cs，WheelForm 是 5 个 partial）：
        完整版     普通编译            -> SnapWheel.exe          含万能键
        无万能键版 csc /define:NO_KEY  -> SnapWheel-nokey.exe    不含万能键
#>
param(
    [switch]$Test,
    [switch]$Package,
    [switch]$Deploy,
    [switch]$Clean,
    [switch]$Sign,
    [string]$OutDir = "build"
)

$ErrorActionPreference = 'Stop'
# 脚本在 tools\ 下，项目根目录是它的上一级（也兼容直接放在根目录的情况）
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = $scriptDir
if (-not (Test-Path (Join-Path $root 'src'))) { $root = Split-Path -Parent $scriptDir }
# 0.4.9 起源码拆到 src\ 下的多个文件（WheelForm 是 5 个 partial），不再有单文件 SnapWheel.cs
$codeDir = Join-Path $root 'src'
$sources = @(Get-ChildItem $codeDir -Filter *.cs -File | Sort-Object Name | ForEach-Object { $_.FullName })
$ico  = Join-Path $root 'snapwheel.ico'
$out  = Join-Path $root $OutDir

function Info($s) { Write-Host $s -ForegroundColor Cyan }
function Ok($s)   { Write-Host "  [OK] $s" -ForegroundColor Green }
function Bad($s)  { Write-Host "  [X]  $s" -ForegroundColor Red }

if ($sources.Count -eq 0) { Bad "找不到源码: $codeDir"; exit 1 }

# OCR 走 WinRT，需要 .NET 框架自带的 WinRT 桥接程序集（不是第三方依赖，系统里就有）
$winrtRefs = @()
foreach ($cand in @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\System.Runtime.WindowsRuntime.dll'))) {
    if (Test-Path $cand) { $winrtRefs = @("/r:$cand"); break }
}

if ($winrtRefs.Count -eq 0) { Write-Host "  [i] 没找到 System.Runtime.WindowsRuntime.dll —— OCR 会编译不进去" -ForegroundColor Yellow }
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
$text = ($sources | ForEach-Object { [System.IO.File]::ReadAllText($_, [System.Text.Encoding]::UTF8) }) -join "`n"
$mNoKey = [regex]::Match($text, '#if NO_KEY\s*\r?\n\s*public const string Version = "([0-9]+\.[0-9]+\.[0-9]+)"')
$mFull  = [regex]::Match($text, '#else\s*\r?\n\s*public const string Version = "([0-9]+\.[0-9]+\.[0-9]+)"')
$vFull  = if ($mFull.Success)  { $mFull.Groups[1].Value }  else { "?" }
$vNoKey = if ($mNoKey.Success) { $mNoKey.Groups[1].Value } else { "?" }

if ($Clean -and (Test-Path $out)) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

Info ""
Info "SnapWheel 构建"
Info "  源码      $codeDir （$($sources.Count) 个文件）"
Info "  编译器    $csc"
Info "  完整版    v$vFull"
Info "  无万能键版 v$vNoKey"
Info "  输出      $out"
Info ""

# ---- 编译两条线 ----
function Invoke-Build($define, $exeName, $label) {
    $target = Join-Path $out $exeName
    $args = @('/nologo', '/optimize+', '/target:winexe', "/win32icon:$ico", "/out:$target") + $winrtRefs + $sources
    if ($define) { $args = @('/nologo', '/optimize+', "/define:$define", '/target:winexe', "/win32icon:$ico", "/out:$target") + $winrtRefs + $sources }
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

# ---- 顺便编译一键安装器 ----
$setupSrc = Join-Path $root 'tools\setup\Setup.cs'
$setupExe = Join-Path $out 'SnapWheelSetup.exe'
if (Test-Path $setupSrc) {
    $log = & $csc @('/nologo', '/optimize+', '/target:winexe', "/out:$setupExe", $setupSrc) 2>&1
    if (Test-Path $setupExe) {
        Ok ("{0,-14} {1,-24} {2,8:N0} 字节" -f '一键安装器', 'SnapWheelSetup.exe', (Get-Item $setupExe).Length)
    }
}

# ---- 数字签名 ----
# 注意：Set-AuthenticodeSignature 会联网做证书链/吊销校验，本机常常要等几十秒甚至卡住，
# 所以不放进默认构建。需要签名时加 -Sign，或单独跑 .\tools\签名.ps1
if ($Sign) {
    $signScript = Join-Path $root 'tools\签名.ps1'
    if (Test-Path $signScript) {
        Info "签名："
        foreach ($f in @('SnapWheel.exe', 'SnapWheel-nokey.exe', 'SnapWheelSetup.exe')) {
            $p = Join-Path $out $f
            if (Test-Path $p) {
                & powershell -NoProfile -ExecutionPolicy Bypass -File $signScript -Exe $p *> $null
                $sig = Get-AuthenticodeSignature $p
                Ok ("{0,-14} 已签名（{1}）" -f $f, $sig.Status)
            }
        }
    }
}

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
        if ($main -or $define) { $a += $winrtRefs; $a += $sources }
        $a += (Join-Path $root "tests\$file")
        & $csc @a 2>&1 | Where-Object { $_ -match ': error' } | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
        if (-not (Test-Path $exe)) { Bad "$name 编译失败"; return }
        $o = & $exe 2>&1
        $last = ($o | Where-Object { $_ -match '通过|ALL PASS|FAILURES' } | Select-Object -Last 1)
        $failed = ($o | Where-Object { $_ -match 'FAIL' }).Count
        if ($failed -eq 0) { Ok ("{0,-22} {1}" -f $name, $last) }
        else {
            Bad ("{0,-22} {1}（有 {2} 处 FAIL）" -f $name, $last, $failed)
            # 把挂掉的那几条**原样打出来**。
            # 原来只报"有 N 处 FAIL"不说哪条 —— 一旦是偶发（负载下才抖的那种），
            # 你根本无从下手：重跑一次可能就过了，名字永远看不到。套件必须告诉你"什么挂了"。
            $o | Where-Object { $_ -match 'FAIL' } | Select-Object -First 8 |
                ForEach-Object { Write-Host ("      " + $_.Trim()) -ForegroundColor Red }
        }
        Remove-Item $exe -Force -ErrorAction SilentlyContinue
    }
    Run-Test '缩放几何'      'resize-geometry-test.cs' $null $null
    Run-Test '图片格式/导入' 'io-test.cs'               'SnapWheel.IoTest' $null
    Run-Test '绘制/风格/DPI' 'render-smoke.cs'          'SnapWheel.RenderSmoke' $null
    Run-Test '绘制（无万能键）' 'render-smoke.cs'        'SnapWheel.RenderSmoke' 'NO_KEY'
    Run-Test '行为/持久化'    'behavior-test.cs'         'SnapWheel.BehaviorTest' $null
    # 界面布局 + 引导窗口的【新】标记。这个探针以前只能手动跑，
    # 于是"引导里标错【新】"这类问题一直没人拦得住。
    Run-Test '界面布局/引导'    'ui-probe.cs'              'SnapWheel.UiProbe' $null
    # 大图长按放大的开销（GitHub issue #2）。
    # 判定用**相对比值**：旧写法 vs 新写法要差 3 倍以上 —— 绝对毫秒在负载下会抖，
    # 这个项目已经在"拿墙钟卡死阈值"上栽过一次了（见 render-smoke 里的耗时对称那条）。
    Run-Test '大图放大性能'    'peek-perf.cs'             'SnapWheel.PeekPerf' $null
    # 空态提示 ↔ 计数胶囊的交叉淡入。两条断言：把 _emptyT 拨到 1/0.5/0 三张图必须两两不同
    # （证明是渐变、不是过阈值就切换），以及这段过渡确实花了时间（≥60ms 的下界，负载下安全）。
    Run-Test '空态交叉淡入'    'empty-fade-test.cs'       'SnapWheel.EmptyFadeTest' $null

    # 「代码被注释吞掉」检查 —— 编译器和测试都看不见这类事故，但真出过：
    # v0.8.1 插入的 --apply-update 分支被挤进注释里，让「下载并安装更新」静默失效了好几个版本。
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\check-swallowed.ps1')
    if ($LASTEXITCODE -ne 0) { Bad "有代码被注释吞掉（见上，这类问题测试抓不到）" }

    Write-Host "  （拖放测试会模拟鼠标真的拖拽，需要时手动跑：见 README）" -ForegroundColor DarkGray
}

# ---- 打包 ----
if ($Package) {
    Info ""
    Info "打包："

    # 先把 README 里"从构建结果推出来的数字"同步成实测值（版本徽章 / 体积徽章 / 正文体积）。
    # 以前靠人手改，已经漂过一次（徽章 343 而实际 346），仓库简介里更夸张（还写着 274）。
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\sync-readme-facts.ps1') `
        -Exe (Join-Path $out 'SnapWheel.exe') -Version $vFull

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

# ---- 部署到桌面（会自动核对字节数，防止复制到旧文件）----
if ($Deploy) {
    Info ""
    Info "部署到桌面："
    $desk = [Environment]::GetFolderPath('Desktop')
    $full = Join-Path $out 'SnapWheel.exe'
    $len = (Get-Item $full).Length
    $targets = @( (Join-Path $desk 'SnapWheel 快照轮环.exe') )
    $distDir = Join-Path $desk ('SnapWheel 快照轮环 v' + $vFull)
    if (Test-Path $distDir) { $targets += (Join-Path $distDir 'SnapWheel 快照轮环.exe') }
    # 关键：必须先停掉正在跑的程序，否则桌面上的 exe 被占用，Copy-Item 会失败
    # （PowerShell 默认只报错不中断，会出现"以为复制成功了其实还是旧文件"的坑）
    Get-Process | Where-Object { $_.ProcessName -like '*SnapWheel*' } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 900
    $allOk = $true
    foreach ($tg in $targets) {
        try { Copy-Item $full $tg -Force } catch { Bad ("复制失败：{0}（{1}）" -f (Split-Path $tg -Leaf), $_.Exception.Message); $allOk = $false; continue }
        Start-Sleep -Milliseconds 150
        $l = (Get-Item $tg).Length
        if ($l -ne $len) { Bad ("{0} 大小不一致（{1} != {2}）" -f (Split-Path $tg -Leaf), $l, $len); $allOk = $false }
        else { Ok ("{0}  {1} 字节" -f (Split-Path $tg -Leaf), $l) }
    }
    if ($allOk) {
        Start-Process explorer.exe -ArgumentList ('"' + $targets[0] + '"')
        Ok "已重启（新实例）"
    } else {
        Bad "部署有问题，没有启动（桌面上可能还是旧文件）"
    }
}

Info ""
Ok "完成，产物在 $out"
Info ""
