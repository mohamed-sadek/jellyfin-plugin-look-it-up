using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.LookItUp.Configuration;
using Jellyfin.Plugin.LookItUp.Models;
using Jellyfin.Plugin.LookItUp.Services;
using Microsoft.Extensions.Logging;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    var options = ParseArgs(args);
    if (options is null)
    {
        PrintUsage();
        return 1;
    }

    if (!File.Exists(options.SubtitlePath))
    {
        Console.Error.WriteLine($"Subtitle file not found: {options.SubtitlePath}");
        return 1;
    }

    var config = LoadConfig(options.ConfigPath);
    ApplyEnvOverrides(config);

    if (!options.DryRun && !HasAiOrLegacy(config))
    {
        Console.Error.WriteLine(
            "AI is not configured (set AiProvider + AiApiKey in config or LOOKITUP_AI_API_KEY). " +
            "Use --dry-run to test window candidate selection without API calls.");
        return 1;
    }

    var subtitleContent = await File.ReadAllTextAsync(options.SubtitlePath).ConfigureAwait(false);
    var subtitleName = Path.GetFileName(options.SubtitlePath);

    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Information);
    });

    var engine = new IncrementalPrepareEngine(
        new SubtitleParser(),
        new NameCandidateFinder(),
        new EntityExtractor(),
        new WikipediaLookupService(loggerFactory.CreateLogger<WikipediaLookupService>()),
        new OpenAiCompatibleEntityExtractor(
            loggerFactory.CreateLogger<OpenAiCompatibleEntityExtractor>(),
            new AiCallRateLimiter()),
        loggerFactory.CreateLogger<IncrementalPrepareEngine>());

    var request = new IncrementalPrepareRequest
    {
        SubtitleContent = subtitleContent,
        SubtitleFileName = subtitleName,
        ItemTitle = options.ItemTitle ?? Path.GetFileNameWithoutExtension(subtitleName),
        ItemId = options.ItemId ?? Guid.NewGuid(),
        WindowMs = options.WindowMinutes * 60_000L,
        ExcludeCastNames = options.ExcludeCast,
        ShowName = options.ShowName ?? options.ItemTitle ?? Path.GetFileNameWithoutExtension(subtitleName),
        EpisodeName = options.EpisodeName,
        DryRun = options.DryRun
    };

    Console.WriteLine($"Simulating incremental prepare ({options.WindowMinutes} min windows)");
    Console.WriteLine($"  Subtitle: {options.SubtitlePath}");
    Console.WriteLine($"  Mode: {(options.DryRun ? "dry-run (candidates only)" : (string.IsNullOrWhiteSpace(config.AiApiKey) && !IsOllama(config) ? "legacy Wikipedia" : "AI"))}");
    Console.WriteLine();

    var result = await engine
        .SimulateAsync(request, config, CancellationToken.None)
        .ConfigureAwait(false);

    PrintWindowSummary(result);

    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    var json = JsonSerializer.Serialize(result.Cache, jsonOptions);

    if (!string.IsNullOrWhiteSpace(options.OutputPath))
    {
        var outDir = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        await File.WriteAllTextAsync(options.OutputPath, json).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine($"Wrote cache JSON: {options.OutputPath}");
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine("=== ItemAnnotationCache JSON ===");
        Console.WriteLine(json);
    }

    if (!string.IsNullOrWhiteSpace(result.Warning))
    {
        Console.WriteLine();
        Console.WriteLine($"Warning: {result.Warning}");
    }

    return 0;
}

static void PrintWindowSummary(IncrementalPrepareSimulationResult result)
{
    Console.WriteLine(
        $"Subtitle duration: {FormatClock(result.SubtitleDurationMs)} ({result.SubtitleDurationMs} ms)");
    Console.WriteLine($"Windows: {result.Windows.Count} | Final annotations: {result.Cache.Annotations.Count}");
    Console.WriteLine(
        $"Prepared through: {FormatClock(result.Cache.PreparedThroughMs)} | Fully prepared: {result.Cache.FullyPrepared}");
    Console.WriteLine();

    foreach (var window in result.Windows)
    {
        Console.WriteLine(
            $"[{FormatClock(window.FromMs)} – {FormatClock(window.ToMs)}] " +
            $"candidates={window.CandidatesInWindow} verify={window.CandidatesVerified} " +
            $"added={window.AnnotationsAdded} skipped={window.SkippedTerms.Count}");
        if (window.VerifiedTerms.Count > 0)
        {
            Console.WriteLine($"  verify: {string.Join(", ", window.VerifiedTerms)}");
        }

        if (window.SkippedTerms.Count > 0)
        {
            Console.WriteLine($"  skipped (already cached): {string.Join(", ", window.SkippedTerms)}");
        }
    }
}

static string FormatClock(long ms)
{
    var totalSec = Math.Max(0, ms / 1000);
    var h = totalSec / 3600;
    var m = (totalSec % 3600) / 60;
    var s = totalSec % 60;
    return h > 0
        ? $"{h}:{m:D2}:{s:D2}"
        : $"{m}:{s:D2}";
}

static bool HasAiOrLegacy(PluginConfiguration config)
    => !string.IsNullOrWhiteSpace(config.AiApiKey)
       || IsOllama(config)
       || true; // legacy Wikipedia always available

