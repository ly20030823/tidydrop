param([switch]$SelfContained, [string]$OutputDirectory = 'artifacts/release', [string]$RuntimeVersion = '9.0.20')
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & ./scripts/build-shell.ps1
    & ./scripts/sign-shell.ps1
    & ./scripts/check-shell.ps1
    Push-Location frontend
    try {
        pnpm install --frozen-lockfile
        if ($LASTEXITCODE -ne 0) { throw '安装前端依赖失败' }
        pnpm build
        if ($LASTEXITCODE -ne 0) { throw '前端构建失败' }
    } finally { Pop-Location }
    dotnet run --project tests/TidyDrop.Checks/TidyDrop.Checks.csproj -c Release -- artifacts/checks
    if ($LASTEXITCODE -ne 0) { throw '核心检查失败' }
    $publishArguments = @('publish','src/TidyDrop.App/TidyDrop.App.csproj','-c','Release','-o',$OutputDirectory)
    if ($SelfContained) { $publishArguments += @('-r','win-x64','--self-contained','true',"-p:RuntimeFrameworkVersion=$RuntimeVersion") }
    else { $publishArguments += '--no-self-contained' }
    dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw '程序发布失败' }
    $licensePath = Join-Path (Get-Location) "$OutputDirectory/LICENSES"
    New-Item -ItemType Directory -Path $licensePath -Force | Out-Null
    Copy-Item -LiteralPath 'frontend/node_modules/react/LICENSE' -Destination (Join-Path $licensePath 'React.txt')
    Copy-Item -LiteralPath 'frontend/node_modules/react-dom/LICENSE' -Destination (Join-Path $licensePath 'ReactDOM.txt')
    Copy-Item -LiteralPath 'frontend/node_modules/lucide-react/LICENSE' -Destination (Join-Path $licensePath 'Lucide.txt')
    if ($SelfContained) {
        $packageCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
        $corePackage = Join-Path $packageCache "microsoft.netcore.app.runtime.win-x64/$RuntimeVersion"
        $desktopPackage = Join-Path $packageCache "microsoft.windowsdesktop.app.runtime.win-x64/$RuntimeVersion"
        Copy-Item -LiteralPath (Join-Path $corePackage 'LICENSE.TXT') -Destination (Join-Path $licensePath 'DotNET-Core.txt')
        Copy-Item -LiteralPath (Join-Path $corePackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licensePath 'DotNET-Notices.txt')
        Copy-Item -LiteralPath (Join-Path $desktopPackage 'LICENSE') -Destination (Join-Path $licensePath 'DotNET-Desktop.txt')
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sdkZip = [System.IO.Compression.ZipFile]::OpenRead((Join-Path (Get-Location) 'vendor/nuget/Microsoft.Web.WebView2.1.0.4258.31.nupkg'))
    try {
        foreach ($entry in $sdkZip.Entries | Where-Object { $_.Name -match '^(LICENSE|NOTICE)' }) {
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $licensePath ('WebView2-' + $entry.Name)), $true)
        }
    } finally { $sdkZip.Dispose() }
    Copy-Item -LiteralPath README.md -Destination (Join-Path $OutputDirectory '使用说明.md')
    $documentationPath = Join-Path (Get-Location) "$OutputDirectory/docs"
    New-Item -ItemType Directory -Path $documentationPath -Force | Out-Null
    Get-ChildItem -LiteralPath docs | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $documentationPath -Recurse -Force }
    Write-Host "构建完成：$OutputDirectory/TidyDrop.exe"
} finally { Pop-Location }
