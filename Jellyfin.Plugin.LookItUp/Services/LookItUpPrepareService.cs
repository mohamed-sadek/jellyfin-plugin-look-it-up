using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LookItUp.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
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
    /// Starts a background prepare for a Series, Season, Episode, or Movie.
    /// </summary>
    /// <param name="rootItemId">Library item id (series preferred).</param>
    /// <param name="force">When true, re-prepare items that already have cache.</param>
    /// <param name="error">Human-readable error when start fails.</param>
    /// <returns>True if a new job was started.</returns>
    bool TryStartScopedPrepare(Guid rootItemId, bool force, out string? error);

    /// <summary>
    /// Starts a background prepare for explicitly selected terms per item.
    /// </summary>
    bool TryStartSelectedPrepare(PrepareSelectedRequest request, out string? error);

    /// <summary>
    /// Cancels a running library prepare job, if any.
    /// </summary>
    /// <returns>True if a job was cancelled.</returns>
    bool TryCancelLibraryPrepare();

    /// <summary>
    /// Prepares a single item synchronously (for API / scheduled task item loops).
    /// </summary>
    Task<PrepareItemResult> PrepareItemAsync(
        Guid itemId,
        bool force,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? selectedTerms = null);

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
    private readonly IPrepareQueueStore _queueStore;
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
        IPrepareQueueStore queueStore,
        ILogger<LookItUpPrepareService> logger)
    {
        _libraryManager = libraryManager;
        _lookItUpService = lookItUpService;
        _queueStore = queueStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public PrepareStatus GetStatus()
    {
        var queue = _queueStore.Load();
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
                FinishedAtUtc = _status.FinishedAtUtc,
                QueuePending = queue.Pending.Count,
                QueueFailed = queue.Failed.Count,
                OpenSubtitlesDownloads = _status.OpenSubtitlesDownloads,
                StatusNote = _status.StatusNote
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
    public bool TryStartScopedPrepare(Guid rootItemId, bool force, out string? error)
    {
        error = null;
        var root = _libraryManager.GetItemById(rootItemId);
        if (root is null)
        {
            error = "Item not found.";
            return false;
        }

        List<BaseItem> items;
        try
        {
            items = ResolvePrepareTargets(root);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (items.Count == 0)
        {
            error = "No episodes/movies found under this item.";
            return false;
        }

        lock (_gate)
        {
            if (_status.IsRunning)
            {
                error = "A prepare job is already running.";
                return false;
            }

            var scopeLabel = root.Name ?? rootItemId.ToString("N");
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _running = Task.Run(async () =>
            {
                try
                {
                    await RunPrepareForItemsAsync(items, force, scopeLabel, progress: null, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Look it up scoped prepare cancelled ({Scope})", scopeLabel);
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
                    _logger.LogError(ex, "Look it up scoped prepare failed ({Scope})", scopeLabel);
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
    public bool TryStartSelectedPrepare(PrepareSelectedRequest request, out string? error)
    {
        error = null;
        if (request?.Items is null || request.Items.Count == 0)
        {
            error = "No items selected.";
            return false;
        }

        var work = request.Items
            .Where(i => i.ItemId != Guid.Empty && i.Terms is { Count: > 0 })
            .Select(i => (
                ItemId: i.ItemId,
                Terms: (IReadOnlyList<string>)i.Terms
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .Where(i => i.Terms.Count > 0)
            .ToList();

        if (work.Count == 0)
        {
            error = "No terms selected.";
            return false;
        }

        lock (_gate)
        {
            if (_status.IsRunning)
            {
                error = "A prepare job is already running.";
                return false;
            }

            var force = request.Force;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _running = Task.Run(async () =>
            {
                try
                {
                    await RunSelectedPrepareAsync(work, force, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Look it up selected prepare cancelled");
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
                    _logger.LogError(ex, "Look it up selected prepare failed");
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

    private async Task RunSelectedPrepareAsync(
        IReadOnlyList<(Guid ItemId, IReadOnlyList<string> Terms)> work,
        bool force,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _status = new PrepareStatus
            {
                IsRunning = true,
                Total = work.Count,
                StartedAtUtc = DateTime.UtcNow,
                CurrentItem = "selected terms"
            };
        }

        _logger.LogInformation(
            "Look it up selected prepare starting for {Count} items (force={Force})",
            work.Count,
            force);

        for (var i = 0; i < work.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (itemId, terms) = work[i];
            var item = _libraryManager.GetItemById(itemId);
            var label = item?.Name ?? itemId.ToString("N");

            lock (_gate)
            {
                _status.CurrentItem = label + " (" + terms.Count + " names)";
            }

            try
            {
                var result = await _lookItUpService
                    .PrepareItemAsync(itemId, force, cancellationToken, terms)
                    .ConfigureAwait(false);

                lock (_gate)
                {
                    _status.Completed = i + 1;
                    if (result.Cache is null)
                    {
                        _status.Failed++;
                        if (!string.IsNullOrWhiteSpace(result.Warning))
                        {
                            _status.LastError = result.Warning;
                        }
                    }
                    else if (result.Cache.Annotations.Count == 0)
                    {
                        _status.Skipped++;
                        if (!string.IsNullOrWhiteSpace(result.Warning))
                        {
                            _status.LastError = result.Warning;
                        }
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
                _logger.LogWarning(ex, "Selected prepare failed for {Item}", label);
                lock (_gate)
                {
                    _status.Completed = i + 1;
                    _status.Failed++;
                    _status.LastError = ex.Message;
                }
            }
        }

        lock (_gate)
        {
            _status.IsRunning = false;
            _status.CurrentItem = null;
            _status.FinishedAtUtc = DateTime.UtcNow;
            _status.Completed = work.Count;
        }

        _logger.LogInformation(
            "Look it up selected prepare finished: {With} with annotations, {Skipped} skipped, {Failed} failed of {Total}",
            _status.WithAnnotations,
            _status.Skipped,
            _status.Failed,
            work.Count);
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

            _logger.LogInformation("Look it up prepare cancel requested");
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // ignored
            }

            _status.IsRunning = false;
            _status.CurrentItem = null;
            _status.FinishedAtUtc = DateTime.UtcNow;
            _status.LastError = "Cancelled by user";
            return true;
        }
    }

    /// <inheritdoc />
    public Task<PrepareItemResult> PrepareItemAsync(
        Guid itemId,
        bool force,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? selectedTerms = null)
        => _lookItUpService.PrepareItemAsync(itemId, force, cancellationToken, selectedTerms);

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

        await RunPrepareForItemsAsync(items, force, "library", progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunPrepareForItemsAsync(
        IReadOnlyList<BaseItem> items,
        bool force,
        string scopeLabel,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled)
        {
            _logger.LogInformation("Look it up prepare skipped — plugin disabled");
            return;
        }

        var maxRetries = Math.Max(1, config.PrepareMaxRetries);
        var delayMs = Math.Max(0, config.PrepareDelayMsBetweenItems);
        var queue = _queueStore.Load();

        // Drain due retries first (only within this scope), then remaining scope items.
        var scopeIds = items.Select(i => i.Id).ToHashSet();
        var dueRetries = queue.Failed
            .Where(f => f.NextRetryUtc <= DateTime.UtcNow
                        && f.Attempts < maxRetries
                        && scopeIds.Contains(f.ItemId))
            .Select(f => f.ItemId)
            .Distinct()
            .ToList();
        var remaining = items.Select(i => i.Id).Where(id => !dueRetries.Contains(id));
        var workIds = dueRetries.Concat(remaining).ToList();

        queue.Pending = workIds.ToList();
        _queueStore.Save(queue);

        lock (_gate)
        {
            _status = new PrepareStatus
            {
                IsRunning = true,
                Total = workIds.Count,
                StartedAtUtc = DateTime.UtcNow,
                CurrentItem = scopeLabel,
                QueuePending = queue.Pending.Count,
                QueueFailed = queue.Failed.Count
            };
        }

        _logger.LogInformation(
            "Look it up prepare starting for {Count} items in scope {Scope} (force={Force}, retriesDue={Retries})",
            workIds.Count,
            scopeLabel,
            force,
            dueRetries.Count);

        for (var i = 0; i < workIds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemId = workIds[i];
            var item = _libraryManager.GetItemById(itemId) ?? items.FirstOrDefault(x => x.Id == itemId);
            var itemName = item?.Name ?? itemId.ToString("N");

            lock (_gate)
            {
                _status.CurrentItem = itemName;
                _status.StatusNote = null;
                _status.QueuePending = Math.Max(0, workIds.Count - i);
            }

            try
            {
                var skipExisting = config.SkipAlreadyPrepared && !force;
                if (skipExisting
                    && _lookItUpService.TryGetPrepared(itemId, out var prepared)
                    && prepared is not null
                    && _lookItUpService.IsSuccessfullyPrepared(prepared))
                {
                    RemoveFromQueue(queue, itemId);
                    lock (_gate)
                    {
                        _status.Completed = i + 1;
                        _status.Skipped++;
                    }

                    progress?.Report(100.0 * (i + 1) / workIds.Count);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var result = await _lookItUpService
                    .PrepareItemAsync(itemId, force: true, cancellationToken)
                    .ConfigureAwait(false);

                lock (_gate)
                {
                    _status.Completed = i + 1;
                    if (result.Cache?.SubtitleSource == "opensubtitles")
                    {
                        _status.OpenSubtitlesDownloads++;
                    }

                    if (result.Cache is null
                        || string.Equals(result.Cache.PrepareOutcome, "failed", StringComparison.OrdinalIgnoreCase)
                        || (result.Cache.Annotations.Count == 0
                            && string.Equals(result.Cache.PrepareOutcome, "no-subtitles", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(result.Warning)
                            && result.Warning.Contains("rate", StringComparison.OrdinalIgnoreCase)))
                    {
                        _status.Failed++;
                        if (!string.IsNullOrWhiteSpace(result.Warning))
                        {
                            _status.LastError = result.Warning;
                        }

                        EnqueueFailure(queue, itemId, itemName, result.Warning ?? "prepare failed", maxRetries);
                    }
                    else if (result.Cache.Annotations.Count == 0)
                    {
                        RemoveFromQueue(queue, itemId);
                        _status.Skipped++;
                        if (!string.IsNullOrWhiteSpace(result.Warning))
                        {
                            _status.LastError = result.Warning;
                        }
                    }
                    else
                    {
                        RemoveFromQueue(queue, itemId);
                        _status.WithAnnotations++;
                    }
                }

                _queueStore.Save(queue);
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

                _queueStore.Save(queue);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prepare failed for {Item}", itemName);
                EnqueueFailure(queue, itemId, itemName, ex.Message, maxRetries);
                _queueStore.Save(queue);
                lock (_gate)
                {
                    _status.Completed = i + 1;
                    _status.Failed++;
                    _status.LastError = ex.Message;
                    if (ex is OpenSubtitlesRateLimitedException)
                    {
                        _status.StatusNote = "OpenSubtitles rate-limited; item queued for retry";
                    }
                }
            }

            progress?.Report(100.0 * (i + 1) / workIds.Count);
            if (delayMs > 0 && i < workIds.Count - 1)
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        queue.Pending.Clear();
        _queueStore.Save(queue);

        lock (_gate)
        {
            _status.IsRunning = false;
            _status.CurrentItem = null;
            _status.FinishedAtUtc = DateTime.UtcNow;
            _status.Completed = workIds.Count;
            _status.QueuePending = 0;
            _status.QueueFailed = queue.Failed.Count;
        }

        _logger.LogInformation(
            "Look it up prepare finished ({Scope}): {With} with annotations, {Skipped} skipped, {Failed} failed of {Total}; queueFailed={QueueFailed}",
            scopeLabel,
            _status.WithAnnotations,
            _status.Skipped,
            _status.Failed,
            workIds.Count,
            queue.Failed.Count);
    }

    private static void RemoveFromQueue(PrepareQueueState queue, Guid itemId)
    {
        queue.Pending.RemoveAll(id => id == itemId);
        queue.Failed.RemoveAll(f => f.ItemId == itemId);
    }

    private static void EnqueueFailure(
        PrepareQueueState queue,
        Guid itemId,
        string itemName,
        string error,
        int maxRetries)
    {
        queue.Pending.RemoveAll(id => id == itemId);
        var existing = queue.Failed.FirstOrDefault(f => f.ItemId == itemId);
        var attempts = (existing?.Attempts ?? 0) + 1;
        queue.Failed.RemoveAll(f => f.ItemId == itemId);
        if (attempts >= maxRetries)
        {
            // Keep a record but nextRetry far in the future so overnight won't spin forever.
            queue.Failed.Add(new PrepareQueueFailure
            {
                ItemId = itemId,
                ItemName = itemName,
                Attempts = attempts,
                LastError = error,
                NextRetryUtc = DateTime.UtcNow.AddDays(7)
            });
            return;
        }

        var backoffMinutes = Math.Min(120, (int)Math.Pow(2, Math.Min(attempts, 6)));
        queue.Failed.Add(new PrepareQueueFailure
        {
            ItemId = itemId,
            ItemName = itemName,
            Attempts = attempts,
            LastError = error,
            NextRetryUtc = DateTime.UtcNow.AddMinutes(backoffMinutes)
        });
    }

    private List<BaseItem> ResolvePrepareTargets(BaseItem root)
    {
        if (root is Movie or Episode)
        {
            return string.IsNullOrWhiteSpace(root.Path) ? [] : [root];
        }

        if (root is not Series and not Season)
        {
            throw new InvalidOperationException(
                "Look it up can prepare a Series, Season, Episode, or Movie from this endpoint.");
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [root.Id],
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
                MediaTypes = [MediaType.Video]
            })
            .Where(i => !string.IsNullOrWhiteSpace(i.Path))
            .OrderBy(i => i.ParentIndexNumber ?? 0)
            .ThenBy(i => i.IndexNumber ?? 0)
            .ThenBy(i => i.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
