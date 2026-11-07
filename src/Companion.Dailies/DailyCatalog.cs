using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Companion.Dailies;

/// <summary>When an item's day ends (DAILIES_MODULE.md, resets).</summary>
public enum DailyReset
{
    /// <summary>The daily update (07:00 local in 2026-11): most dailies.</summary>
    Daily,

    /// <summary>The calendar day (02:00 local in 2026-11).</summary>
    Calendar,

    /// <summary>The weekly update (Monday 07:00 local in 2026-11).</summary>
    Weekly,
}

/// <summary>What an item is.</summary>
public enum DailyKind
{
    /// <summary>A chore on the list.</summary>
    Task,

    /// <summary>A timed event shown in "Coming up" (its rules tell whether the user joined).</summary>
    Event,

    /// <summary>A homestead producer card (Lumi's Hut, the helper): never ticked; empty, making (2 h) or ready.</summary>
    Producer,
}

/// <summary>One time an event is open: start (local, 'HH:mm') and length. Times before the daily reset belong to the night.</summary>
public sealed record DailyWindow
{
    /// <summary>Start, 'HH:mm' local.</summary>
    public string At { get; init; } = "";

    /// <summary>Length in minutes.</summary>
    public int Minutes { get; init; } = 20;
}

/// <summary>
/// A stored count that grows at resets and shrinks with use (learning day, entries 10/21/29): Hugo train requests
/// (+3 a day, up to 9), demon trial rewards (+2 on trial days, up to 4), Yggdrasil attempts (+1 a day, up to 5). In
/// 'fill' mode it grows with each hit instead (the special train: 6 fills, then it can be summoned).
/// </summary>
public sealed record DailyStackDef
{
    /// <summary>Added at each daily reset of an allowed day.</summary>
    public int PerReset { get; init; }

    /// <summary>Most it can hold.</summary>
    public int Cap { get; init; }

    /// <summary>Days whose reset adds (empty = every day), 'mon'..'sun'.</summary>
    public IReadOnlyList<string> ResetDays { get; init; } = [];

    /// <summary>'use' (a hit takes one) or 'fill' (a hit adds one).</summary>
    public string Mode { get; init; } = "use";

    /// <summary>Only due ("to do") when the stack holds at least this many; 0 = the item's own count decides.</summary>
    public int DueAt { get; init; }

    /// <summary>'fill' mode: minutes the summoned thing lasts after the user's tick (0 = no timer).</summary>
    public int TimerMinutes { get; init; }

    /// <summary>
    /// 'use' mode: a hit only counts while something is stored (Hugo train, 2026-11-05: 4 rewarded fills on each of two
    /// nights for 3 requests; the one past the stored requests is not a regular request).
    /// </summary>
    public bool Gate { get; init; }

    /// <summary>
    /// 'use' mode: the 'fill' item each counted hit also adds one to (Hugo train → special train). A gated hit with
    /// nothing stored while that item is ready (at its 'dueAt') or summoned is the special train itself being filled: it
    /// goes back to 0 and its timer ends (2026-11-05: 3 fills, the summon, a 4th fill; the game showed 0/9 and 0/6).
    /// </summary>
    public string? Feeds { get; init; }
}

/// <summary>A homestead producer (learning day, entries 24/25/39/41/42).</summary>
public sealed record DailyProducerDef
{
    /// <summary>The make dialog: each opening is one item queued (starts or resets the timer).</summary>
    public string QueueScreen { get; init; } = "";

    /// <summary>The producer's own screen (collecting happens here).</summary>
    public string ViewScreen { get; init; } = "";

    /// <summary>How taking an item shows: 'toasts' (an item toast while the screen is open) or 'slots' (the helper's slot lines).</summary>
    public string Collect { get; init; } = "toasts";

    /// <summary>Items it holds.</summary>
    public int Items { get; init; } = 6;

    /// <summary>The "go and check" countdown after queueing.</summary>
    public int Minutes { get; init; } = 120;
}

/// <summary>One list section.</summary>
/// <param name="Id">Section id ('day').</param>
/// <param name="Title">Shown title ('Every day').</param>
public sealed record DailySection(string Id, string Title);

