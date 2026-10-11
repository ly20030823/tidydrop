param(
    [string]$AppDirectory = 'artifacts/installer-app',
    [string]$InnoCompiler = 'artifacts/tools/inno/ISCC.exe',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
Push-Location $workspace
try {
    if (-not $SkipBuild) { & ./scripts/build.ps1 -SelfContained -OutputDirectory $AppDirectory }
    $source = (Resolve-Path -LiteralPath $AppDirectory).Path
    $runtime = Get-Content -LiteralPath (Join-Path $source 'TidyDrop.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (-not $runtime.runtimeOptions.includedFrameworks -or -not (Test-Path -LiteralPath (Join-Path $source 'coreclr.dll'))) { throw '安装包必须使用自带运行环境的 win-x64 发布目录。' }
    if (@(Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { $_.Extension -in @('.pfx','.p12') -or $_.FullName -match '[\\/]data[\\/]|[\\/]smoke[^\\/]*' }).Count -gt 0) { throw '发布目录含私人数据、检查产物或私钥，停止打包。' }
    & ./scripts/sign-shell.ps1 -Directory $AppDirectory -Files @('TidyDrop.exe') -SkipExport
    $bootstrapper = Join-Path $workspace 'artifacts/tools/MicrosoftEdgeWebview2Setup.exe'
    if (-not (Test-Path -LiteralPath $bootstrapper)) {
        New-Item -ItemType Directory -Path (Split-Path $bootstrapper -Parent) -Force | Out-Null
        Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper
    }
    $signature = Get-AuthenticodeSignature -FilePath $bootstrapper
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw '微软 WebView2 安装器签名验证失败。' }
    $compiler = if (Test-Path -LiteralPath $InnoCompiler) { (Resolve-Path -LiteralPath $InnoCompiler).Path } else { (Get-Command ISCC.exe -ErrorAction Stop).Source }
    [xml]$project = Get-Content -LiteralPath 'src/TidyDrop.App/TidyDrop.App.csproj'
    $version = [string]$project.Project.PropertyGroup.Version
    & $compiler "/DAppVersion=$version" "/DAppSource=$source" "/DBootstrapper=$bootstrapper" 'installer/TidyDrop.iss'
    if ($LASTEXITCODE -ne 0) { throw '归序安装程序编译失败。' }
    $installer = "artifacts/installer/TidyDrop-Setup-$version-win-x64.exe"
    & ./scripts/sign-shell.ps1 -Directory 'artifacts/installer' -Files @("TidyDrop-Setup-$version-win-x64.exe") -SkipExport
    Get-FileHash -LiteralPath $installer -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $(Split-Path $installer -Leaf)" } | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
    Write-Host "安装程序完成：$installer"
} finally { Pop-Location }
