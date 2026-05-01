param(
    [string]$Version = "0.1.1.0",
    [string]$Configuration = "Release",
    [string]$Framework = "net9.0",
    [string]$TargetAbi = "10.11.6.0",
    [string]$SourceUrl = ""
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishDir = Join-Path $root "Jellyfin.Plugin.Casifier\bin\$Configuration\$Framework\publish"
$distDir = Join-Path $root "dist"
$zipPath = Join-Path $distDir "Casifier_$Version.zip"
$manifestPath = Join-Path $root "repository\manifest.json"

$env:DOTNET_CLI_HOME = $root
dotnet publish (Join-Path $root "Casifier.sln") -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath
$checksum = (Get-FileHash -Algorithm MD5 -LiteralPath $zipPath).Hash.ToLowerInvariant()

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$plugin = $manifest[0]
$plugin.versions[0].version = $Version
$plugin.versions[0].targetAbi = $TargetAbi
$plugin.versions[0].checksum = $checksum
$plugin.versions[0].timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

if ($SourceUrl) {
    $plugin.versions[0].sourceUrl = $SourceUrl
}

$manifestJson = "[`n" + ($plugin | ConvertTo-Json -Depth 10) + "`n]"
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8NoBom)

Write-Host "Created $zipPath"
Write-Host "MD5 $checksum"
Write-Host "Updated $manifestPath"