/// <summary>
/// One detection rule (any-of within an item). <see cref="On"/> picks the trigger:
/// <list type="bullet">
/// <item>'reward': a reward pop-up while one of <see cref="Screens"/> is the newest open activity screen;</item>
/// <item>'shown': one of <see cref="Screens"/> opened;</item>
/// <item>'reply': the server refused <see cref="Request"/> (with <see cref="Code"/> when set);</item>
/// <item>'push': the server push <see cref="Name"/> ('module.notify_x'), optionally only with <see cref="Win"/>;</item>
/// <item>'map': a map whose name starts with <see cref="Prefix"/> finished loading;</item>
/// <item>'job': a companion job in <see cref="Jobs"/> finished in one of <see cref="States"/>;</item>
/// <item>'handIn': a reward pop-up on <see cref="Map"/> right after a quest commit containing <see cref="QuestContains"/>
/// or an NPC talk, not right after one of <see cref="NotAfterScreens"/>, confirmed by the throttled
/// <see cref="ConfirmRequest"/> (Kanu's Wish, 2026-11-04).</item>
/// </list>
/// <see cref="Full"/> fills the item's daily count at once ("no tries left"). Learning-day kinds (2026-11-04):
/// <list type="bullet">
/// <item>'settle': a results screen (UI_COMMON_SETTLE_VIEW) on a matching map / with a matching screen open / while a
/// switch is on / inside the item's window (see the map, screen and switch fields), optionally only when the run's
/// room_finish logged enough seconds (<see cref="FinishAtLeast"/>, demon trial reward runs);</item>
/// <item>'guide': the guide step <see cref="Key"/> finished; 'shopRefresh': shop <see cref="Shop"/> refreshed;</item>
/// <item>'presence': being on <see cref="Map"/> while the item's event is open (the guild spa);</item>
/// <item>'box': a reward pop-up within 5 s after a progress bar, soon after the item's <see cref="AfterPart"/> part
/// (the world boss box).</item>
/// <item>'openItem' (2026-11-05): an item opened from its bag tip: the tip of one of <see cref="Items"/> (or an id from
/// <see cref="ItemMin"/> to <see cref="ItemMax"/>), its close, and the results screen within 1 s of the close (the
/// Hyperweather loot boxes).</item>
/// </list>
/// </summary>
public sealed record DailyRule
{
    /// <summary>Trigger kind.</summary>
    public string On { get; init; } = "";

    /// <summary>Screens ('reward', 'shown').</summary>
    public IReadOnlyList<string> Screens { get; init; } = [];

    /// <summary>Request ('reply').</summary>
    public string? Request { get; init; }

    /// <summary>Error code ('reply'); null matches any code.</summary>
    public int? Code { get; init; }

    /// <summary>The match completes the item for its day.</summary>
    public bool Full { get; init; }

    /// <summary>Push name ('push').</summary>
    public string? Name { get; init; }

    /// <summary>Required win flag ('push'); null matches any.</summary>
    public bool? Win { get; init; }

    /// <summary>Map name prefix ('map').</summary>
    public string? Prefix { get; init; }

    /// <summary>Job names ('job').</summary>
    public IReadOnlyList<string> Jobs { get; init; } = [];

    /// <summary>Job end states ('job'); empty = Succeeded.</summary>
    public IReadOnlyList<string> States { get; init; } = [];

    /// <summary>Required current map ('handIn').</summary>
    public string? Map { get; init; }

    /// <summary>
    /// Text a qualifying quest commit's id contains ('handIn'). Kanu's quests are '10014' + 3 digits after a varying
    /// front: 110014042-110014056 (11-02, 11-04), 3310014006/008 (11-06; a "starts with 1100140" rule missed them).
    /// </summary>
    public string? QuestContains { get; init; }

    /// <summary>Longest gap after the quest commit ('handIn').</summary>
    public double QuestSeconds { get; init; } = 30;

    /// <summary>Longest gap after an NPC talk ('handIn'); 0 = NPC talks do not qualify.</summary>
    public double NpcTalkSeconds { get; init; }

    /// <summary>Screens (shops) after which a pop-up is a purchase, not a hand-in ('handIn').</summary>
    public IReadOnlyList<string> NotAfterScreens { get; init; } = [];

    /// <summary>How long after such a screen closes it still disqualifies ('handIn').</summary>
    public double NotAfterSeconds { get; init; } = 2;

    /// <summary>Throttled request that must follow or precede the pop-up ('handIn'); null = no confirmation.</summary>
    public string? ConfirmRequest { get; init; }

