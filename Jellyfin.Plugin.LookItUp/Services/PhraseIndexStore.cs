using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.LookItUp.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Loads the generated phrase index from an optional data-dir override, then the embedded resource.
/// </summary>
public interface IPhraseIndexStore
{
    /// <summary>Gets loaded entries (empty if the index is missing).</summary>
    IReadOnlyList<PhraseIndexEntry> Entries { get; }

    /// <summary>Gets when the loaded index was generated.</summary>
    DateTime? GeneratedAtUtc { get; }
}

/// <summary>
/// Embedded gzip phrase index, with a sidecar override next to the plugin DLL.
/// </summary>
public sealed class PhraseIndexStore : IPhraseIndexStore
{
    private const string ResourceName = "Jellyfin.Plugin.LookItUp.Data.phrase-index.json.gz";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="PhraseIndexStore"/> class.
    /// </summary>
    public PhraseIndexStore(ILogger<PhraseIndexStore> logger)
    {
        var overridePath = Path.Combine(AppContext.BaseDirectory, "phrase-index.json");
        if (File.Exists(overridePath))
        {
            try
            {
                var json = File.ReadAllText(overridePath);
                var file = JsonSerializer.Deserialize<PhraseIndexFile>(json, JsonOptions);
                if (file?.Entries is { Count: > 0 })
                {
                    Entries = Normalize(file.Entries);
                    GeneratedAtUtc = file.GeneratedAtUtc;
                    logger.LogInformation(
                        "Look it up loaded phrase index override {Path} ({Count} phrases)",
                        overridePath,
                        Entries.Count);
                    return;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Look it up failed to read phrase index override {Path}", overridePath);
            }
        }

        var gzOverride = Path.Combine(AppContext.BaseDirectory, "phrase-index.json.gz");
        if (File.Exists(gzOverride))
        {
            try
            {
                using var fs = File.OpenRead(gzOverride);
                var file = ReadGzip(fs);
                if (file?.Entries is { Count: > 0 })
                {
                    Entries = Normalize(file.Entries);
                    GeneratedAtUtc = file.GeneratedAtUtc;
                    logger.LogInformation(
                        "Look it up loaded phrase index {Path} ({Count} phrases)",
                        gzOverride,
                        Entries.Count);
                    return;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Look it up failed to read {Path}", gzOverride);
            }
        }

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            logger.LogWarning("Look it up embedded phrase index {Resource} is missing", ResourceName);
            Entries = [];
            return;
        }

        try
        {
            var file = ReadGzip(stream);
            Entries = Normalize(file?.Entries ?? []);
            GeneratedAtUtc = file?.GeneratedAtUtc;
            logger.LogInformation("Look it up loaded embedded phrase index ({Count} phrases)", Entries.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Look it up failed to load embedded phrase index");
            Entries = [];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PhraseIndexEntry> Entries { get; }

    /// <inheritdoc />
    public DateTime? GeneratedAtUtc { get; }

    private static PhraseIndexFile? ReadGzip(Stream gzip)
    {
        using var ds = new GZipStream(gzip, CompressionMode.Decompress, leaveOpen: true);
        return JsonSerializer.Deserialize<PhraseIndexFile>(ds, JsonOptions);
    }

    private static IReadOnlyList<PhraseIndexEntry> Normalize(IEnumerable<PhraseIndexEntry> entries)
    {
        return entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Phrase) && !string.IsNullOrWhiteSpace(e.Title))
            .Select(e => new PhraseIndexEntry
            {
                Phrase = e.Phrase.Trim().ToLowerInvariant(),
                Title = e.Title.Trim(),
                Qid = e.Qid,
                Kind = string.IsNullOrWhiteSpace(e.Kind) ? "other" : e.Kind
            })
            .GroupBy(e => e.Phrase, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }
}
