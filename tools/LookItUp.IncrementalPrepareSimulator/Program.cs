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

    string subtitleContent;
    string subtitleName;
    try
    {
        (subtitleContent, subtitleName) = await LoadInputAsync(options).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    if (string.IsNullOrWhiteSpace(subtitleContent))
    {
        Console.Error.WriteLine("No subtitle or text to scan.");
        return 1;
    }

    var config = LoadConfig(options.ConfigPath);
    ApplyEnvOverrides(config);
    config.StoreAiDecisions = true;
    if (string.IsNullOrWhiteSpace(config.AiProvider))
    {
        config.AiProvider = "None";
    }

    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Information);
    });

    var pipeline = new WikimediaReferencePipeline(
        new WikimediaReferenceResolver(loggerFactory.CreateLogger<WikimediaReferenceResolver>()),
        new ReferenceGate(),
        loggerFactory.CreateLogger<WikimediaReferencePipeline>());

    var aiExtractor = new OpenAiCompatibleEntityExtractor(
        loggerFactory.CreateLogger<OpenAiCompatibleEntityExtractor>(),
        new AiCallRateLimiter());
    var wikipedia = new WikipediaLookupService(loggerFactory.CreateLogger<WikipediaLookupService>());
    var complement = new AiComplementService(
        aiExtractor,
        wikipedia,
        loggerFactory.CreateLogger<AiComplementService>());
    var engine = new IncrementalPrepareEngine(
        new SubtitleParser(),
        new NameCandidateFinder(),
        wikipedia,
        pipeline,
        complement,
        loggerFactory.CreateLogger<IncrementalPrepareEngine>());

    var request = new IncrementalPrepareRequest
    {
        SubtitleContent = subtitleContent,
        SubtitleFileName = subtitleName,
        ItemTitle = options.ItemTitle ?? Path.GetFileNameWithoutExtension(subtitleName),
        ItemId = options.ItemId ?? Guid.NewGuid(),
        WindowMs = options.WindowMinutes * 60_000L,
        ExcludeCastNames = options.ExcludeCast,
        ShowName = options.ShowName ?? options.ItemTitle ?? "Unknown show",
        EpisodeName = options.EpisodeName,
        DryRun = options.DryRun
    };

    Console.WriteLine($"Simulating incremental prepare ({options.WindowMinutes} min windows)");
    Console.WriteLine($"  Input: {DescribeInput(options, subtitleName)}");
    Console.WriteLine($"  Show: {request.ShowName}");
    Console.WriteLine(
        $"  Mode: {(options.DryRun ? "dry-run (candidates only)" : (complement.IsEnabled(config) ? "Wikimedia + Groq complement" : "Wikimedia"))}");
    Console.WriteLine();

    var result = await engine
        .SimulateAsync(request, config, CancellationToken.None)
        .ConfigureAwait(false);

    PrintWindowSummary(result);
    if (!options.DryRun)
    {
        Console.WriteLine();
        PrintDecisionTable(result.Cache);
    }

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
    else if (options.DumpJson)
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

static string DescribeInput(CliOptions options, string subtitleName)
{
    if (!string.IsNullOrWhiteSpace(options.Text))
    {
        return "pasted --text";
    }

    if (options.FromStdin || options.SubtitlePath == "-")
    {
        return "stdin";
    }

    return options.SubtitlePath ?? subtitleName;
}

static void PrintDecisionTable(ItemAnnotationCache cache)
{
    var decisions = cache.AiDecisions ?? [];
    Console.WriteLine("=== KEEP / DROP ===");
    if (decisions.Count == 0)
    {
        Console.WriteLine("(no decisions)");
        return;
    }

    foreach (var d in decisions.OrderBy(x => x.StartMs).ThenBy(x => x.Term, StringComparer.OrdinalIgnoreCase))
    {
        var flag = d.Kept ? "KEEP" : "DROP";
        var category = (d.Category ?? "").PadRight(16);
        var annotation = cache.Annotations.FirstOrDefault(a =>
            a.Term.Equals(d.Term, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(d.Term) && a.Term.Contains(d.Term, StringComparison.OrdinalIgnoreCase)));
        var detail = annotation?.Summary ?? d.Reason ?? "";
        if (detail.Length > 90)
        {
            detail = detail[..87] + "...";
        }

        Console.WriteLine($"{flag,-4}  {d.Term,-22}  {category}  {detail}");
    }

    Console.WriteLine();
    Console.WriteLine($"Kept {decisions.Count(d => d.Kept)} / {decisions.Count} candidates.");
    if (cache.Annotations.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("=== POPUPS ===");
        foreach (var a in cache.Annotations.OrderBy(x => x.StartMs))
        {
            var summary = a.Summary ?? "";
            if (summary.Length > 120)
            {
                summary = summary[..117] + "...";
            }

            Console.WriteLine($"  {FormatClock(a.StartMs)}  {a.Term}: {summary}");
        }
    }
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

static async Task<(string Content, string FileName)> LoadInputAsync(CliOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.Text))
    {
        return (WrapPlainTextAsSrt(options.Text), "paste.srt");
    }

    if (options.FromStdin || options.SubtitlePath == "-")
    {
        var raw = await Console.In.ReadToEndAsync().ConfigureAwait(false);
        return LooksLikeSubtitle(raw)
            ? (raw, "stdin.srt")
            : (WrapPlainTextAsSrt(raw), "paste.srt");
    }

    var path = options.SubtitlePath!;
    if (!File.Exists(path))
    {
        throw new FileNotFoundException($"Subtitle file not found: {path}");
    }

    var rawFile = await File.ReadAllTextAsync(path).ConfigureAwait(false);
    var ext = Path.GetExtension(path).ToLowerInvariant();
    if (ext is ".txt" or "" || !LooksLikeSubtitle(rawFile))
    {
        return (WrapPlainTextAsSrt(rawFile), Path.GetFileNameWithoutExtension(path) + ".srt");
    }

    return (rawFile, Path.GetFileName(path));
}