    /// <summary>Window for <see cref="ConfirmRequest"/>, either side of the pop-up.</summary>
    public double ConfirmSeconds { get; init; } = 1;

    /// <summary>Guide key ('guide').</summary>
    public string? Key { get; init; }

    /// <summary>Shop id ('shopRefresh').</summary>
    public int? Shop { get; init; }

    /// <summary>The current map must start with one of these ('settle').</summary>
    public IReadOnlyList<string> Maps { get; init; } = [];

    /// <summary>The current map must contain one of these ('settle').</summary>
    public IReadOnlyList<string> MapsContain { get; init; } = [];

    /// <summary>The current map must not start with any of these ('settle').</summary>
    public IReadOnlyList<string> NotMaps { get; init; } = [];

    /// <summary>The current map must not contain any of these ('settle').</summary>
    public IReadOnlyList<string> NotMapsContain { get; init; } = [];

    /// <summary>One of these screens must be open (opened within the context time and not closed) ('settle').</summary>
    public IReadOnlyList<string> ScreensOpen { get; init; } = [];

    /// <summary>None of these screens may be open ('settle').</summary>
    public IReadOnlyList<string> NotScreensOpen { get; init; } = [];

    /// <summary>One of these feature switches must be on ('settle').</summary>
    public IReadOnlyList<string> WhileOn { get; init; } = [];

    /// <summary>
    /// 'settle': counts only when the run's room_finish in the same map visit logged at least this many seconds, once
    /// per visit. Demon trial reward runs log 30 with loot drops, helping runs 3 with none (2026-11-06: 6 runs on 11-03,
    /// 2 on 11-05). The 4.35 rule "an item toast in the run" met a periodic 10-minute toast and missed a real reward run.
    /// </summary>
    public double? FinishAtLeast { get; init; }

    /// <summary>Only inside one of the item's windows ('settle').</summary>
    public bool InWindow { get; init; }

    /// <summary>Hits closer than this to the item's previous hit are the same moment (forge: pop-up + toast).</summary>
    public double GapSeconds { get; init; }

    /// <summary>The item part this rule ticks (items with <see cref="DailyItemDef.Parts"/>).</summary>
    public string? Part { get; init; }

    /// <summary>'box': the part that must come first, and how long before.</summary>
    public string? AfterPart { get; init; }

    /// <summary>'box': longest gap after <see cref="AfterPart"/>.</summary>
    public double AfterPartSeconds { get; init; } = 180;

    /// <summary>Empties the item's stack ("no attempts left").</summary>
    public bool Empties { get; init; }

    /// <summary>'reward': one of these screens must have closed just before the reward (the train's one-click fill dialog).</summary>
    public IReadOnlyList<string> AfterClose { get; init; } = [];

    /// <summary>'reward': longest gap after an <see cref="AfterClose"/> screen closed.</summary>
    public double AfterCloseSeconds { get; init; } = 1;

    /// <summary>'openItem': item ids whose opening counts (900001 = Escort Loot).</summary>
    public IReadOnlyList<long> Items { get; init; } = [];

    /// <summary>'openItem': also any id in this range (the gear rift boxes, one id per dungeon).</summary>
    public long? ItemMin { get; init; }

    /// <summary>'openItem': see <see cref="ItemMin"/>.</summary>
    public long? ItemMax { get; init; }

    /// <summary>'openItem': does this rule take <paramref name="itemId"/>.</summary>
    public bool TakesItem(long itemId) => Items.Contains(itemId) || (ItemMin is { } lo && ItemMax is { } hi && itemId >= lo && itemId <= hi);
}

/// <summary>One daily as the catalog defines it.</summary>
public sealed record DailyItemDef
{
    /// <summary>Stable id ('arena').</summary>
    public string Id { get; init; } = "";

    /// <summary>Default name ('Arena'); the user may rename it.</summary>
    public string Name { get; init; } = "";

    /// <summary>Section id.</summary>
    public string Section { get; init; } = "";

    /// <summary>How it is seen, for the user ('Rewards on the Arena screen').</summary>
    public string How { get; init; } = "";

    /// <summary>Times per day (escort 3); 0 = a counter with no target (Honor War rounds).</summary>
    public int PerDay { get; init; } = 1;

    /// <summary>When its day ends.</summary>
    public DailyReset Reset { get; init; } = DailyReset.Daily;

    /// <summary>On the list unless the user hides it.</summary>
    public bool Shown { get; init; } = true;

