param([string]$OutputDirectory = 'artifacts/shell')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $workspace $OutputDirectory))
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw '需要 Visual Studio 2022 的 C++ 桌面开发工具。' }
$sdkRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$makeAppx = Get-ChildItem -Path (Join-Path $sdkRoot 'bin/*/x64/makeappx.exe') | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeAppx) { throw '需要 Windows 11 SDK 的 MakeAppx 工具。' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
& (Join-Path $vs 'MSBuild/Current/Bin/MSBuild.exe') (Join-Path $workspace 'src/TidyDrop.Shell/TidyDrop.Shell.vcxproj') /nologo /v:minimal /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$output\" "/p:IntDir=$output\obj\"
if ($LASTEXITCODE -ne 0) { throw '右键菜单组件构建失败。' }
$packageInput = Join-Path $output 'package-input'
New-Item -ItemType Directory -Path $packageInput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $workspace 'src/TidyDrop.Shell/AppxManifest.xml') -Destination $packageInput -Force
$packOutput = & $makeAppx.FullName pack /o /nv /d $packageInput /p (Join-Path $output 'TidyDrop.CopyPath.msix') 2>&1
if ($LASTEXITCODE -ne 0) { $packOutput | Write-Output; throw '右键菜单身份包构建失败。' }
$assets = Join-Path $output 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$icon = [Drawing.Icon]::new((Join-Path $workspace 'src/TidyDrop.App/Assets/TidyDrop.ico'), 256, 256)
try { $bitmap = $icon.ToBitmap(); try { $bitmap.Save((Join-Path $assets 'Logo.png'), [Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() } } finally { $icon.Dispose() }
Copy-Item -LiteralPath (Join-Path $workspace 'scripts/context-menu.ps1') -Destination (Join-Path $output 'context-menu.ps1') -Force
Write-Host '右键菜单组件与身份包构建完成。'
