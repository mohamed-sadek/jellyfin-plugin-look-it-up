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
    private const string DefaultGroqModel = "openai/gpt-oss-20b";
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

        // Ollama is usually local and often has no API key.
        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return true;
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
        var limit = Math.Clamp(config.AiNamesPerPrepare, 1, 20);
        var batch = candidates.Take(limit).ToList();
        var mentions = new List<AiEntityMention>();
        var outcomes = new List<string>(batch.Count);
        var failed = 0;
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
                "Look it up AI verify {Index}/{Total}: {Term} @ {StartMs}ms cue={Cue}",
                i + 1,
                batch.Count,
                candidate.Term,
                candidate.StartMs,
                Truncate(candidate.CueText, 120));

            AiEntityMention? mention = null;
            string? error = null;
            string? rejectReason = null;
            try
            {
                var result = await VerifyOneAsync(
                        itemName,
                        candidate,
                        config,
                        model,
                        baseUrl,
                        cancellationToken)
                    .ConfigureAwait(false);
                mention = result.Mention;
                error = result.Error;
                rejectReason = result.RejectReason;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = $"exception: {ex.Message}";
                _logger.LogWarning(
                    ex,
                    "Look it up AI verify threw for {Term}; continuing with remaining names",
                    candidate.Term);
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
                outcomes.Add($"{candidate.Term}: reject ({rejectReason ?? "keep=false"})");
                continue;
            }

            mentions.Add(mention);
            outcomes.Add($"{candidate.Term}: keep");
        }

        var summary =
            $"AI verify {mentions.Count} kept / {rejected} rejected / {failed} failed of {batch.Count}. " +
            string.Join("; ", outcomes);

        _logger.LogInformation("Look it up AI batch result for {Item}: {Summary}", itemName, summary);

        if (mentions.Count == 0)
        {
            _logger.LogWarning("Look it up AI produced 0 mentions for {Item}: {Summary}", itemName, summary);
            return new AiExtractionResult { Warning = summary };
        }

        return new AiExtractionResult
        {
            Mentions = mentions.OrderBy(m => m.StartMs).ToList(),
            Warning = failed > 0 ? summary : null
        };
    }

    private async Task<(AiEntityMention? Mention, string? Error, string? RejectReason)> VerifyOneAsync(
        string itemName,
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

            var payload = BuildChatPayload(itemName, candidate, model, attempt);
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
                    return (null, lastError, null);
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
                    _logger.LogInformation("Look it up AI rejected {Term}: {Reason}", candidate.Term, reason);
                    return (null, null, reason);
                }

                var term = string.IsNullOrWhiteSpace(parsed.Term) ? candidate.Term : parsed.Term!.Trim();
                var summary = (parsed.Summary ?? string.Empty).Trim();
                if (summary.Length == 0)
                {
                    return (null, $"kept {term} but empty summary", null);
                }

                if (!summary.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                {
                    summary = term + ": " + summary;
                }

                _logger.LogInformation(
                    "Look it up AI kept {Term} → {Canonical}: {Summary}",
                    candidate.Term,
                    term,
                    Truncate(summary, 160));

                return (new AiEntityMention
                {
                    Term = term,
                    Kind = "other",
                    Summary = summary.Length > 160 ? summary[..160] : summary,
                    StartMs = candidate.StartMs,
                    EndMs = Math.Max(candidate.EndMs, candidate.StartMs + 3000)
                }, null, null);
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
                    return (null, lastError, null);
                }

                await Task.Delay(600 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        return (null, lastError ?? "verify failed", null);
    }

    private static Dictionary<string, object?> BuildChatPayload(
        string itemName,
        NameCandidate candidate,
        string model,
        int attempt)
    {
        // Short user-only prompts: llama-3.1-8b-instant often returned finish_reason=stop with empty content.
        string user;
        if (attempt == 1)
        {
            user =
                "Return a JSON object about this subtitle name candidate.\n" +
                "keep=true only for a real person/place/film/brand/cultural reference worth a short popup.\n" +
                "Otherwise keep=false.\n" +
                "Schema: {\"keep\":true,\"term\":\"Jon Voight\",\"summary\":\"American actor.\"}\n" +
                "or {\"keep\":false,\"reason\":\"not a notable name\"}\n" +
                "Show: " + itemName + "\n" +
                "Candidate: " + candidate.Term + "\n" +
                "Line: " + candidate.CueText;
        }
        else
        {
            user =
                "JSON only. Is \"" + candidate.Term + "\" a real name/reference in \"" +
                candidate.CueText + "\"?\n" +
                "{\"keep\":true,\"term\":\"...\",\"summary\":\"...\"} or {\"keep\":false,\"reason\":\"...\"}";
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
                Summary = parsed.Summary,
                Reason = parsed.Reason,
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
}