static bool LooksLikeSubtitle(string content)
    => content.Contains("-->", StringComparison.Ordinal);

static string WrapPlainTextAsSrt(string text)
{
    var body = (text ?? string.Empty).Trim();
    return $"""
        1
        00:00:00,000 --> 00:00:08,000
        {body}

        """;
}

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
    if (args.Contains("-h") || args.Contains("--help"))
    {
        return null;
    }

    string? subtitle = null;
    string? text = null;
    string? config = null;
    string? output = null;
    string? title = null;
    string? show = null;
    string? episode = null;
    Guid? itemId = null;
    var windowMinutes = 5;
    var dryRun = false;
    var verbose = false;
    var dumpJson = false;
    var fromStdin = false;
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
            case "--text":
                text = RequireValue(args, ref i, arg);
                break;
            case "--file":
                subtitle = RequireValue(args, ref i, arg);
                break;
            case "--json":
                dumpJson = true;
                break;
            case "--dry-run":
                dryRun = true;
                break;
            case "--verbose" or "-v":
                verbose = true;
                break;
            case "-":
                fromStdin = true;
                subtitle = "-";
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

    if (string.IsNullOrWhiteSpace(text)
        && string.IsNullOrWhiteSpace(subtitle)
        && !fromStdin
        && !Console.IsInputRedirected)
    {
        return null;
    }

    if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(subtitle) && Console.IsInputRedirected)
    {
        fromStdin = true;
        subtitle = "-";
    }

    return new CliOptions(
        subtitle,
        text,
        config,
        output,
        title,
        show,
        episode,
        itemId,
        windowMinutes,
        dryRun,
        verbose,
        dumpJson,
        fromStdin,
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
        Look it up — local preview (no plugin install)

        Usage:
          dotnet run --project tools/LookItUp.IncrementalPrepareSimulator -- --show Seinfeld --text "..."
          dotnet run --project tools/LookItUp.IncrementalPrepareSimulator -- --show Seinfeld --file episode.srt
          type episode.srt | dotnet run --project tools/LookItUp.IncrementalPrepareSimulator -- --show Seinfeld -

        Options:
          --text <dialogue>         Paste cue text (wrapped as a single 8s cue)
          --file <path>             Subtitle .srt/.vtt or plain .txt
          -                         Read stdin
          -c, --config <file>       JSON config (PluginConfiguration fields)
          -o, --output <file>       Write cache JSON to file
          --json                    Also print cache JSON to stdout
          -t, --title <name>        Media/episode title for name finding
          --show <name>             Show/series title (in-show filter)
          --episode <name>          Episode title
          --item-id <guid>          Stable item id in output JSON
          -w, --window-minutes <n>  Incremental window size (default: 5)
          --exclude-cast <name>     Cast name to exclude (repeatable)
          --dry-run                 List candidates without Wikipedia
          -v, --verbose             Debug logging
          -h, --help                Show this help

        Example:
          dotnet run --project tools/LookItUp.IncrementalPrepareSimulator -- \
            --show Seinfeld --exclude-cast Jerry --exclude-cast George \
            --text "No baron has ever owned a LeBaron. Jon Voight's LeBaron."
        """);
}

file sealed record CliOptions(
    string? SubtitlePath,
    string? Text,
    string? ConfigPath,
    string? OutputPath,
    string? ItemTitle,
    string? ShowName,
    string? EpisodeName,
    Guid? ItemId,
    int WindowMinutes,
    bool DryRun,
    bool Verbose,
    bool DumpJson,
    bool FromStdin,
    IReadOnlyList<string> ExcludeCast);
