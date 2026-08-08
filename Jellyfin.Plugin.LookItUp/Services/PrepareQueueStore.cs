using System.Text.Json;
using Jellyfin.Plugin.LookItUp.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Persists the overnight prepare pending/failed queue.
/// </summary>
public interface IPrepareQueueStore
{
    /// <summary>Loads the queue from disk.</summary>
    PrepareQueueState Load();

    /// <summary>Saves the queue to disk.</summary>
    void Save(PrepareQueueState state);

    /// <summary>Deletes the queue file and resets to empty.</summary>
    void Clear();
}

/// <summary>
/// JSON file-backed prepare queue under the plugin data folder.
/// </summary>
public sealed class PrepareQueueStore : IPrepareQueueStore
{
    private readonly ILogger<PrepareQueueStore> _logger;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    /// <summary>
    /// Initializes a new instance of the <see cref="PrepareQueueStore"/> class.
    /// </summary>
    public PrepareQueueStore(IApplicationPaths appPaths, ILogger<PrepareQueueStore> logger)
    {
        _logger = logger;
        var root = appPaths.PluginConfigurationsPath
                   ?? appPaths.ProgramDataPath
                   ?? Path.GetTempPath();
        var dir = Path.Combine(root, "LookItUp");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "prepare-queue.json");
    }

    /// <inheritdoc />
    public PrepareQueueState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new PrepareQueueState();
            }

            try
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<PrepareQueueState>(json) ?? new PrepareQueueState();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read prepare queue");
                return new PrepareQueueState();
            }
        }
    }

    /// <inheritdoc />
    public void Save(PrepareQueueState state)
    {
        lock (_gate)
        {
            state.UpdatedAtUtc = DateTime.UtcNow;
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(state, _json));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write prepare queue");
            }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete prepare queue at {Path}", _path);
            }
        }
    }
}