    /// <summary>Detection or reset still being confirmed (shown as "learning").</summary>
    public bool Learning { get; init; }

    /// <summary>Task or event.</summary>
    public DailyKind Kind { get; init; } = DailyKind.Task;

    /// <summary>Feature-switch instances that mean "open now" (events).</summary>
    public IReadOnlyList<string> Instances { get; init; } = [];

    /// <summary>Usual opening hours, shown when the game is closed ('21:59–22:19').</summary>
    public string? Usual { get; init; }

    /// <summary>Detection rules (any-of); empty = ticked by hand only.</summary>
    public IReadOnlyList<DailyRule> Rules { get; init; } = [];

    /// <summary>Game days it exists on ('mon'..'sun', the day the 07:00 reset starts); empty = every day.</summary>
    public IReadOnlyList<string> Days { get; init; } = [];

    /// <summary>Events: when it is open each day (local); empty = only the feature switch tells.</summary>
    public IReadOnlyList<DailyWindow> Windows { get; init; } = [];

    /// <summary>Named parts that must all be seen (world boss: fight + box); empty = counted.</summary>
    public IReadOnlyList<string> Parts { get; init; } = [];

    /// <summary>A stored count (null = none).</summary>
    public DailyStackDef? Stack { get; init; }

    /// <summary>
    /// Not on today's list while a feature switch whose name starts with one of these is on (seen switched on within the
    /// last 30 hours). Hyperweather days ('spec_weather_*' on) have no guild raccoons (2026-11-05: on 11-03 and 11-05
    /// with 'spec_weather_*' on, no raccoon round; on 11-02 and 11-04 without, all five).
    /// </summary>
    public IReadOnlyList<string> SkipWhileOn { get; init; } = [];

    /// <summary>
    /// Only on today's list while a feature switch whose name starts with one of these is on (the Hyperweather raccoon
    /// box, 2026-11-05). The opposite of <see cref="SkipWhileOn"/>.
    /// </summary>
    public IReadOnlyList<string> OnlyWhileOn { get; init; } = [];

    /// <summary>Producer cards (kind 'producer').</summary>
    public DailyProducerDef? Producer { get; init; }
}

/// <summary>
/// Reset times (local, as the game log writes time). In a shifted catalog (<see cref="DailyCatalog.Shifted"/>) the daily
/// and calendar times may lie before 00:00 or after 24:00, so a game day keeps its own date and weekday name (a PC at
/// UTC-5 starts Monday's game day at -01:00, Sunday 23:00); the weekly time is moved to the day it falls on instead.
/// </summary>
public sealed record DailyResets
{
    /// <summary>Daily update.</summary>
    public TimeSpan Daily { get; init; } = TimeSpan.FromHours(7);

    /// <summary>Calendar day.</summary>
    public TimeSpan Calendar { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Weekly update day.</summary>
    public DayOfWeek WeeklyDay { get; init; } = DayOfWeek.Monday;

    /// <summary>Weekly update time.</summary>
    public TimeSpan Weekly { get; init; } = TimeSpan.FromHours(7);
}

/// <summary>The checklist's data (data/dailies.json): reset times, screen classes, sections and items.</summary>
public sealed partial record DailyCatalog
{
    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>
    /// The UTC offset the file's times were written in ('+03:00', the default when absent). The service shifts every time
    /// to the PC's own offset, or by the shift it learned from the game log's daily update (<see cref="Shifted"/>).
    /// </summary>
    public string TimesUtcOffset { get; init; } = DefaultTimesUtcOffset;

    /// <summary><see cref="TimesUtcOffset"/> as a time span.</summary>
    [JsonIgnore]
    public TimeSpan TimesOffset => TryParseOffset(TimesUtcOffset, out var offset) ? offset : TimeSpan.FromHours(3);

    /// <summary>The shift already applied to this catalog's times (zero for the file as read).</summary>
    [JsonIgnore]
    public TimeSpan Shift { get; init; }

    /// <summary>Reset times.</summary>
    public DailyResets Resets { get; init; } = new();

    /// <summary>How long an opened screen stays the context of a reward.</summary>
    public double ContextSeconds { get; init; } = 180;

    /// <summary>Pop-ups that mean "a reward arrived".</summary>
    public IReadOnlyList<string> RewardScreens { get; init; } = [];

    /// <summary>HUD and overlay screens that are never an activity (exact names).</summary>
    public IReadOnlyList<string> PassiveScreens { get; init; } = [];

