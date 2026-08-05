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
        MaxAnnotationsPerItem = 40;
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
        WriteSidecarFiles = true;
        PrepareMovies = true;
        PrepareEpisodes = true;
        SkipAlreadyPrepared = true;
        AiProvider = "Groq";
        AiApiKey = string.Empty;
        AiModel = "openai/gpt-oss-20b";
        AiBaseUrl = "https://api.groq.com/openai/v1";
        AiNamesPerPrepare = 5;
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

    /// <summary>Gets or sets delay in ms after a cue match before the popup appears. Default 1000.</summary>
    public int PopupDelayMs { get; set; }

    /// <summary>Gets or sets popup body font size in pixels.</summary>
    public int PopupFontSizePx { get; set; }

    /// <summary>Gets or sets popup text color (CSS color).</summary>
    public string PopupTextColor { get; set; }

    /// <summary>Gets or sets popup background color (CSS color, including rgba).</summary>
    public string PopupBackgroundColor { get; set; }

    /// <summary>
    /// Gets or sets popup placement:
    /// BottomCenter, BottomLeft, BottomRight, TopCenter, TopLeft, TopRight, Center.
    /// </summary>
    public string PopupPlacement { get; set; }

    /// <summary>Gets or sets distance from the screen edge as a viewport percent (for top/bottom/side placements).</summary>
    public int PopupEdgeOffsetPct { get; set; }

    /// <summary>Gets or sets preferred subtitle language codes.</summary>
    public string PreferredSubtitleLanguages { get; set; }

    /// <summary>Gets or sets whether playback may prepare on demand.</summary>
    public bool ScanOnPlayback { get; set; }

    /// <summary>Gets or sets whether to write *.lookitup.json sidecars.</summary>
    public bool WriteSidecarFiles { get; set; }

    /// <summary>Gets or sets whether prepare includes movies.</summary>
    public bool PrepareMovies { get; set; }

    /// <summary>Gets or sets whether prepare includes episodes.</summary>
    public bool PrepareEpisodes { get; set; }

    /// <summary>Gets or sets whether prepare skips already-prepared items.</summary>
    public bool SkipAlreadyPrepared { get; set; }

    /// <summary>
    /// Gets or sets AI provider: <c>Groq</c>, <c>OpenAI</c>, <c>OpenRouter</c>, <c>Ollama</c>, or <c>None</c> (legacy Wikipedia).
    /// </summary>
    public string AiProvider { get; set; }

    /// <summary>Gets or sets the AI API key (stored in plugin config).</summary>
    public string AiApiKey { get; set; }

    /// <summary>Gets or sets the chat model id (e.g. openai/gpt-oss-20b, gpt-4o-mini).</summary>
    public string AiModel { get; set; }

    /// <summary>Gets or sets the OpenAI-compatible base URL (e.g. https://api.groq.com/openai/v1).</summary>
    public string AiBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets how many local name candidates to verify with AI (one request each). Default 5.
    /// </summary>
    public int AiNamesPerPrepare { get; set; }
}
