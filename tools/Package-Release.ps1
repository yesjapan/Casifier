param(
    [string]$Version = "0.1.10.0",
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
$metaPath = Join-Path $distDir "meta.json"

$env:DOTNET_CLI_HOME = $root
dotnet publish (Join-Path $root "Casifier.sln") -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath
}

$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$metadata = [ordered]@{
    category = "Library"
    changelog = "Detect resolution from media source and file names when stream height metadata is missing."
    description = "Casifier wraps movie primary posters in DVD, Blu-ray, and Ultra HD case artwork based on video resolution. It can run on a schedule and after Jellyfin library scans."
    guid = "6de807d7-041d-4592-a278-91f04a37ec0f"
    imageUrl = ""
    name = "Casifier"
    overview = "Generate media-case posters for Jellyfin movies."
    owner = "Casifier"
    targetAbi = $TargetAbi
    timestamp = $timestamp
    version = $Version
}

$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($metaPath, ($metadata | ConvertTo-Json -Depth 10), $utf8NoBom)

Compress-Archive -Path (Join-Path $publishDir "*"), $metaPath -DestinationPath $zipPath
$checksum = (Get-FileHash -Algorithm MD5 -LiteralPath $zipPath).Hash.ToLowerInvariant()

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$plugin = $manifest[0]
$plugin.versions[0].version = $Version
$plugin.versions[0].changelog = $metadata.changelog
$plugin.versions[0].targetAbi = $TargetAbi
$plugin.versions[0].checksum = $checksum
$plugin.versions[0].timestamp = $timestamp

if ($SourceUrl) {
    $plugin.versions[0].sourceUrl = $SourceUrl
}

$manifestJson = "[`n" + ($plugin | ConvertTo-Json -Depth 10) + "`n]"
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8NoBom)

Write-Host "Created $zipPath"
Write-Host "MD5 $checksum"
Write-Host "Updated $manifestPath"
