# 给 SnapWheel 加数字签名（自签名版）
#
# 说明：
#  - 自签名证书是免费的，能在本机把"签名 → 校验"整条链路跑通，
#    但没有商业 CA 背书，别人下载后 SmartScreen 仍会提示"未知发布者"。
#  - 以后买到真证书（pfx）时，用 -Pfx 参数直接换即可，其它流程不用动：
#      .\tools\签名.ps1 -Exe build\SnapWheel.exe -Pfx D:\cert.pfx -PfxPass 密码
#
# 用法：
#   .\tools\签名.ps1                      # 给 build\SnapWheel.exe 签名（证书不存在就自动建一张）
#   .\tools\签名.ps1 -Exe 路径            # 指定要签的文件
#   .\tools\签名.ps1 -NewCert             # 强制重新生成自签名证书
#   .\tools\签名.ps1 -InstallTrust        # 顺便把证书装进"受信任的发布者/根"（本机不再提示）

param(
    [string]$Exe = "",
    [switch]$NewCert,
    [switch]$InstallTrust,
    [switch]$Stamp,
    [string]$Pfx = "",
    [string]$PfxPass = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'build\SnapWheel.exe' }
if (-not (Test-Path $Exe)) { Write-Host "[X] 找不到要签名的文件: $Exe" -ForegroundColor Red; exit 1 }

$subject = "CN=SnapWheel 快照轮环 (exper7), O=exper7, C=CN"

function Get-SigningCert {
    if ($Pfx) {
        $sec = ConvertTo-SecureString -String $PfxPass -Force -AsPlainText
        return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($Pfx, $sec)
    }
    $c = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
         Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
    if ($NewCert -or -not $c) {
        Write-Host "  正在生成自签名证书…" -ForegroundColor Yellow
        $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
             -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5) `
             -KeyUsage DigitalSignature -KeyExportPolicy Exportable `
             -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
    }
    return $c
}

Write-Host ""
Write-Host "=== SnapWheel 签名 ===" -ForegroundColor Cyan
$cert = Get-SigningCert
if (-not $cert) { Write-Host "[X] 拿不到签名证书" -ForegroundColor Red; exit 1 }
Write-Host ("  证书: " + $cert.Subject)
Write-Host ("  指纹: " + $cert.Thumbprint)
Write-Host ("  有效期至: " + $cert.NotAfter.ToString('yyyy-MM-dd'))

# 顺便导出 pfx，方便以后在别的机器/CI 上复用同一张证书
$outDir = Join-Path $root 'build'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$pfxOut = Join-Path $outDir 'SnapWheel-selfsigned.pfx'
if (-not $Pfx) {
    try {
        $sec = ConvertTo-SecureString -String 'snapwheel' -Force -AsPlainText
        Export-PfxCertificate -Cert $cert -FilePath $pfxOut -Password $sec -ErrorAction Stop | Out-Null
        Write-Host ("  已导出证书备份: " + $pfxOut + "  (密码 snapwheel)") -ForegroundColor DarkGray
    } catch { Write-Host "  （pfx 导出跳过）" -ForegroundColor DarkGray }
}

Write-Host "  正在签名…" -ForegroundColor Yellow
# 时间戳要联网，自签名阶段用不上（也容易卡住），默认关掉；-Stamp 时再开
if ($Stamp) {
    $r = Set-AuthenticodeSignature -FilePath $Exe -Certificate $cert -HashAlgorithm SHA256 `
         -TimestampServer 'http://timestamp.digicert.com'
} else {
    $r = Set-AuthenticodeSignature -FilePath $Exe -Certificate $cert -HashAlgorithm SHA256
}
Write-Host ("  签名状态: " + $r.Status)

if ($InstallTrust) {
    foreach ($store in 'TrustedPublisher', 'Root') {
        try {
            $s = New-Object System.Security.Cryptography.X509Certificates.X509Store($store, 'CurrentUser')
            $s.Open('ReadWrite'); $s.Add($cert); $s.Close()
            Write-Host ("  [OK] 已装入 " + $store) -ForegroundColor Green
        } catch { Write-Host ("  [!] 装入 " + $store + " 失败: " + $_.Exception.Message) -ForegroundColor Yellow }
    }
    $r2 = Get-AuthenticodeSignature -FilePath $Exe
    Write-Host ("  重新校验: " + $r2.Status + "  " + $r2.StatusMessage)
}

Write-Host ""
Write-Host "提示：自签名能让本机校验通过，但别人下载后 SmartScreen 仍会提示" -ForegroundColor DarkGray
Write-Host "      『未知发布者』—— 那需要买一张受信任 CA 签发的代码签名证书。" -ForegroundColor DarkGray
Write-Host "      拿到 pfx 后用： .\tools\签名.ps1 -Pfx 你的证书.pfx -PfxPass 密码" -ForegroundColor DarkGray
Write-Host ""
