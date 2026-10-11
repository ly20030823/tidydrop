param(
    [ValidateSet('status','install','remove','trust-install','trust-remove')][string]$Action = 'status',
    [switch]$TrustConsent,
    [switch]$IfBoundHere
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
# Only this helper process uses built-in Windows PowerShell modules. An installer launched
# from pwsh may inherit a PSModulePath pointing to incompatible PowerShell 7 modules.
$env:PSModulePath = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\Modules'
$packageName = 'TidyDrop.CopyPath'
$publisher = 'CN=TidyDrop Local Development'
$packageVersion = '0.6.1.0'
$appDirectory = Split-Path $PSScriptRoot -Parent
$marker = 'HKCU:\Software\TidyDrop\CopyPathMenu'
$machineMarker = 'HKLM:\SOFTWARE\TidyDrop\CopyPathTrust'
# A 64-bit child can inherit PROCESSOR_ARCHITECTURE=x86 from the installer; inspect the runtime instead.
$supported = [Environment]::OSVersion.Version.Build -ge 22000 -and [Environment]::Is64BitProcess -and [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::X64
$available = $supported -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'TidyDrop.Shell.dll')) -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'TidyDrop.CopyPath.msix')) -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'TidyDrop.CopyPath.cer'))
function Get-MenuPackage {
    $packages = @(Get-AppxPackage -Name $packageName -ErrorAction Stop)
    if ($packages | Where-Object { $_.Publisher -ne $publisher }) { throw '发现同名的其他发布者组件，归序未更改它。' }
    return $packages
}
function Test-MachineTrust($Certificate) {
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','LocalMachine')
    try { $store.Open('ReadOnly'); return @($store.Certificates | Where-Object Thumbprint -eq $Certificate.Thumbprint).Count -gt 0 }
    finally { $store.Close() }
}
function Get-MenuStatus {
    $packages = if ($supported) { @(Get-MenuPackage) } else { @() }
    $installed = $packages.Count -gt 0
    return @{ supported=$supported; available=$available; installed=$installed; checked=$true; detail=$(if ($installed) { '已添加到 Windows 11 右键菜单' } elseif (-not $supported) { '此功能需要 Windows 11 的 x64 系统' } elseif (-not $available) { '此版本未包含已签名的菜单组件' } else { '文件和文件夹都能用，多选时每行一个路径，不带引号' }) }
}
function Read-VerifiedCertificate {
    if (-not $available) { throw '需要 Windows 11 x64 和随程序提供的已签名菜单组件。' }
    $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $PSScriptRoot 'TidyDrop.CopyPath.cer'))
    try {
        if ($cert.Subject -ne $publisher -or $cert.HasPrivateKey -or $cert.NotAfter -lt (Get-Date) -or $cert.NotBefore -gt (Get-Date)) { throw '菜单组件的公开签名证书不匹配或已过期。' }
        $eku = $cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
        if (-not ($eku.EnhancedKeyUsages | Where-Object Value -eq '1.3.6.1.5.5.7.3.3')) { throw '随附证书不是代码签名证书。' }
        foreach ($file in @('TidyDrop.CopyPath.msix','TidyDrop.Shell.dll')) {
            $signature = Get-AuthenticodeSignature -FilePath (Join-Path $PSScriptRoot $file)
            if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $cert.Thumbprint -or $signature.Status -in @('HashMismatch','NotSigned','NotSupported')) { throw '菜单组件的签名不匹配，未注册。' }
        }
        return $cert
    } catch { $cert.Dispose(); throw }
}
function Invoke-TrustHelper([string]$HelperAction) {
    # Change machine trust in the elevated helper; register AppX in the original account.
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $start.Arguments = '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Action ' + $HelperAction + ' -TrustConsent'
    $start.UseShellExecute = $true; $start.Verb = 'runas'; $start.WindowStyle = 'Hidden'
    try {
        $process = [Diagnostics.Process]::Start($start)
        try { $process.WaitForExit(); if ($process.ExitCode -ne 0) { throw '管理员签名信任操作失败，请确认有管理员权限后重试。' } }
        finally { $process.Dispose() }
    } catch {
        if ($_.Exception.NativeErrorCode -eq 1223 -or $_.Exception.InnerException.NativeErrorCode -eq 1223) { throw '已取消管理员确认，签名信任未更改。' }
        throw
    }
}
function Remove-LegacyUserTrust {
    $record = Get-ItemProperty -LiteralPath $marker -ErrorAction SilentlyContinue
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','CurrentUser')
    try {
        $store.Open('ReadWrite')
        foreach ($thumbprint in @($record.OwnedTrustThumbprints | Where-Object { $_ })) {
            foreach ($cert in @($store.Certificates | Where-Object { $_.Thumbprint -eq $thumbprint -and $_.Subject -eq $publisher })) { $store.Remove($cert) }
        }
    } finally { $store.Close() }
}
$guard = $null; $locked = $false; $certificate = $null
$addedMachineTrust = $false; $oldOwned = @()
try {
    if ($Action -in @('install','remove')) {
        $guard = [Threading.Mutex]::new($false, 'Local\TidyDrop.CopyPath.Setup')
    } elseif ($Action -like 'trust-*') {
        if (-not $TrustConsent) { throw '本机证书信任修改需要明确授权。' }
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '此签名信任步骤需要管理员权限。' }
        $guard = [Threading.Mutex]::new($false, 'Global\TidyDrop.CopyPath.Trust')
    }
    if ($guard) {
        try { $locked = $guard.WaitOne(30000) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked) { throw '菜单组件正在更新，请稍后再试。' }
    }
    if ($Action -eq 'trust-install') {
        $certificate = Read-VerifiedCertificate
        if (-not (Test-MachineTrust $certificate)) {
            $record = Get-ItemProperty -LiteralPath $machineMarker -ErrorAction SilentlyContinue
            $oldOwned = @($record.OwnedTrustThumbprints | Where-Object { $_ })
            $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','LocalMachine')
            try { $store.Open('ReadWrite'); $store.Add($certificate); $addedMachineTrust = $true }
            finally { $store.Close() }
            New-Item -Path $machineMarker -Force | Out-Null
            New-ItemProperty -LiteralPath $machineMarker -Name OwnedTrustThumbprints -PropertyType MultiString -Value ([string[]]@($oldOwned + $certificate.Thumbprint | Select-Object -Unique)) -Force | Out-Null
        }
    }
    elseif ($Action -eq 'trust-remove') {
        # Do not break another user's registered menu.
        $inUse = @(Get-AppxPackage -AllUsers -Name $packageName -ErrorAction Stop | Where-Object { $_.Publisher -eq $publisher -and @($_.PackageUserInformation | Where-Object InstallState -eq 'Installed').Count -gt 0 })
        if ($inUse.Count -eq 0) {
            $record = Get-ItemProperty -LiteralPath $machineMarker -ErrorAction SilentlyContinue
            $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','LocalMachine')
            try {
                $store.Open('ReadWrite')
                foreach ($thumbprint in @($record.OwnedTrustThumbprints | Where-Object { $_ })) {
                    foreach ($cert in @($store.Certificates | Where-Object { $_.Thumbprint -eq $thumbprint -and $_.Subject -eq $publisher })) { $store.Remove($cert) }
                }
            } finally { $store.Close() }
            if (Test-Path -LiteralPath $machineMarker) { Remove-Item -LiteralPath $machineMarker -Force }
        }
    }
    elseif ($Action -eq 'install') {
        $certificate = Read-VerifiedCertificate
        $packages = @(Get-MenuPackage)
        if (-not (Test-MachineTrust $certificate)) {
            if (-not $TrustConsent) { throw 'Windows 需要本机「受信任人」证书库中的签名信任。请在归序设置中确认授权后添加菜单。' }
            Invoke-TrustHelper 'trust-install'
            if (-not (Test-MachineTrust $certificate)) { throw '本机签名信任未建立，菜单未注册。' }
        }
        $previous = Get-ItemProperty -LiteralPath $marker -ErrorAction SilentlyContinue
        $sameVersion = @($packages | Where-Object { $_.Version.ToString() -eq $packageVersion })
        $sameLocation = $previous -and $previous.ExternalLocation -eq $appDirectory
        if (-not ($sameVersion.Count -gt 0 -and $sameLocation)) {
            if ($sameVersion.Count -gt 0) { foreach ($package in $sameVersion) { Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop } }
            try { Add-AppxPackage -Path (Join-Path $PSScriptRoot 'TidyDrop.CopyPath.msix') -ExternalLocation $appDirectory -ErrorAction Stop }
            catch {
                if ($sameVersion.Count -gt 0 -and $previous.ExternalLocation) {
                    $oldPackage = Join-Path $previous.ExternalLocation 'Shell/TidyDrop.CopyPath.msix'
                    if (Test-Path -LiteralPath $oldPackage) { Add-AppxPackage -Path $oldPackage -ExternalLocation $previous.ExternalLocation -ErrorAction SilentlyContinue }
                }
                throw
            }
        }
        Remove-LegacyUserTrust
        New-Item -Path $marker -Force | Out-Null
        New-ItemProperty -LiteralPath $marker -Name ExternalLocation -PropertyType String -Value $appDirectory -Force | Out-Null
        New-ItemProperty -LiteralPath $marker -Name OwnedTrustThumbprints -PropertyType MultiString -Value ([string[]]@()) -Force | Out-Null
    }
    elseif ($Action -eq 'remove') {
        $record = Get-ItemProperty -LiteralPath $marker -ErrorAction SilentlyContinue
        if ($IfBoundHere -and (-not $record.ExternalLocation -or $record.ExternalLocation -ne $appDirectory)) {
            @{ ok=$true; status=(Get-MenuStatus) } | ConvertTo-Json -Depth 4 -Compress
            exit 0
        }
        if ($record.ExternalLocation -and $record.ExternalLocation -ne $appDirectory) { throw '菜单已绑定到另一处归序，请从那处程序移除，避免影响正在使用的版本。' }
        foreach ($package in @(Get-MenuPackage)) { Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop }
        Remove-LegacyUserTrust
        $owned = Get-ItemProperty -LiteralPath $machineMarker -ErrorAction SilentlyContinue
        if (@($owned.OwnedTrustThumbprints | Where-Object { $_ }).Count -gt 0) { Invoke-TrustHelper 'trust-remove' }
        if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force }
    }
    if ($Action -like 'trust-*') { @{ ok=$true } | ConvertTo-Json -Compress }
    else { @{ ok=$true; status=(Get-MenuStatus) } | ConvertTo-Json -Depth 4 -Compress }
}
catch {
    $failure = $_.Exception.Message
    if ($addedMachineTrust -and $certificate) {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','LocalMachine')
        try { $store.Open('ReadWrite'); $store.Remove($certificate) } finally { $store.Close() }
        if (Test-Path -LiteralPath $machineMarker) { New-ItemProperty -LiteralPath $machineMarker -Name OwnedTrustThumbprints -PropertyType MultiString -Value ([string[]]$oldOwned) -Force | Out-Null }
    }
    if ($failure -match '800B0109') { $failure = 'Windows 尚未信任归序的本机签名证书。请确认管理员授权后重试。' }
    @{ ok=$false; error=$failure } | ConvertTo-Json -Compress
    exit 1
}
finally {
    if ($certificate) { $certificate.Dispose() }
    if ($locked) { $guard.ReleaseMutex() }
    if ($guard) { $guard.Dispose() }
}
