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
        if (!IsConfigured(config) || string.IsNullOrWhiteSpace(item.Path))
        {
            return null;
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
            return null;
        }

        var langs = (config.PreferredSubtitleLanguages ?? "en")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var langCsv = langs.Length == 0 ? "en" : string.Join(',', langs.Select(NormalizeLang));

        var movieHash = OpenSubtitlesMovieHash.Compute(item.Path);
        long? bytes = null;
        try
        {
            bytes = new FileInfo(item.Path).Length;
        }
        catch
        {
            // ignore
        }

        OpenSubtitlesHit? hit = null;
        var matchedBy = "metadata";

        if (!string.IsNullOrWhiteSpace(movieHash) && bytes is > 0)
        {
            hit = await SearchBestAsync(
                    creds,
                    BuildQuery(langCsv, movieHash: movieHash, moviebytesize: bytes),
                    item,
                    preferHash: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (hit is not null)
            {
                matchedBy = "moviehash";
            }
        }

        hit ??= await SearchBestAsync(
                creds,
                BuildQuery(langCsv, item: item),
                item,
                preferHash: false,
                cancellationToken)
            .ConfigureAwait(false);

        if (hit is null)
        {
            _logger.LogInformation("OpenSubtitles: no subtitle found for {Item}", item.Name);
            return null;
        }

        var link = await RequestDownloadLinkAsync(creds, hit.FileId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        using var dl = await Http.GetAsync(link, cancellationToken).ConfigureAwait(false);
        var content = await dl.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!dl.IsSuccessStatusCode || string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning(
                "OpenSubtitles download failed HTTP {Status} for file {FileId}",
                (int)dl.StatusCode,
                hit.FileId);
            return null;
        }

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(destPath, content, cancellationToken).ConfigureAwait(false);

        return new OpenSubtitlesDownloadResult
        {
            Content = content,
            Path = destPath,
            MatchedBy = matchedBy,
            MovieHash = movieHash,
            Label = $"opensubtitles:{hit.FileId}:{matchedBy}"
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
            return null;
        }

        var videoFps = TryGetVideoFps(item);
        OpenSubtitlesHit? best = null;
        var bestScore = int.MinValue;

        foreach (var row in data.EnumerateArray())
        {
            if (!row.TryGetProperty("attributes", out var attrs))
            {
                continue;
            }

            var downloads = attrs.TryGetProperty("download_count", out var dc) ? dc.GetInt32() : 0;
            double? fps = null;
            if (attrs.TryGetProperty("fps", out var fpsEl) && fpsEl.ValueKind == JsonValueKind.Number)
            {
                fps = fpsEl.GetDouble();
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

            var fileId = fileIdEl.GetInt64();
            var score = downloads;
            if (preferHash)
            {
                score += 10_000;
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

        return best;
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

    private static string BuildQuery(
        string langCsv,
        string? movieHash = null,
        long? moviebytesize = null,
        BaseItem? item = null)
    {
        var parts = new List<string> { "languages=" + Uri.EscapeDataString(langCsv) };
        if (!string.IsNullOrWhiteSpace(movieHash))
        {
            parts.Add("moviehash=" + Uri.EscapeDataString(movieHash));
        }

        if (moviebytesize is > 0)
        {
            parts.Add("moviebytesize=" + moviebytesize.Value);
        }

        if (item is not null)
        {
            var imdb = item.GetProviderId(MetadataProvider.Imdb);
            if (!string.IsNullOrWhiteSpace(imdb))
            {
                var digits = new string(imdb.Where(char.IsDigit).ToArray());
                if (digits.Length > 0)
                {
                    parts.Add("imdb_id=" + digits);
                }
            }

            var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
            if (!string.IsNullOrWhiteSpace(tmdb) && long.TryParse(tmdb, out var tmdbId))
            {
                parts.Add("tmdb_id=" + tmdbId);
            }

            if (item is Episode ep)
            {
                if (ep.ParentIndexNumber is int season)
                {
                    parts.Add("season_number=" + season);
                }

                if (ep.IndexNumber is int episode)
                {
                    parts.Add("episode_number=" + episode);
                }
            }

            if (!string.IsNullOrWhiteSpace(item.Path))
            {
                parts.Add("query=" + Uri.EscapeDataString(Path.GetFileNameWithoutExtension(item.Path)));
            }
        }

        return string.Join('&', parts);
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
