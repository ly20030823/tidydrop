param([switch]$Registered, [string]$AppDirectory = 'artifacts/app')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$output = Join-Path $workspace 'artifacts/shell-checks'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw '需要 Visual Studio C++ 桌面开发工具。' }
& (Join-Path $vs 'MSBuild/Current/Bin/MSBuild.exe') (Join-Path $workspace 'tests/TidyDrop.ShellChecks/TidyDrop.ShellChecks.vcxproj') /nologo /v:minimal /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$output\" "/p:IntDir=$output\obj\"
if ($LASTEXITCODE -ne 0) { throw '菜单检查程序构建失败。' }
$dll = if ($Registered) { Join-Path (Join-Path $workspace $AppDirectory) 'Shell/TidyDrop.Shell.dll' } else { Join-Path $workspace 'artifacts/shell/TidyDrop.Shell.dll' }
$fixture = Join-Path $output ('fixtures/' + [Guid]::NewGuid().ToString('N'))
$arguments = @($dll, $fixture)
if ($Registered) { $arguments += '--registered' }
& (Join-Path $output 'TidyDrop.ShellChecks.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw '菜单复制与剪贴板检查失败。' }