    /// <summary>Name parts that make a screen passive ('TIPS').</summary>
    public IReadOnlyList<string> PassiveContains { get; init; } = [];

    /// <summary>Sections in display order.</summary>
    public IReadOnlyList<DailySection> Sections { get; init; } = [];

    /// <summary>Items in display order.</summary>
    public IReadOnlyList<DailyItemDef> Items { get; init; } = [];

    /// <summary>
    /// Hash of the data file (set by <see cref="Parse"/>): a saved state counted with other rules is recounted from the
    /// log (Release 4.35's first start kept 4.34's counts: Arena 1/5 instead of 5/5).
    /// </summary>
    [JsonIgnore]
    public string Fingerprint { get; init; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The offset of a file without 'timesUtcOffset' (the times were first written on a UTC+3 PC).</summary>
    public const string DefaultTimesUtcOffset = "+03:00";

    private static readonly HashSet<string> Kinds = ["reward", "shown", "reply", "push", "map", "job", "handIn", "settle", "guide", "shopRefresh", "presence", "box", "openItem"];

    /// <summary>Day names used in 'days' and 'resetDays'.</summary>
    public static readonly IReadOnlyList<string> DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary>'mon' for Monday (the catalog's day names).</summary>
    public static string DayName(DayOfWeek d) => DayNames[(int)d];

    /// <summary>Parses and checks the catalog; throws <see cref="InvalidDataException"/> naming the first problem.</summary>
    public static DailyCatalog Parse(ReadOnlySpan<byte> json)
    {
        DailyCatalog? c;
        try
        {
            c = JsonSerializer.Deserialize<DailyCatalog>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("dailies.json: " + ex.Message, ex);
        }

        if (c is null || c.SchemaVersion != 1)
        {
            throw new InvalidDataException("dailies.json: schemaVersion must be 1");
        }

        c = c with { Fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(json))[..16] };
        Check(TryParseOffset(c.TimesUtcOffset, out _), $"timesUtcOffset '{c.TimesUtcOffset}' must look like '+03:00' (from -12:00 to +14:00)");

        var sections = c.Sections.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in c.Items)
        {
            Check(!string.IsNullOrWhiteSpace(item.Id) && ids.Add(item.Id), $"item id '{item.Id}' is empty or used twice");
            Check(!string.IsNullOrWhiteSpace(item.Name), $"item '{item.Id}' has no name");
            Check(sections.Contains(item.Section), $"item '{item.Id}' names an unknown section '{item.Section}'");
            Check(item.PerDay >= 0, $"item '{item.Id}' has a negative perDay");
            Check(item.Days.All(d => DayNames.Contains(d)), $"item '{item.Id}' names an unknown day");
            Check(item.Windows.All(w => TimeOnly.TryParseExact(w.At, "HH:mm", out _) && w.Minutes > 0), $"item '{item.Id}' has a bad window");
            Check(item.Kind != DailyKind.Producer || item.Producer is { Items: > 0, Minutes: > 0 } pr
                && !string.IsNullOrEmpty(pr.QueueScreen) && !string.IsNullOrEmpty(pr.ViewScreen) && pr.Collect is "toasts" or "slots",
                $"item '{item.Id}' is a producer without a complete 'producer' block");
            Check(item.Stack is null || (item.Stack.Cap > 0 && item.Stack.PerReset >= 0 && item.Stack.Mode is "use" or "fill"
                && item.Stack.ResetDays.All(d => DayNames.Contains(d))), $"item '{item.Id}' has a bad 'stack'");
            Check(item.Stack?.Feeds is not { } fed || c.Items.Any(o => o.Id == fed && o.Stack is { Mode: "fill" }),
                $"item '{item.Id}' feeds '{item.Stack?.Feeds}', which is not an item with a 'fill' stack");
            foreach (var r in item.Rules)
            {
                Check(Kinds.Contains(r.On), $"item '{item.Id}' has an unknown rule '{r.On}'");
                Check(r.On switch
                {
                    "reward" or "shown" => r.Screens.Count > 0,
                    "reply" => !string.IsNullOrEmpty(r.Request),
                    "push" => !string.IsNullOrEmpty(r.Name),
                    "map" => !string.IsNullOrEmpty(r.Prefix),
                    "job" => r.Jobs.Count > 0,
                    "settle" => true,
                    "guide" => !string.IsNullOrEmpty(r.Key),
                    "shopRefresh" => r.Shop is not null,
                    "presence" => !string.IsNullOrEmpty(r.Map) && (item.Windows.Count > 0 || item.Instances.Count > 0),
                    "box" => !string.IsNullOrEmpty(r.AfterPart) && item.Parts.Contains(r.AfterPart),
                    "openItem" => r.Items.Count > 0 || (r.ItemMin is { } lo && r.ItemMax is { } hi && lo <= hi),
                    _ => r.NpcTalkSeconds > 0 || !string.IsNullOrEmpty(r.QuestContains),
                }, $"item '{item.Id}' has an incomplete '{r.On}' rule");
                Check(r.Part is null || item.Parts.Contains(r.Part), $"item '{item.Id}' names an unknown part '{r.Part}'");
            }
        }

        return c;
    }

