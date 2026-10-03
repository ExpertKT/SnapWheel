# verify-manual-en.ps1 —— 验收一份英文用户手册（HY4 交回来的那一份）
#
# 用法：
#   .\verify-manual-en.ps1 -En <英文手册>
#   .\verify-manual-en.ps1 -En <英文手册> -Zh <中文原文> -Draft <机器草稿>
#
# 只判机械上判得了的：围栏、废话前后言、行首编号、快捷键、路径与文件名、反引号里的
# 记号、术语、残留中文、数字。判不了的（句子通不通、有没有悄悄改错意思）会单独列成
# 清单，交给人眼。硬指标全过才打印"通过"。
#
# 为什么要有它：让"改没改错事实"这件事不靠读得多，而靠比对 —— 两份文件对着数。

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$En,
    [string]$Zh    = "C:\Users\Maverick\SnapWheel\dist\使用说明.txt",
    [string]$Draft = "C:\Users\Maverick\SnapWheel\dist\使用说明.en.draft.txt"
)

$ErrorActionPreference = 'Stop'
$hard = New-Object System.Collections.ArrayList
$soft = New-Object System.Collections.ArrayList
function Hard([string]$what, [bool]$ok, [string]$detail) {
    [void]$hard.Add([pscustomobject]@{ 项 = $what; 结果 = $(if ($ok) { 'OK' } else { '不通过' }); 说明 = $detail })
}
function Soft([string]$what, [string]$detail) {
    if ($detail) { [void]$soft.Add([pscustomobject]@{ 项 = $what; 说明 = $detail }) }
}
function ReadText([string]$p) {
    if (-not (Test-Path -LiteralPath $p)) { throw ("找不到文件：" + $p) }
    $t = [System.IO.File]::ReadAllText([System.IO.Path]::GetFullPath($p), [System.Text.Encoding]::UTF8)
    return $t.TrimStart([char]0xFEFF)
}
# 名字里带 \ 的记号统一小写比较，其余原样
function Norm([string]$s) { return $s.Trim().ToLowerInvariant() }
function Tok([string]$pattern, [string]$text) {
    $r = New-Object System.Collections.ArrayList
    foreach ($m in [regex]::Matches($text, $pattern)) { [void]$r.Add((Norm $m.Value)) }
    return @($r)
}
# ⚠️ 名字不能叫 Diff —— PowerShell 里 diff 是 Compare-Object 的内置别名，别名优先于函数
function SetDiff($a, $b) {
    $miss = @($a | Where-Object { $_ -and ($b -notcontains $_) } | Select-Object -Unique)
    $extra = @($b | Where-Object { $_ -and ($a -notcontains $_) } | Select-Object -Unique)
    return @{ miss = $miss; extra = $extra }
}
function DiffText($d, [string]$unit) {
    $out = @()
    if ($d.miss.Count) { $out += ('少 ' + $d.miss.Count + ' 个' + $unit + '：' + (($d.miss | Select-Object -First 12) -join '  ')) }
    if ($d.extra.Count) { $out += ('多 ' + $d.extra.Count + ' 个' + $unit + '：' + (($d.extra | Select-Object -First 12) -join '  ')) }
    return ($out -join ' ｜ ')
}

# ---------- 读三份文件 ----------
$zhText = ReadText $Zh
$enRaw = ReadText $En
$draftText = ReadText $Draft

# 围栏：要求用 ~~~text 包住；三个反引号一律不许出现
$enText = $enRaw
$fenceOpen = $enRaw -match '(?m)^\s*~~~'
$fenceClose = ([regex]::Matches($enRaw, '(?m)^\s*~~~')).Count
$enText = [regex]::Replace($enRaw, '(?m)^\s*~~~[a-zA-Z]*\s*$', '')
Hard '围栏：没有三个反引号（正文不是 Markdown）' ($enRaw -notmatch '```') $(if ($enRaw -match '```') { '正文里出现了 ```' } else { '' })
Hard '围栏：~~~ 成对（开了就关）' ((-not $fenceOpen) -or ($fenceClose -ge 2)) ('找到 ' + $fenceClose + ' 个 ~~~ 行')

$enLines = $enText -split "`r?`n"
$draftLines = $draftText -split "`r?`n"

