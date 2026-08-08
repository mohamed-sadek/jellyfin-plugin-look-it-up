using System.Collections.Concurrent;
using System.Text.Json;
using Jellyfin.Plugin.LookItUp.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Persists scanned annotations per media item.
/// </summary>
public interface IAnnotationStore
{
    /// <summary>
    /// Tries to get a cached scan for an item.
    /// </summary>
    /// <param name="itemId">Media item id.</param>
    /// <returns>Cached entry, or null.</returns>
    ItemAnnotationCache? Get(Guid itemId);

    /// <summary>
    /// Saves a scan result.
    /// </summary>
    /// <param name="cache">Cache entry.</param>
    void Save(ItemAnnotationCache cache);

    /// <summary>
    /// Removes a cached scan.
    /// </summary>
    /// <param name="itemId">Media item id.</param>
    void Remove(Guid itemId);

    /// <summary>
    /// Deletes every cache entry (memory + on-disk JSON files).
    /// </summary>
    /// <returns>Number of cache files removed.</returns>
    int ClearAll();
}

/// <summary>
/// JSON file-backed annotation cache under the plugin data folder.
/// </summary>
public class AnnotationStore : IAnnotationStore
{
    private readonly ILogger<AnnotationStore> _logger;
    private readonly string _directory;
    private readonly ConcurrentDictionary<Guid, ItemAnnotationCache> _memory = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Initializes a new instance of the <see cref="AnnotationStore"/> class.
    /// </summary>
    /// <param name="appPaths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public AnnotationStore(IApplicationPaths appPaths, ILogger<AnnotationStore> logger)
    {
        _logger = logger;
        var root = appPaths.PluginConfigurationsPath
                   ?? appPaths.ProgramDataPath
                   ?? Path.GetTempPath();
        _directory = Path.Combine(root, "LookItUp", "cache");
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create Look it up cache directory at {Directory}", _directory);
        }
    }

    /// <inheritdoc />
    public ItemAnnotationCache? Get(Guid itemId)
    {
        if (_memory.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        var path = GetPath(itemId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var entry = JsonSerializer.Deserialize<ItemAnnotationCache>(json);
            if (entry is not null)
            {
                _memory[itemId] = entry;
            }

            return entry;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Look it up cache for {ItemId}", itemId);
            return null;
        }
    }

    /// <inheritdoc />
    public void Save(ItemAnnotationCache cache)
    {
        _memory[cache.ItemId] = cache;
        var path = GetPath(cache.ItemId);
        try
        {
            var json = JsonSerializer.Serialize(cache, _jsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write Look it up cache for {ItemId}", cache.ItemId);
        }
    }

    /// <inheritdoc />
    public void Remove(Guid itemId)
    {
        _memory.TryRemove(itemId, out _);
        var path = GetPath(itemId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <inheritdoc />
    public int ClearAll()
    {
        _memory.Clear();
        var removed = 0;
        try
        {
            if (!Directory.Exists(_directory))
            {
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete Look it up cache file {Path}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed clearing Look it up cache directory {Dir}", _directory);
        }

        return removed;
    }

    private string GetPath(Guid itemId) => Path.Combine(_directory, $"{itemId:N}.json");
}
