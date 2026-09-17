# readorder.ps1 -- work out a real reading order for src/*.cs
#
# Why: the file-name numbering (00..93) is only an INTENT ("small numbers do not
# depend on big ones"). This script measures the ACTUAL dependency graph and
# topologically sorts it, so the reading order is based on fact, not on the naming
# convention holding true forever.
#
# ASCII-only on purpose: this repo has been burned by PowerShell 5.1 reading
# non-BOM .ps1 files as GBK, which eats quotes and breaks the parse.

$ErrorActionPreference = 'Stop'
$root = 'C:\Users\Maverick\SnapWheel\src'
$files = Get-ChildItem "$root\*.cs" | Sort-Object Name

$typeFile = @{}
$fileLines = @{}
$fileText = @{}
$dupeTypes = @{}

foreach ($f in $files) {
    $raw = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
    $fileLines[$f.Name] = ([System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)).Length
    # strip block then line comments so that mentions inside comments do not create edges
    $s = [regex]::Replace($raw, '/\*.*?\*/', ' ', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $s = [regex]::Replace($s, '//[^\r\n]*', ' ')
    $fileText[$f.Name] = $s
    foreach ($m in [regex]::Matches($s, '(?<![\w.])(class|struct|interface|enum)\s+([A-Za-z_]\w*)')) {
        $n = $m.Groups[2].Value
        if ($typeFile.ContainsKey($n)) {
            if ($typeFile[$n] -ne $f.Name) { $dupeTypes[$n] = $typeFile[$n] + ' + ' + $f.Name }
        } else {
            $typeFile[$n] = $f.Name
        }
    }
}

$names = @($files | ForEach-Object { $_.Name })
$deps = @{}
foreach ($n in $names) {
    $set = New-Object System.Collections.Generic.HashSet[string]
    foreach ($ty in $typeFile.Keys) {
        $owner = $typeFile[$ty]
        if ($owner -eq $n) { continue }
        if ([regex]::IsMatch($fileText[$n], '(?<![\w.])' + [regex]::Escape($ty) + '(?![\w])')) {
            [void]$set.Add($owner)
        }
    }
    $deps[$n] = $set
}

# Kahn's algorithm; among ready nodes take the smallest file name so the
# numbering is respected wherever the graph leaves a free choice.
$remaining = New-Object System.Collections.Generic.HashSet[string]
foreach ($n in $names) { [void]$remaining.Add($n) }
$order = @()
while ($remaining.Count -gt 0) {
    $ready = @($remaining | Where-Object {
        $n = $_
        -not (@($deps[$n]) | Where-Object { $remaining.Contains($_) }).Count
    } | Sort-Object)
    if ($ready.Count -eq 0) {
        Write-Host "CYCLE among:" ($remaining -join ', ')
        break
    }
    foreach ($r in $ready) { $order += $r; [void]$remaining.Remove($r) }
}

Write-Host ""
Write-Host ("Reading order for {0} files, {1} lines total" -f $names.Count, (($fileLines.Values | Measure-Object -Sum).Sum))
Write-Host ""
$i = 0
$cum = 0
foreach ($n in $order) {
    $i++
    $cum += $fileLines[$n]
    $d = @($deps[$n] | Sort-Object)
    $shown = if ($d.Count -le 4) { $d -join ',' } else { ($d[0..2] -join ',') + (',...(' + $d.Count + ')') }
    Write-Host ("{0,3}  {1,-34} {2,5} 行  累计{3,6}  依赖: {4}" -f $i, $n, $fileLines[$n], $cum, $shown)
}

Write-Host ""
Write-Host "Most depended-upon files (read these early / read these carefully):"
$typeFile.GetEnumerator() | Out-Null
$inbound = @{}
foreach ($n in $names) { $inbound[$n] = 0 }
foreach ($n in $names) { foreach ($d in $deps[$n]) { $inbound[$d] = $inbound[$d] + 1 } }
$inbound.GetEnumerator() | Sort-Object { -$_.Value } | Select-Object -First 12 | ForEach-Object {
    Write-Host ("   {0,-34} 被 {1} 个文件依赖" -f $_.Key, $_.Value)
}

Write-Host ""
Write-Host "Duplicate type names across files (same name declared twice):"
if ($dupeTypes.Count -eq 0) { Write-Host "   none" } else {
    $dupeTypes.GetEnumerator() | ForEach-Object { Write-Host ("   {0}: {1}" -f $_.Key, $_.Value) }
}