    /// <summary>
    /// This catalog with every time moved by <paramref name="shift"/> (rounded to whole minutes): the daily and calendar
    /// resets (kept outside 00:00-24:00 when they cross midnight, see <see cref="DailyResets"/>), the weekly reset with
    /// its day, every window's start (each window stays on the game day it belonged to) and the 'usual' hours. The
    /// fingerprint names the shift, so a state counted with another shift is recounted. A zero shift returns this catalog.
    /// </summary>
    public DailyCatalog Shifted(TimeSpan shift)
    {
        shift = TimeSpan.FromMinutes(Math.Round(shift.TotalMinutes));
        if (shift == TimeSpan.Zero)
        {
            return this;
        }

        var weekly = Resets.Weekly + shift;
        var weeklyDay = (int)Resets.WeeklyDay;
        while (weekly < TimeSpan.Zero)
        {
            weekly += TimeSpan.FromDays(1);
            weeklyDay += 6;
        }

        while (weekly >= TimeSpan.FromDays(1))
        {
            weekly -= TimeSpan.FromDays(1);
            weeklyDay++;
        }

        var total = Shift + shift;
        var at = Fingerprint.IndexOf('@', StringComparison.Ordinal);
        var file = at < 0 ? Fingerprint : Fingerprint[..at];
        return this with
        {
            Shift = total,
            Fingerprint = total == TimeSpan.Zero ? file : file + "@" + ((int)total.TotalMinutes).ToString(CultureInfo.InvariantCulture),
            Resets = Resets with
            {
                Daily = Resets.Daily + shift,
                Calendar = Resets.Calendar + shift,
                Weekly = weekly,
                WeeklyDay = (DayOfWeek)(weeklyDay % 7),
            },
            Items = [.. Items.Select(i => i.Windows.Count == 0 && i.Usual is null ? i : i with
            {
                Windows = [.. i.Windows.Select(w => w with { At = ShiftClock(w.At, shift) })],
                Usual = i.Usual is null ? null : ClockTime().Replace(i.Usual, m => ShiftClock(m.Value, shift)),
            })],
        };
    }

    /// <summary>Reads an offset such as '+03:00' or '-05:00' (from -12:00 to +14:00).</summary>
    public static bool TryParseOffset(string? text, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (text is null || OffsetText().Match(text) is not { Success: true } m)
        {
            return false;
        }

        var hours = int.Parse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
        var minutes = int.Parse(m.Groups[3].ValueSpan, CultureInfo.InvariantCulture);
        var value = new TimeSpan(hours, minutes, 0);
        offset = m.Groups[1].ValueSpan[0] == '-' ? -value : value;
        return minutes < 60 && offset >= TimeSpan.FromHours(-12) && offset <= TimeSpan.FromHours(14);
    }

    /// <summary>'HH:mm' moved by <paramref name="shift"/> around the clock; text that is not a time stays as it is.</summary>
    private static string ShiftClock(string hhmm, TimeSpan shift) =>
        TimeOnly.TryParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.Add(shift).ToString("HH:mm", CultureInfo.InvariantCulture)
            : hhmm;

    [GeneratedRegex(@"^([+-])(\d{2}):(\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetText();

    [GeneratedRegex(@"\b(?:[01]\d|2[0-3]):[0-5]\d\b", RegexOptions.CultureInvariant)]
    private static partial Regex ClockTime();

    private static void Check(bool ok, string problem)
    {
        if (!ok)
        {
            throw new InvalidDataException("dailies.json: " + problem);
        }
    }
}
