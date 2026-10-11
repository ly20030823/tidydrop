param([ValidateSet('status','install','remove')][string]$Action = 'status')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$packageName = 'TidyDrop.CopyPath'
$publisher = 'CN=TidyDrop Local Development'
$appDirectory = Split-Path $PSScriptRoot -Parent
$marker = 'HKCU:\Software\TidyDrop\CopyPathMenu'
$supported = [Environment]::OSVersion.Version.Build -ge 22000 -and $env:PROCESSOR_ARCHITECTURE -eq 'AMD64'
$available = $supported -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'TidyDrop.Shell.dll')) -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'TidyDrop.CopyPath.msix')) -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'TidyDrop.CopyPath.cer'))
function Get-MenuPackage {
    $packages = @(Get-AppxPackage -Name $packageName -ErrorAction Stop)
    if ($packages | Where-Object { $_.Publisher -ne $publisher }) { throw '发现同名的其他发布者组件，归序未更改它。' }
    return $packages
}
function Get-MenuStatus {
    $packages = if ($supported) { @(Get-MenuPackage) } else { @() }
    $installed = $packages.Count -gt 0
    return @{ supported=$supported; available=$available; installed=$installed; checked=$true; detail=$(if ($installed) { '已添加到 Windows 11 右键菜单' } elseif (-not $supported) { '此功能需要 Windows 11 的 x64 系统' } elseif (-not $available) { '此版本未包含已签名的菜单组件' } else { '文件和文件夹都能用，多选时每行一个路径，不带引号' }) }
}
$guard = $null
$locked = $false
$addedTrust = $false
$certificate = $null
try {
    if ($Action -ne 'status') {
        $guard = [Threading.Mutex]::new($false, 'Local\TidyDrop.CopyPath.Setup')
        try { $locked = $guard.WaitOne(30000) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked) { throw '菜单组件正在更新，请稍后再试。' }
    }
    if ($Action -eq 'install') {
        if (-not $available) { throw '需要 Windows 11 x64 和随程序提供的已签名菜单组件。' }
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $PSScriptRoot 'TidyDrop.CopyPath.cer'))
        if ($certificate.Subject -ne $publisher -or $certificate.HasPrivateKey) { throw '菜单组件的公开签名证书不匹配。' }
        foreach ($file in @('TidyDrop.CopyPath.msix','TidyDrop.Shell.dll')) {
            $signature = Get-AuthenticodeSignature -FilePath (Join-Path $PSScriptRoot $file)
            if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or $signature.Status -in @('HashMismatch','NotSigned','NotSupported')) { throw '菜单组件的签名不匹配，未注册。' }
        }
        $packages = @(Get-MenuPackage)
        $previous = Get-ItemProperty -LiteralPath $marker -ErrorAction SilentlyContinue
        $ownedThumbprints = @($previous.OwnedTrustThumbprints | Where-Object { $_ })
        $trust = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','CurrentUser')
        try {
            $trust.Open('ReadWrite')
            if (-not ($trust.Certificates | Where-Object { $_.Thumbprint -eq $certificate.Thumbprint })) {
                $trust.Add($certificate); $addedTrust = $true
                $ownedThumbprints += $certificate.Thumbprint
            }
        } finally { $trust.Close() }
        New-Item -Path $marker -Force | Out-Null
        New-ItemProperty -LiteralPath $marker -Name OwnedTrustThumbprints -PropertyType MultiString -Value ([string[]]$ownedThumbprints) -Force | Out-Null
        $sameVersion = $packages | Where-Object { $_.Version.ToString() -eq '0.6.0.0' }
        $sameLocation = $previous -and $previous.ExternalLocation -eq $appDirectory
        if (-not ($sameVersion -and $sameLocation)) {
            # Rebinding the same identity version requires unregistering it first.
            if ($sameVersion) { foreach ($package in $sameVersion) { Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop } }
            try { Add-AppxPackage -Path (Join-Path $PSScriptRoot 'TidyDrop.CopyPath.msix') -ExternalLocation $appDirectory -ErrorAction Stop }
            catch {
                if ($sameVersion -and $previous.ExternalLocation) {
                    $oldPackage = Join-Path $previous.ExternalLocation 'Shell/TidyDrop.CopyPath.msix'
                    if (Test-Path -LiteralPath $oldPackage) { Add-AppxPackage -Path $oldPackage -ExternalLocation $previous.ExternalLocation -ErrorAction SilentlyContinue }
                }
                throw
            }
        }
        New-ItemProperty -LiteralPath $marker -Name ExternalLocation -PropertyType String -Value $appDirectory -Force | Out-Null
    }
    elseif ($Action -eq 'remove') {
        foreach ($package in @(Get-MenuPackage)) { Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop }
        $record = Get-ItemProperty -LiteralPath $marker -ErrorAction SilentlyContinue
        $trust = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','CurrentUser')
        try {
            $trust.Open('ReadWrite')
            foreach ($thumbprint in @($record.OwnedTrustThumbprints | Where-Object { $_ })) {
                foreach ($cert in @($trust.Certificates | Where-Object { $_.Thumbprint -eq $thumbprint -and $_.Subject -eq $publisher })) { $trust.Remove($cert) }
            }
        } finally { $trust.Close() }
        if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force }
    }
    @{ ok=$true; status=(Get-MenuStatus) } | ConvertTo-Json -Depth 4 -Compress
}
catch {
    $failure = $_.Exception.Message
    if ($addedTrust -and $certificate) {
        $trust = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','CurrentUser')
        try { $trust.Open('ReadWrite'); $trust.Remove($certificate) } finally { $trust.Close() }
        $record = Get-ItemProperty -LiteralPath $marker -ErrorAction SilentlyContinue
        if ($record) { New-ItemProperty -LiteralPath $marker -Name OwnedTrustThumbprints -PropertyType MultiString -Value ([string[]]@($record.OwnedTrustThumbprints | Where-Object { $_ -ne $certificate.Thumbprint })) -Force | Out-Null }
    }
    @{ ok=$false; error=$failure } | ConvertTo-Json -Compress
    exit 1
}
finally {
    if ($certificate) { $certificate.Dispose() }
    if ($locked) { $guard.ReleaseMutex() }
    if ($guard) { $guard.Dispose() }
}
