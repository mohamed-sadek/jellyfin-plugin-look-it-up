using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Resolves library items, including when the client accidentally sends a MediaSourceId.
/// </summary>
public static class LibraryItemResolver
{
    /// <summary>
    /// Finds a library item by id, falling back to a media-source id scan when needed.
    /// </summary>
    public static BaseItem? GetItem(ILibraryManager libraryManager, Guid id)
    {
        var item = libraryManager.GetItemById(id);
        if (item is not null)
        {
            return item;
        }

        return TryGetItemByMediaSourceId(libraryManager, id);
    }

    private static BaseItem? TryGetItemByMediaSourceId(ILibraryManager libraryManager, Guid mediaSourceId)
    {
        var idNoDash = mediaSourceId.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
        var idDashed = mediaSourceId.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        var query = new InternalItemsQuery
        {
            Recursive = true,
            MediaTypes = [MediaType.Video],
            IsVirtualItem = false,
            Limit = 5000
        };

        foreach (var candidate in libraryManager.GetItemList(query))
        {
            if (ItemHasMediaSourceId(candidate, idNoDash, idDashed))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool ItemHasMediaSourceId(BaseItem item, string mediaSourceIdNoDash, string mediaSourceIdDashed)
    {
        try
        {
            foreach (var source in item.GetMediaSources(false))
            {
                if (string.Equals(source.Id, mediaSourceIdNoDash, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(source.Id, mediaSourceIdDashed, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
