namespace Jellyfin.Plugin.Casifier.Services;

public sealed record CasifierResult(int Processed, int Updated, int Skipped, int Failed);
