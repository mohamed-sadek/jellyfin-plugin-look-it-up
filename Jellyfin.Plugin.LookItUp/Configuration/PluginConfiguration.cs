using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LookItUp.Configuration;

/// <summary>
/// Plugin configuration for Look it up.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        Enabled = true;
        WikipediaLanguage = "en";
        MaxAnnotationsPerItem = 60;
        MinEntityLength = 3;
        PopupDurationMs = 3000;
        PopupDelayMs = 1000;
        PopupFontSizePx = 16;
        PopupTextColor = "#f7fafc";
        PopupBackgroundColor = "rgba(8, 12, 20, 0.96)";
        PopupPlacement = "BottomCenter";
        PopupEdgeOffsetPct = 10;
        PreferredSubtitleLanguages = "en";
        ScanOnPlayback = false;
        IncrementalPrepareOnPlayback = true;
        IncrementalPrepareWindowMs = 300_000;
        IncrementalPrepareBootstrapWindowMs = 60_000;
        IncrementalAiNamesPerWindow = 40;
        StoreAiDecisions = true;
        ShowPopupsDuringPlayback = true;
        WriteSidecarFiles = true;
        PrepareMovies = true;
        PrepareEpisodes = true;
        SkipAlreadyPrepared = true;
        AiProvider = "Groq";
        AiApiKey = string.Empty;
        AiModel = "openai/gpt-oss-20b";
        AiBaseUrl = "https://api.groq.com/openai/v1";
        // 0 = unlimited (capped at AI safety max).
        AiNamesPerPrepare = 0;
        PrepareDelayMsBetweenItems = 1500;
        PrepareMaxAiCallsPerMinute = 30;
        PrepareMaxRetries = 3;
        OpenSubtitlesEnabled = false;
        OpenSubtitlesApiKey = string.Empty;
        OpenSubtitlesUsername = string.Empty;
        OpenSubtitlesPassword = string.Empty;
    }

    /// <summary>Gets or sets whether Look it up is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets Wikipedia language (legacy heuristic mode).</summary>
    public string WikipediaLanguage { get; set; }

    /// <summary>Gets or sets max annotations per item.</summary>
    public int MaxAnnotationsPerItem { get; set; }

    /// <summary>Gets or sets minimum entity length (legacy heuristic mode).</summary>
    public int MinEntityLength { get; set; }

    /// <summary>Gets or sets popup duration in ms.</summary>
    public int PopupDurationMs { get; set; }

    /// <summary>Gets or sets delay in ms after a cue match before the popup appears.</summary>
    public int PopupDelayMs { get; set; }

    /// <summary>Gets or sets popup body font size in pixels.</summary>
    public int PopupFontSizePx { get; set; }

    /// <summary>Gets or sets popup text color (CSS color).</summary>
    public string PopupTextColor { get; set; }

    /// <summary>Gets or sets popup background color (CSS color, including rgba).</summary>
    public string PopupBackgroundColor { get; set; }

    /// <summary>Gets or sets popup placement.</summary>
    public string PopupPlacement { get; set; }

    /// <summary>Gets or sets distance from the screen edge as a viewport percent.</summary>
    public int PopupEdgeOffsetPct { get; set; }

    /// <summary>Gets or sets preferred subtitle language codes.</summary>
    public string PreferredSubtitleLanguages { get; set; }

    /// <summary>Gets or sets whether playback may prepare on demand (full blocking prepare).</summary>
    public bool ScanOnPlayback { get; set; }

    /// <summary>Gets or sets whether playback incrementally prepares ahead in time windows.</summary>
    public bool IncrementalPrepareOnPlayback { get; set; }

    /// <summary>Gets or sets incremental prepare lookahead window in milliseconds.</summary>
    public int IncrementalPrepareWindowMs { get; set; }

    /// <summary>
    /// Gets or sets smaller prepare chunk at start or when catching up after seek,
    /// so opening-minute references finish before playback passes them.
    /// </summary>
    public int IncrementalPrepareBootstrapWindowMs { get; set; }

    /// <summary>Gets or sets max AI verifications per incremental playback window.</summary>
    public int IncrementalAiNamesPerWindow { get; set; }

    /// <summary>Gets or sets whether to persist AI keep/reject reasons in cache JSON for debugging.</summary>
    public bool StoreAiDecisions { get; set; }

    /// <summary>
    /// Gets or sets whether timed popups appear during playback.
    /// When false, incremental prepare still runs and sidecar JSON is still written.
    /// </summary>
    public bool ShowPopupsDuringPlayback { get; set; }

    /// <summary>Gets or sets whether to write *.lookitup.json sidecars.</summary>
    public bool WriteSidecarFiles { get; set; }

    /// <summary>Gets or sets whether prepare includes movies.</summary>
    public bool PrepareMovies { get; set; }

    /// <summary>Gets or sets whether prepare includes episodes.</summary>
    public bool PrepareEpisodes { get; set; }

    /// <summary>Gets or sets whether prepare skips already-prepared items.</summary>
    public bool SkipAlreadyPrepared { get; set; }

    /// <summary>Gets or sets AI provider.</summary>
    public string AiProvider { get; set; }

    /// <summary>Gets or sets the AI API key.</summary>
    public string AiApiKey { get; set; }

    /// <summary>Gets or sets the chat model id.</summary>
    public string AiModel { get; set; }

    /// <summary>Gets or sets the OpenAI-compatible base URL.</summary>
    public string AiBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets max AI name verifies per item (0 = unlimited up to safety cap).
    /// </summary>
    public int AiNamesPerPrepare { get; set; }

    /// <summary>Gets or sets delay between library items during prepare.</summary>
    public int PrepareDelayMsBetweenItems { get; set; }

    /// <summary>Gets or sets max AI HTTP calls per minute (global).</summary>
    public int PrepareMaxAiCallsPerMinute { get; set; }

    /// <summary>Gets or sets max prepare retries for a failed item.</summary>
    public int PrepareMaxRetries { get; set; }

    /// <summary>Gets or sets whether OpenSubtitles download is enabled.</summary>
    public bool OpenSubtitlesEnabled { get; set; }

    /// <summary>Gets or sets the OpenSubtitles.com API key.</summary>
    public string OpenSubtitlesApiKey { get; set; }

    /// <summary>Gets or sets optional OpenSubtitles username (higher quota).</summary>
    public string OpenSubtitlesUsername { get; set; }

    /// <summary>Gets or sets optional OpenSubtitles password.</summary>
    public string OpenSubtitlesPassword { get; set; }
}
