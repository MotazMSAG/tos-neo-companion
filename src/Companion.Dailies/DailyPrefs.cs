namespace Companion.Dailies;

/// <summary>One item the user added themselves (ticked by hand only).</summary>
public sealed record DailyCustomItem
{
    /// <summary>Stable id ('own1').</summary>
    public string Id { get; init; } = "";

    /// <summary>The user's name for it.</summary>
    public string Name { get; init; } = "";
}

/// <summary>
/// The user's choices from "Edit my list" (RuntimeDir\dailies\prefs.json): which items are on the list, their names and
/// the user's own items. Anything not mentioned keeps the catalog's default.
/// </summary>
public sealed record DailyPrefs
{
    /// <summary>Schema version (1).</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Shown (true) or hidden (false) per item id; absent = the catalog's default.</summary>
    public IReadOnlyDictionary<string, bool> Shown { get; init; } = new Dictionary<string, bool>();

    /// <summary>The user's names per item id; absent = the catalog's name.</summary>
    public IReadOnlyDictionary<string, string> Names { get; init; } = new Dictionary<string, string>();

    /// <summary>The user's own items.</summary>
    public IReadOnlyList<DailyCustomItem> Custom { get; init; } = [];

    /// <summary>True when <paramref name="item"/> is on the list.</summary>
    public bool IsShown(DailyItemView item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Shown.TryGetValue(item.Id, out var shown) ? shown : item.Shown;
    }

    /// <summary>The name to show for <paramref name="item"/>.</summary>
    public string NameOf(DailyItemView item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Names.TryGetValue(item.Id, out var name) && !string.IsNullOrWhiteSpace(name) ? name.Trim() : item.Name;
    }
}
