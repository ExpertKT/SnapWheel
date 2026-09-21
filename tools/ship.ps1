<#
  SnapWheel 发布收尾（一条命令，替代手拼 5~6 条）
  用法：
    .\tools\ship.ps1                 # 只打包 + 部署到桌面 + 核对（本地行为，随时可用）
    .\tools\ship.ps1 -Push -Tag v0.5.3   # 额外：推 main、打 tag、覆盖 Release 两个附件、拷 zip 到桌面、核对
  注意：-Push 会动远程，按纪律**必须先问过用户**。
#>
param([switch]$Push, [string]$Tag = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root
& (Join-Path $root 'tools\build.ps1') -Package -Deploy 2>&1 | Select-Object -Last 8
$desk = [Environment]::GetFolderPath('Desktop')
# 版本名**不写死**：原来这里钉着 v0.5.3 / v0.2.22，跳过几次版本之后就走的是下面那个 glob 兜底，
# 而兜底挑的是"最近修改的那个 zip" —— 打包顺序一变就可能拿到上一个版本的包。按 tag 推导才靠谱。
$ver = if ($Tag) { $Tag.TrimStart('v') } else { '' }
$full = if ($ver) { Join-Path $root ("build\SnapWheel-v{0}-full.zip" -f $ver) } else { '' }
$nokey = if ($ver) { Join-Path $root ("build\SnapWheel-v{0}-nokey.zip" -f $ver) } else { '' }
if (-not $full -or -not (Test-Path $full)) { $full = (Get-ChildItem "$root\build\SnapWheel-v*-full.zip" | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
if (-not $nokey -or -not (Test-Path $nokey)) { $nokey = (Get-ChildItem "$root\build\SnapWheel-v*-nokey.zip" | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
if (-not $full -or -not (Test-Path $full)) { throw '找不到完整版 zip' }

Write-Host "`n=== 核对 ===" -ForegroundColor Cyan
$dexe = Join-Path $desk 'SnapWheel 快照轮环.exe'
$bexe = Join-Path $root 'build\SnapWheel.exe'
$m1 = (Get-FileHash $dexe -Algorithm MD5).Hash; $m2 = (Get-FileHash $bexe -Algorithm MD5).Hash
Write-Host ("  桌面 exe {0} 字节 / build {1} 字节 / MD5 一致：{2}" -f (Get-Item $dexe).Length, (Get-Item $bexe).Length, ($m1 -eq $m2))

if ($Push) {
  Write-Host "`n=== 推送与发布 ===" -ForegroundColor Cyan
  git push origin main 2>&1 | Select-Object -Last 1
  if ($Tag) { if (git tag -l $Tag) { git tag -f $Tag } else { git tag -a $Tag -m $Tag }; git push -f origin $Tag 2>&1 | Select-Object -Last 1 }
  if ($Tag) { gh release upload $Tag $full $nokey --clobber 2>&1 | Select-Object -Last 1 }
  Copy-Item $full (Join-Path $desk ("SnapWheel-v{0}-完整版.zip" -f $ver)) -Force
  Copy-Item $nokey (Join-Path $desk 'SnapWheel-无万能键版.zip') -Force
  Write-Host "  未推送：$(git rev-list --count origin/main..HEAD)  工作树：$(if (git status --short) { '有改动' } else { '干净' })"
}
Write-Host "[完成]" -ForegroundColor Cyan