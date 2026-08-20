using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LookItUp.Configuration;
using Jellyfin.Plugin.LookItUp.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Result of an AI name verification pass.
/// </summary>
public sealed class AiExtractionResult
{
    /// <summary>Gets the verified mentions to show as popups.</summary>
    public IReadOnlyList<AiEntityMention> Mentions { get; init; } = [];

    /// <summary>Gets keep/reject decisions for debugging and tuning.</summary>
    public IReadOnlyList<AiVerifyDecision> Decisions { get; init; } = [];

    /// <summary>Gets a short warning when AI failed or returned nothing useful.</summary>
    public string? Warning { get; set; }
}

/// <summary>
/// Media context for AI verification (show vs episode title, known cast).
/// </summary>
public sealed class AiMediaContext
{
    /// <summary>Series or movie title (e.g. "The Larry Sanders Show").</summary>
    public string ShowName { get; init; } = string.Empty;

    /// <summary>Episode or item title when different from the show.</summary>
    public string? EpisodeName { get; init; }

    /// <summary>Known cast / character names from Jellyfin metadata.</summary>
    public IReadOnlyList<string> KnownCastNames { get; init; } = [];
}

/// <summary>
/// Verifies local name candidates with an LLM (one request per name).
/// </summary>
public interface IAiEntityExtractor
{
    /// <summary>
    /// Returns true when AI prepare is configured.
    /// </summary>
    bool IsConfigured(PluginConfiguration config);

    /// <summary>
    /// Verifies candidates one-by-one and returns kept mentions with short summaries.
    /// </summary>
    Task<AiExtractionResult> ResolveNamesAsync(
        AiMediaContext media,
        IReadOnlyList<NameCandidate> candidates,
        PluginConfiguration config,
        CancellationToken cancellationToken);
}

/// <summary>
/// OpenAI-compatible per-name verifier (Groq, OpenAI, OpenRouter, Ollama).
/// </summary>
public class OpenAiCompatibleEntityExtractor : IAiEntityExtractor
{
    private const string DefaultGroqModel = "openai/gpt-oss-20b";
    private static readonly HttpClient Http = CreateClient();
    private readonly ILogger<OpenAiCompatibleEntityExtractor> _logger;
    private readonly IAiCallRateLimiter _rateLimiter;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAiCompatibleEntityExtractor"/> class.
    /// </summary>
    public OpenAiCompatibleEntityExtractor(
        ILogger<OpenAiCompatibleEntityExtractor> logger,
        IAiCallRateLimiter rateLimiter)
    {
        _logger = logger;
        _rateLimiter = rateLimiter;
    }

