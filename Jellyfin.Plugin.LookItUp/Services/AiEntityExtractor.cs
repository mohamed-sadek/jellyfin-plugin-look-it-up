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
/// Result of an AI subtitle extraction pass.
/// </summary>
public sealed class AiExtractionResult
{
    /// <summary>Gets the extracted mentions.</summary>
    public IReadOnlyList<AiEntityMention> Mentions { get; init; } = [];

    /// <summary>Gets a short warning when AI failed or returned nothing useful.</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// Extracts timed named entities from subtitle cues using an LLM.
/// </summary>
public interface IAiEntityExtractor
{
    /// <summary>
    /// Returns true when AI prepare is configured.
    /// </summary>
    bool IsConfigured(PluginConfiguration config);

    /// <summary>
    /// Analyzes subtitle cues with surrounding context and returns mentions.
    /// </summary>
    Task<AiExtractionResult> ExtractAsync(
        string itemName,
        IReadOnlyList<SubtitleCue> cues,
        PluginConfiguration config,
        int maxAnnotations,
        CancellationToken cancellationToken);
}

/// <summary>
/// OpenAI Chat Completions–compatible extractor (OpenAI, Groq, Azure OpenAI-style, local gateways).
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
    public async Task<AiExtractionResult> ExtractAsync(
        string itemName,
        IReadOnlyList<SubtitleCue> cues,
        PluginConfiguration config,
        int maxAnnotations,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(config) || cues.Count == 0)
        {
            return new AiExtractionResult { Warning = "AI not configured or no subtitle cues." };
        }

        var model = string.IsNullOrWhiteSpace(config.AiModel) ? "gpt-4o-mini" : config.AiModel.Trim();
        var baseUrl = ResolveBaseUrl(config, model);
        var isGroq = baseUrl.Contains("groq.com", StringComparison.OrdinalIgnoreCase);

        // Small batches keep Groq JSON mode reliable and reduce TPM spikes.
        var batches = ChunkCues(cues, linesPerBatch: isGroq ? 18 : 30);
        var results = new List<AiEntityMention>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? lastError = null;
        var failedBatches = 0;

        for (var i = 0; i < batches.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count >= maxAnnotations)
            {
                break;
            }

            if (i > 0)
            {
                // Free-tier Groq TPM is tight; spacing batches avoids cascading 429s.
                await Task.Delay(isGroq ? 1600 : 200, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Look it up AI batch {Index}/{Total} for {Item} ({Lines} lines) via {BaseUrl}",
                i + 1,
                batches.Count,
                itemName,
                batches[i].Count,
                baseUrl);

            var batchResult = await RequestBatchAsync(
                    itemName,
                    batches[i],
                    config,
                    model,
                    baseUrl,
                    maxAnnotations - results.Count,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(batchResult.Error))
            {
                failedBatches++;
                lastError = batchResult.Error;
            }

            foreach (var mention in batchResult.Mentions)
            {
                if (string.IsNullOrWhiteSpace(mention.Term) || string.IsNullOrWhiteSpace(mention.Summary))
                {
                    continue;
                }

                var key = mention.Term.Trim();
                if (!seen.Add(key))
                {
                    continue;
                }

                results.Add(mention);
                if (results.Count >= maxAnnotations)
                {
                    break;
                }
            }
        }

        string? warning = null;
        if (results.Count == 0)
        {
            warning = lastError
                      ?? (failedBatches > 0
                          ? $"AI returned no mentions ({failedBatches}/{batches.Count} batches failed)."
                          : "AI returned no mentions.");
            _logger.LogWarning("Look it up AI produced 0 mentions for {Item}: {Warning}", itemName, warning);
        }

