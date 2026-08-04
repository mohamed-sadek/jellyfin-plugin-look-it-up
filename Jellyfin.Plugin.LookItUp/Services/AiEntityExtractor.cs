using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.LookItUp.Configuration;
using Jellyfin.Plugin.LookItUp.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

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
    Task<IReadOnlyList<AiEntityMention>> ExtractAsync(
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
    public async Task<IReadOnlyList<AiEntityMention>> ExtractAsync(
        string itemName,
        IReadOnlyList<SubtitleCue> cues,
        PluginConfiguration config,
        int maxAnnotations,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(config) || cues.Count == 0)
        {
            return [];
        }

        var results = new List<AiEntityMention>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batches = ChunkCues(cues, linesPerBatch: 45);

        for (var i = 0; i < batches.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count >= maxAnnotations)
            {
                break;
            }

            _logger.LogInformation(
                "Look it up AI batch {Index}/{Total} for {Item} ({Lines} lines)",
                i + 1,
                batches.Count,
                itemName,
                batches[i].Count);

            var batchMentions = await RequestBatchAsync(
                    itemName,
                    batches[i],
                    config,
                    maxAnnotations - results.Count,
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var mention in batchMentions)
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

        return results
            .OrderBy(m => m.StartMs)
            .ToList();
    }

    private async Task<IReadOnlyList<AiEntityMention>> RequestBatchAsync(
        string itemName,
        IReadOnlyList<SubtitleCue> batch,
        PluginConfiguration config,
        int remainingSlots,
        CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(config.AiBaseUrl)
            ? "https://api.openai.com/v1"
            : config.AiBaseUrl.Trim().TrimEnd('/');
        var model = string.IsNullOrWhiteSpace(config.AiModel) ? "gpt-4o-mini" : config.AiModel.Trim();
        var url = baseUrl + "/chat/completions";

        var cueBlock = new StringBuilder();
        foreach (var cue in batch)
        {
            cueBlock.Append(cue.StartMs)
                .Append('-')
                .Append(cue.EndMs)
                .Append('|')
                .Append(cue.Text.Replace('\n', ' ').Trim())
                .Append('\n');
        }

        var system = """
            You extract names, places, films, songs, brands, and cultural references from TV/movie subtitles
            for on-screen explainer popups.

            Rules:
            - Use the subtitle CONTEXT. Prefer the meaning intended by the dialogue.
            - Return the FULL common name (e.g. "Jon Voight" the actor, never a surname etymology).
            - Skip ordinary dialogue words, days of week, greetings, and generic nouns unless they are a clear reference.
            - Skip character first names that are only the show's regular cast unless famously referential.
            - Summary: one short sentence a viewer would find useful (who/what it is in this context). Max ~160 chars.
            - startMs/endMs must come from the provided cue timings for that mention.
            - Return ONLY valid JSON (no markdown) as: {"mentions":[{"term":"","kind":"person|place|film|org|other","summary":"","startMs":0,"endMs":0}]}
            - If nothing worth explaining, return {"mentions":[]}
            """;

        var user = $"""
            Title: {itemName}
            Max mentions for this batch: {Math.Min(12, remainingSlots)}

            Subtitle cues (startMs-endMs|text):
            {cueBlock}
            """;

        var payload = new
        {
            model,
            temperature = 0.2,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AiApiKey.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "AI lookup failed ({Status}): {Body}",
                (int)response.StatusCode,
                body.Length > 400 ? body[..400] : body);
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
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
            var parsed = JsonSerializer.Deserialize<AiResponse>(content, JsonOptions);
            return parsed?.Mentions ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse AI entity JSON");
            return [];
        }
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
