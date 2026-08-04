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
/// OpenAI-compatible extractor: local NER candidates, then one (or two) LLM resolve calls.
/// </summary>
public class OpenAiCompatibleEntityExtractor : IAiEntityExtractor
{
    private const int MaxCandidates = 40;
    private static readonly HttpClient Http = CreateClient();
    private readonly IEntityExtractor _entityExtractor;
    private readonly ILogger<OpenAiCompatibleEntityExtractor> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAiCompatibleEntityExtractor"/> class.
    /// </summary>
    public OpenAiCompatibleEntityExtractor(
        IEntityExtractor entityExtractor,
        ILogger<OpenAiCompatibleEntityExtractor> logger)
    {
        _entityExtractor = entityExtractor;
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

        var model = ResolveModel(config);
        var baseUrl = ResolveBaseUrl(config, model);
        var candidates = BuildCandidates(cues, Math.Max(3, config.MinEntityLength), maxAnnotations);

        if (candidates.Count == 0)
        {
            return new AiExtractionResult
            {
                Warning = "No local NER candidates found in subtitles to send to AI."
            };
        }

        _logger.LogInformation(
            "Look it up AI one-shot for {Item}: {CandidateCount} candidates → {BaseUrl} model={Model}",
            itemName,
            candidates.Count,
            baseUrl,
            model);

        var first = await ResolveCandidatesAsync(
                itemName,
                candidates,
                config,
                model,
                baseUrl,
                maxAnnotations,
                cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(first.Error) && first.Mentions.Count == 0)
        {
            _logger.LogWarning("Look it up AI produced 0 mentions for {Item}: {Warning}", itemName, first.Error);
            return new AiExtractionResult { Warning = first.Error };
        }

        var results = new List<AiEntityMention>(first.Mentions);
        var seen = new HashSet<string>(results.Select(m => m.Term.Trim()), StringComparer.OrdinalIgnoreCase);

        // One continuation for candidates the model skipped (truncation / too many).
        var leftover = candidates
            .Where(c => !seen.Contains(c.Term)
                        && !results.Any(m =>
                            m.Term.Contains(c.Term, StringComparison.OrdinalIgnoreCase)
                            || c.Term.Contains(m.Term, StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Max(0, maxAnnotations - results.Count))
            .ToList();

        if (leftover.Count >= 3 && results.Count < maxAnnotations && first.MayHaveMore)
        {
            _logger.LogInformation(
                "Look it up AI continuation for {Item}: {Leftover} leftover candidates",
                itemName,
                leftover.Count);

            var second = await ResolveCandidatesAsync(
                    itemName,
                    leftover,
                    config,
                    model,
                    baseUrl,
                    maxAnnotations - results.Count,
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var mention in second.Mentions)
            {
                if (string.IsNullOrWhiteSpace(mention.Term) || string.IsNullOrWhiteSpace(mention.Summary))
                {
                    continue;
                }

                if (!seen.Add(mention.Term.Trim()))
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

        var ordered = results
            .Where(m => !string.IsNullOrWhiteSpace(m.Term) && !string.IsNullOrWhiteSpace(m.Summary))
            .OrderBy(m => m.StartMs)
            .Take(maxAnnotations)
            .ToList();

        if (ordered.Count == 0)
        {
            var warning = first.Error ?? "AI returned no mentions for the candidate list.";
            _logger.LogWarning("Look it up AI produced 0 mentions for {Item}: {Warning}", itemName, warning);
            return new AiExtractionResult { Warning = warning };
        }

        return new AiExtractionResult { Mentions = ordered };
    }

    private List<AiCandidate> BuildCandidates(
        IReadOnlyList<SubtitleCue> cues,
        int minLength,
        int maxAnnotations)
    {
        var byTerm = new Dictionary<string, AiCandidate>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            IReadOnlyList<string> entities;
            try
            {
                entities = _entityExtractor.Extract(cue.Text, minLength);
            }
            catch
            {
                continue;
            }

            foreach (var term in entities)
            {
                if (byTerm.ContainsKey(term))
                {
                    continue;
                }

                var prev = i > 0 ? cues[i - 1].Text.Replace('\n', ' ').Trim() : string.Empty;
                var curr = cue.Text.Replace('\n', ' ').Trim();
                var next = i + 1 < cues.Count ? cues[i + 1].Text.Replace('\n', ' ').Trim() : string.Empty;
                var context = string.Join(" / ", new[] { prev, curr, next }.Where(s => s.Length > 0));
                if (context.Length > 220)
                {
                    context = context[..220];
                }

                byTerm[term] = new AiCandidate
                {
                    Term = term,
                    StartMs = cue.StartMs,
                    EndMs = Math.Max(cue.EndMs, cue.StartMs + 3000),
                    Context = context
                };
            }
        }

        // Prefer multi-word / longer names (Jon Voight before Car).
        var ranked = byTerm.Values
            .OrderByDescending(c => c.Term.Count(ch => ch == ' '))
            .ThenByDescending(c => c.Term.Length)
            .ThenBy(c => c.StartMs)
            .Take(Math.Min(MaxCandidates, Math.Max(maxAnnotations, 20)))
            .OrderBy(c => c.StartMs)
            .ToList();

        return ranked;
    }

    private async Task<(IReadOnlyList<AiEntityMention> Mentions, string? Error, bool MayHaveMore)> ResolveCandidatesAsync(
        string itemName,
        IReadOnlyList<AiCandidate> candidates,
        PluginConfiguration config,
        string model,
        string baseUrl,
        int maxMentions,
        CancellationToken cancellationToken)
    {
        var url = baseUrl + "/chat/completions";
        var mentionCap = Math.Clamp(maxMentions, 1, candidates.Count);

        var candidateBlock = new StringBuilder();
        foreach (var c in candidates)
        {
            candidateBlock.Append("- candidate=\"")
                .Append(c.Term.Replace('"', '\''))
                .Append("\" t=")
                .Append(c.StartMs)
                .Append("ms context=\"")
                .Append(c.Context.Replace('"', '\''))
                .Append('"')
                .Append('\n');
        }

        var system = """
            You resolve subtitle name candidates into short on-screen explainer popups.
            Reply with ONE JSON object only. No markdown. No prose.
            Schema: {"mentions":[{"term":"Jon Voight","kind":"person","summary":"American actor (Midnight Cowboy).","startMs":59766,"endMs":62766}]}
            Rules:
            - Only keep candidates that are real people, places, films, songs, brands, or cultural references.
            - Drop ordinary dialogue words, greetings, and the show's regular cast first names unless famously referential.
            - Use CONTEXT to pick the intended meaning (Jon Voight the actor, not a surname etymology).
            - term = full common name; summary <= 140 chars; kind = person|place|film|org|other.
            - startMs must be the candidate's t= value (or nearby); endMs ~= startMs + 3000.
            - If none worth keeping: {"mentions":[]}
            """;

        var user = $"""
            Show/episode: {itemName}
            Return at most {mentionCap} mentions from these candidates:
            {candidateBlock}
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
                ["max_tokens"] = 2500,
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

                    if ((int)response.StatusCode == 400
                        && body.Contains("json_validate_failed", StringComparison.OrdinalIgnoreCase)
                        && useJsonObjectMode)
                    {
                        _logger.LogWarning(
                            "AI JSON mode failed on attempt {Attempt}; disabling response_format. {Snippet}",
                            attempt,
                            snippet);
                        useJsonObjectMode = false;
                        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    _logger.LogWarning("{Error}", lastError);
                    return ([], lastError, false);
                }

                var (mentions, finishReason) = ParseMentions(body);
                var mayHaveMore = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)
                                  || mentions.Count < Math.Min(mentionCap, candidates.Count) / 2;
                return (mentions, null, mayHaveMore);
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
                    return ([], lastError, false);
                }

                await Task.Delay(800 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        return ([], lastError ?? "AI request failed after retries.", false);
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

    private static (IReadOnlyList<AiEntityMention> Mentions, string? FinishReason) ParseMentions(string completionBody)
    {
        using var doc = JsonDocument.Parse(completionBody);
        string? finishReason = null;
        if (doc.RootElement.TryGetProperty("choices", out var choices)
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("finish_reason", out var fr))
        {
            finishReason = fr.GetString();
        }

        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
        {
            return ([], finishReason);
        }

        content = StripCodeFence(content);
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            content = content[start..(end + 1)];
        }

        var parsed = JsonSerializer.Deserialize<AiResponse>(content, JsonOptions);
        return (parsed?.Mentions ?? [], finishReason);
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

    private sealed class AiCandidate
    {
        public string Term { get; init; } = string.Empty;

        public long StartMs { get; init; }

        public long EndMs { get; init; }

        public string Context { get; init; } = string.Empty;
    }

    private sealed class AiResponse
    {
        [JsonPropertyName("mentions")]
        public List<AiEntityMention> Mentions { get; set; } = [];
    }
}
