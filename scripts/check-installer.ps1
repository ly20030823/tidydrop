param([string]$Installer = 'artifacts/installer/TidyDrop-Setup-0.9.0-win-x64.exe', [switch]$WithMenu)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
Push-Location $workspace
try {
    $setupPath = (Resolve-Path -LiteralPath $Installer).Path
    $fixture = Join-Path $workspace ('artifacts/installer-checks/' + [Guid]::NewGuid().ToString('N'))
    $appPath = Join-Path $fixture '归序 app'
    New-Item -ItemType Directory -Path (Join-Path $appPath 'data') -Force | Out-Null
    $archive = Join-Path $fixture '归档 材料'
    New-Item -ItemType Directory -Path $archive -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $archive '保留.txt'), 'Installer must preserve material')
    @{ RootFolder=$archive; AutoOrganize=$true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $appPath 'data/settings.json') -Encoding utf8
    '[]' | Set-Content -LiteralPath (Join-Path $appPath 'data/history.json') -Encoding utf8
    $settingsHash = (Get-FileHash -LiteralPath (Join-Path $appPath 'data/settings.json')).Hash
    $historyHash = (Get-FileHash -LiteralPath (Join-Path $appPath 'data/history.json')).Hash
    function Run-Setup([string]$LogName) {
        $taskOption = if ($WithMenu) { '/TASKS=copypath' } else { '/TASKS=' }
        $process = Start-Process -FilePath $setupPath -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS',$taskOption,('/DIR="'+$appPath+'"'),('/LOG="'+(Join-Path $fixture $LogName)+'"')) -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(120000)) { throw '安装检查超时，请检查是否有管理员确认等待处理。' }
        if ($process.ExitCode -ne 0) { throw "安装检查失败：$($process.ExitCode)，日志：$fixture" }
    }
    Run-Setup 'install.log'
    if ($WithMenu) {
        $binding = Get-ItemProperty -LiteralPath 'HKCU:\Software\TidyDrop\CopyPathMenu' -ErrorAction SilentlyContinue
        if ($binding.ExternalLocation -ne $appPath) { throw "安装向导未将菜单绑定到测试目录，请查看 $fixture/install.log" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $appPath 'coreclr.dll'))) { throw '安装结果缺少自带运行环境。' }
    if ((Get-FileHash -LiteralPath (Join-Path $appPath 'data/settings.json')).Hash -ne $settingsHash -or (Get-FileHash -LiteralPath (Join-Path $appPath 'data/history.json')).Hash -ne $historyHash) { throw '安装改动了现有设置或记录。' }
    Write-Host 'PASS install into a Chinese path and preserve existing settings/history'
    $app = Start-Process -FilePath (Join-Path $appPath 'TidyDrop.exe') -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
    if (-not $app.WaitForExit(45000)) { throw '安装后界面检查超时。' }
    if ($app.ExitCode -ne 0) { Get-Content -LiteralPath (Join-Path $appPath 'smoke-error.txt'); throw '安装后界面检查失败。' }
    Write-Host 'PASS installed self-contained app and UI checks'
    Run-Setup 'upgrade.log'
    if ((Get-FileHash -LiteralPath (Join-Path $appPath 'data/settings.json')).Hash -ne $settingsHash -or (Get-FileHash -LiteralPath (Join-Path $appPath 'data/history.json')).Hash -ne $historyHash) { throw '升级改动了现有设置或记录。' }
    Write-Host 'PASS reinstall/upgrade preserves settings/history'
    if ($WithMenu) {
        & ./scripts/check-shell.ps1 -Registered -AppDirectory $appPath
        if ($LASTEXITCODE -ne 0) { throw '安装后的实际菜单检查失败。' }
    }
    $remove = Start-Process -FilePath (Join-Path $appPath 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $fixture 'uninstall.log')+'"')) -WindowStyle Hidden -PassThru
    if (-not $remove.WaitForExit(120000)) { throw '卸载检查超时，请检查管理员确认。' }
    if ($remove.ExitCode -ne 0) { throw "卸载检查失败：$($remove.ExitCode)，日志：$fixture" }
    if (Test-Path -LiteralPath (Join-Path $appPath 'TidyDrop.exe')) { throw '卸载后程序文件仍存在。' }
    if ((Get-FileHash -LiteralPath (Join-Path $appPath 'data/settings.json')).Hash -ne $settingsHash -or (Get-FileHash -LiteralPath (Join-Path $appPath 'data/history.json')).Hash -ne $historyHash -or [IO.File]::ReadAllText((Join-Path $archive '保留.txt')) -ne 'Installer must preserve material') { throw '卸载改动了保留的设置、记录或材料。' }
    Write-Host 'PASS uninstall removes program and preserves data/materials'
    Write-Host "检查完成：$fixture"
} finally { Pop-Location }
