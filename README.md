# Casifier

Casifier is a Jellyfin 10.11.6-compatible plugin that renders movie primary posters into media-case artwork based on the movie resolution:

- DVD for SD/720p movies
- Blu-ray for 1080p movies
- Ultra HD for 2160p and higher movies

It runs both as a Jellyfin scheduled task and after library scans via `ILibraryPostScanTask`.

## How It Works

Casifier reads each movie's current primary poster, stores a one-time backup beside it, and overwrites the poster path Jellyfin already knows about with the generated case image. Future runs regenerate from the backup so the case is not repeatedly wrapped.

Backups are named with `.casifier-original` before the original extension.

## Movie Versions

Dashboard -> Plugins -> **Movie Versions** (also linked from the Casifier settings page) lists every movie you have more than one copy of, including copies on different drives or in different libraries that Jellyfin shows as separate movies. Copies are matched when they:

- are grouped as versions by Jellyfin (`Movie (2000) - DVD.mkv` beside `Movie (2000) - 2160p.mkv`), or
- share a TMDb or IMDb ID, or
- have the same title (ignoring accents, punctuation and a leading "The") and a year no more than one apart, unless their TMDb/IMDb IDs disagree. Title-only matches are flagged so you can confirm they are the same film.

Copies on drives that are offline are skipped and counted at the top of the page.

For each movie, versions are ranked by resolution (cropped encodes such as 1920x800 count as 1080p), then HDR, bitrate and size. The highest one is always kept. For every version the page shows:

- The DVD / Blu-ray / Ultra HD badge, codec, HDR format, size, runtime and container
- Audio and subtitle languages, with anything **no other version has** highlighted (an extra Japanese dub or Korean subtitles on the DVD, a commentary track)
- Cautions it can't settle automatically: a runtime that doesn't match the other versions (possible different cut; PAL 4% speed-up is accounted for) and tracks with no language tag

Lower versions can be deleted from the page after a confirmation that lists what would be lost and exactly what will be removed:

- A copy that is the only video in its own movie folder inside a library (the usual case for a DVD on another drive) has that **folder** deleted, taking its artwork, NFO and subtitles with it.
- Anything else (a folder holding other movies or extras, a flat folder of movies, a version Jellyfin grouped with others) has only its **video file** and the external subtitle/audio files Jellyfin attributes to it deleted.

Casifier does not use Jellyfin's item delete for files, because for a movie stored in its own folder that removes the whole folder, including any other versions in it. Jellyfin is then told about the change so it drops the copy from the library. The Jellyfin server account needs write access to the media folders.

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
  -Version 0.1.14.0 `
  -SourceUrl "https://github.com/yesjapan/Casifier/releases/download/v0.1.14.0/Casifier_0.1.14.0.zip"
```

Use this repository URL in Jellyfin. It always lists the latest release, so new versions show up as updates:

```text
https://raw.githubusercontent.com/yesjapan/Casifier/main/repository/manifest.json
```

The `manifest-10.11.6-v*.json` files in `repository/` are older pinned manifests. Each one only ever lists a single version, so a Jellyfin server pointed at one of them never sees newer releases.

After the release ZIP is attached to a GitHub release, add the URL in Jellyfin under Dashboard -> Plugins -> Repositories.

## GitHub Setup

If the GitHub CLI is not logged in, refresh auth first:

```powershell
gh auth login -h github.com
```

Then create the public GitHub repo, push this project, and publish the first release:

```powershell
gh repo create yesjapan/Casifier --public --source . --push

gh release create v0.1.14.0 `
  .\dist\Casifier_0.1.14.0.zip `
  --repo yesjapan/Casifier `
  --title "Casifier 0.1.14.0" `
  --notes "Add a Movie Versions page for finding and removing duplicate copies of movies."
```

## Notes

This version targets Jellyfin 10.11.6 packages.
