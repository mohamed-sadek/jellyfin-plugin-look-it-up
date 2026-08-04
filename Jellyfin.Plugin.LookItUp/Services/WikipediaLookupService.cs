using System.Collections.Concurrent;
using System.Net.Http.Json;
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
    /// <param name="term">Entity name.</param>
    /// <param name="language">Wikipedia language code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Lookup result.</returns>
    Task<EntityLookupResult> LookupAsync(string term, string language, CancellationToken cancellationToken);
}

/// <summary>
/// Uses the Wikipedia REST summary API for short explanations.
/// </summary>
public class WikipediaLookupService : IWikipediaLookupService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WikipediaLookupService> _logger;
    private readonly ConcurrentDictionary<string, EntityLookupResult> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="WikipediaLookupService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public WikipediaLookupService(IHttpClientFactory httpClientFactory, ILogger<WikipediaLookupService> logger)
    {
        _httpClientFactory = httpClientFactory;
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
        var client = _httpClientFactory.CreateClient(nameof(WikipediaLookupService));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.LookItUp/1.0 (https://jellyfin.org)");

        try
        {
            var encoded = Uri.EscapeDataString(term.Replace(' ', '_'));
            var url = $"https://{languageCode}.wikipedia.org/api/rest_v1/page/summary/{encoded}";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var missed = new EntityLookupResult { Title = term, Found = false };
                _cache[cacheKey] = missed;
                return missed;
            }

            var payload = await response.Content.ReadFromJsonAsync<WikipediaSummary>(cancellationToken).ConfigureAwait(false);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Extract))
            {
                var missed = new EntityLookupResult { Title = term, Found = false };
                _cache[cacheKey] = missed;
                return missed;
            }

            // Prefer a short first-sentence style summary.
            var summary = payload.Extract.Trim();
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
                Title = string.IsNullOrWhiteSpace(payload.Title) ? term : payload.Title,
                Summary = summary,
                Url = payload.ContentUrls?.Desktop?.Page,
                Found = true
            };

            _cache[cacheKey] = result;
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Wikipedia lookup failed for {Term}", term);
            return new EntityLookupResult { Title = term, Found = false };
        }
    }

    private sealed class WikipediaSummary
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("extract")]
        public string? Extract { get; set; }

        [JsonPropertyName("content_urls")]
        public ContentUrls? ContentUrls { get; set; }
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
