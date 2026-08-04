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
        PopupDurationMs = 5000;
        PreferredSubtitleLanguages = "en";
    }

    /// <summary>
    /// Gets or sets a value indicating whether Look it up is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the Wikipedia language code used for lookups (e.g. en, fr, de).
    /// </summary>
    public string WikipediaLanguage { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of annotations stored per media item.
    /// </summary>
    public int MaxAnnotationsPerItem { get; set; }

    /// <summary>
    /// Gets or sets the minimum character length for a candidate entity name.
    /// </summary>
    public int MinEntityLength { get; set; }

    /// <summary>
    /// Gets or sets how long a popup stays visible in milliseconds.
    /// </summary>
    public int PopupDurationMs { get; set; }

    /// <summary>
    /// Gets or sets a comma-separated list of preferred subtitle language codes.
    /// </summary>
    public string PreferredSubtitleLanguages { get; set; }
}
