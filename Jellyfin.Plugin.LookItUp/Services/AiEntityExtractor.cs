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

    /// <summary>Gets a short warning when AI failed or returned nothing useful.</summary>
    public string? Warning { get; init; }
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
        string itemName,
        IReadOnlyList<NameCandidate> candidates,
        PluginConfiguration config,
        CancellationToken cancellationToken);
}

/// <summary>
/// OpenAI-compatible per-name verifier (Groq, OpenAI, OpenRouter, Ollama).
/// </summary>
public class OpenAiCompatibleEntityExtractor : IAiEntityExtractor
{
    private static readonly HttpClient Http = CreateClient();
    private readonly ILogger<OpenAiCompatibleEntityExtractor> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAiCompatibleEntityExtractor"/> class.
    /// </summary>
    public OpenAiCompatibleEntityExtractor(ILogger<OpenAiCompatibleEntityExtractor> logger)
    {
        _logger = logger;
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

        return !string.IsNullOrWhiteSpace(config.AiApiKey);
    }

    /// <inheritdoc />
    public async Task<AiExtractionResult> ResolveNamesAsync(
        string itemName,
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

        var model = ResolveModel(config);
        var baseUrl = ResolveBaseUrl(config, model);
        var limit = Math.Clamp(config.AiNamesPerPrepare, 1, 20);
        var batch = candidates.Take(limit).ToList();
        var mentions = new List<AiEntityMention>();
        string? lastError = null;
        var rejected = 0;

        _logger.LogInformation(
            "Look it up AI per-name verify for {Item}: {Count} candidates via {BaseUrl} model={Model}",
            itemName,
            batch.Count,
            baseUrl,
            model);

        for (var i = 0; i < batch.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i > 0)
            {
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            }

            var candidate = batch[i];
            _logger.LogInformation(
                "Look it up AI verify {Index}/{Total}: {Term} @ {StartMs}ms",
                i + 1,
                batch.Count,
                candidate.Term,
                candidate.StartMs);

            var result = await VerifyOneAsync(
                    itemName,
                    candidate,
                    config,
                    model,
                    baseUrl,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                lastError = result.Error;
                continue;
            }

            if (result.Mention is null)
            {
                rejected++;
                continue;
            }

            mentions.Add(result.Mention);
        }

        if (mentions.Count == 0)
        {
            var warning = lastError
                          ?? $"AI kept 0/{batch.Count} names (rejected {rejected}).";
            _logger.LogWarning("Look it up AI produced 0 mentions for {Item}: {Warning}", itemName, warning);
            return new AiExtractionResult { Warning = warning };
        }

