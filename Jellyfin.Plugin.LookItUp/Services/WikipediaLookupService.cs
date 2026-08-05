using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.LookItUp.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Looks up short explanations for entity names.
/// </summary>
public interface IWikipediaLookupService
{
    /// <summary>
    /// Looks up a term on Wikipedia.
    /// </summary>
    Task<EntityLookupResult> LookupAsync(string term, string language, CancellationToken cancellationToken);
}

/// <summary>
/// Uses the Wikipedia REST summary API for short explanations.
/// </summary>
public class WikipediaLookupService : IWikipediaLookupService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly ILogger<WikipediaLookupService> _logger;
    private readonly ConcurrentDictionary<string, EntityLookupResult> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="WikipediaLookupService"/> class.
    /// </summary>
    public WikipediaLookupService(ILogger<WikipediaLookupService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EntityLookupResult> LookupAsync(string term, string language, CancellationToken cancellationToken)
    {
        var cacheKey = $"{language}:{term}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var languageCode = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant();

        try
        {
            var encoded = Uri.EscapeDataString(term.Replace(' ', '_'));
            var url = $"https://{languageCode}.wikipedia.org/api/rest_v1/page/summary/{encoded}";
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Miss(term, cacheKey);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<WikipediaSummary>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (payload is null || string.IsNullOrWhiteSpace(payload.Extract))
            {
                return Miss(term, cacheKey);
            }

            // Drop disambiguation / non-article pages ("Mom (disambiguation)", "All", etc.).
            if (!string.Equals(payload.Type, "standard", StringComparison.OrdinalIgnoreCase))
            {
                return Miss(term, cacheKey);
            }

            var title = string.IsNullOrWhiteSpace(payload.Title) ? term : payload.Title.Trim();
            if (title.Contains("disambiguation", StringComparison.OrdinalIgnoreCase))
            {
                return Miss(term, cacheKey);
            }

            var extract = payload.Extract.Trim();
            if (extract.Contains("may refer to", StringComparison.OrdinalIgnoreCase)
                || extract.Contains("can refer to", StringComparison.OrdinalIgnoreCase)
                || extract.Contains("commonly refers to", StringComparison.OrdinalIgnoreCase)
                || extract.Contains("usually refers to", StringComparison.OrdinalIgnoreCase))
            {
                return Miss(term, cacheKey);
            }

            // Single-word query that Wikipedia "corrected" into a totally different multi-concept title
            // is often noise (e.g. TIM → something odd). Keep person/film-like redirects.
            if (!term.Contains(' ', StringComparison.Ordinal)
                && title.Contains("List of", StringComparison.OrdinalIgnoreCase))
            {
                return Miss(term, cacheKey);
            }

            var summary = extract;
            var sentenceEnd = summary.IndexOf(". ", StringComparison.Ordinal);
            if (sentenceEnd > 40 && sentenceEnd < 220)
            {
                summary = summary[..(sentenceEnd + 1)];
            }
            else if (summary.Length > 220)
            {
                summary = summary[..217].TrimEnd() + "...";
            }

            var result = new EntityLookupResult
            {
                Title = title,
                Summary = summary,
                Url = payload.ContentUrls?.Desktop?.Page,
                ImageUrl = payload.Thumbnail?.Source ?? payload.OriginalImage?.Source,
                Found = true
            };

            _cache[cacheKey] = result;
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Wikipedia lookup failed for {Term}", term);
            return new EntityLookupResult { Title = term, Found = false };
        }
    }

    private EntityLookupResult Miss(string term, string cacheKey)
    {
        var missed = new EntityLookupResult { Title = term, Found = false };
        _cache[cacheKey] = missed;
        return missed;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Jellyfin.Plugin.LookItUp", "1.0"));
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("(+https://github.com/mohamed-sadek/jellyfin-plugin-look-it-up)"));
        return client;
    }

    private sealed class WikipediaSummary
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("extract")]
        public string? Extract { get; set; }

        [JsonPropertyName("thumbnail")]
        public WikipediaImage? Thumbnail { get; set; }

        [JsonPropertyName("originalimage")]
        public WikipediaImage? OriginalImage { get; set; }

        [JsonPropertyName("content_urls")]
        public ContentUrls? ContentUrls { get; set; }
    }

    private sealed class WikipediaImage
    {
        [JsonPropertyName("source")]
        public string? Source { get; set; }
    }

    private sealed class ContentUrls
    {
        [JsonPropertyName("desktop")]
        public DesktopUrls? Desktop { get; set; }
    }

    private sealed class DesktopUrls
    {
        [JsonPropertyName("page")]
        public string? Page { get; set; }
    }
}
