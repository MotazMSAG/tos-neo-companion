namespace Companion.Dailies;

/// <summary>
/// Homestead producer cards (learning day, entries 24/25/39/41/42; Lumi's Hut and the helper). Rules, as the user set
/// them:
/// <list type="bullet">
/// <item>only queueing (each opening of the make dialog) starts or resets the countdown: <b>Making</b>;</item>
/// <item>the countdown reaching zero makes it <b>Ready</b> (one notification);</item>
/// <item>opened while Ready and closed with fewer than all items taken (and nothing queued): <b>Making</b> again, a
/// fresh countdown ("probably nothing is done yet");</item>
/// <item>all items taken and nothing queued: <b>Empty</b>. The lumi shows each item as an item toast while its screen
/// is open; the helper logs one line per slot (2026-11-04: slots 3, 4, 6, 1, 1, 2, 5 = all six).</item>
/// </list>
/// Collecting never touches a running countdown. Not thread-safe (owned by <see cref="DailyTracker"/>).
/// </summary>
internal sealed class DailyProducers
{
    private readonly List<DailyItemDef> _defs;
    private readonly Dictionary<string, Run> _runs = new(StringComparer.Ordinal);

    public DailyProducers(IEnumerable<DailyItemDef> producers)
    {
        _defs = [.. producers];
        foreach (var d in _defs)
        {
            _runs[d.Id] = new Run();
        }
    }

    /// <summary>A screen opened.</summary>
    public bool OnShow(string ui, DateTime t)
    {
        var changed = false;
        foreach (var d in _defs)
        {
            var p = d.Producer!;
            var r = _runs[d.Id];
            if (ui == p.QueueScreen)
            {
                r.State = ProducerState.Making;
                r.Until = t.AddMinutes(p.Minutes);
                r.Slots.Clear();
                r.QueuedInView = r.ViewOpen;
                changed = true;
            }
            else if (ui == p.ViewScreen && !r.ViewOpen)
            {
                r.ViewOpen = true;
                r.Toasts = 0;
                r.QueuedInView = false;
            }
        }

        return changed;
    }

    /// <summary>A screen closed.</summary>
    public bool OnClose(string ui, DateTime t)
    {
        var changed = false;
        foreach (var d in _defs)
        {
            var p = d.Producer!;
            var r = _runs[d.Id];
            if (ui != p.ViewScreen || !r.ViewOpen)
            {
                continue;
            }

            r.ViewOpen = false;
            if (r.QueuedInView)
            {
                continue;
            }

            var allTaken = p.Collect == "slots" ? r.Slots.Count >= p.Items : r.Toasts >= p.Items;
            if (allTaken)
            {
                r.State = ProducerState.Empty;
                r.Until = null;
                r.Slots.Clear();
                changed = true;
            }
            else if (StateAt(r, t) == ProducerState.Ready)
            {
                r.State = ProducerState.Making;
                r.Until = t.AddMinutes(p.Minutes);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>An item toast (the lumi shows one per item taken).</summary>
    public void OnToast()
    {
        foreach (var d in _defs.Where(d => d.Producer!.Collect == "toasts"))
        {
            var r = _runs[d.Id];
            if (r.ViewOpen)
            {
                r.Toasts++;
            }
        }
    }

    /// <summary>A helper slot taken.</summary>
    public void OnSlot(int slot)
    {
        foreach (var d in _defs.Where(d => d.Producer!.Collect == "slots"))
        {
            _runs[d.Id].Slots.Add(slot);
        }
    }

    /// <summary>The cards at <paramref name="now"/>.</summary>
    public IReadOnlyList<DailyProducerView> Views(DateTime now) =>
        [.. _defs.Select(d =>
        {
            var r = _runs[d.Id];
            return new DailyProducerView(d.Id, d.Name, StateAt(r, now), r.Until, d.How);
        })];

    /// <summary>Producers whose countdown ended in (<paramref name="from"/>, <paramref name="to"/>].</summary>
    public IEnumerable<DailyItemDef> BecameReady(DateTime from, DateTime to) =>
        _defs.Where(d => _runs[d.Id] is { State: ProducerState.Making, Until: { } u } && u > from && u <= to);

    public IEnumerable<DailyProducerDto> Export() =>
        _runs.Select(kv => new DailyProducerDto { Item = kv.Key, State = kv.Value.State, Until = kv.Value.Until, Slots = [.. kv.Value.Slots] });

    public void Import(DailyProducerDto dto)
    {
        if (_runs.TryGetValue(dto.Item, out var r))
        {
            r.State = dto.State;
            r.Until = dto.Until;
            r.Slots.Clear();
            r.Slots.UnionWith(dto.Slots);
        }
    }

    private static ProducerState StateAt(Run r, DateTime now) =>
        r.State == ProducerState.Making && r.Until is { } u && now >= u ? ProducerState.Ready : r.State;

    private sealed class Run
    {
        public ProducerState State { get; set; }

        public DateTime? Until { get; set; }

        public bool ViewOpen { get; set; }

        public bool QueuedInView { get; set; }

        public int Toasts { get; set; }

        public HashSet<int> Slots { get; } = [];
    }
}

/// <summary>One saved producer card.</summary>
public sealed record DailyProducerDto
{
    /// <summary>Item id.</summary>
    public string Item { get; init; } = "";

    /// <summary>State (Ready is derived from Making and the time).</summary>
    public ProducerState State { get; init; }

    /// <summary>End of the countdown.</summary>
    public DateTime? Until { get; init; }

    /// <summary>Helper slots taken since the last queue.</summary>
    public IReadOnlyList<int> Slots { get; init; } = [];
}
