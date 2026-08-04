using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LookItUp.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Precomputes Look it up annotations for library items (offline prepare).
/// </summary>
public interface ILookItUpPrepareService
{
    /// <summary>
    /// Gets the latest prepare job status.
    /// </summary>
    PrepareStatus GetStatus();

    /// <summary>
    /// Starts a background library prepare if none is running.
    /// </summary>
    /// <param name="force">When true, re-prepare items that already have cache.</param>
    /// <returns>True if a new job was started.</returns>
    bool TryStartLibraryPrepare(bool force);

    /// <summary>
    /// Cancels a running library prepare job, if any.
    /// </summary>
    /// <returns>True if a job was cancelled.</returns>
    bool TryCancelLibraryPrepare();

    /// <summary>
    /// Prepares a single item synchronously (for API / scheduled task item loops).
    /// </summary>
    Task<ItemAnnotationCache?> PrepareItemAsync(Guid itemId, bool force, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a full library prepare (used by the scheduled task).
    /// </summary>
    Task RunLibraryPrepareAsync(bool force, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Coordinates offline prepare jobs across movies/episodes.
/// </summary>
public class LookItUpPrepareService : ILookItUpPrepareService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILookItUpService _lookItUpService;
    private readonly ILogger<LookItUpPrepareService> _logger;
    private readonly object _gate = new();
    private PrepareStatus _status = new();
    private CancellationTokenSource? _cts;
    private Task? _running;

    /// <summary>
    /// Initializes a new instance of the <see cref="LookItUpPrepareService"/> class.
    /// </summary>
    public LookItUpPrepareService(
        ILibraryManager libraryManager,
        ILookItUpService lookItUpService,
        ILogger<LookItUpPrepareService> logger)
    {
        _libraryManager = libraryManager;
        _lookItUpService = lookItUpService;
        _logger = logger;
    }

    /// <inheritdoc />
    public PrepareStatus GetStatus()
    {
        lock (_gate)
        {
            return new PrepareStatus
            {
                IsRunning = _status.IsRunning,
                Total = _status.Total,
                Completed = _status.Completed,
                WithAnnotations = _status.WithAnnotations,
                Skipped = _status.Skipped,
                Failed = _status.Failed,
                CurrentItem = _status.CurrentItem,
                LastError = _status.LastError,
                StartedAtUtc = _status.StartedAtUtc,
                FinishedAtUtc = _status.FinishedAtUtc
            };
        }
    }

    /// <inheritdoc />
    public bool TryStartLibraryPrepare(bool force)
    {
        lock (_gate)
        {
            if (_status.IsRunning)
            {
                return false;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _running = Task.Run(async () =>
            {
                try
                {
                    await RunLibraryPrepareAsync(force, progress: null, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Look it up library prepare cancelled");
                    lock (_gate)
                    {
                        _status.IsRunning = false;
                        _status.CurrentItem = null;
                        _status.FinishedAtUtc = DateTime.UtcNow;
                        _status.LastError = "Cancelled by user";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Look it up library prepare failed");
                    lock (_gate)
                    {
                        _status.LastError = ex.Message;
                        _status.IsRunning = false;
                        _status.FinishedAtUtc = DateTime.UtcNow;
                    }
                }
            }, token);

            return true;
        }
    }

    /// <inheritdoc />
    public bool TryCancelLibraryPrepare()
    {
        lock (_gate)
        {
            if (!_status.IsRunning || _cts is null)
            {
                return false;
            }

            _logger.LogInformation("Look it up library prepare cancel requested");
            _cts.Cancel();
            _status.LastError = "Cancelled by user";
            return true;
        }
    }

    /// <inheritdoc />
    public Task<ItemAnnotationCache?> PrepareItemAsync(Guid itemId, bool force, CancellationToken cancellationToken)
        => _lookItUpService.PrepareItemAsync(itemId, force, cancellationToken);

    /// <inheritdoc />
    public async Task RunLibraryPrepareAsync(bool force, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled)
        {
            _logger.LogInformation("Look it up prepare skipped — plugin disabled");
            return;
        }

        var kinds = new List<BaseItemKind>();
        if (config.PrepareMovies)
        {
            kinds.Add(BaseItemKind.Movie);
        }

        if (config.PrepareEpisodes)
        {
            kinds.Add(BaseItemKind.Episode);
        }

        if (kinds.Count == 0)
        {
            _logger.LogWarning("Look it up prepare: no item types enabled");
            return;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = kinds.ToArray(),
            IsVirtualItem = false,
            MediaTypes = [MediaType.Video]
        })
        .Where(i => !string.IsNullOrWhiteSpace(i.Path))
        .ToList();

        lock (_gate)
        {
            _status = new PrepareStatus
            {
                IsRunning = true,
                Total = items.Count,
                StartedAtUtc = DateTime.UtcNow
            };
        }

        _logger.LogInformation("Look it up prepare starting for {Count} items (force={Force})", items.Count, force);

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];

            lock (_gate)
            {
                _status.CurrentItem = item.Name;
            }

            try
            {
                var skipExisting = config.SkipAlreadyPrepared && !force;
                if (skipExisting && _lookItUpService.TryGetPrepared(item.Id, out _))
                {
                    lock (_gate)
                    {
                        _status.Completed = i + 1;
                        _status.Skipped++;
                    }

                    progress?.Report(100.0 * (i + 1) / items.Count);
                    continue;
                }

                var cache = await _lookItUpService
                    .PrepareItemAsync(item.Id, force: true, cancellationToken)
                    .ConfigureAwait(false);

                lock (_gate)
                {
                    _status.Completed = i + 1;
                    if (cache is null)
                    {
                        _status.Failed++;
                    }
                    else if (cache.Annotations.Count == 0)
                    {
                        _status.Skipped++;
                    }
                    else
                    {
                        _status.WithAnnotations++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _status.IsRunning = false;
                    _status.CurrentItem = null;
                    _status.FinishedAtUtc = DateTime.UtcNow;
                    _status.LastError = "Cancelled by user";
                }

                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prepare failed for {Item}", item.Name);
                lock (_gate)
                {
                    _status.Completed = i + 1;
                    _status.Failed++;
                    _status.LastError = ex.Message;
                }
            }

            progress?.Report(100.0 * (i + 1) / items.Count);
        }

        lock (_gate)
        {
            _status.IsRunning = false;
            _status.CurrentItem = null;
            _status.FinishedAtUtc = DateTime.UtcNow;
            _status.Completed = items.Count;
        }

        _logger.LogInformation(
            "Look it up prepare finished: {With} with annotations, {Skipped} skipped, {Failed} failed of {Total}",
            _status.WithAnnotations,
            _status.Skipped,
            _status.Failed,
            items.Count);
    }
}
