using System;
using System.Collections.Generic;
using System.Linq;

namespace DebugPlus.Common;

public static class BookmarkStore
{
    public static IEnumerable<TeleportBookmark> ForMap(IEnumerable<TeleportBookmark> bookmarks, string mapId) =>
        bookmarks.Where(b => b != null && !string.IsNullOrWhiteSpace(mapId) && b.MapId == mapId);

    public static void Save(List<TeleportBookmark> bookmarks, TeleportBookmark bookmark)
    {
        if (bookmark == null || string.IsNullOrWhiteSpace(bookmark.MapId) || string.IsNullOrWhiteSpace(bookmark.Name)
            || bookmark.MapId.Length > 128 || bookmark.Name.Length > 100
            || !new[] { bookmark.X, bookmark.Y, bookmark.Z, bookmark.Yaw }.All(Commands.Finite))
            throw new ArgumentException("Bookmark needs a name, map, and valid coordinates.");
        bookmarks.RemoveAll(b => b.MapId == bookmark.MapId && b.Name == bookmark.Name);
        bookmarks.Add(bookmark);
    }
}
