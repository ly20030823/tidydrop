param([string]$Directory = 'artifacts/shell')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $workspace $Directory))
$subject = 'CN=TidyDrop Local Development'
$certificate = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddMonths(1) } | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $certificate) {
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(3)
}
$sdkRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$signTool = Get-ChildItem -Path (Join-Path $sdkRoot 'bin/*/x64/signtool.exe') | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signTool) { throw '需要 Windows SDK 的 SignTool。' }
foreach ($file in @('TidyDrop.Shell.dll','TidyDrop.CopyPath.msix')) {
    & $signTool.FullName sign /fd SHA256 /s My /sha1 $certificate.Thumbprint (Join-Path $output $file) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '菜单组件本地签名失败。' }
}
Export-Certificate -Cert $certificate -FilePath (Join-Path $output 'TidyDrop.CopyPath.cer') -Force | Out-Null
Write-Host '本地签名完成。仅导出公开证书，私钥保留在当前用户证书库，不能导出。'
