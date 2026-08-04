using Jellyfin.Plugin.LookItUp.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LookItUp;

/// <summary>
/// Registers Look it up services with Jellyfin's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ISubtitleParser, SubtitleParser>();
        serviceCollection.AddSingleton<IEntityExtractor, EntityExtractor>();
        serviceCollection.AddSingleton<IWikipediaLookupService, WikipediaLookupService>();
        serviceCollection.AddSingleton<IAnnotationStore, AnnotationStore>();
        serviceCollection.AddSingleton<ILookItUpService, LookItUpService>();
        serviceCollection.AddHttpClient(nameof(WikipediaLookupService), client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Jellyfin.Plugin.LookItUp/1.0 (https://github.com/mohamed-sadek/jellyfin-plugin-look-it-up)");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
    }
}
