<#
  SnapWheel 按需验收（省钱省时）
  用法：
    .\tools\check.ps1 -Only 设置        # 只跑行为测试里名字含"设置"的几条（最常用）
    .\tools\check.ps1 -Render           # 顺带跑绘制套件（界面/绘制改动才需要）
    .\tools\check.ps1 -Full             # 五套全套（只在发布前跑）
    .\tools\check.ps1 -Only 堆叠 -Render
  约定：日常改动只跑 -Only 相关几条；发布前才 -Full。
#>
param([string]$Only = "", [switch]$Render, [switch]$Full)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$winrt = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll"
$src = @(Get-ChildItem src -Filter *.cs -File | Sort-Object Name | ForEach-Object { $_.FullName })

function Compile($main, $extra, $out, $define) {
  # 参数顺序照 build.ps1 的来（/main 与 /out 在前、源文件在后），否则会 CS1557
  $a = @('/nologo', '/target:exe')
  if ($define) { $a += "/define:$define" }
  if ($main) { $a += "/main:$main" }
  $a += @("/out:$out", "/r:$winrt")
  $a += $src
  $a += $extra
  $log = & $csc @a 2>&1
  $log | Where-Object { $_ -match ': error' } | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
  return ((Test-Path $out) -and -not ($log | Where-Object { $_ -match ': error' }))
}

if ($Full) {
  Write-Host "[全套] 五套测试（发布前才该跑）..." -ForegroundColor Cyan
  & (Join-Path $root 'tools\build.ps1') -Test 2>&1 | Select-Object -Last 10
  exit $LASTEXITCODE
}

Write-Host "[按需] 行为测试$(if ($Only) { "（只跑含「$Only」的几条）" })" -ForegroundColor Cyan
$exe = Join-Path $env:TEMP 'sw_check_behavior.exe'
if (-not (Compile 'SnapWheel.BehaviorTest' @((Join-Path $root 'tests\behavior-test.cs')) $exe $null)) { Write-Host '  编译失败' -ForegroundColor Red; exit 1 }
if ($Only) { $env:SW_TEST_ONLY = $Only } else { Remove-Item Env:\SW_TEST_ONLY -ErrorAction SilentlyContinue }
$out = & $exe 2>&1
$out | Where-Object { $_ -match 'OK|FAIL|跳过|通过' } | Select-Object -Last 25
$failed = ($out | Where-Object { $_ -match 'FAIL' }).Count
if ($failed -eq 0) { Write-Host "  -> 行为测试通过" -ForegroundColor Green } else { Write-Host "  -> 有 $failed 处 FAIL" -ForegroundColor Red }
Remove-Item $exe -Force -ErrorAction SilentlyContinue

if ($Render) {
  Write-Host "[按需] 绘制套件（完整版 + 无万能键版）" -ForegroundColor Cyan
  foreach ($v in @(@{ d = $null; n = '绘制/风格/DPI' }, @{ d = 'NO_KEY'; n = '绘制（无万能键）' })) {
    $e = Join-Path $env:TEMP ('sw_check_render' + $(if ($v.d) { '_nk' } else { '' }) + '.exe')
    if (-not (Compile 'SnapWheel.RenderSmoke' @((Join-Path $root 'tests\render-smoke.cs')) $e $v.d)) { Write-Host "  $($v.n) 编译失败" -ForegroundColor Red; continue }
    $o = & $e 2>&1
    $last = ($o | Where-Object { $_ -match '通过 \d+ / 失败' } | Select-Object -Last 1)
    $f = ($o | Where-Object { $_ -match 'FAIL' }).Count
    if ($f -eq 0) { Write-Host ("  {0,-14} {1}" -f $v.n, $last) -ForegroundColor Green } else { Write-Host ("  {0,-14} {1}（{2} 处 FAIL）" -f $v.n, $last, $f) -ForegroundColor Red }
    Remove-Item $e -Force -ErrorAction SilentlyContinue
  }
}
Write-Host "[完成]" -ForegroundColor Cyan