    /// <inheritdoc />
    public bool IsConfigured(PluginConfiguration config)
    {
        var provider = (config.AiProvider ?? "None").Trim();
        if (provider.Equals("None", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }

        // Ollama is usually local and often has no API key.
        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(config.AiApiKey);
    }

    /// <inheritdoc />
    public async Task<AiExtractionResult> ResolveNamesAsync(
        AiMediaContext media,
        IReadOnlyList<NameCandidate> candidates,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(config))
        {
            return new AiExtractionResult { Warning = "AI not configured." };
        }

        if (candidates.Count == 0)
        {
            return new AiExtractionResult { Warning = "No local name candidates to verify." };
        }

        var configuredModel = string.IsNullOrWhiteSpace(config.AiModel) ? "(default)" : config.AiModel.Trim();
        var model = ResolveModel(config);
        if (!string.Equals(configuredModel, model, StringComparison.OrdinalIgnoreCase)
            && configuredModel != "(default)")
        {
            _logger.LogWarning(
                "Look it up remapped deprecated AI model {Configured} → {Model}",
                configuredModel,
                model);
        }

        var baseUrl = ResolveBaseUrl(config, model);
        // Caller controls batch size (auto top-N or full UI selection). Safety cap only.
        const int absoluteMax = 250;
        var batch = candidates.Take(absoluteMax).ToList();
        var mentions = new List<AiEntityMention>();
        var decisions = new List<AiVerifyDecision>(batch.Count);
        var outcomes = new List<string>(batch.Count);
        var failed = 0;
        var rejected = 0;

        if (candidates.Count > absoluteMax)
        {
            _logger.LogWarning(
                "Look it up AI verify truncating {Total} candidates to safety cap {Max}",
                candidates.Count,
                absoluteMax);
        }

        var itemLabel = string.IsNullOrWhiteSpace(media.ShowName)
            ? (media.EpisodeName ?? "item")
            : media.ShowName;

        _logger.LogInformation(
            "Look it up AI per-name verify for {Item}: {Count} candidates via {BaseUrl} model={Model}",
            itemLabel,
            batch.Count,
            baseUrl,
            model);

        for (var i = 0; i < batch.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _rateLimiter
                .WaitTurnAsync(config.PrepareMaxAiCallsPerMinute, cancellationToken)
                .ConfigureAwait(false);
            if (i > 0)
            {
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            }

            var candidate = batch[i];
            _logger.LogInformation(
                "Look it up AI verify {Index}/{Total}: {Term} @ {StartMs}ms cue={Cue}",
                i + 1,
                batch.Count,
                candidate.Term,
                candidate.StartMs,
                Truncate(candidate.CueText, 120));

            AiEntityMention? mention = null;
            AiVerifyDecision? decision = null;
            string? error = null;
            try
            {
                var result = await VerifyOneAsync(
                        media,
                        candidate,
                        config,
                        model,
                        baseUrl,
                        cancellationToken)
                    .ConfigureAwait(false);
                mention = result.Mention;
                decision = result.Decision;
                error = result.Error;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = $"exception: {ex.Message}";
                decision = BuildDecision(candidate, kept: false, reason: error, category: "error");
                _logger.LogWarning(
                    ex,
                    "Look it up AI verify threw for {Term}; continuing with remaining names",
                    candidate.Term);
            }

            if (decision is not null)
            {
                decisions.Add(decision);
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                failed++;
                outcomes.Add($"{candidate.Term}: FAIL ({error})");
                _logger.LogWarning(
                    "Look it up AI verify failed for {Term}; continuing ({Done}/{Total})",
                    candidate.Term,
                    i + 1,
                    batch.Count);
                continue;
            }

            if (mention is null)
            {
                rejected++;
                outcomes.Add($"{candidate.Term}: reject ({decision?.Reason ?? "keep=false"})");
                continue;
            }

            mentions.Add(mention);
            outcomes.Add($"{candidate.Term}: keep ({decision?.Reason ?? "ok"})");
        }

        var summary =
            $"AI verify {mentions.Count} kept / {rejected} rejected / {failed} failed of {batch.Count}. " +
            string.Join("; ", outcomes);

        _logger.LogInformation("Look it up AI batch result for {Item}: {Summary}", itemLabel, summary);

        if (mentions.Count == 0)
        {
            _logger.LogWarning("Look it up AI produced 0 mentions for {Item}: {Summary}", itemLabel, summary);
            return new AiExtractionResult { Decisions = decisions, Warning = summary };
        }

        return new AiExtractionResult
        {
            Mentions = mentions.OrderBy(m => m.StartMs).ToList(),
            Decisions = decisions,
            Warning = failed > 0 ? summary : null
        };
    }

    private async Task<(AiEntityMention? Mention, AiVerifyDecision Decision, string? Error)> VerifyOneAsync(
        AiMediaContext media,
        NameCandidate candidate,
        PluginConfiguration config,
        string model,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var url = baseUrl + "/chat/completions";
        const int maxAttempts = 3;
        string? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var payload = BuildChatPayload(media, candidate, model, attempt);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                string.IsNullOrWhiteSpace(config.AiApiKey) ? "ollama" : config.AiApiKey.Trim());
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                _logger.LogInformation(
                    "Look it up AI request {Term} attempt {Attempt}/{Max} → POST {Url} model={Model}",
                    candidate.Term,
                    attempt,
                    maxAttempts,
                    url,
                    model);