# ---------- 废话：前后言 / 小结 ----------
$head = ($enLines | Where-Object { $_.Trim() } | Select-Object -First 3) -join "`n"
$tail = ($enLines | Where-Object { $_.Trim() } | Select-Object -Last 3) -join "`n"
$badHead = [regex]::Matches($head, '(?im)^\s*(sure|of course|here(?:''s| is)|below is|note:|translation:|i (?:have|''ve) (?:proofread|revised|polished))')
$badTail = [regex]::Matches($tail, '(?im)(in summary|to summar|i hope|let me know|feel free|as an ai|(?:this|the) (?:manual|translation) (?:is|has been) (?:proofread|revised|complete))')
Hard '废话：开头没有"这是校订版"这类前言' ($badHead.Count -eq 0) $(if ($badHead.Count) { '开头命中：' + ($badHead | ForEach-Object { $_.Value }) -join ' ' } else { '' })
Hard '废话：结尾没有小结/客套' ($badTail.Count -eq 0) $(if ($badTail.Count) { '结尾命中：' + ($badTail | ForEach-Object { $_.Value }) -join ' ' } else { '' })

# ---------- 行首编号序列 ----------
function NumSeq($lines) {
    $r = New-Object System.Collections.ArrayList
    foreach ($l in $lines) {
        $m = [regex]::Match($l, '^\s*(\d+)\s*[.、)）]')
        if ($m.Success) { [void]$r.Add($m.Groups[1].Value) }
    }
    return @($r)
}
$zhSeq = NumSeq $zhText.Split("`n")
$enSeq = NumSeq $enLines
$seqDiff = SetDiff $zhSeq $enSeq
Hard '编号：行首 1. 2. 3. 的顺序与条数跟中文一致' (($seqDiff.miss.Count + $seqDiff.extra.Count) -eq 0) (DiffText $seqDiff '个编号')

# ---------- 事实级不变项：快捷键 / 路径与文件名 / 反引号 / 数字 ----------
$hotkeyPat = '\b(?:Ctrl|Alt|Shift|Win|F\d{1,2})(?:\s*\+\s*(?:Ctrl|Alt|Shift|Win|F\d{1,2}|[A-Za-z0-9]))+\b'
$pathPat = '[A-Za-z]:\\[^\s\r\n，。"''）)]*'
$filePat = '[\w\-.]+\.(?:exe|ini|txt|png|jpg|jpeg|dll|zip|json|log|md|onnx)\b'
$tickPat = '`([^`\r\n]+)`'

foreach ($pair in @(
    @{ n = '快捷键'; p = $hotkeyPat; u = '个按钮' },
    @{ n = '路径'; p = $pathPat; u = '条路径' },
    @{ n = '文件名'; p = $filePat; u = '个文件名' },
    @{ n = '反引号里的记号'; p = $tickPat; u = '个记号' })) {
    $a = Tok $pair.p $zhText
    $b = Tok $pair.p $enText
    $d = SetDiff $a $b
    # 逐项把两边抓到几个也打出来 —— 否则正则一旦没对上，"两边都是空"会静默算通过
    $detail = if ($a.Count -eq 0) { '中文里就没有这类记号，跳过' } else { ('中文 ' + $a.Count + ' 个 / 英文 ' + $b.Count + ' 个；' + (DiffText $d $pair.u)) }
    Hard ($pair.n + '：跟中文一模一样（一个都不许改/删/加）') (($d.miss.Count + $d.extra.Count) -eq 0) $detail
}

# 数字：行首编号不算事实，先剥掉再数
function NumTok($lines) {
    $t = ($lines | ForEach-Object { [regex]::Replace($_, '^\s*\d+\s*[.、)）]', '') }) -join "`n"
    return Tok '\d+(?:\.\d+)?' $t
}
$numDiff = SetDiff (NumTok $zhText.Split("`n")) (NumTok $enLines)
Soft '数字（人工看一眼）' (DiffText $numDiff '个数字')

# ---------- 术语表 ----------
$terms = @(
    @{ zh = '轮盘'; en = 'ring' },
    @{ zh = '环';   en = 'ring' },
    @{ zh = '格子'; en = 'cell' },
    @{ zh = '万能键'; en = 'Universal key' },
    @{ zh = '移进来'; en = 'Move in' },
    @{ zh = '截图'; en = 'Screenshot' },
    @{ zh = '取字'; en = 'OCR' },
    @{ zh = '剪贴板'; en = 'clipboard' },
    @{ zh = '弧上张数'; en = 'Items on the arc' },
    @{ zh = '环半径'; en = 'Ring radius' },
    @{ zh = '长图'; en = 'long shot' }
)
$termMissing = @()
foreach ($t in $terms) {
    if (($zhText -match [regex]::Escape($t.zh)) -and ($enText -notmatch [regex]::Escape($t.en))) { $termMissing += ($t.zh + '→' + $t.en) }
}
Hard '术语：中文用过的词，英文都用对照表里那个说法' ($termMissing.Count -eq 0) $(if ($termMissing.Count) { '缺：' + ($termMissing -join '，') } else { '' })

