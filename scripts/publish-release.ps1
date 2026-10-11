param(
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
    [Parameter(Mandatory)][string]$BodyFile,
    [Parameter(Mandatory)][string[]]$Assets,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
Push-Location $workspace
$credentialData = @{}; $headers = @{}
try {
    $body = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $BodyFile).Path)
    $assetFiles = @($Assets | ForEach-Object { Get-Item -LiteralPath $_ -ErrorAction Stop })
    if ($assetFiles.Count -eq 0 -or @($assetFiles | Where-Object { $_.Extension -in @('.pfx','.p12') -or $_.PSIsContainer }).Count -gt 0) { throw 'Release 附件无效或含私钥。' }
    # Retrieve the existing authorized login in memory; never write or print the token.
    $credentialLines = @('protocol=https','host=github.com','') | git -c credential.interactive=never credential fill
    if ($LASTEXITCODE -ne 0) { throw 'GitHub 登录不可用。' }
    foreach ($line in $credentialLines) { $parts = $line.Split('=',2); if ($parts.Count -eq 2) { $credentialData[$parts[0]] = $parts[1] } }
    if (-not $credentialData.password) { throw '没有可用的 GitHub 授权。' }
    $headers = @{ Authorization='Bearer '+$credentialData.password; Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28'; 'User-Agent'='TidyDrop-release' }
    $api = 'https://api.github.com/repos/ly20030823/tidydrop/releases'
    $release = $null
    try { $release = Invoke-RestMethod -Uri "$api/tags/$Tag" -Headers $headers }
    catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
    if ($release -and -not $release.draft) { throw '该版本已经发布，未覆盖现有 Release。' }
    if (-not $release) {
        $commit = (git rev-parse HEAD).Trim()
        $payload = @{ tag_name=$Tag; target_commitish=$commit; name="归序 $Tag"; body=$body; draft=$true; prerelease=$false } | ConvertTo-Json
        $release = Invoke-RestMethod -Uri $api -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($payload))
    }
    foreach ($asset in $assetFiles) {
        $existing = @($release.assets | Where-Object name -eq $asset.Name)
        if ($existing.Count -gt 0) {
            $digest = 'sha256:' + (Get-FileHash -LiteralPath $asset.FullName).Hash.ToLowerInvariant()
            if ($existing.Count -ne 1 -or $existing[0].size -ne $asset.Length -or $existing[0].digest -ne $digest) { throw "草稿已有不同附件：$($asset.Name)，未覆盖。" }
            Write-Host "附件已核对：$($asset.Name)"
            continue
        }
        $uploadUri = 'https://uploads.github.com/repos/ly20030823/tidydrop/releases/' + $release.id + '/assets?name=' + [Uri]::EscapeDataString($asset.Name)
        $uploaded = Invoke-RestMethod -Uri $uploadUri -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile $asset.FullName
        if ($uploaded.size -ne $asset.Length) { throw "附件上传不完整：$($asset.Name)" }
        Write-Host "已上传：$($asset.Name)"
    }
    $payload = @{ body=$body; draft=(-not $Publish); prerelease=$false } | ConvertTo-Json
    $release = Invoke-RestMethod -Uri "$api/$($release.id)" -Method Patch -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($payload))
    $release | Select-Object id,tag_name,draft,html_url,@{n='assets';e={@($_.assets | Select-Object name,size,browser_download_url,digest)}} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath 'artifacts/release-result.json' -Encoding utf8
    Write-Host $(if ($release.draft) { 'Release 草稿准备完成。' } else { "已发布：$($release.html_url)" })
} finally {
    $headers.Clear(); $credentialData.Clear(); $credentialLines = $null
    Pop-Location
}
