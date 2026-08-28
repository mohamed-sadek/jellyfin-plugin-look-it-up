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
        serviceCollection.AddSingleton<IPhraseIndexStore, PhraseIndexStore>();
        serviceCollection.AddSingleton<IPhraseIndexScanner, PhraseIndexScanner>();
        serviceCollection.AddSingleton<IPhraseReferencePipeline, PhraseReferencePipeline>();
        serviceCollection.AddSingleton<INameCandidateFinder, NameCandidateFinder>();
        serviceCollection.AddSingleton<IWikipediaLookupService, WikipediaLookupService>();
        serviceCollection.AddSingleton<IReferenceGate, ReferenceGate>();
        serviceCollection.AddSingleton<IWikimediaReferenceResolver, WikimediaReferenceResolver>();
        serviceCollection.AddSingleton<IWikimediaReferencePipeline, WikimediaReferencePipeline>();
        serviceCollection.AddSingleton<IAiEntityExtractor, OpenAiCompatibleEntityExtractor>();
        serviceCollection.AddSingleton<IAiCallRateLimiter, AiCallRateLimiter>();
        serviceCollection.AddSingleton<IAiComplementService, AiComplementService>();
        serviceCollection.AddSingleton<IOpenSubtitlesClient, OpenSubtitlesClient>();
        serviceCollection.AddSingleton<IAnnotationStore, AnnotationStore>();
        serviceCollection.AddSingleton<IPrepareQueueStore, PrepareQueueStore>();
        serviceCollection.AddSingleton<IncrementalPrepareEngine>();
        serviceCollection.AddSingleton<ILookItUpService, LookItUpService>();
        serviceCollection.AddSingleton<ILookItUpPrepareService, LookItUpPrepareService>();
    }
}