        return new AiExtractionResult
        {
            Mentions = mentions.OrderBy(m => m.StartMs).ToList(),
            Warning = lastError is null
                ? null
                : $"Partial AI errors; kept {mentions.Count}/{batch.Count}. Last: {lastError}"
        };
    }

    private async Task<(AiEntityMention? Mention, string? Error)> VerifyOneAsync(
        string itemName,
        NameCandidate candidate,
        PluginConfiguration config,
        string model,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var url = baseUrl + "/chat/completions";
        var system = """
            You verify possible names from TV/movie subtitles for short on-screen explainer popups.
            First decide if the candidate is a real person, place, film, song, brand, or cultural reference worth explaining.
            Reject ordinary dialogue words, speaker labels, greetings, and vague fragments.
            If keep=true, return a canonical term and a one-sentence summary (max 120 chars) using the subtitle line for context.
            Reply with ONLY one JSON object, no markdown:
            {"keep":true,"term":"Jon Voight","summary":"American actor (Midnight Cowboy)."}
            or
            {"keep":false,"reason":"not a notable name"}
            """;

        var user =
            "Show/episode: " + itemName + "\n" +
            "Candidate: " + candidate.Term + "\n" +
            "Subtitle line: " + candidate.CueText + "\n" +
            "TimestampMs: " + candidate.StartMs + "\n" +
            "Does this candidate contain a real name/reference worth explaining? JSON only.";

        const int maxAttempts = 3;
        string? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["temperature"] = 0.1,
                ["max_tokens"] = 200,
                ["messages"] = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AiApiKey.Trim());
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if ((int)response.StatusCode == 429)
                {
                    var delay = ParseRetryDelay(body) ?? TimeSpan.FromSeconds(2 * attempt);
                    lastError = $"AI HTTP 429 for {candidate.Term}: waiting {delay.TotalSeconds:0.0}s";
                    _logger.LogWarning("{Error}", lastError);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    lastError =
                        $"AI HTTP {(int)response.StatusCode} for {candidate.Term}: {Truncate(body, 200)}";
                    _logger.LogWarning("{Error}", lastError);
                    return (null, lastError);
                }

                var parsed = TryParseVerifyResponse(body);
                if (!parsed.Ok)
                {
                    lastError = $"AI parse failed for {candidate.Term}: {parsed.Error}";
                    _logger.LogWarning("{Error}. Body: {Snippet}", lastError, Truncate(body, 180));
                    await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!parsed.Keep)
                {
                    _logger.LogInformation(
                        "Look it up AI rejected {Term}: {Reason}",
                        candidate.Term,
                        parsed.Reason ?? "keep=false");
                    return (null, null);
                }

                var term = string.IsNullOrWhiteSpace(parsed.Term) ? candidate.Term : parsed.Term!.Trim();
                var summary = (parsed.Summary ?? string.Empty).Trim();
                if (summary.Length == 0)
                {
                    return (null, $"AI kept {term} but returned empty summary.");
                }

                if (!summary.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                {
                    summary = term + ": " + summary;
                }

                return (new AiEntityMention
                {
                    Term = term,
                    Kind = "other",
                    Summary = summary.Length > 160 ? summary[..160] : summary,
                    StartMs = candidate.StartMs,
                    EndMs = Math.Max(candidate.EndMs, candidate.StartMs + 3000)
                }, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = $"AI request failed for {candidate.Term}: {ex.Message}";
                _logger.LogWarning(ex, "{Error}", lastError);
                if (attempt == maxAttempts)
                {
                    return (null, lastError);
                }

                await Task.Delay(600 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        return (null, lastError ?? $"AI verify failed for {candidate.Term}.");
    }

    private static VerifyParseResult TryParseVerifyResponse(string completionBody)
    {
        if (string.IsNullOrWhiteSpace(completionBody) || completionBody.TrimStart()[0] != '{')
        {
            // Chat completions wrapper expected.
        }

        try
        {
            using var doc = JsonDocument.Parse(completionBody);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return VerifyParseResult.Fail("missing choices");
            }

            var message = choices[0].GetProperty("message");
            var content = ReadMessageContent(message);
            if (string.IsNullOrWhiteSpace(content))
            {
                return VerifyParseResult.Fail("empty content");
            }

            content = StripCodeFence(content.Trim());
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return VerifyParseResult.Fail("no JSON object in content: " + Truncate(content, 80));
            }

            content = content[start..(end + 1)];
            var parsed = JsonSerializer.Deserialize<VerifyResponse>(content, JsonOptions);
            if (parsed is null)
            {
                return VerifyParseResult.Fail("deserialize null");
            }

            return new VerifyParseResult
            {
                Ok = true,
                Keep = parsed.Keep,
                Term = parsed.Term,
                Summary = parsed.Summary,
                Reason = parsed.Reason
            };
        }
        catch (JsonException ex)
        {
            return VerifyParseResult.Fail(ex.Message);
        }
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

    private static string ResolveModel(PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.AiModel))
        {
            return config.AiModel.Trim();
        }

        var baseUrl = (config.AiBaseUrl ?? string.Empty).Trim();
        var provider = (config.AiProvider ?? string.Empty).Trim();
        if (provider.Equals("Groq", StringComparison.OrdinalIgnoreCase)
            || baseUrl.Contains("groq.com", StringComparison.OrdinalIgnoreCase))
        {
            return "llama-3.1-8b-instant";
        }

        return "gpt-4o-mini";
    }

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
        if (string.IsNullOrWhiteSpace(model) || model.Contains('/', StringComparison.Ordinal))
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
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Jellyfin.Plugin.LookItUp", "1.3"));
        return client;
    }

    private sealed class VerifyResponse
    {
        [JsonPropertyName("keep")]
        public bool Keep { get; set; }

        [JsonPropertyName("term")]
        public string? Term { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }

    private sealed class VerifyParseResult
    {
        public bool Ok { get; init; }

        public bool Keep { get; init; }

        public string? Term { get; init; }

        public string? Summary { get; init; }

        public string? Reason { get; init; }

        public string? Error { get; init; }

        public static VerifyParseResult Fail(string error) => new() { Ok = false, Error = error };
    }
}
