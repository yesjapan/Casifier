using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Casifier.Services;

public sealed record MatchCandidate(string? TmdbId, string? ImdbId, IReadOnlyList<string?> Titles, int? Year, bool HasAlternateVersions);

public sealed record MatchCluster(IReadOnlyList<int> Members, IReadOnlyList<string> MatchedBy)
{
    public bool TitleMatchOnly => MatchedBy.Contains(MovieMatcher.TitleReason)
        && !MatchedBy.Any(reason => reason is MovieMatcher.TmdbReason or MovieMatcher.ImdbReason);
}

/// <summary>
/// Finds separate Jellyfin movie items that are the same film, e.g. a DVD rip on one drive and a
/// Blu-ray rip on another. Items are linked by shared TMDb/IMDb ids, and otherwise by normalized
/// title with a year no more than one apart, unless their ids say they're different films.
/// </summary>
public static class MovieMatcher
{
    public const string GroupedReason = "Grouped in Jellyfin";
    public const string TmdbReason = "TMDb ID";
    public const string ImdbReason = "IMDb ID";
    public const string TitleReason = "Title + year";

    private static readonly string[] LeadingArticles = ["the ", "a ", "an "];

    public static List<MatchCluster> Cluster(IReadOnlyList<MatchCandidate> candidates)
    {
        var parent = Enumerable.Range(0, candidates.Count).ToArray();
        var edges = new List<(int A, int B, string Reason)>();

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        void Link(int a, int b, string reason)
        {
            var rootA = Find(a);
            var rootB = Find(b);
            if (rootA != rootB)
            {
                parent[rootB] = rootA;
                edges.Add((a, b, reason));
            }
        }

        LinkByKey(candidates, c => c.TmdbId, TmdbReason, Link);
        LinkByKey(candidates, c => c.ImdbId, ImdbReason, Link);

        var byTitle = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Year is null)
            {
                continue; // too easy to confuse remakes without a year
            }

            foreach (var title in candidates[i].Titles.Select(NormalizeTitle).Where(t => t.Length > 0).Distinct())
            {
                if (!byTitle.TryGetValue(title, out var list))
                {
                    byTitle[title] = list = [];
                }

                list.Add(i);
            }
        }

        foreach (var list in byTitle.Values.Where(list => list.Count > 1))
        {
            for (var x = 0; x < list.Count; x++)
            {
                for (var y = x + 1; y < list.Count; y++)
                {
                    var a = candidates[list[x]];
                    var b = candidates[list[y]];
                    if (Math.Abs(a.Year!.Value - b.Year!.Value) <= 1 && !IdsConflict(a, b))
                    {
                        Link(list[x], list[y], TitleReason);
                    }
                }
            }
        }

        var clusters = new List<MatchCluster>();
        foreach (var members in Enumerable.Range(0, candidates.Count).GroupBy(Find))
        {
            var memberList = members.ToList();
            if (memberList.Count == 1 && !candidates[memberList[0]].HasAlternateVersions)
            {
                continue;
            }

            var reasons = new List<string>();
            if (memberList.Any(i => candidates[i].HasAlternateVersions))
            {
                reasons.Add(GroupedReason);
            }

            var memberSet = memberList.ToHashSet();
            reasons.AddRange(edges.Where(e => memberSet.Contains(e.A)).Select(e => e.Reason).Distinct());
            clusters.Add(new MatchCluster(memberList, reasons));
        }

        return clusters;
    }

    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(title.Length);
        foreach (var ch in title.Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        var normalized = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var article in LeadingArticles)
        {
            if (normalized.StartsWith(article, StringComparison.Ordinal) && normalized.Length > article.Length)
            {
                normalized = normalized[article.Length..];
                break;
            }
        }

        return normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static void LinkByKey(IReadOnlyList<MatchCandidate> candidates, Func<MatchCandidate, string?> key, string reason, Action<int, int, string> link)
    {
        var first = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < candidates.Count; i++)
        {
            var value = key(candidates[i])?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (first.TryGetValue(value, out var existing))
            {
                link(existing, i, reason);
            }
            else
            {
                first[value] = i;
            }
        }
    }

    private static bool IdsConflict(MatchCandidate a, MatchCandidate b)
        => Differs(a.TmdbId, b.TmdbId) || Differs(a.ImdbId, b.ImdbId);

    private static bool Differs(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
            && !string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
