using System.Text.RegularExpressions;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Navigation;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.Navigation;

/// <summary>
/// The merged UI map (core data\ui-map.json plus feature fragments), loaded once at host start and sealed before the
/// first job (SPEC 8.1). <see cref="AddFragment"/> validates a fragment as a whole and throws
/// <see cref="InvalidDataException"/> (the fragment is rejected) on a duplicate id, an unknown screen reference, a
/// key that is not whitelisted, a commit button without a gate or a deny zone outside the client. Template
/// references are checked at <see cref="Seal"/> against the template catalog (whatever the load order): a button whose
/// template is missing becomes NeedsSurvey and a missing anchor is dropped (journaled). Thread-safe.
/// </summary>
public sealed partial class UiMapCatalog : IUiMapCatalogBuilder
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex UiNameRegex();

    private readonly ITemplateCatalog _templates;
    private readonly IJournal _journal;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly List<ScreenDefinition> _screens = [];
    private readonly List<ButtonDefinition> _buttons = [];
    private readonly List<DenyZone> _zones = [];
    private readonly List<string> _sealReport = [];
    private UiMap? _map;
    private bool _sealed;

    /// <summary>Creates an empty catalog (no side effects).</summary>
    public UiMapCatalog(ITemplateCatalog templates, IJournal journal, IClock clock)
    {
        _templates = templates;
        _journal = journal;
        _clock = clock;
    }

    /// <summary>Changes made at seal time ("button.needsSurvey:id", "anchor.dropped:screen:template").</summary>
    internal IReadOnlyList<string> SealReport
    {
        get
        {
            lock (_gate)
            {
                return [.. _sealReport];
            }
        }
    }

    /// <inheritdoc />
    public UiMap Map
    {
        get
        {
            lock (_gate)
            {
                return _map ??= new UiMap(1, [.. _screens], [.. _buttons], [.. _zones]);
            }
        }
    }

    /// <inheritdoc />
    public ScreenDefinition? GetScreen(string id)
    {
        lock (_gate)
        {
            return id is null ? null : _screens.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        }
    }

    /// <inheritdoc />
    public ButtonDefinition? GetButton(string id)
    {
        lock (_gate)
        {
            return id is null ? null : _buttons.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.Ordinal));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ButtonDefinition> ButtonsForTemplate(string templateId)
    {
        lock (_gate)
        {
            return _buttons.Where(b => string.Equals(b.TemplateId, templateId, StringComparison.Ordinal)).ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DenyZone> DenyZonesFor(string screenId)
    {
        lock (_gate)
        {
            return _zones.Where(z => string.Equals(z.ScreenId, screenId, StringComparison.Ordinal)).ToList();
        }
    }

    /// <inheritdoc />
    public void AddFragment(UiMap fragment, string sourceCode)
    {
        lock (_gate)
        {
            if (_sealed)
            {
                throw new InvalidOperationException("uimap.sealed");
            }
            var problems = Validate(fragment);
            if (problems.Count > 0)
            {
                throw new InvalidDataException($"uimap.invalid:{sourceCode}:{string.Join(",", problems.Take(5))}");
            }
            _screens.AddRange(fragment.Screens);
            _buttons.AddRange(fragment.Buttons);
            _zones.AddRange(fragment.DenyZones);
            _map = null;
        }
    }

    /// <inheritdoc />
    public void Seal()
    {
        lock (_gate)
        {
            if (_sealed)
            {
                return;
            }
            for (var i = 0; i < _buttons.Count; i++)
            {
                var b = _buttons[i];
                if (b.TemplateId is { } t && _templates.Get(t) is null && b.Status != SurveyStatus.NeedsSurvey)
                {
                    _buttons[i] = b with { Status = SurveyStatus.NeedsSurvey };
                    Report("button.needsSurvey:" + b.Id);
                }
            }
            for (var i = 0; i < _screens.Count; i++)
            {
                var s = _screens[i];
                var kept = s.AnchorTemplateIds.Where(a => _templates.Get(a) is not null).ToList();
                if (kept.Count != s.AnchorTemplateIds.Count)
                {
                    foreach (var dropped in s.AnchorTemplateIds.Except(kept, StringComparer.Ordinal))
                    {
                        Report("anchor.dropped:" + s.Id + ":" + dropped);
                    }
                    _screens[i] = s with { AnchorTemplateIds = kept };
                }
            }
            _map = null;
            _sealed = true;
        }
    }

    private void Report(string code)
    {
        _sealReport.Add(code);
        try
        {
            _journal.Write(new JournalRecord(_clock.UtcNow, JournalKind.FeatureLoad, null, null, null,
                new Dictionary<string, object?> { ["code"] = "uimap.seal", ["detail"] = code.Length <= 120 ? code : code[..120] }));
        }
        catch
        {
            // journal never throws into control flow
        }
    }

    /// <summary>Validates a fragment against itself and the already-merged map (pure apart from reading the merged lists).</summary>
    private List<string> Validate(UiMap? f)
    {
        var problems = new List<string>();
        if (f is null)
        {
            problems.Add("fragment.null");
            return problems;
        }
        if (f.SchemaVersion != 1)
        {
            problems.Add("schemaVersion");
        }
        if (f.Screens is null || f.Buttons is null || f.DenyZones is null)
        {
            problems.Add("lists.null");
            return problems;
        }
        var screenIds = new HashSet<string>(_screens.Select(s => s.Id), StringComparer.Ordinal);
        foreach (var s in f.Screens)
        {
            if (s is null || s.Id is null || !IdRegex().IsMatch(s.Id))
            {
                problems.Add("screen.id");
                continue;
            }
            if (!screenIds.Add(s.Id))
            {
                problems.Add("screen.duplicate:" + s.Id);
            }
            if (s.LogUiName is not null && !UiNameRegex().IsMatch(s.LogUiName))
            {
                problems.Add("screen.logUiName:" + s.Id);
            }
            if (s.AnchorTemplateIds is null || s.AnchorTemplateIds.Any(a => a is null || !IdRegex().IsMatch(a)))
            {
                problems.Add("screen.anchors:" + s.Id);
            }
            if (s.IsHud != (s.Hud is HudState.Collapsed or HudState.Expanded))
            {
                problems.Add("screen.hud:" + s.Id);
            }
            if (s.IsHud && s.LogUiName is not null)
            {
                problems.Add("screen.hudWithLogUi:" + s.Id);
            }
            if (!s.IsHud && s.LogUiName is null)
            {
                problems.Add("screen.noIdentity:" + s.Id);
            }
            if (!Enum.IsDefined(s.Status))
            {
                problems.Add("screen.status:" + s.Id);
            }
        }
        var buttonIds = new HashSet<string>(_buttons.Select(b => b.Id), StringComparer.Ordinal);
        foreach (var b in f.Buttons)
        {
            if (b is null || b.Id is null || !IdRegex().IsMatch(b.Id))
            {
                problems.Add("button.id");
                continue;
            }
            if (!buttonIds.Add(b.Id))
            {
                problems.Add("button.duplicate:" + b.Id);
            }
            if (b.ScreenId is null || !screenIds.Contains(b.ScreenId))
            {
                problems.Add("button.screen:" + b.Id);
            }
            if (b.LeadsTo is not null && !screenIds.Contains(b.LeadsTo))
            {
                problems.Add("button.leadsTo:" + b.Id);
            }
            if (b.TemplateId is not null && b.Key is not null)
            {
                problems.Add("button.templateAndKey:" + b.Id);
            }
            if (b.TemplateId is null && b.Key is null && b.Status != SurveyStatus.NeedsSurvey)
            {
                problems.Add("button.noTarget:" + b.Id);
            }
            if (b.TemplateId is not null && !IdRegex().IsMatch(b.TemplateId))
            {
                problems.Add("button.template:" + b.Id);
            }
            if (b.Key is { } key && (!HardRules.AllowedKeys.TryGetValue(key, out var code) || HardRules.DeniedKeys.Contains(code)))
            {
                problems.Add("button.key:" + b.Id);
            }
            if (b.ExpectedUi is not null && !UiNameRegex().IsMatch(b.ExpectedUi))
            {
                problems.Add("button.expectedUi:" + b.Id);
            }
            if (b.Safety == ButtonSafety.Commit && b.Gate == AutomationSwitch.None)
            {
                problems.Add("button.commitWithoutGate:" + b.Id);
            }
            if (b.Safety == ButtonSafety.Toggle && b.Key is not null)
            {
                problems.Add("button.toggleKey:" + b.Id);
            }
            if (!Enum.IsDefined(b.Safety) || !Enum.IsDefined(b.Gate) || !Enum.IsDefined(b.Status))
            {
                problems.Add("button.enum:" + b.Id);
            }
        }
        var zoneIds = new HashSet<string>(_zones.Select(z => z.Id), StringComparer.Ordinal);
        var client = new RectI(0, 0, HardRules.ExpectedClientWidth, HardRules.ExpectedClientHeight);
        foreach (var z in f.DenyZones)
        {
            if (z is null || z.Id is null || !IdRegex().IsMatch(z.Id))
            {
                problems.Add("zone.id");
                continue;
            }
            if (!zoneIds.Add(z.Id))
            {
                problems.Add("zone.duplicate:" + z.Id);
            }
            if (z.ScreenId is null || !screenIds.Contains(z.ScreenId))
            {
                problems.Add("zone.screen:" + z.Id);
            }
            if (z.ClientRect.IsEmpty || z.ClientRect.Intersect(client) != z.ClientRect)
            {
                problems.Add("zone.rect:" + z.Id);
            }
        }
        return problems;
    }
}
