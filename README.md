# Casifier

Casifier is a Jellyfin 10.11.6-compatible plugin that renders movie primary posters into media-case artwork based on the movie resolution:

- DVD for SD/720p movies
- Blu-ray for 1080p movies
- Ultra HD for 2160p and higher movies

It runs both as a Jellyfin scheduled task and after library scans via `ILibraryPostScanTask`.

## How It Works

Casifier reads each movie's current primary poster, stores a one-time backup beside it, and overwrites the poster path Jellyfin already knows about with the generated case image. Future runs regenerate from the backup so the case is not repeatedly wrapped.

Backups are named with `.casifier-original` before the original extension.

## Build

```powershell
$env:DOTNET_CLI_HOME = (Get-Location).Path
dotnet publish .\Casifier.sln -c Release
```

Copy the publish output from `Jellyfin.Plugin.Casifier\bin\Release\net9.0\publish` into a Jellyfin plugin folder, then restart Jellyfin.

## Repository Install Flow

Jellyfin can install third-party plugins from a repository manifest URL:

1. Publish a release ZIP containing the files from `Jellyfin.Plugin.Casifier\bin\Release\net9.0\publish`.
2. Replace the placeholder `sourceUrl` and `checksum` in `repository/manifest.json`.
3. Host `repository/manifest.json`, for example through GitHub Pages or a raw GitHub URL.
4. In Jellyfin, go to Dashboard -> Plugins -> Repositories -> Add, then paste the manifest URL.
5. Go to Catalog, install Casifier, restart Jellyfin, then open the Casifier plugin settings.

The plugin also includes a Jellyfin settings page for enabling/disabling post-scan processing and poster replacement.

You can package a release and update the manifest checksum with:

```powershell
.\tools\Package-Release.ps1 `
  -Version 0.1.5.0 `
  -SourceUrl "https://github.com/yesjapan/Casifier/releases/download/v0.1.5.0/Casifier_0.1.5.0.zip"
```

For the `yesjapan/Casifier` GitHub repo, the Jellyfin repository URL will be:

```text
https://raw.githubusercontent.com/yesjapan/Casifier/main/repository/manifest.json
```

For Jellyfin 10.11.6 specifically, this cache-busting manifest URL is also available:

```text
https://raw.githubusercontent.com/yesjapan/Casifier/main/repository/manifest-10.11.6.json
```

If Jellyfin keeps showing an older cached version, use the versioned manifest URL:

```text
https://raw.githubusercontent.com/yesjapan/Casifier/main/repository/manifest-10.11.6-v0.1.4.0.json
```

If Jellyfin still shows an older version, use the force manifest URL:

```text
https://raw.githubusercontent.com/yesjapan/Casifier/main/repository/casifier-force-0.1.4.json
```

For the latest poster-dimension-preserving overlay build, use:

```text
https://raw.githubusercontent.com/yesjapan/Casifier/main/repository/manifest-10.11.6-v0.1.5.0.json
```

After the repo exists and the release ZIP is attached to a GitHub release, add that URL in Jellyfin under Dashboard -> Plugins -> Repositories.

## GitHub Setup

If the GitHub CLI is not logged in, refresh auth first:

```powershell
gh auth login -h github.com
```

Then create the public GitHub repo, push this project, and publish the first release:

```powershell
gh repo create yesjapan/Casifier --public --source . --push

gh release create v0.1.5.0 `
  .\dist\Casifier_0.1.5.0.zip `
  --repo yesjapan/Casifier `
  --title "Casifier 0.1.5.0" `
  --notes "Overlayed format banners on top of posters without changing poster dimensions."
```

## Notes

This version targets Jellyfin 10.11.6 packages.
