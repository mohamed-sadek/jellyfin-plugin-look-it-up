using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.LookItUp.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Downloads matching subtitles from OpenSubtitles.com.
/// </summary>
public interface IOpenSubtitlesClient
{
    /// <summary>
    /// Returns true when OpenSubtitles credentials are available.
    /// </summary>
    bool IsConfigured(PluginConfiguration config);

    /// <summary>
    /// Searches and downloads the best subtitle for an item into <paramref name="destPath"/>.
    /// </summary>
    Task<OpenSubtitlesDownloadResult?> TryDownloadAsync(
        BaseItem item,
        PluginConfiguration config,
        string destPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Same as <see cref="TryDownloadAsync"/> but reports why download was skipped or failed.
    /// </summary>
    Task<OpenSubtitlesAttempt> TryDownloadWithReasonAsync(
        BaseItem item,
        PluginConfiguration config,
        string destPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of an OpenSubtitles attempt including a user-facing reason when empty.
/// </summary>
public sealed class OpenSubtitlesAttempt
{
    /// <summary>Gets the download result when successful.</summary>
    public OpenSubtitlesDownloadResult? Result { get; init; }

    /// <summary>Gets a short reason when <see cref="Result"/> is null.</summary>
    public string? FailureReason { get; init; }
}

/// <summary>
/// Result of an OpenSubtitles download.
/// </summary>
public sealed class OpenSubtitlesDownloadResult
{
    /// <summary>Gets the downloaded SRT content.</summary>
    public required string Content { get; init; }

    /// <summary>Gets the local path where content was saved.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the match method (<c>moviehash</c> or <c>metadata</c>).</summary>
    public required string MatchedBy { get; init; }

    /// <summary>Gets the moviehash used for search, if any.</summary>
    public string? MovieHash { get; init; }

    /// <summary>Gets a short label for logs/UI.</summary>
    public required string Label { get; init; }
}

/// <summary>
/// OpenSubtitles.com REST API client (search + download).
/// </summary>
public sealed class OpenSubtitlesClient : IOpenSubtitlesClient
{
    private static readonly HttpClient Http = CreateClient();
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<OpenSubtitlesClient> _logger;
    private string? _jwt;
    private DateTime _jwtExpiryUtc = DateTime.MinValue;
    private string _baseHost = "api.opensubtitles.com";
    private string? _jwtUsername;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenSubtitlesClient"/> class.
    /// </summary>
    public OpenSubtitlesClient(IApplicationPaths appPaths, ILogger<OpenSubtitlesClient> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConfigured(PluginConfiguration config)
        => OpenSubtitlesCredentialResolver.IsConfigured(config, _appPaths);

    /// <inheritdoc />
    public async Task<OpenSubtitlesDownloadResult?> TryDownloadAsync(
        BaseItem item,
        PluginConfiguration config,
        string destPath,
        CancellationToken cancellationToken)
    {
        var attempt = await TryDownloadWithReasonAsync(item, config, destPath, cancellationToken)
            .ConfigureAwait(false);
        return attempt.Result;
    }

    /// <inheritdoc />
    public async Task<OpenSubtitlesAttempt> TryDownloadWithReasonAsync(
        BaseItem item,
        PluginConfiguration config,
        string destPath,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(config))
        {
            return new OpenSubtitlesAttempt
            {
                FailureReason =
                    "OpenSubtitles credentials missing — set username/password in Look it up settings, or configure the Jellyfin OpenSubtitles plugin."
            };
        }

        var creds = OpenSubtitlesCredentialResolver.Resolve(config, _appPaths);
        if (creds.UsesJellyfinPluginCredentials)
        {
            _logger.LogInformation(
                "OpenSubtitles: using credentials imported from Jellyfin OpenSubtitles plugin for {User}",
                creds.Username);
        }

        await EnsureLoginAsync(creds, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(_jwt))
        {
            _logger.LogWarning(
                "OpenSubtitles: login required but no JWT obtained for {User}",
                creds.Username);
            return new OpenSubtitlesAttempt
            {
                FailureReason =
                    "OpenSubtitles login failed — check username/password (opensubtitles.com account, not .org)."
            };
        }

        var langs = (config.PreferredSubtitleLanguages ?? "en")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var langCsv = langs.Length == 0 ? "en" : string.Join(',', langs.Select(NormalizeLang));

        string? movieHash = null;
        long? bytes = null;
        if (!string.IsNullOrWhiteSpace(item.Path) && File.Exists(item.Path))
        {
            movieHash = OpenSubtitlesMovieHash.Compute(item.Path);
            try
            {
                bytes = new FileInfo(item.Path).Length;
            }
            catch
            {
                // ignore
            }
        }

        OpenSubtitlesHit? hit = null;
        var matchedBy = "metadata";

        // 1) Hash-only (remuxes rarely match; keep separate so metadata params don't poison the hash query)
        if (!string.IsNullOrWhiteSpace(movieHash) && bytes is > 0)
        {
            hit = await SearchBestAsync(
                    creds,
                    BuildHashQuery(langCsv, movieHash, bytes.Value),
                    item,
                    preferHash: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (hit is not null)
            {
                matchedBy = "moviehash";
            }
        }

        // 2) Metadata: for episodes use parent_imdb_id (series) + season/episode — not episode imdb_id
        hit ??= await SearchBestAsync(
                creds,
                BuildMetadataQuery(langCsv, item),
                item,
                preferHash: false,
                cancellationToken)
            .ConfigureAwait(false);

        // 3) Fallback: series/title text query when provider ids were missing or returned nothing
        if (hit is null && item is Episode)
        {
            hit = await SearchBestAsync(
                    creds,
                    BuildEpisodeQueryFallback(langCsv, (Episode)item),
                    item,
                    preferHash: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (hit is not null)
            {
                matchedBy = "query";
            }
        }

        if (hit is null)
        {
            _logger.LogInformation(
                "OpenSubtitles: no subtitle found for {Item} ({Kind})",
                item.Name,
                item.GetType().Name);
            return new OpenSubtitlesAttempt
            {
                FailureReason =
                    "OpenSubtitles found no matching text subtitle for this title (search used series id + S/E)."
            };
        }

        var link = await RequestDownloadLinkAsync(creds, hit.FileId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(link))
        {
            return new OpenSubtitlesAttempt
            {
                FailureReason = "OpenSubtitles matched a file but download link request failed (quota or API error)."
            };
        }

        using var dl = await Http.GetAsync(link, cancellationToken).ConfigureAwait(false);
        var content = await dl.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!dl.IsSuccessStatusCode || string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning(
                "OpenSubtitles download failed HTTP {Status} for file {FileId}",
                (int)dl.StatusCode,
                hit.FileId);
            return new OpenSubtitlesAttempt
            {
                FailureReason = $"OpenSubtitles download failed (HTTP {(int)dl.StatusCode})."
            };
        }

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(destPath, content, cancellationToken).ConfigureAwait(false);

        return new OpenSubtitlesAttempt
        {
            Result = new OpenSubtitlesDownloadResult
            {
                Content = content,
                Path = destPath,
                MatchedBy = matchedBy,
                MovieHash = movieHash,
                Label = $"opensubtitles:{hit.FileId}:{matchedBy}"
            }
        };
    }

    private async Task EnsureLoginAsync(
        OpenSubtitlesEffectiveCredentials creds,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_jwt)
            && DateTime.UtcNow < _jwtExpiryUtc
            && string.Equals(_jwtUsername, creds.Username, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(creds.Username) || string.IsNullOrWhiteSpace(creds.Password))
        {
            _jwt = null;
            _jwtUsername = null;
            return;
        }

        var url = $"https://{_baseHost}/api/v1/login";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyHeaders(req, creds, includeAuth: false);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                username = creds.Username.Trim(),
                password = creds.Password
            }),
            Encoding.UTF8,
            "application/json");

        using var resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenSubtitles login failed HTTP {Status}: {Body}", (int)resp.StatusCode, Truncate(body));
            _jwt = null;
            _jwtUsername = null;
            return;
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("token", out var token))
        {
            _jwt = token.GetString();
            _jwtExpiryUtc = DateTime.UtcNow.AddHours(20);
            _jwtUsername = creds.Username;
        }

