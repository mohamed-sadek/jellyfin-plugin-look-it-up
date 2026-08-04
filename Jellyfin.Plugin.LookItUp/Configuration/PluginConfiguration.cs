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
        PopupDurationMs = 2000;
        PreferredSubtitleLanguages = "en";
        ScanOnPlayback = false;
        WriteSidecarFiles = true;
        PrepareMovies = true;
        PrepareEpisodes = true;
        SkipAlreadyPrepared = true;
        AiProvider = "OpenAI";
        AiApiKey = string.Empty;
        AiModel = "gpt-4o-mini";
        AiBaseUrl = "https://api.openai.com/v1";
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
    /// Gets or sets AI provider: <c>OpenAI</c> (OpenAI-compatible chat API) or <c>None</c> (legacy Wikipedia heuristics).
    /// </summary>
    public string AiProvider { get; set; }

    /// <summary>Gets or sets the AI API key (stored in plugin config).</summary>
    public string AiApiKey { get; set; }

    /// <summary>Gets or sets the chat model id (e.g. gpt-4o-mini).</summary>
    public string AiModel { get; set; }

    /// <summary>Gets or sets the OpenAI-compatible base URL (e.g. https://api.openai.com/v1).</summary>
    public string AiBaseUrl { get; set; }
}
