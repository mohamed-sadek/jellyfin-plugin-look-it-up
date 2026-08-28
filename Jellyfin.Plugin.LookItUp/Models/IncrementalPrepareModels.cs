namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// Input for offline incremental prepare simulation (subtitle file + plugin settings).
/// </summary>
public sealed class IncrementalPrepareRequest
{
    /// <summary>Gets or sets raw subtitle file text.</summary>
    public string SubtitleContent { get; init; } = string.Empty;

    /// <summary>Gets or sets subtitle file name (used for format detection).</summary>
    public string SubtitleFileName { get; init; } = "subtitle.srt";

    /// <summary>Gets or sets media title passed to name finding (episode/movie name).</summary>
    public string ItemTitle { get; init; } = "Test item";

    /// <summary>Gets or sets optional stable item id for the simulated cache.</summary>
    public Guid ItemId { get; init; } = Guid.NewGuid();

    /// <summary>Gets or sets incremental window size in milliseconds (default 5 minutes).</summary>
    public long WindowMs { get; init; } = 300_000;

    /// <summary>Gets or sets cast/character names to exclude from candidates.</summary>
    public IReadOnlyList<string> ExcludeCastNames { get; init; } = [];

    /// <summary>Gets or sets show/series title for AI context.</summary>
    public string ShowName { get; init; } = "Test show";

    /// <summary>Gets or sets episode title for AI context when different from show.</summary>
    public string? EpisodeName { get; init; }

    /// <summary>Gets or sets when true, lists window candidates without AI/Wikipedia calls.</summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// One simulated incremental prepare window (e.g. 0–5 min, 5–10 min).
/// </summary>
public sealed class IncrementalPrepareWindowResult
{
    /// <summary>Gets or sets window start (ms).</summary>
    public long FromMs { get; init; }

    /// <summary>Gets or sets window end (ms).</summary>
    public long ToMs { get; init; }

    /// <summary>Gets or sets candidates found for this window before dedupe.</summary>
    public int CandidatesInWindow { get; init; }

    /// <summary>Gets or sets candidates sent to AI after skipping cached terms.</summary>
    public int CandidatesVerified { get; init; }

    /// <summary>Gets or sets annotations added to cache in this window.</summary>
    public int AnnotationsAdded { get; init; }

    /// <summary>Gets or sets terms skipped because they were already in cache.</summary>
    public IReadOnlyList<string> SkippedTerms { get; init; } = [];

    /// <summary>Gets or sets terms verified in this window (dry-run or live).</summary>
    public IReadOnlyList<string> VerifiedTerms { get; init; } = [];
}

/// <summary>
/// Result of simulating incremental prepare over a full subtitle timeline.
/// </summary>
public sealed class IncrementalPrepareSimulationResult
{
    /// <summary>Gets or sets the cache JSON that would be written after all windows.</summary>
    public ItemAnnotationCache Cache { get; init; } = new();

    /// <summary>Gets or sets per-window simulation steps.</summary>
    public IReadOnlyList<IncrementalPrepareWindowResult> Windows { get; init; } = [];

    /// <summary>Gets or sets subtitle duration inferred from cues (ms).</summary>
    public long SubtitleDurationMs { get; init; }

    /// <summary>Gets or sets prepare mode: ai, wikimedia, or dry-run.</summary>
    public string Mode { get; init; } = "ai";

    /// <summary>Gets or sets optional warning (e.g. AI not configured).</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// Result of a single incremental prepare-ahead call during playback.
/// </summary>
public sealed class PrepareAheadResult
{
    /// <summary>Gets or sets whether the cache was updated.</summary>
    public bool Changed { get; init; }

    /// <summary>Gets or sets annotations added in this call.</summary>
    public IReadOnlyList<ContextAnnotation> Added { get; init; } = [];

    /// <summary>Gets or sets the updated cache entry.</summary>
    public ItemAnnotationCache? Cache { get; init; }

    /// <summary>Gets or sets window details when work ran.</summary>
    public IncrementalPrepareWindowResult? Window { get; init; }

    /// <summary>Gets or sets subtitle duration (ms).</summary>
    public long SubtitleDurationMs { get; init; }

    /// <summary>Gets or sets prepare mode: ai, legacy, cache, or skipped.</summary>
    public string Mode { get; init; } = "skipped";

    /// <summary>Gets or sets optional warning.</summary>
    public string? Warning { get; init; }
}