# ---------- 残留中文 ----------
# 汉字 + CJK 标点（【】「」，。）+ 半角/全角兼容区 + 全角字符：漏一个都算不通过。
# ★ → — … 不在这些区里，是允许的装饰符号。
$cjkRe  = '[\u4e00-\u9fff\u3000-\u303f\ufe30-\ufe4f\uff00-\uffef]'
$okCjk  = '使用说明|项目'
$cjkLines = @()
for ($i = 0; $i -lt $enLines.Count; $i++) {
    $l = $enLines[$i]
    if ($l -notmatch $cjkRe) { continue }
    $toks = [regex]::Matches($l, ($cjkRe + '+'))
    if ($toks | Where-Object { $_.Value -notmatch ('^(' + $okCjk + ')$') }) { $cjkLines += ('第 ' + ($i + 1) + ' 行：' + $l.Trim()) }
}
Hard '残留中文：正文里没有没翻的汉字和中文标点（文件名除外）' ($cjkLines.Count -eq 0) $(if ($cjkLines.Count) { '共 ' + $cjkLines.Count + ' 行，前 6 行 → ' + (($cjkLines | Select-Object -First 6) -join ' ｜ ') } else { '' })

# ---------- 与机器草稿的骨架对照（松一点：只列给人看） ----------
function Skeleton($lines) {
    return @($lines | ForEach-Object {
        if (-not $_.Trim()) { 'blank' } else {
            $m = [regex]::Match($_, '^\s*([^\w\s])')
            if ($m.Success) { 'sym:' + $m.Groups[1].Value } else { 'text' }
        }
    })
}
$sk1 = Skeleton $draftLines
$sk2 = Skeleton $enLines
$mismatch = @()
for ($i = 0; $i -lt [Math]::Min($sk1.Count, $sk2.Count); $i++) { if ($sk1[$i] -ne $sk2[$i]) { $mismatch += ($i + 1) } }
Soft '跟机器草稿的行骨架（空格/符号节奏）不一样的行的前 8 个' $(if ($mismatch.Count) { ($mismatch | Select-Object -First 8) -join '，' } else { '' })
Soft '非空行数（中文 / 草稿 / 交回来）' ('中文 ' + (@($zhText.Split("`n") | Where-Object { $_.Trim() }).Count) + ' / 草稿 ' + (@($draftLines | Where-Object { $_.Trim() }).Count) + ' / 这份 ' + (@($enLines | Where-Object { $_.Trim() }).Count))

# ---------- 报告 ----------
Write-Host ''
Write-Host '=== 英文手册验收 ===' -ForegroundColor Cyan
Write-Host ('  交回来的：{0}' -f [System.IO.Path]::GetFullPath($En))
Write-Host ('  中文原文：{0}   机器草稿：{1}' -f [System.IO.Path]::GetFullPath($Zh), [System.IO.Path]::GetFullPath($Draft))
Write-Host ''
foreach ($h in $hard) {
    $c = if ($h.结果 -eq 'OK') { 'Green' } else { 'Red' }
    Write-Host ('  [{0}] {1}' -f $h.结果, $h.项) -ForegroundColor $c
    if ($h.说明) { Write-Host ('        ' + $h.说明) -ForegroundColor DarkGray }
}
if ($soft.Count) {
    Write-Host ''
    Write-Host '  --- 机器判不了，给人眼 ---' -ForegroundColor Yellow
    foreach ($s in $soft) {
        Write-Host ('  · ' + $s.项) -ForegroundColor DarkYellow
        if ($s.说明) { Write-Host ('      ' + $s.说明) -ForegroundColor DarkGray }
    }
}
$fail = @($hard | Where-Object { $_.结果 -ne 'OK' }).Count
Write-Host ''
if ($fail -eq 0) {
    Write-Host ('验收结论：机械指标全过（{0} 项）——句子好坏仍需读一遍' -f $hard.Count) -ForegroundColor Green
} else {
    Write-Host ('验收结论：不通过 —— {0} 项硬指标没过（上面红字）' -f $fail) -ForegroundColor Red
}
exit $(if ($fail -eq 0) { 0 } else { 1 })
