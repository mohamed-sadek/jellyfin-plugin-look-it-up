using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.LookItUp.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LookItUp;

/// <summary>
/// Look it up — timed subtitle context popups for Jellyfin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        RestorePopupAppearance();
    }

    /// <inheritdoc />
    public override string Name => "Look it up";

    /// <inheritdoc />
    public override string Description =>
        "Scans subtitles for names and shows short Wikipedia explanations during playback.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a8ab0fed-cac9-406d-b98b-58161bf970b8");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            },
            new PluginPageInfo
            {
                Name = "LookItUpPrepare",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.preparePage.html",
                    GetType().Namespace),
                EnableInMainMenu = false
            }
        ];
    }

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration incoming)
        {
            PreserveSecret(incoming, Configuration);
            PopupAppearanceStore.Save(ApplicationPaths, incoming);
        }

        base.UpdateConfiguration(configuration);
    }

    private void RestorePopupAppearance()
    {
        var config = Configuration;
        if (PopupAppearanceStore.TryRestore(ApplicationPaths, config))
        {
            SaveConfiguration(config);
            return;
        }

        // First run of this backup: keep whatever XML still has so the next update can restore it.
        PopupAppearanceStore.Save(ApplicationPaths, config);
    }

    private static void PreserveSecret(PluginConfiguration incoming, PluginConfiguration existing)
    {
        if (string.IsNullOrWhiteSpace(incoming.AiApiKey)
            && !string.IsNullOrWhiteSpace(existing.AiApiKey))
        {
            incoming.AiApiKey = existing.AiApiKey;
        }

        if (string.IsNullOrWhiteSpace(incoming.OpenSubtitlesPassword)
            && !string.IsNullOrWhiteSpace(existing.OpenSubtitlesPassword))
        {
            incoming.OpenSubtitlesPassword = existing.OpenSubtitlesPassword;
        }

        if (string.IsNullOrWhiteSpace(incoming.OpenSubtitlesApiKey)
            && !string.IsNullOrWhiteSpace(existing.OpenSubtitlesApiKey))
        {
            incoming.OpenSubtitlesApiKey = existing.OpenSubtitlesApiKey;
        }
    }
}