        if (doc.RootElement.TryGetProperty("base_url", out var baseUrl)
            && !string.IsNullOrWhiteSpace(baseUrl.GetString()))
        {
            _baseHost = baseUrl.GetString()!.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim().TrimEnd('/');
        }
    }

    private async Task<OpenSubtitlesHit?> SearchBestAsync(
        OpenSubtitlesEffectiveCredentials creds,
        string query,
        BaseItem item,
        bool preferHash,
        CancellationToken cancellationToken)
    {
        var url = $"https://{_baseHost}/api/v1/subtitles?{query}";
        _logger.LogInformation("OpenSubtitles search: {Query}", query);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(req, creds, includeAuth: true);

        using var resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if ((int)resp.StatusCode is 429 or 503)
        {
            throw new OpenSubtitlesRateLimitedException($"OpenSubtitles search HTTP {(int)resp.StatusCode}");
        }

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenSubtitles search failed HTTP {Status}: {Body}", (int)resp.StatusCode, Truncate(body));
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            _logger.LogInformation("OpenSubtitles search returned no data array for query {Query}", query);
            return null;
        }

        var total = data.GetArrayLength();
        var videoFps = TryGetVideoFps(item);
        OpenSubtitlesHit? best = null;
        var bestScore = int.MinValue;
        var considered = 0;

        foreach (var row in data.EnumerateArray())
        {
            if (!row.TryGetProperty("attributes", out var attrs))
            {
                continue;
            }

            if (item is Episode ep
                && attrs.TryGetProperty("feature_details", out var details)
                && details.ValueKind == JsonValueKind.Object)
            {
                var featureType = details.TryGetProperty("feature_type", out var ft) ? ft.GetString() : null;
                if (!string.IsNullOrWhiteSpace(featureType)
                    && !string.Equals(featureType, "Episode", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(featureType, "Tvshow", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!MatchesSeasonEpisode(details, ep))
                {
                    continue;
                }
            }

            if (!attrs.TryGetProperty("files", out var files) || files.GetArrayLength() == 0)
            {
                continue;
            }

            var file = files[0];
            if (!file.TryGetProperty("file_id", out var fileIdEl))
            {
                continue;
            }

            considered++;
            var fileId = fileIdEl.GetInt64();
            var downloads = attrs.TryGetProperty("download_count", out var dc) && dc.TryGetInt32(out var dcv)
                ? dcv
                : 0;
            double? fps = null;
            if (attrs.TryGetProperty("fps", out var fpsEl) && fpsEl.ValueKind == JsonValueKind.Number)
            {
                fps = fpsEl.GetDouble();
            }

            var score = downloads;
            if (preferHash)
            {
                score += 10_000;
            }

            if (attrs.TryGetProperty("moviehash_match", out var hashMatch)
                && hashMatch.ValueKind == JsonValueKind.True)
            {
                score += 5_000;
            }

            if (videoFps is > 0 && fps is > 0 && Math.Abs(videoFps.Value - fps.Value) < 0.15)
            {
                score += 500;
            }

            if (attrs.TryGetProperty("hearing_impaired", out var hi)
                && hi.ValueKind == JsonValueKind.True)
            {
                score -= 50;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = new OpenSubtitlesHit(fileId, fps);
            }
        }

        _logger.LogInformation(
            "OpenSubtitles search results: total={Total} considered={Considered} bestFileId={FileId}",
            total,
            considered,
            best?.FileId);

        return best;
    }

    private static bool MatchesSeasonEpisode(JsonElement details, Episode ep)
    {
        if (ep.ParentIndexNumber is int seasonWanted)
        {
            var seasonGot = ReadInt(details, "season_number");
            if (seasonGot is not null && seasonGot != seasonWanted)
            {
                return false;
            }
        }

        if (ep.IndexNumber is int episodeWanted)
        {
            var episodeGot = ReadInt(details, "episode_number");
            if (episodeGot is not null && episodeGot != episodeWanted)
            {
                return false;
            }
        }

        return true;
    }

    private static int? ReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
        {
            return null;
        }

        if (el.TryGetInt32(out var n))
        {
            return n;
        }

        if (el.ValueKind == JsonValueKind.String
            && int.TryParse(el.GetString(), out var fromString))
        {
            return fromString;
        }

        return null;
    }

    private async Task<string?> RequestDownloadLinkAsync(
        OpenSubtitlesEffectiveCredentials creds,
        long fileId,
        CancellationToken cancellationToken)
    {
        var url = $"https://{_baseHost}/api/v1/download";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyHeaders(req, creds, includeAuth: true);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { file_id = fileId }),
            Encoding.UTF8,
            "application/json");

        using var resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if ((int)resp.StatusCode is 429 or 503)
        {
            throw new OpenSubtitlesRateLimitedException($"OpenSubtitles download HTTP {(int)resp.StatusCode}");
        }

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenSubtitles /download failed HTTP {Status}: {Body}", (int)resp.StatusCode, Truncate(body));
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("link", out var link))
        {
            return link.GetString();
        }

        return null;
    }

    private void ApplyHeaders(HttpRequestMessage req, OpenSubtitlesEffectiveCredentials creds, bool includeAuth)
    {
        req.Headers.TryAddWithoutValidation("Api-Key", creds.ApiKey);
        req.Headers.TryAddWithoutValidation("User-Agent", "JellyfinLookItUp v1.2.40");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (includeAuth && !string.IsNullOrWhiteSpace(_jwt))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
        }
    }

    private static string BuildHashQuery(string langCsv, string movieHash, long moviebytesize)
    {
        return string.Join(
            '&',
            "languages=" + Uri.EscapeDataString(langCsv),
            "moviehash=" + Uri.EscapeDataString(movieHash),
            "moviebytesize=" + moviebytesize);
    }

    private static string BuildMetadataQuery(string langCsv, BaseItem item)
    {
        var parts = new List<string> { "languages=" + Uri.EscapeDataString(langCsv) };

        if (item is Episode ep)
        {
            parts.Add("type=episode");

            // OpenSubtitles expects the *series* id as parent_imdb_id / parent_tmdb_id.
            var series = ep.Series;
            var parentImdb = series?.GetProviderId(MetadataProvider.Imdb);
            if (string.IsNullOrWhiteSpace(parentImdb))
            {
                // Some libraries only store series IMDb on the episode.
                parentImdb = ep.GetProviderId(MetadataProvider.Imdb);
            }

            var parentImdbDigits = DigitsOnly(parentImdb);
            if (!string.IsNullOrWhiteSpace(parentImdbDigits))
            {
                parts.Add("parent_imdb_id=" + parentImdbDigits);
            }

            var parentTmdb = series?.GetProviderId(MetadataProvider.Tmdb)
                             ?? ep.GetProviderId(MetadataProvider.Tmdb);
            if (!string.IsNullOrWhiteSpace(parentTmdb) && long.TryParse(parentTmdb, out var parentTmdbId))
            {
                parts.Add("parent_tmdb_id=" + parentTmdbId);
            }

            if (ep.ParentIndexNumber is int season)
            {
                parts.Add("season_number=" + season);
            }

            if (ep.IndexNumber is int episode)
            {
                parts.Add("episode_number=" + episode);
            }

            // Text query only when we lack a series id — otherwise it hurts precision.
            if (string.IsNullOrWhiteSpace(parentImdbDigits))
            {
                var seriesName = !string.IsNullOrWhiteSpace(ep.SeriesName) ? ep.SeriesName : series?.Name;
                if (!string.IsNullOrWhiteSpace(seriesName))
                {
                    parts.Add("query=" + Uri.EscapeDataString(seriesName));
                }
            }
        }
        else
        {
            parts.Add("type=movie");
            var imdbDigits = DigitsOnly(item.GetProviderId(MetadataProvider.Imdb));
            if (!string.IsNullOrWhiteSpace(imdbDigits))
            {
                parts.Add("imdb_id=" + imdbDigits);
            }

            var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
            if (!string.IsNullOrWhiteSpace(tmdb) && long.TryParse(tmdb, out var tmdbId))
            {
                parts.Add("tmdb_id=" + tmdbId);
            }

            if (string.IsNullOrWhiteSpace(imdbDigits) && !string.IsNullOrWhiteSpace(item.Name))
            {
                parts.Add("query=" + Uri.EscapeDataString(item.Name));
            }
        }

        return string.Join('&', parts);
    }

    private static string BuildEpisodeQueryFallback(string langCsv, Episode ep)
    {
        var parts = new List<string>
        {
            "languages=" + Uri.EscapeDataString(langCsv),
            "type=episode"
        };

        var seriesName = !string.IsNullOrWhiteSpace(ep.SeriesName)
            ? ep.SeriesName
            : ep.Series?.Name;
        if (!string.IsNullOrWhiteSpace(seriesName))
        {
            parts.Add("query=" + Uri.EscapeDataString(seriesName));
        }

        if (ep.ParentIndexNumber is int season)
        {
            parts.Add("season_number=" + season);
        }

        if (ep.IndexNumber is int episode)
        {
            parts.Add("episode_number=" + episode);
        }

        return string.Join('&', parts);
    }

    private static string? DigitsOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length > 0 ? digits : null;
    }

    private static double? TryGetVideoFps(BaseItem item)
    {
        try
        {
            var video = item.GetMediaStreams()
                .FirstOrDefault(s => s.Type == MediaStreamType.Video && s.AverageFrameRate is > 0);
            return video?.AverageFrameRate;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeLang(string lang)
    {
        lang = lang.Trim().ToLowerInvariant();
        return lang switch
        {
            "eng" => "en",
            "fre" or "fra" => "fr",
            "ger" or "deu" => "de",
            "spa" => "es",
            _ => lang
        };
    }

    private static string Truncate(string s)
        => s.Length <= 240 ? s : s[..240] + "…";

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinLookItUp/1.2.40");
        return c;
    }

    private sealed record OpenSubtitlesHit(long FileId, double? Fps);
}

/// <summary>
/// Thrown when OpenSubtitles rate-limits the client.
/// </summary>
public sealed class OpenSubtitlesRateLimitedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenSubtitlesRateLimitedException"/> class.
    /// </summary>
    public OpenSubtitlesRateLimitedException(string message)
        : base(message)
    {
    }
}