        return new AiExtractionResult
        {
            Mentions = results.OrderBy(m => m.StartMs).ToList(),
            Warning = warning
        };
    }

    private async Task<(IReadOnlyList<AiEntityMention> Mentions, string? Error)> RequestBatchAsync(
        string itemName,
        IReadOnlyList<SubtitleCue> batch,
        PluginConfiguration config,
        string model,
        string baseUrl,
        int remainingSlots,
        CancellationToken cancellationToken)
    {
        var url = baseUrl + "/chat/completions";
        var maxMentions = Math.Clamp(remainingSlots, 1, 6);

        var cueBlock = new StringBuilder();
        foreach (var cue in batch)
        {
            // Avoid "start-end|text" — Groq often echoes that pattern instead of JSON.
            cueBlock.Append("[t=")
                .Append(cue.StartMs)
                .Append("ms] ")
                .Append(cue.Text.Replace('\n', ' ').Trim())
                .Append('\n');
        }

        var system = """
            You extract notable references from TV/movie subtitle lines for short on-screen popups.
            Reply with ONE JSON object only. No markdown. No prose. No subtitle echoes.
            Schema: {"mentions":[{"term":"Jon Voight","kind":"person","summary":"American actor (Midnight Cowboy).","startMs":59766,"endMs":62000}]}
            Rules:
            - term = full common name; use dialogue context (Jon Voight the actor, not surname etymology).
            - kind = person|place|film|org|other
            - summary <= 140 chars
            - startMs must match a [t=...ms] value from the input; endMs ~= startMs + 3000
            - skip regular cast first names, greetings, generic nouns
            - if none, {"mentions":[]}
            """;

        var user = $"""
            Show/episode: {itemName}
            Return at most {maxMentions} mentions from these lines:
            {cueBlock}
            """;

        const int maxAttempts = 4;
        string? lastError = null;
        var useJsonObjectMode = true;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["temperature"] = 0.1,
                ["max_tokens"] = 1200,
                ["messages"] = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user }
                }
            };
            if (useJsonObjectMode)
            {
                payload["response_format"] = new { type = "json_object" };
            }

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
                    lastError = $"AI HTTP 429 rate limit (attempt {attempt}/{maxAttempts}): waiting {delay.TotalSeconds:0.0}s";
                    _logger.LogWarning("{Error}", lastError);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var snippet = body.Length > 350 ? body[..350] : body;
                    lastError = $"AI HTTP {(int)response.StatusCode} from {baseUrl} model={model}: {snippet}";

                    // Groq JSON mode often fails validation; disable it and retry.
                    if ((int)response.StatusCode == 400
                        && body.Contains("json_validate_failed", StringComparison.OrdinalIgnoreCase)
                        && useJsonObjectMode)
                    {
                        _logger.LogWarning(
                            "AI JSON mode failed on attempt {Attempt}; disabling response_format. {Snippet}",
                            attempt,
                            snippet);
                        useJsonObjectMode = false;
                        await Task.Delay(600, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    _logger.LogWarning("{Error}", lastError);
                    return ([], lastError);
                }

                return (ParseMentions(body), null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = $"AI request failed ({baseUrl}, model={model}): {ex.Message}";
                _logger.LogWarning(ex, "{Error}", lastError);
                if (attempt == maxAttempts)
                {
                    return ([], lastError);
                }

                await Task.Delay(1000 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        return ([], lastError ?? "AI request failed after retries.");
    }

    private static IReadOnlyList<AiEntityMention> ParseMentions(string completionBody)
    {
        using var doc = JsonDocument.Parse(completionBody);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        content = StripCodeFence(content);
        // Some models wrap JSON in prose — pull the first object.
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            content = content[start..(end + 1)];
        }

        var parsed = JsonSerializer.Deserialize<AiResponse>(content, JsonOptions);
        return parsed?.Mentions ?? [];
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

        // Add a little cushion beyond Groq's suggested wait.
        return TimeSpan.FromSeconds(Math.Clamp(seconds + 0.4, 1.0, 30.0));
    }

    /// <summary>
    /// Picks the chat-completions root for the configured provider/model.
    /// Groq model ids must not be sent to api.openai.com.
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

        // Common misconfig: Groq model + leftover OpenAI base URL / provider label.
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

        // OpenRouter-style "org/model" ids should not be rewritten to Groq.
        if (model.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        // Groq catalog ids (not OpenAI gpt-*).
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

    private static List<List<SubtitleCue>> ChunkCues(IReadOnlyList<SubtitleCue> cues, int linesPerBatch)
    {
        var batches = new List<List<SubtitleCue>>();
        for (var i = 0; i < cues.Count; i += linesPerBatch)
        {
            batches.Add(cues.Skip(i).Take(linesPerBatch).ToList());
        }

        return batches;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Jellyfin.Plugin.LookItUp", "1.2"));
        return client;
    }

    private sealed class AiResponse
    {
        [JsonPropertyName("mentions")]
        public List<AiEntityMention> Mentions { get; set; } = [];
    }
}
