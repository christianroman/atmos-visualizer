// The last few files opened, for the welcome card. Each entry is what was chosen in Open… (a movie,
// a .atmos, or a .atmos with a movie), so reopening goes through the same path, and a prepared
// movie comes straight from the cache. Stored in PlayerPrefs (the registry on Windows).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public sealed class RecentItem
{
    public string Title;
    public string[] Paths;
}

public static class RecentFiles
{
    const string Key = "recent";
    const int Max = 5;

    /// <summary>Most recent first; entries whose files have gone are left out.</summary>
    public static List<RecentItem> Load()
    {
        var items = new List<RecentItem>();
        foreach (var line in PlayerPrefs.GetString(Key, "").Split('\n'))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            var paths = parts.Skip(1).Where(p => p.Length > 0).ToArray();
            if (paths.Length > 0 && paths.All(File.Exists)) items.Add(new RecentItem { Title = parts[0], Paths = paths });
        }
        return items;
    }

    public static void Add(string title, string[] paths)
    {
        if (paths == null || paths.Length == 0) return;
        var items = Load();
        items.RemoveAll(i => i.Paths.OrderBy(p => p).SequenceEqual(paths.OrderBy(p => p), StringComparer.OrdinalIgnoreCase));
        items.Insert(0, new RecentItem { Title = title, Paths = paths });
        PlayerPrefs.SetString(Key, string.Join("\n", items.Take(Max).Select(i => i.Title + "\t" + string.Join("\t", i.Paths))));
        PlayerPrefs.Save();
    }
}
