namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// Counts of files removed by a clear-generated-data request.
/// </summary>
public sealed class ClearGeneratedDataResult
{
    /// <summary>Plugin cache JSON files under LookItUp/cache.</summary>
    public int CacheFilesDeleted { get; set; }

    /// <summary>Extracted embedded subtitle cache files.</summary>
    public int SubtitleCacheFilesDeleted { get; set; }

    /// <summary>Downloaded OpenSubtitles files.</summary>
    public int OpenSubtitlesFilesDeleted { get; set; }

    /// <summary>Media-folder *.lookitup.json sidecars.</summary>
    public int SidecarFilesDeleted { get; set; }

    /// <summary>Whether prepare-queue.json was cleared.</summary>
    public bool PrepareQueueCleared { get; set; }

    /// <summary>Whether a running prepare job was asked to stop.</summary>
    public bool PrepareJobCancelled { get; set; }

    /// <summary>Total files deleted (cache + subtitle + opensubtitles + sidecars).</summary>
    public int TotalFilesDeleted =>
        CacheFilesDeleted + SubtitleCacheFilesDeleted + OpenSubtitlesFilesDeleted + SidecarFilesDeleted;
}
