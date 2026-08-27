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
            var current = Configuration;
            // Config UI leaves secret fields blank on purpose; never wipe saved credentials.
            PreserveIfBlank(incoming.AiApiKey, current.AiApiKey, v => incoming.AiApiKey = v);
            PreserveIfBlank(incoming.OpenSubtitlesPassword, current.OpenSubtitlesPassword, v => incoming.OpenSubtitlesPassword = v);
            PreserveIfBlank(incoming.OpenSubtitlesApiKey, current.OpenSubtitlesApiKey, v => incoming.OpenSubtitlesApiKey = v);
        }

        base.UpdateConfiguration(configuration);
    }

    private static void PreserveIfBlank(string? incoming, string? existing, Action<string> set)
    {
        if (string.IsNullOrWhiteSpace(incoming) && !string.IsNullOrWhiteSpace(existing))
        {
            set(existing);
        }
    }
}
