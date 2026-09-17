<#
  check-swallowed.ps1 —— 扫描「代码被注释吞掉」这类事故。

  真实事故（不是假想）：
    v0.8.1 给 SnapWheel 加自动更新时，往 Main 里插了一个 --apply-update 分支。
    插入时它被挤进了**同一行前面那段注释**里，于是整行都成了注释。
    后果：
      · 编译通过，0 警告 —— 注释本来就是合法的；
      · 测试全过 —— 当时只测了版本比较和 ZIP 解压，没人测入口；
      · README 里「自动更新」写得漂漂亮亮；
      · 实际上「下载并安装更新」**永远不会生效** —— 点「是」，程序重启，版本号一点没变。
    它在发布版里活了 v0.8.1 到 v0.9.3 好几个版本，直到有人**逐行读代码**才发现。
    事故原文：git show 88560fd -- src/90-App.cs

  为什么需要自动化：这类 bug 编译器、警告、测试、CI 全都看不见 ——
  从它们的角度看，那行就是一段再普通不过的注释。**只有读才能发现**，
  那就把「读」里能机械化的那部分交给机器。

  判据（只留一条，经实测：事故命中、全仓库 0 误报）：
    纯注释行里出现**完整块语句** —— 花括号内部还有分号。
    中文/英文注释不会写出这种形状；而文档注释里贴代码示例（以分号结尾那种）很常见，
    所以「注释以分号结尾」这种判据实测误报 5 处，已弃用。

  用法：powershell -File tools\check-swallowed.ps1
        退出码 0 = 干净，1 = 有命中
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$pattern = '\{[^{}]*;[^{}]*\}'

$targets = @()
foreach ($d in @('src', 'tests')) {
    $p = Join-Path $root $d
    if (Test-Path $p) { $targets += (Get-ChildItem $p -Filter *.cs -File -Recurse) }
}
$tp = Join-Path $root 'tools'
if (Test-Path $tp) { $targets += (Get-ChildItem $tp -Filter *.ps1 -File -Recurse) }

# 脚本自己的说明里必然在描述这个形状，扫自己没有意义
$self = $MyInvocation.MyCommand.Path

$hits = 0
foreach ($f in $targets) {
    if ($f.FullName -eq $self) { continue }
    $rel = $f.FullName.Substring($root.Length + 1)
    $lines = [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $t = $lines[$i].TrimStart()
        if (-not $t.StartsWith('//') -and -not $t.StartsWith('#')) { continue }
        if ($lines[$i] -match $pattern) {
            $hits++
            Write-Host ("  [X] {0}:{1}   注释里藏着完整块语句" -f $rel, ($i + 1)) -ForegroundColor Red
            Write-Host ("      {0}" -f $lines[$i].Trim()) -ForegroundColor DarkGray
        }
    }
}

if ($hits -gt 0) {
    Write-Host ""
    Write-Host "  代码可能被注释吞掉了 $hits 处 —— 编译器、警告、测试都不会告诉你。" -ForegroundColor Red
    Write-Host "  这类事故曾让「自动更新」静默失效好几个版本，请逐行确认。" -ForegroundColor Yellow
    exit 1
}

Write-Host ("  [OK] 注释吞代码检查    {0} 个文件，无命中" -f $targets.Count) -ForegroundColor Green
exit 0
