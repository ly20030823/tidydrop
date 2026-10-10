param([string]$AppDirectory = 'artifacts/release')
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    $published = (Resolve-Path -LiteralPath $AppDirectory).Path
    $fixture = Join-Path (Get-Location) ('artifacts/desktop-checks/' + [Guid]::NewGuid().ToString('N'))
    $appPath = Join-Path $fixture 'app'
    New-Item -ItemType Directory -Path $appPath -Force | Out-Null
    Get-ChildItem -LiteralPath $published | Where-Object { $_.Name -ne 'data' -and $_.Name -notlike 'smoke*' } | Copy-Item -Destination $appPath -Recurse
    $archivePath = Join-Path $fixture '归档空间'
    $dataPath = Join-Path $appPath 'data'
    New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
    @{ RootFolder = $archivePath; AutoOrganize = $true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataPath 'settings.json') -Encoding utf8
    $sourceFile = Join-Path $fixture '有 空格的材料.txt'
    $sourceFolder = Join-Path $fixture '项目.v1'
    New-Item -ItemType Directory -Path (Join-Path $sourceFolder '空目录') -Force | Out-Null
    [System.IO.File]::WriteAllText($sourceFile, '中文 shortcut check')
    [System.IO.File]::WriteAllText((Join-Path $sourceFolder '说明.md'), 'nested folder check')
    $shell = New-Object -ComObject WScript.Shell
    $linkPath = Join-Path $fixture '归序.lnk'
    $link = $shell.CreateShortcut($linkPath)
    $link.TargetPath = Join-Path $appPath 'TidyDrop.exe'
    $link.WorkingDirectory = $appPath
    $link.Save()
    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null
    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    $process = Start-Process -FilePath $linkPath -ArgumentList ('"{0}" "{1}"' -f $sourceFile, $sourceFolder) -WindowStyle Hidden -PassThru
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $visibleWindow = $false
    while (-not $process.WaitForExit(30)) {
        $process.Refresh()
        if ($process.MainWindowHandle -ne 0) { $visibleWindow = $true }
        if ($timer.Elapsed.TotalSeconds -gt 30) { Stop-Process -Id $process.Id; throw '后台收纳检查超时' }
    }
    if ($visibleWindow -or $process.ExitCode -ne 0) { throw '后台收纳产生了窗口或返回错误' }
    if (Test-Path -LiteralPath (Join-Path $dataPath 'webview')) { throw '后台收纳不应加载界面浏览器' }
    $history = Get-Content -LiteralPath (Join-Path $dataPath 'history.json') -Raw | ConvertFrom-Json
    if ($history.Count -ne 2 -or @($history | Where-Object State -ne '已归档').Count -ne 0) { throw '收纳记录不完整' }
    if ((Test-Path -LiteralPath $sourceFile) -or (Test-Path -LiteralPath $sourceFolder)) { throw '源材料未完成移动' }
    $fileEntry = $history | Where-Object { -not $_.IsDirectory }
    $folderEntry = $history | Where-Object IsDirectory
    if ([System.IO.File]::ReadAllText($fileEntry.Destination) -ne '中文 shortcut check') { throw '文件内容不一致' }
    if ([System.IO.File]::ReadAllText((Join-Path $folderEntry.Destination '说明.md')) -ne 'nested folder check') { throw '文件夹内容不一致' }
    if (-not (Test-Path -LiteralPath (Join-Path $folderEntry.Destination '空目录'))) { throw '空目录未保留' }
    Write-Host 'PASS desktop shortcut with Chinese names and spaces'
    Write-Host 'PASS file and folder archive without opening a window'
    Write-Host 'PASS background process exits without loading WebView2'
    Write-Host ('Test data: ' + $fixture)
} finally { Pop-Location }