                using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Look it up AI response {Term} attempt {Attempt}: HTTP {Status} body={Body}",
                    candidate.Term,
                    attempt,
                    (int)response.StatusCode,
                    Truncate(body, 2000));

                if ((int)response.StatusCode == 429)
                {
                    var delay = ParseRetryDelay(body) ?? TimeSpan.FromSeconds(2 * attempt);
                    lastError = $"HTTP 429 (retry in {delay.TotalSeconds:0.0}s)";
                    _logger.LogWarning("Look it up AI rate-limited for {Term}: {Error}", candidate.Term, lastError);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    lastError = $"HTTP {(int)response.StatusCode}: {Truncate(body, 200)}";
                    return (null, BuildDecision(candidate, false, lastError, "error"), lastError);
                }

                var parsed = TryParseVerifyResponse(body);
                if (!parsed.Ok)
                {
                    lastError = parsed.Error ?? "parse failed";
                    _logger.LogWarning(
                        "Look it up AI parse failed for {Term} attempt {Attempt}: {Error}; finish={Finish}; message={Message}",
                        candidate.Term,
                        attempt,
                        lastError,
                        parsed.FinishReason ?? "?",
                        Truncate(parsed.RawMessageJson ?? string.Empty, 500));
                    await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!parsed.Keep)
                {
                    var reason = parsed.Reason ?? "keep=false";
                    var category = parsed.Category ?? InferRejectCategory(reason);
                    _logger.LogInformation(
                        "Look it up AI rejected {Term}: [{Category}] {Reason}",
                        candidate.Term,
                        category,
                        reason);
                    return (null, BuildDecision(candidate, false, reason, category), null);
                }

                var term = string.IsNullOrWhiteSpace(parsed.Term) ? candidate.Term : parsed.Term!.Trim();
                if (TryGetLocalKeepReject(term, candidate.CueText, out var localReason, out var localCategory))
                {
                    _logger.LogInformation("Look it up AI kept {Term} but local filter dropped it: {Reason}", term, localReason);
                    return (null, BuildDecision(candidate, false, localReason, localCategory), null);
                }

                if (IsTooBasicToKeep(term))
                {
                    const string filterReason = "Local filter: term is too common for a popup";
                    _logger.LogInformation("Look it up AI kept {Term} but local filter dropped it as too basic", term);
                    return (null, BuildDecision(candidate, false, filterReason, "too-common"), null);
                }

                var summary = (parsed.Summary ?? string.Empty).Trim();
                if (summary.Length == 0)
                {
                    var emptySummaryError = $"kept {term} but empty summary";
                    return (null, BuildDecision(candidate, false, emptySummaryError, "error"), emptySummaryError);
                }

                if (!summary.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                {
                    summary = term + ": " + summary;
                }

                summary = ClampSummary(SanitizeSummary(summary, term), 280);

                if (IsSongOrMusicWork(term, parsed.Kind, summary))
                {
                    const string songReason = "Local filter: song/album/track title";
                    _logger.LogInformation(
                        "Look it up AI kept {Term} but local filter dropped it as a song/album/track",
                        term);
                    return (null, BuildDecision(candidate, false, songReason, "song-title"), null);
                }

                var keepReason = string.IsNullOrWhiteSpace(parsed.KeepReason)
                    ? "Non-obvious cultural reference worth a short popup"
                    : parsed.KeepReason!.Trim();
                var keepCategory = parsed.Category ?? NormalizeKind(parsed.Kind);

                _logger.LogInformation(
                    "Look it up AI kept {Term} → {Canonical}: [{Category}] {Reason}",
                    candidate.Term,
                    term,
                    keepCategory,
                    keepReason);

                return (new AiEntityMention
                {
                    Term = term,
                    Kind = FixMentionKind(term, NormalizeKind(parsed.Kind), summary),
                    Summary = summary,
                    StartMs = candidate.StartMs,
                    EndMs = Math.Max(candidate.EndMs, candidate.StartMs + 3000)
                }, BuildDecision(candidate, true, keepReason, keepCategory), null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _logger.LogWarning(ex, "Look it up AI request exception for {Term} attempt {Attempt}", candidate.Term, attempt);
                if (attempt == maxAttempts)
                {
                    return (null, BuildDecision(candidate, false, lastError, "error"), lastError);
                }

                await Task.Delay(600 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        lastError ??= "verify failed";
        return (null, BuildDecision(candidate, false, lastError, "error"), lastError);
    }

    private static AiVerifyDecision BuildDecision(
        NameCandidate candidate,
        bool kept,
        string? reason,
        string? category) =>
        new()
        {
            Term = candidate.Term,
            StartMs = candidate.StartMs,
            CueText = candidate.CueText,
            Kept = kept,
            Reason = reason,
            Category = category,
            AtUtc = DateTime.UtcNow
        };

    private static bool TryGetLocalKeepReject(
        string term,
        string? cueText,
        out string reason,
        out string category)
    {
        if (IsTaxiHailNotShow(term, cueText))
        {
            reason = "Local filter: shouting to hail a cab, not the TV sitcom";
            category = "ordinary-prop";
            return true;
        }

        if (IsEpisodicFakeHoliday(term))
        {
            reason = "Local filter: in-joke fake holiday name, not a real observance";
            category = "in-show";
            return true;
        }

        reason = string.Empty;
        category = string.Empty;
        return false;
    }

    private static bool IsTaxiHailNotShow(string term, string? cueText)
    {
        if (!term.Equals("Taxi", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var cue = (cueText ?? string.Empty).Trim();
        return cue.Equals("Taxi!", StringComparison.OrdinalIgnoreCase)
               || cue.Equals("Taxi", StringComparison.OrdinalIgnoreCase)
               || Regex.IsMatch(cue, @"^Taxi!?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsEpisodicFakeHoliday(string term)
    {
        if (!term.EndsWith(" Day", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2 && words.Length <= 4;
    }

    private static string FixMentionKind(string term, string kind, string summary)
    {
        if (string.Equals(kind, "film", StringComparison.OrdinalIgnoreCase))
        {
            return "film";
        }

        var knownFilms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Deliverance", "Midnight Cowboy", "Runaway Train"
        };
        if (knownFilms.Contains(term.Trim()))
        {
            return "film";
        }

        if (string.Equals(kind, "person", StringComparison.OrdinalIgnoreCase)
            && (summary.Contains(" film", StringComparison.OrdinalIgnoreCase)
                || summary.Contains(" movie", StringComparison.OrdinalIgnoreCase)))
        {
            return "film";
        }

        return kind;
    }

    private static string InferRejectCategory(string reason)
    {
        var r = reason.ToLowerInvariant();
        if (r.Contains("in-show", StringComparison.Ordinal) || r.Contains("cast", StringComparison.Ordinal))
        {
            return "in-show";
        }

        if (r.Contains("song", StringComparison.Ordinal) || r.Contains("album", StringComparison.Ordinal))
        {
            return "song-title";
        }

        if (r.Contains("common", StringComparison.Ordinal) || r.Contains("obvious", StringComparison.Ordinal))
        {
            return "too-common";
        }

        if (r.Contains("car", StringComparison.Ordinal) || r.Contains("model", StringComparison.Ordinal)
            || r.Contains("brand", StringComparison.Ordinal) || r.Contains("product", StringComparison.Ordinal))
        {
            return "ordinary-prop";
        }

        return "no-value";
    }

    private static Dictionary<string, object?> BuildChatPayload(
        AiMediaContext media,
        NameCandidate candidate,
        string model,
        int attempt)
    {
        var show = string.IsNullOrWhiteSpace(media.ShowName) ? "unknown show" : media.ShowName.Trim();
        var episode = string.IsNullOrWhiteSpace(media.EpisodeName) ? null : media.EpisodeName.Trim();
        var castHint = media.KnownCastNames.Count == 0
            ? string.Empty
            : "Known cast/characters from metadata (always reject): "
              + string.Join(", ", media.KnownCastNames.Take(40))
              + "\n";

        // Short user-only prompts: llama-3.1-8b-instant often returned finish_reason=stop with empty content.
        string user;
        if (attempt == 1)
        {
            user =
                "You gatekeep short on-screen popups during TV/film playback. " +
                "Return JSON about this subtitle name candidate.\n" +
                "Dialogue is from \"" + show + "\".\n\n" +
                "KEEP (keep=true) ONLY when a typical viewer would benefit from 1–2 factual sentences — " +
                "a non-obvious CULTURAL REFERENCE worth looking up:\n" +
                "- Real people: historical figures, artists, authors, scientists, politicians, public figures, " +
                "directors, actors, musicians referenced in dialogue (when NOT a character in this show). " +
                "KEEP celebrities/actors even if famous when they are clearly referenced as real people.\n" +
                "- Specific real places: cities, landmarks, regions, institutions when used as meaningful references\n" +
                "- Real organizations, movements, events, awards, ideologies, medical/scientific terms, niche brands\n" +
                "- Films, books, artworks as cultural objects (not song/album/track titles playing in the scene)\n\n" +
                "REJECT (keep=false) when a popup adds little value:\n" +
                "- Any cast member, character, nickname, or in-universe place/org from \"" + show + "\" (in-show)\n" +
                "- Song titles, album names, singles, tracks, or lyric-line titles\n" +
                "- Universal common knowledge: major countries/demonyms used generically, days/months, religious exclamations, " +
                "basic nature words (sun, moon, earth, god), generic family words (mom, dad)\n" +
                "- Ordinary consumer products used casually (car models, everyday brands, generic props) " +
                "when dialogue is comparison/shopping/small-talk without cultural significance\n" +
                "- Shouting \"Taxi!\" to hail a cab (not the TV sitcom Taxi)\n" +
                "- Fake in-joke holiday names like \"Jon Voight Day\" or \"Joe Pepitone Day\" in comedy lists\n" +
                "- Generic city mentions with no notable context (e.g. \"worked a club in Dallas\")\n" +
                "- Subtitle credits, dialogue filler, ordinary capitalized grammar\n" +
                "- Borderline terms where context gives enough clue — when uncertain, reject\n" +
                "When several Jon Voight films are listed in one breath (Deliverance, Midnight Cowboy, Runaway Train), " +
                "KEEP each film title — they are cultural references, not song titles.\n" +
                castHint +
                "Always explain your decision in one sentence.\n" +
                "Schema KEEP: {\"keep\":true,\"term\":\"Jon Voight\",\"kind\":\"person\"," +
                "\"summary\":\"American actor known for Midnight Cowboy and Deliverance.\"," +
                "\"keepReason\":\"Referenced real actor, not in-show cast\",\"category\":\"person-reference\"}\n" +
                "Schema REJECT: {\"keep\":false,\"reason\":\"Ordinary car model in casual comparison dialogue\"," +
                "\"category\":\"ordinary-prop\"}\n" +
                "category tags: person-reference | place-reference | cultural-work | niche-term | in-show | " +
                "song-title | too-common | ordinary-prop | filler | no-value\n" +
                "kind (when keep=true): person | place | film | brand | other\n" +
                "Summary: factual, 1–2 short sentences. Never mention the show. " +
                "Never say \"real-world\", \"fictional\", or whether it is from the show.\n" +
                "Show: " + show + "\n" +
                (episode is null ? string.Empty : "Episode: " + episode + "\n") +
                "Candidate: " + candidate.Term + "\n" +
                "Line: " + candidate.CueText;
        }
        else
        {
            user =
                "JSON only. Gatekeep popups — keep=false when uncertain.\n" +
                "Candidate \"" + candidate.Term + "\" in \"" + candidate.CueText + "\" from \"" + show + "\".\n" +
                "Keep ONLY non-obvious cultural references (people, places, events, niche terms). " +
                "Keep referenced real actors/celebrities unless in-show cast. " +
                "Reject: in-show cast/places, song/album titles, too-common geography/words, " +
                "ordinary car models/brands in casual dialogue, filler.\n" +
                "Always include reason (reject) or keepReason (keep) and category.\n" +
                "{\"keep\":true,\"term\":\"…\",\"kind\":\"person\",\"summary\":\"…\",\"keepReason\":\"…\",\"category\":\"…\"} " +
                "or {\"keep\":false,\"reason\":\"…\",\"category\":\"…\"}";
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = 0.1,
            ["messages"] = new object[]
            {
                new { role = "user", content = user }
            },
            ["response_format"] = new { type = "json_object" }
        };

        if (IsGptOssModel(model))
        {
            payload["max_completion_tokens"] = 1024;
            payload["reasoning_effort"] = "low";
            payload["include_reasoning"] = false;
        }
        else
        {
            payload["max_tokens"] = 400;
        }

        return payload;
    }

    private static VerifyParseResult TryParseVerifyResponse(string completionBody)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(completionBody))
            {
                return VerifyParseResult.Fail("empty HTTP body");
            }

            using var doc = JsonDocument.Parse(completionBody);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return VerifyParseResult.Fail("missing choices");
            }

            var choice = choices[0];
            var finishReason = choice.TryGetProperty("finish_reason", out var fr)
                ? fr.GetString()
                : null;

            if (!choice.TryGetProperty("message", out var message)
                || message.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return VerifyParseResult.Fail("missing message", finishReason, null);
            }

            var rawMessage = message.GetRawText();
            var content = ReadMessageContent(message);
            if (string.IsNullOrWhiteSpace(content))
            {
                content = ReadAlternateText(message, "reasoning")
                          ?? ReadAlternateText(message, "reasoning_content");
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return VerifyParseResult.Fail(
                    "empty content" + (finishReason is null ? string.Empty : $" (finish_reason={finishReason})"),
                    finishReason,
                    rawMessage);
            }

            content = StripCodeFence(content.Trim());
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return VerifyParseResult.Fail(
                    "no JSON object in content: " + Truncate(content, 80),
                    finishReason,
                    rawMessage);
            }

            content = content[start..(end + 1)];
            var parsed = JsonSerializer.Deserialize<VerifyResponse>(content, JsonOptions);
            if (parsed is null)
            {
                return VerifyParseResult.Fail("deserialize null", finishReason, rawMessage);
            }

            return new VerifyParseResult
            {
                Ok = true,
                Keep = parsed.Keep,
                Term = parsed.Term,
                Kind = parsed.Kind,
                Summary = parsed.Summary,
                Reason = parsed.Reason,
                KeepReason = parsed.KeepReason,
                Category = parsed.Category,
                FinishReason = finishReason,
                RawMessageJson = rawMessage
            };
        }
        catch (JsonException ex)
        {
            return VerifyParseResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return VerifyParseResult.Fail(ex.Message);
        }
    }

    private static string? ReadAlternateText(JsonElement message, string propertyName)
    {
        if (!message.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.GetRawText()
        };
    }

    private static string? ReadMessageContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return null;
        }

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Concat(content.EnumerateArray().Select(part =>
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    return part.GetString() ?? string.Empty;
                }

                if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text))
                {
                    return text.GetString() ?? string.Empty;
                }

                return string.Empty;
            })),
            JsonValueKind.Null => null,
            _ => content.GetRawText()
        };
    }

    /// <summary>
    /// Resolves the chat model, remapping deprecated Groq IDs that return empty content.
    /// </summary>
    public static string ResolveModel(PluginConfiguration config)
    {
        var configured = string.IsNullOrWhiteSpace(config.AiModel)
            ? string.Empty
            : config.AiModel.Trim();

        var isGroq = IsGroq(config);

        if (string.IsNullOrWhiteSpace(configured))
        {
            return isGroq ? DefaultGroqModel : "gpt-4o-mini";
        }

        if (isGroq && IsDeprecatedGroqModel(configured))
        {
            return DefaultGroqModel;
        }

        return configured;
    }

    private static bool IsGroq(PluginConfiguration config)
    {
        var baseUrl = (config.AiBaseUrl ?? string.Empty).Trim();
        var provider = (config.AiProvider ?? string.Empty).Trim();
        return provider.Equals("Groq", StringComparison.OrdinalIgnoreCase)
               || baseUrl.Contains("groq.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeprecatedGroqModel(string model) =>
        model.Equals("llama-3.1-8b-instant", StringComparison.OrdinalIgnoreCase)
        || model.Equals("llama-3.3-70b-versatile", StringComparison.OrdinalIgnoreCase);

    private static bool IsGptOssModel(string model) =>
        model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? ParseRetryDelay(string body)
    {
        var match = Regex.Match(
            body,
            @"try again in ([0-9]+(?:\.[0-9]+)?)s",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Clamp(seconds + 0.4, 1.0, 30.0));
    }

    /// <summary>
    /// Picks the chat-completions root for the configured provider/model.
    /// </summary>
    public static string ResolveBaseUrl(PluginConfiguration config, string model)
    {
        var provider = (config.AiProvider ?? string.Empty).Trim();
        var configured = string.IsNullOrWhiteSpace(config.AiBaseUrl)
            ? string.Empty
            : config.AiBaseUrl.Trim().TrimEnd('/');

        if (provider.Equals("Groq", StringComparison.OrdinalIgnoreCase))
        {
            return "https://api.groq.com/openai/v1";
        }

        if (provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
        {
            return "https://openrouter.ai/api/v1";
        }

        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(configured) ? "http://127.0.0.1:11434/v1" : configured;
        }

        if (LooksLikeGroqModel(model)
            && (string.IsNullOrWhiteSpace(configured)
                || configured.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase)))
        {
            return "https://api.groq.com/openai/v1";
        }

        return string.IsNullOrWhiteSpace(configured) ? "https://api.openai.com/v1" : configured;
    }

    private static bool LooksLikeGroqModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        if (model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("qwen/", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("meta-llama/", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("moonshotai/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (model.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        return model.Contains("llama", StringComparison.OrdinalIgnoreCase)
               || model.Contains("mixtral", StringComparison.OrdinalIgnoreCase)
               || model.Contains("gemma", StringComparison.OrdinalIgnoreCase)
               || model.Contains("qwen", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNl = trimmed.IndexOf('\n');
        if (firstNl < 0)
        {
            return trimmed;
        }

        trimmed = trimmed[(firstNl + 1)..];
        var fence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            trimmed = trimmed[..fence];
        }

        return trimmed.Trim();
    }

    private static string SanitizeSummary(string summary, string term)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }

        var s = summary.Trim();

        // Drop meta clauses the model loves: "..., not a character from the show."
        s = Regex.Replace(
            s,
            @"[,:;]?\s*(which\s+)?(is\s+)?(a\s+)?real[-\s]?world[^.]*?(from the show[^.]*?)?\.\s*",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        s = Regex.Replace(
            s,
            @"[,:;]?\s*(and\s+)?(is|isn'?t|is not|are not)?\s*(a\s+)?(fictional\s+)?character\b[^.]*\.?\s*",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        s = Regex.Replace(
            s,
            @"[,:;]?\s*not a (fictional\s+)?character\b[^.]*\.?\s*",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        s = Regex.Replace(
            s,
            @"\b(from|in) the show\b[^.]*\.?",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        s = s.TrimStart(',', ';', ':', '-', '—', ' ').Trim();

        if (s.Length == 0)
        {
            return term + ".";
        }

        // Ensure it still leads with the term when possible.
        if (!s.StartsWith(term, StringComparison.OrdinalIgnoreCase)
            && s.Length < 220)
        {
            s = term + ": " + char.ToUpperInvariant(s[0]) + s[1..];
        }

        return s;
    }

    private static string ClampSummary(string summary, int maxChars)
    {
        if (string.IsNullOrEmpty(summary) || summary.Length <= maxChars)
        {
            return summary;
        }

        var slice = summary[..maxChars];
        var lastSentence = Math.Max(slice.LastIndexOf(". "), slice.LastIndexOf("! "));
        lastSentence = Math.Max(lastSentence, slice.LastIndexOf("? "));
        if (lastSentence >= maxChars / 2)
        {
            return slice[..(lastSentence + 1)].TrimEnd();
        }

        var lastSpace = slice.LastIndexOf(' ');
        if (lastSpace >= maxChars / 2)
        {
            return slice[..lastSpace].TrimEnd() + "…";
        }

        return slice.TrimEnd() + "…";
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Jellyfin.Plugin.LookItUp", "1.2.39"));
        return client;
    }

    private sealed class VerifyResponse
    {
        [JsonPropertyName("keep")]
        public bool Keep { get; set; }

        [JsonPropertyName("term")]
        public string? Term { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("keepReason")]
        public string? KeepReason { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }
    }

    private sealed class VerifyParseResult
    {
        public bool Ok { get; init; }

        public bool Keep { get; init; }

        public string? Term { get; init; }

        public string? Kind { get; init; }

        public string? Summary { get; init; }

        public string? Reason { get; init; }

        public string? KeepReason { get; init; }

        public string? Category { get; init; }

        public string? Error { get; init; }

        public string? FinishReason { get; init; }

        public string? RawMessageJson { get; init; }

        public static VerifyParseResult Fail(
            string error,
            string? finishReason = null,
            string? rawMessageJson = null) =>
            new()
            {
                Ok = false,
                Error = error,
                FinishReason = finishReason,
                RawMessageJson = rawMessageJson
            };
    }

    private static bool IsTooBasicToKeep(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return true;
        }

        var t = term.Trim();
        // Mirror the local finder junk list for terms AI might still keep.
        var basic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "god", "jesus", "christ", "jesus christ", "lord", "heaven", "hell",
            "earth", "world", "moon", "sun", "sky", "sea", "ocean",
            "america", "american", "americans", "usa",
            "china", "chinese", "japan", "japanese", "france", "french",
            "germany", "german", "italy", "italian", "spain", "spanish",
            "britain", "british", "england", "english", "russia", "russian",
            "india", "indian", "europe", "european", "africa", "african", "asia", "asian",
            "opensubtitles", "subtitles", "subtitle",
            "man", "woman", "boy", "girl", "love", "life", "death", "time", "home", "school"
        };

        if (basic.Contains(t))
        {
            return true;
        }

        var compact = t.Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.Contains("opensubtitle", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeKind(string? kind)
    {
        var value = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "person" or "people" or "actor" or "actress" or "artist" or "author" or "writer"
                or "singer" or "musician" or "band" => "person",
            "place" or "location" or "city" or "country" or "planet" or "region" => "place",
            "film" or "movie" or "show" or "tv" or "series" => "film",
            "song" or "album" or "track" or "single" or "ep" or "mixtape" or "soundtrack" or "record" => "song",
            "brand" or "company" or "org" or "organization" or "product" or "drug" or "medication" => "brand",
            "event" or "history" or "historical" or "war" or "award" or "condition" or "disorder"
                or "group" or "people-group" or "culture" or "subculture"
                or "other" or "real-world reference" or "reference" => "other",
            _ => "other"
        };
    }

    /// <summary>
    /// True when the entity is a song/album/track title (not an artist/band as a person).
    /// Used at prepare time and when serving cached annotations.
    /// </summary>
    public static bool IsSongOrMusicWork(string? term, string? kind, string? summary)
    {
        var k = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (k is "song" or "album" or "track" or "single" or "ep" or "mixtape" or "soundtrack" or "record")
        {
            return true;
        }

        // Artists/bands stay; only drop the musical work itself.
        if (k is "person" or "people" or "actor" or "actress" or "artist" or "author" or "writer"
            or "singer" or "musician" or "band")
        {
            return false;
        }

        var s = summary ?? string.Empty;
        if (s.Length == 0)
        {
            return false;
        }

        // Strip "Term: " prefix so patterns match the explanation body.
        var body = s;
        if (!string.IsNullOrWhiteSpace(term)
            && body.StartsWith(term.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            body = body[term.Trim().Length..].TrimStart(':', ' ', '-', '–');
        }

        // "… is a song / hit single / studio album …"
        if (Regex.IsMatch(
                body,
                @"\b(?:is|was)\s+(?:a|an|the)\s+(?:(?:hit|debut|studio|live|concept|cover)\s+)?(?:song|single|album|track|ep|record)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        // "song/single/track by Artist"
        if (Regex.IsMatch(
                body,
                @"\b(?:song|single|track|album|ep)\s+by\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        // "from the album / soundtrack …"
        if (Regex.IsMatch(
                body,
                @"\bfrom\s+the\s+(?:album|soundtrack|ep|mixtape)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        // "charting hit / Billboard single"
        if (Regex.IsMatch(
                body,
                @"\b(?:billboard|chart(?:ing)?)\s+(?:hit|single|song)\b|\bhit\s+single\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        return false;
    }
}
