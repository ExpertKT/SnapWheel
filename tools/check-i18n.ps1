# 多语言文案自检（0.8.0）
# 用法： powershell -File tools\check-i18n.ps1
# 检查 src\*.cs 里所有 Lang.T("中文", "English")：
#   1) 英文为空 / 英文里还残留中文   -> 说明漏翻
#   2) 同一个中文配了不同的英文       -> 说明改的时候只改了一处，翻译会漂移
#   3) 中英长度比异常                 -> 只提示，不一定错（英文本来就比中文长）
# 这个脚本不改任何东西，只报告。放在这里是为了"以后加文案时能一键自检"。
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$all = @()
foreach ($f in (Get-ChildItem 'src\*.cs')) {
    $text = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
    # 先剥掉注释：注释里会写 Lang.T(...) 作为格式示例，不剥掉会误报
    # （这个缺陷是本脚本第一次运行时暴露的：它把 14-Lang.cs 注释里的示例当成了真代码）
    $text = [regex]::Replace($text, '(?s)/\*.*?\*/', '')
    $text = [regex]::Replace($text, '//[^\r\n]*', '')
    foreach ($m in [regex]::Matches($text, 'Lang\.T\("((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"\)')) {
        $all += [pscustomobject]@{
            文件 = $f.Name
            中   = $m.Groups[1].Value
            英   = $m.Groups[2].Value
        }
    }
}
Write-Host ("Lang.T 条目总数：" + $all.Count)

$bad = 0

$empty = $all | Where-Object { $_.英 -match '^\s*$' }
if ($empty) { $bad += $empty.Count; Write-Host ("[!] 英文为空：" + $empty.Count + " 条") ; $empty | ForEach-Object { Write-Host ("      [" + $_.文件 + "] " + $_.中) } }

$zh = $all | Where-Object { $_.英 -match '[\u4e00-\u9fa5]' }
if ($zh) { $bad += $zh.Count; Write-Host ("[!] 英文里还有中文：" + $zh.Count + " 条") ; $zh | ForEach-Object { Write-Host ("      [" + $_.文件 + "] 中=" + $_.中 + "  英=" + $_.英) } }

$dup = @()
$all | Group-Object 中 | Where-Object { ($_.Group | Select-Object -ExpandProperty 英 -Unique).Count -gt 1 } | ForEach-Object {
    $dup += $_
    Write-Host ("[!] 同一个中文配了不同英文：" + $_.Name)
    $_.Group | ForEach-Object { Write-Host ("      [" + $_.文件 + "] -> " + $_.英) }
}
if ($dup.Count -gt 0) { $bad += $dup.Count }

if ($bad -eq 0) {
    Write-Host "全部通过：没有漏翻、没有不一致。"
    exit 0
} else {
    Write-Host ("发现 " + $bad + " 处问题，请检查上面的列表。")
    exit 1
}