static bool IsOllama(PluginConfiguration config)
    => string.Equals(config.AiProvider, "Ollama", StringComparison.OrdinalIgnoreCase);

static PluginConfiguration LoadConfig(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return new PluginConfiguration();
    }

    if (!File.Exists(path))
    {
        throw new FileNotFoundException($"Config file not found: {path}");
    }

    var json = File.ReadAllText(path);
    var loaded = JsonSerializer.Deserialize<PluginConfiguration>(
        json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    return loaded ?? new PluginConfiguration();
}

static void ApplyEnvOverrides(PluginConfiguration config)
{
    var apiKey = Environment.GetEnvironmentVariable("LOOKITUP_AI_API_KEY");
    if (!string.IsNullOrWhiteSpace(apiKey))
    {
        config.AiApiKey = apiKey;
    }

    var provider = Environment.GetEnvironmentVariable("LOOKITUP_AI_PROVIDER");
    if (!string.IsNullOrWhiteSpace(provider))
    {
        config.AiProvider = provider;
    }

    var model = Environment.GetEnvironmentVariable("LOOKITUP_AI_MODEL");
    if (!string.IsNullOrWhiteSpace(model))
    {
        config.AiModel = model;
    }

    var baseUrl = Environment.GetEnvironmentVariable("LOOKITUP_AI_BASE_URL");
    if (!string.IsNullOrWhiteSpace(baseUrl))
    {
        config.AiBaseUrl = baseUrl;
    }

    var rpm = Environment.GetEnvironmentVariable("LOOKITUP_AI_RPM");
    if (int.TryParse(rpm, out var parsedRpm) && parsedRpm > 0)
    {
        config.PrepareMaxAiCallsPerMinute = parsedRpm;
    }
}

static CliOptions? ParseArgs(string[] args)
{
    if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
    {
        return null;
    }

    string? subtitle = null;
    string? config = null;
    string? output = null;
    string? title = null;
    string? show = null;
    string? episode = null;
    Guid? itemId = null;
    var windowMinutes = 5;
    var dryRun = false;
    var verbose = false;
    var excludeCast = new List<string>();

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "--config" or "-c":
                config = RequireValue(args, ref i, arg);
                break;
            case "--output" or "-o":
                output = RequireValue(args, ref i, arg);
                break;
            case "--title" or "-t":
                title = RequireValue(args, ref i, arg);
                break;
            case "--show":
                show = RequireValue(args, ref i, arg);
                break;
            case "--episode":
                episode = RequireValue(args, ref i, arg);
                break;
            case "--item-id":
                itemId = Guid.Parse(RequireValue(args, ref i, arg));
                break;
            case "--window-minutes" or "-w":
                windowMinutes = int.Parse(RequireValue(args, ref i, arg));
                break;
            case "--exclude-cast":
                excludeCast.Add(RequireValue(args, ref i, arg));
                break;
            case "--dry-run":
                dryRun = true;
                break;
            case "--verbose" or "-v":
                verbose = true;
                break;
            default:
                if (arg.StartsWith('-'))
                {
                    throw new ArgumentException($"Unknown option: {arg}");
                }

                subtitle ??= arg;
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(subtitle))
    {
        return null;
    }

    return new CliOptions(
        subtitle,
        config,
        output,
        title,
        show,
        episode,
        itemId,
        windowMinutes,
        dryRun,
        verbose,
        excludeCast);
}

static string RequireValue(string[] args, ref int i, string flag)
{
    if (i + 1 >= args.Length)
    {
        throw new ArgumentException($"Missing value for {flag}");
    }

    i++;
    return args[i];
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        Look it up — incremental prepare simulator

        Usage:
          dotnet run --project tools/LookItUp.IncrementalPrepareSimulator -- [options] <subtitle.srt|vtt>
          ./scripts/simulate-incremental-prepare.sh [options] <subtitle.srt|vtt>

        Options:
          -c, --config <file>       JSON config (PluginConfiguration fields)
          -o, --output <file>       Write ItemAnnotationCache JSON to file (default: stdout)
          -t, --title <name>        Media/episode title for name finding
          --show <name>             Show/series title for AI context
          --episode <name>          Episode title for AI context
          --item-id <guid>          Stable item id in output JSON
          -w, --window-minutes <n>  Incremental window size (default: 5)
          --exclude-cast <name>     Cast name to exclude (repeatable)
          --dry-run                 List per-window candidates without AI/Wikipedia
          -v, --verbose             Debug logging
          -h, --help                Show this help

        Environment (override config):
          LOOKITUP_AI_API_KEY
          LOOKITUP_AI_PROVIDER
          LOOKITUP_AI_MODEL
          LOOKITUP_AI_BASE_URL
          LOOKITUP_AI_RPM

        Example:
          LOOKITUP_AI_API_KEY=gsk_... ./scripts/simulate-incremental-prepare.sh \
            -c tools/incremental-prepare.config.example.json \
            -o /tmp/out.lookitup.json \
            -t "Pilot" --show "My Show" \
            ~/subs/episode01.srt
        """);
}

file sealed record CliOptions(
    string SubtitlePath,
    string? ConfigPath,
    string? OutputPath,
    string? ItemTitle,
    string? ShowName,
    string? EpisodeName,
    Guid? ItemId,
    int WindowMinutes,
    bool DryRun,
    bool Verbose,
    IReadOnlyList<string> ExcludeCast);
