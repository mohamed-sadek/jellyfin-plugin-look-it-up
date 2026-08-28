using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LookItUp.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Resolves subtitle names via MediaWiki search, page summary, and Wikidata P31.
/// </summary>
public sealed class WikimediaReferenceResolver : IWikimediaReferenceResolver
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex TokenRegex = new(@"[A-Za-z][A-Za-z0-9'\-]{1,}", RegexOptions.Compiled);
    private static readonly HashSet<string> CueStop = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "if", "to", "of", "in", "on", "at", "for", "from",
        "with", "this", "that", "these", "those", "is", "are", "was", "were", "be", "been",
        "have", "has", "had", "do", "does", "did", "you", "your", "we", "they", "he", "she",
        "it", "my", "his", "her", "our", "no", "not", "ever", "owned", "said", "says", "gonna",
        "going", "got", "get", "just", "also", "very", "too", "then", "than", "them", "their",
        "doesn't", "doesnt", "isn't", "aren't", "can't", "won't", "except", "otherwise",
        "he's", "she's", "it's", "that's", "there's", "here's"
    };

    private readonly ILogger<WikimediaReferenceResolver> _logger;
    private readonly ConcurrentDictionary<string, WikimediaReferenceHit> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="WikimediaReferenceResolver"/> class.
    /// </summary>
    public WikimediaReferenceResolver(ILogger<WikimediaReferenceResolver> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WikimediaReferenceHit> ResolveAsync(
        string term,
        string? cueText,
        string language,
        CancellationToken cancellationToken)
    {
        var languageCode = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant();
        var trimmed = (term ?? string.Empty).Trim();
        if (trimmed.Length < 2)
        {
            return new WikimediaReferenceHit { Term = trimmed, Found = false };
        }

        var cacheKey = $"{languageCode}:{trimmed}:{TrimCue(cueText)}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            var extraToken = TakeCueExtra(trimmed, cueText);
            var neighbors = TakeNeighborPhrases(trimmed, cueText);
            var titles = await SearchTitlesAsync(trimmed, cueText, languageCode, cancellationToken)
                .ConfigureAwait(false);
            titles = PreferTitlesMatchingTerm(trimmed, extraToken, neighbors, titles);
            var scored = new List<(WikimediaReferenceHit Hit, int Score)>();
            foreach (var title in titles.Take(6))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var summary = await FetchSummaryAsync(title, languageCode, cancellationToken)
                    .ConfigureAwait(false);
                if (summary is null)
                {
                    continue;
                }

                IReadOnlyList<string> instanceOf = [];
                string? description = summary.Description;
                if (!string.IsNullOrWhiteSpace(summary.WikidataId))
                {
                    var wd = await FetchWikidataAsync(summary.WikidataId, cancellationToken)
                        .ConfigureAwait(false);
                    instanceOf = wd.InstanceOf;
                    if (!string.IsNullOrWhiteSpace(wd.Description))
                    {
                        description = wd.Description;
                    }
                }

                var hit = new WikimediaReferenceHit
                {
                    Term = trimmed,
                    Title = summary.Title,
                    Summary = summary.Extract,
                    Url = summary.Url,
                    ImageUrl = summary.ImageUrl,
                    WikidataId = summary.WikidataId,
                    InstanceOfIds = instanceOf,
                    WikidataDescription = description,
                    Found = true
                };
                var score = ScoreHit(trimmed, hit, cueText);
                scored.Add((hit, score));
                if (neighbors.Count == 0
                    && score >= 80
                    && TitleContainsAllSignificantTokens(summary.Title, trimmed)
                    && (extraToken is null
                        || summary.Title.Contains(extraToken, StringComparison.OrdinalIgnoreCase)
                        || HitMentions(hit, extraToken)))
                {
                    break;
                }
            }

            var matching = scored.Where(s => TitleContainsAllSignificantTokens(s.Hit.Title, trimmed)).ToList();
            if (extraToken is not null)
            {
                var withExtra = matching
                    .Where(s => s.Hit.Title.Contains(extraToken, StringComparison.OrdinalIgnoreCase)
                                || HitMentions(s.Hit, extraToken))
                    .ToList();
                if (withExtra.Count > 0)
                {
                    matching = withExtra;
                }
            }

            if (neighbors.Count > 0)
            {
                var withNeighbor = matching
                    .Where(s => neighbors.Any(n => HitMentions(s.Hit, n)))
                    .ToList();
                if (withNeighbor.Count > 0)
                {
                    matching = withNeighbor;
                }
                else if (scored.Any(s => neighbors.Any(n =>
                             TitleContainsAllSignificantTokens(s.Hit.Title, n)
                             || (s.Hit.Title?.Equals(n, StringComparison.OrdinalIgnoreCase) ?? false))))
                {
                    // Adjacent cue resolved to its own Wikipedia page; do not keep a namesake
                    // that never mentions that work (Joe Buck the sportscaster vs Midnight Cowboy).
                    matching = [];
                }
            }

            var ordered = matching.OrderByDescending(s => s.Score).ToList();
            var best = ordered.FirstOrDefault().Hit;
            if (best is not null)
            {
                if (ordered.Count > 1)
                {
                    best.AlternateTitles = ordered
                        .Skip(1)
                        .Select(s => s.Hit.Title)
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToList();
                    best.Ambiguous = ordered[0].Score - ordered[1].Score < 20;
                }

                if (extraToken is not null
                    && !best.Title.Contains(extraToken, StringComparison.OrdinalIgnoreCase)
                    && !HitMentions(best, extraToken))
                {
                    best.Ambiguous = true;
                }

                _cache[cacheKey] = best;
                return best;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Wikimedia resolve failed for {Term}", trimmed);
        }

        var missed = new WikimediaReferenceHit { Term = trimmed, Found = false };
        _cache[cacheKey] = missed;
        return missed;
    }

    private async Task<List<string>> SearchTitlesAsync(
        string term,
        string? cueText,
        string language,
        CancellationToken cancellationToken)
    {
        var queries = new List<string>();
        var extra = TakeCueExtra(term, cueText);
        var neighbors = TakeNeighborPhrases(term, cueText);
        var wordCount = term.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Length;
        if (extra is not null)
        {
            queries.Add(term + " " + extra);
        }

        foreach (var neighbor in neighbors)
        {
            var q = term + " " + neighbor;
            if (!queries.Contains(q, StringComparer.OrdinalIgnoreCase))
            {
                queries.Add(q);
            }
        }

        if (!queries.Contains(term, StringComparer.OrdinalIgnoreCase))
        {
            queries.Add(term);
        }

        if (wordCount == 1 && extra is null && neighbors.Count == 0)
        {
            queries.Add(term + " automobile");
        }

        var titles = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            foreach (var title in await SearchOnceAsync(query, language, cancellationToken).ConfigureAwait(false))
            {
                if (seen.Add(title))
                {
                    titles.Add(title);
                }
            }

            if (titles.Count >= 8)
            {
                break;
            }
        }

        if (titles.Count == 0)
        {
            titles.Add(term);
        }

        return titles;
    }

    private async Task<List<string>> SearchOnceAsync(
        string query,
        string language,
        CancellationToken cancellationToken)
    {
        var url =
            $"https://{language}.wikipedia.org/w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(query)}" +
            "&srlimit=5&srprop=&format=json";
        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Wikipedia search HTTP {Status} for {Query}", (int)response.StatusCode, query);
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var titles = new List<string>();
        if (doc.RootElement.TryGetProperty("query", out var q)
            && q.TryGetProperty("search", out var search)
            && search.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in search.EnumerateArray())
            {
                if (!item.TryGetProperty("title", out var titleEl))
                {
                    continue;
                }

                var title = titleEl.GetString();
                if (string.IsNullOrWhiteSpace(title)
                    || title.Contains("disambiguation", StringComparison.OrdinalIgnoreCase)
                    || title.StartsWith("List of", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                titles.Add(title);
            }
        }

        return titles;
    }

    private static bool TitleContainsAllSignificantTokens(string title, string term)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var tokens = SignificantTokens(term);
        if (tokens.Count == 0)
        {
            return title.Contains(term, StringComparison.OrdinalIgnoreCase);
        }

        return tokens.All(t => title.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> SignificantTokens(string term)
    {
        var tokens = new List<string>();
        foreach (var raw in term.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.Trim('\'', '’', '"', '.', '!', '?');
            if (token.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
                || token.EndsWith("’s", StringComparison.OrdinalIgnoreCase))
            {
                token = token[..^2];
            }

            if (token.Length < 2 || CueStop.Contains(token))
            {
                continue;
            }

            tokens.Add(token);
        }

        return tokens;
    }

    private static bool HitMentions(WikimediaReferenceHit hit, string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return false;
        }

        var blob = $"{hit.Title}\n{hit.WikidataDescription}\n{hit.Summary}";
        return blob.Contains(phrase, StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreHit(string term, WikimediaReferenceHit hit, string? cueText)
    {
        var score = 0;
        var title = hit.Title ?? string.Empty;
        if (title.Equals(term, StringComparison.OrdinalIgnoreCase))
        {
            score += 60;
        }
        else if (TitleContainsAllSignificantTokens(title, term))
        {
            score += 40;
        }

        var types = hit.InstanceOfIds ?? [];
        if (types.Any(ReferenceGate.DenyInstanceOf.Contains))
        {
            score -= 80;
        }

        if (types.Any(ReferenceGate.AllowInstanceOf.Contains))
        {
            score += 25;
        }

        var blob = $"{hit.WikidataDescription}\n{hit.Summary}";
        if (blob.Contains("motor vehicle", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("automobile", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("car model", StringComparison.OrdinalIgnoreCase))
        {
            score += 35;
        }

        if (!string.IsNullOrWhiteSpace(cueText))
        {
            var extra = TakeCueExtra(term, cueText);
            if (extra is not null
                && (title.Contains(extra, StringComparison.OrdinalIgnoreCase) || HitMentions(hit, extra)))
            {
                score += 20;
            }

            foreach (var neighbor in TakeNeighborPhrases(term, cueText))
            {
                if (HitMentions(hit, neighbor))
                {
                    score += 30;
                }
            }
        }

        return score;
    }

    private static List<string> PreferTitlesMatchingTerm(
        string term,
        string? extra,
        IReadOnlyList<string> neighbors,
        List<string> titles)
    {
        return titles
            .OrderByDescending(t => extra is not null && t.Contains(extra, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(t => neighbors.Any(n =>
                t.Equals(n, StringComparison.OrdinalIgnoreCase)
                || TitleContainsAllSignificantTokens(t, n)))
            .ThenByDescending(t => t.Equals(term, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(t => t.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(t => TitleContainsAllSignificantTokens(t, term))
            .ToList();
    }

    private static string? TakeCueExtra(string term, string? cueText)
    {
        if (string.IsNullOrWhiteSpace(cueText))
        {
            return null;
        }

        var idx = cueText.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        var after = idx < 0 ? string.Empty : cueText[(idx + term.Length)..];
        foreach (Match match in TokenRegex.Matches(after))
        {
            if (CueStop.Contains(match.Value)
                || match.Value.Length < 3
                || match.Value.Equals(term, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return match.Value;
        }

        return null;
    }

    private static List<string> TakeNeighborPhrases(string term, string? cueText)
    {
        if (string.IsNullOrWhiteSpace(cueText))
        {
            return [];
        }

        var phrases = new List<string>();
        foreach (var line in cueText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var run = new List<string>();
            void Flush()
            {
                if (run.Count >= 2 && run.Count <= 6)
                {
                    var phrase = string.Join(' ', run);
                    if (phrase.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || term.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    {
                        run.Clear();
                        return;
                    }

                    if (!phrases.Contains(phrase, StringComparer.OrdinalIgnoreCase))
                    {
                        phrases.Add(phrase);
                    }
                }

                run.Clear();
            }

            foreach (Match match in TokenRegex.Matches(trimmed))
            {
                if (match.Value.Length >= 2
                    && char.IsUpper(match.Value[0])
                    && !CueStop.Contains(match.Value))
                {
                    run.Add(match.Value);
                }
                else
                {
                    Flush();
                }
            }

            Flush();
        }

        return phrases.Take(3).ToList();
    }

    private async Task<SummaryPage?> FetchSummaryAsync(
        string title,
        string language,
        CancellationToken cancellationToken)
    {
        var encoded = Uri.EscapeDataString(title.Replace(' ', '_'));
        var url = $"https://{language}.wikipedia.org/api/rest_v1/page/summary/{encoded}";
        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
        if (!string.Equals(type, "standard", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var pageTitle = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : title;
        if (string.IsNullOrWhiteSpace(pageTitle)
            || pageTitle.Contains("disambiguation", StringComparison.OrdinalIgnoreCase)
            || pageTitle.StartsWith("List of", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var extract = root.TryGetProperty("extract", out var extractEl) ? extractEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(extract)
            || extract.Contains("may refer to", StringComparison.OrdinalIgnoreCase)
            || extract.Contains("can refer to", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? urlPage = null;
        if (root.TryGetProperty("content_urls", out var urls)
            && urls.TryGetProperty("desktop", out var desktop)
            && desktop.TryGetProperty("page", out var pageUrl))
        {
            urlPage = pageUrl.GetString();
        }

        string? image = null;
        if (root.TryGetProperty("thumbnail", out var thumb) && thumb.TryGetProperty("source", out var src))
        {
            image = src.GetString();
        }

        var wikidataId = root.TryGetProperty("wikibase_item", out var wd) ? wd.GetString() : null;
        var description = root.TryGetProperty("description", out var desc) ? desc.GetString() : null;

        return new SummaryPage(pageTitle, extract.Trim(), urlPage, image, wikidataId, description);
    }

    private static async Task<(IReadOnlyList<string> InstanceOf, string? Description)> FetchWikidataAsync(
        string qid,
        CancellationToken cancellationToken)
    {
        var url =
            "https://www.wikidata.org/w/api.php?action=wbgetentities&ids="
            + Uri.EscapeDataString(qid)
            + "&props=claims|descriptions&languages=en&format=json";
        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ([], null);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("entities", out var entities)
            || !entities.TryGetProperty(qid, out var entity))
        {
            return ([], null);
        }

        string? description = null;
        if (entity.TryGetProperty("descriptions", out var descriptions)
            && descriptions.TryGetProperty("en", out var en)
            && en.TryGetProperty("value", out var value))
        {
            description = value.GetString();
        }

        var instanceOf = new List<string>();
        if (entity.TryGetProperty("claims", out var claims)
            && claims.TryGetProperty("P31", out var p31)
            && p31.ValueKind == JsonValueKind.Array)
        {
            foreach (var claim in p31.EnumerateArray())
            {
                if (claim.TryGetProperty("mainsnak", out var snak)
                    && snak.TryGetProperty("datavalue", out var dv)
                    && dv.TryGetProperty("value", out var val)
                    && val.TryGetProperty("id", out var idEl))
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        instanceOf.Add(id);
                    }
                }
            }
        }

        return (instanceOf, description);
    }

    private static string TrimCue(string? cue)
    {
        if (string.IsNullOrWhiteSpace(cue))
        {
            return string.Empty;
        }

        return cue.Length <= 80 ? cue : cue[..80];
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Jellyfin.Plugin.LookItUp", "1.2.57"));
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("(+https://github.com/mohamed-sadek/jellyfin-plugin-look-it-up)"));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        return client;
    }

    private sealed record SummaryPage(
        string Title,
        string Extract,
        string? Url,
        string? ImageUrl,
        string? WikidataId,
        string? Description);
}
