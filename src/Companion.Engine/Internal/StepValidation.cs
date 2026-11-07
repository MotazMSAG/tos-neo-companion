using System.Text.RegularExpressions;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.Engine.Internal;

/// <summary>Well-known template ids of the HUD (SPEC 7.7).</summary>
internal static class HudTemplates
{
    public const string Bag = "hud.bag";
    public const string ToggleCollapsed = "hud.toggle.collapsed";
    public const string ToggleExpanded = "hud.toggle.expanded";

    /// <summary>The toggle template visible in <paramref name="state"/> (clicking it leaves that state).</summary>
    public static string? ToggleOf(HudState state) => state switch
    {
        HudState.Collapsed => ToggleCollapsed,
        HudState.Expanded => ToggleExpanded,
        _ => null,
    };

    /// <summary>The toggle that must be clickable to reach <paramref name="target"/> from the opposite state.</summary>
    public static string? ToggleToReach(HudState target) => target switch
    {
        HudState.Expanded => ToggleCollapsed,
        HudState.Collapsed => ToggleExpanded,
        _ => null,
    };

    /// <summary>Client rectangle of the chat panel (blacked out in every persisted image, SPEC 12).</summary>
    public static readonly RectI ChatPanel = new(660, 590, 1270, 1009);

    /// <summary>Right HUD strip captured by Calibrate (client x 1440-1919).</summary>
    public static readonly RectI HudStripRight = new(1440, 0, 1920, 1009);

    /// <summary>The full client rectangle.</summary>
    public static readonly RectI Client = new(0, 0, HardRules.ExpectedClientWidth, HardRules.ExpectedClientHeight);
}

/// <summary>References a procedure makes to other catalogs.</summary>
internal sealed record ProcedureRefs(
    IReadOnlyList<string> Templates,
    IReadOnlyList<string> Readers,
    IReadOnlyList<string> Screens,
    IReadOnlyList<string> Procedures);

/// <summary>Structural validation of procedures (ids, limits within the ceilings, actions, flags). Pure.</summary>
internal static partial class StepValidation
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProcedureIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex StepIdRegex();

    [GeneratedRegex(@"^[a-z0-9_-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex RefRegex();

    /// <summary>Ceilings for <see cref="ProcedureLimits"/> (data may lower, never raise, SPEC 6.7).</summary>
    public static ProcedureLimits Ceilings { get; } = new(
        MaxCountedPerStep: 3,
        MaxUncountedPerStep: 4,
        MaxStepBacks: 1,
        MaxRestarts: 2,
        MaxPreconditionLost: 2,
        MaxInputBatches: HardRules.CeilingInputBatchesPerProcedure,
        MaxClicks: HardRules.CeilingClicksPerProcedure,
        MaxEsc: HardRules.CeilingEscPerProcedure,
        PatienceMs: (int)HardRules.ParkPatience.TotalMilliseconds);

    /// <summary>Limits clamped to the ceilings (defence in depth for ad-hoc procedures).</summary>
    public static ProcedureLimits Clamp(ProcedureLimits? l)
    {
        var c = Ceilings;
        l ??= ProcedureLimits.Default;
        return new ProcedureLimits(
            Math.Clamp(l.MaxCountedPerStep, 1, c.MaxCountedPerStep),
            Math.Clamp(l.MaxUncountedPerStep, 0, c.MaxUncountedPerStep),
            Math.Clamp(l.MaxStepBacks, 0, c.MaxStepBacks),
            Math.Clamp(l.MaxRestarts, 0, c.MaxRestarts),
            Math.Clamp(l.MaxPreconditionLost, 0, c.MaxPreconditionLost),
            Math.Clamp(l.MaxInputBatches, 0, c.MaxInputBatches),
            Math.Clamp(l.MaxClicks, 0, c.MaxClicks),
            Math.Clamp(l.MaxEsc, 0, c.MaxEsc),
            Math.Clamp(l.PatienceMs, 0, c.PatienceMs));
    }

    /// <summary>Problems found (codes); empty when the procedure is structurally valid.</summary>
    public static IReadOnlyList<string> Validate(ProcedureDefinition? p)
    {
        var problems = new List<string>();
        if (p is null)
        {
            problems.Add("procedure.null");
            return problems;
        }
        if (p.Id is null || !ProcedureIdRegex().IsMatch(p.Id))
        {
            problems.Add("procedure.id");
        }
        if (p.Description is null || p.Description.Length > 200)
        {
            problems.Add("procedure.description");
        }
        if (!Enum.IsDefined(p.Kind))
        {
            problems.Add("procedure.kind");
        }
        if (!Enum.IsDefined(p.Gate))
        {
            problems.Add("procedure.gate");
        }
        if (p.VerifiesSelection && (p.Kind != ProcedureKind.Navigation || p.Gate != AutomationSwitch.None || p.Steps?.Any(s => s is not null && s.Flags.HasFlag(StepFlags.Commit)) == true))
        {
            problems.Add("procedure.verifiesSelection"); // only a read-only navigation may open the commit window
        }
        ValidateLimits(p.Limits, problems);
        if (p.Steps is null || p.Steps.Count == 0 || p.Steps.Count > 64)
        {
            problems.Add("procedure.steps");
            return problems;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in p.Steps)
        {
            if (s is null)
            {
                problems.Add("step.null");
                continue;
            }
            if (s.Id is null || !StepIdRegex().IsMatch(s.Id))
            {
                problems.Add("step.id");
                continue;
            }
            if (!ids.Add(s.Id))
            {
                problems.Add("step.duplicate:" + s.Id);
            }
            ValidateStep(p, s, problems);
        }
        return problems;
    }

    private static void ValidateLimits(ProcedureLimits? l, List<string> problems)
    {
        if (l is null)
        {
            problems.Add("limits.null");
            return;
        }
        var c = Ceilings;
        void Check(int v, int min, int max, string name)
        {
            if (v < min || v > max)
            {
                problems.Add("limits." + name);
            }
        }
        Check(l.MaxCountedPerStep, 1, c.MaxCountedPerStep, "maxCountedPerStep");
        Check(l.MaxUncountedPerStep, 0, c.MaxUncountedPerStep, "maxUncountedPerStep");
        Check(l.MaxStepBacks, 0, c.MaxStepBacks, "maxStepBacks");
        Check(l.MaxRestarts, 0, c.MaxRestarts, "maxRestarts");
        Check(l.MaxPreconditionLost, 0, c.MaxPreconditionLost, "maxPreconditionLost");
        Check(l.MaxInputBatches, 0, c.MaxInputBatches, "maxInputBatches");
        Check(l.MaxClicks, 0, c.MaxClicks, "maxClicks");
        Check(l.MaxEsc, 0, c.MaxEsc, "maxEsc");
        Check(l.PatienceMs, 0, c.PatienceMs, "patienceMs");
    }

    private static void ValidateStep(ProcedureDefinition p, StepDefinition s, List<string> problems)
    {
        var at = ":" + s.Id;
        if (s.Pre is null)
        {
            problems.Add("step.pre" + at);
        }
        if (s.Expect is null)
        {
            problems.Add("step.expect" + at);
        }
        else
        {
            ValidateExpectation(s.Expect, problems, at, 0);
        }
        if (s.TimeoutMs < 0 || s.TimeoutMs > 120_000)
        {
            problems.Add("step.timeout" + at);
        }
        if (s.Flags.HasFlag(StepFlags.Commit) && s.Flags.HasFlag(StepFlags.Optional))
        {
            problems.Add("step.commitOptional" + at);
        }
        if (s.Flags.HasFlag(StepFlags.Commit) && p.Gate == AutomationSwitch.None)
        {
            problems.Add("step.commitWithoutGate" + at);
        }
        if (s.Flags.HasFlag(StepFlags.SelectionVerified) && (s.Action is not ClickAction || s.Flags.HasFlag(StepFlags.Commit)))
        {
            problems.Add("step.selectionVerified" + at);
        }
        if (s.Flags.HasFlag(StepFlags.DuringAutoCollect)
            && (s.Action is not ClickAction || s.Flags.HasFlag(StepFlags.Commit) || s.Flags.HasFlag(StepFlags.ChangesAutoCollect)))
        {
            problems.Add("step.duringAutoCollect" + at);
        }
        if (s.Flags.HasFlag(StepFlags.ChangesAutoCollect) && (s.Action is not ClickAction || s.Flags.HasFlag(StepFlags.Commit)))
        {
            problems.Add("step.changesAutoCollect" + at);
        }
        if (s.Flags.HasFlag(StepFlags.QuickAttribution)
            && (s.Action is not ClickAction || s.Flags.HasFlag(StepFlags.Commit) || AllUiEvents(s.Expect).Count > 0 || s.Expect?.Hud is not null || s.Expect?.Screen is not null
                || p.Gate != AutomationSwitch.None || p.Kind == ProcedureKind.Commit))
        {
            problems.Add("step.quickAttribution" + at);
        }
        if (s.Pre is { } pre)
        {
            if (pre.TopUi is not null && !RefRegex().IsMatch(pre.TopUi))
            {
                problems.Add("pre.topUi" + at);
            }
            if (pre.Screen is not null && !RefRegex().IsMatch(pre.Screen))
            {
                problems.Add("pre.screen" + at);
            }
            if (pre.TemplatesPresent is not null && pre.TemplatesPresent.Any(t => t is null || !RefRegex().IsMatch(t)))
            {
                problems.Add("pre.templates" + at);
            }
        }
        switch (s.Action)
        {
            case null:
                problems.Add("step.action" + at);
                break;
            case KeyAction k:
                if (!HardRules.AllowedKeys.TryGetValue(k.Key, out var code) || HardRules.DeniedKeys.Contains(code))
                {
                    problems.Add("key.notAllowed" + at);
                }
                else if (k.Key != GameKey.Escape && s.Pre?.HudClean != true)
                {
                    problems.Add("key.letterNeedsHudClean" + at);
                }
                break;
            case ClickAction c:
                if (c.TemplateId is null || !RefRegex().IsMatch(c.TemplateId))
                {
                    problems.Add("click.template" + at);
                }
                if (c.Offset is { } o && (Math.Abs(o.X) >= HardRules.ExpectedClientWidth || Math.Abs(o.Y) >= HardRules.ExpectedClientHeight))
                {
                    problems.Add("click.offset" + at);
                }
                if (c.Offset is not null && s.Flags.HasFlag(StepFlags.Commit))
                {
                    // a commit always lands on its own verified button
                    problems.Add("click.commitOffset" + at);
                }
                break;
            case DragAction d:
                if (d.TemplateId is null || !RefRegex().IsMatch(d.TemplateId))
                {
                    problems.Add("drag.template" + at);
                }
                if (Math.Abs(d.Offset.X) >= HardRules.ExpectedClientWidth || Math.Abs(d.Offset.Y) >= HardRules.ExpectedClientHeight)
                {
                    problems.Add("drag.offset" + at);
                }
                if (d.Delta == default || Math.Abs(d.Delta.X) > HardRules.MaxDragDistancePx || Math.Abs(d.Delta.Y) > HardRules.MaxDragDistancePx)
                {
                    problems.Add("drag.delta" + at);
                }
                if (s.Flags.HasFlag(StepFlags.Commit))
                {
                    // a drag scrolls; it never commits anything
                    problems.Add("drag.commit" + at);
                }
                if (s.Expect is { } de && (de.UiEvents is { Count: > 0 } || de.Alternatives is { Count: > 0 } || de.Hud is not null || de.Screen is not null))
                {
                    // the caller checks a drag's effect on a capture; nothing is expected from the log
                    problems.Add("drag.expect" + at);
                }
                break;
            case ToggleHudAction t:
                if (t.Target is not (HudState.Collapsed or HudState.Expanded))
                {
                    problems.Add("toggle.target" + at);
                }
                break;
            case RestoreHudAction:
                break;
            case ResetAction r:
                if (r.MaxEsc < 1 || r.MaxEsc > HardRules.MaxEscPerReset)
                {
                    problems.Add("reset.maxEsc" + at);
                }
                break;
            case WaitAction w:
                if (w.DurationMs < 0 || w.DurationMs > 600_000)
                {
                    problems.Add("wait.duration" + at);
                }
                break;
            case NavigateAction n:
                if (n.ScreenId is null || !RefRegex().IsMatch(n.ScreenId))
                {
                    problems.Add("navigate.screen" + at);
                }
                break;
            case CaptureAction cap:
                if (cap.Label is null || !LabelRegex().IsMatch(cap.Label) || !Enum.IsDefined(cap.Scope))
                {
                    problems.Add("capture.label" + at);
                }
                break;
            case ReadAction rd:
                if (rd.ReaderId is null || !RefRegex().IsMatch(rd.ReaderId) || rd.OutputKey is null || !RefRegex().IsMatch(rd.OutputKey))
                {
                    problems.Add("read.ids" + at);
                }
                break;
            case CallProcedureAction call:
                if (call.ProcedureId is null || !ProcedureIdRegex().IsMatch(call.ProcedureId))
                {
                    problems.Add("call.procedure" + at);
                }
                break;
            case AwaitCraftAction a:
                if (a.RequestedTicks < 1 || a.RequestedTicks > 999)
                {
                    problems.Add("awaitCraft.ticks" + at);
                }
                break;
            case NeedsSurveyAction ns:
                if (ns.What is null || !RefRegex().IsMatch(ns.What))
                {
                    problems.Add("needsSurvey.what" + at);
                }
                break;
            default:
                problems.Add("step.actionType" + at);
                break;
        }
    }

    private static void ValidateExpectation(Expectation e, List<string> problems, string at, int depth)
    {
        if (depth > 2)
        {
            problems.Add("expect.depth" + at);
            return;
        }
        if (e.UiEvents is not null)
        {
            foreach (var u in e.UiEvents)
            {
                if (u is null || (u.UiName is not null && !RefRegex().IsMatch(u.UiName)))
                {
                    problems.Add("expect.ui" + at);
                }
            }
        }
        if (e.Screen is not null && !RefRegex().IsMatch(e.Screen))
        {
            problems.Add("expect.screen" + at);
        }
        if (e.Alternatives is not null)
        {
            foreach (var a in e.Alternatives)
            {
                if (a is null)
                {
                    problems.Add("expect.alternative" + at);
                }
                else
                {
                    ValidateExpectation(a, problems, at, depth + 1);
                }
            }
        }
    }

    /// <summary>Templates, readers, screens and procedures a procedure refers to.</summary>
    public static ProcedureRefs References(ProcedureDefinition p)
    {
        var t = new SortedSet<string>(StringComparer.Ordinal);
        var r = new SortedSet<string>(StringComparer.Ordinal);
        var s = new SortedSet<string>(StringComparer.Ordinal);
        var c = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var step in p.Steps ?? [])
        {
            if (step?.Pre is { } pre)
            {
                foreach (var id in pre.TemplatesPresent ?? [])
                {
                    t.Add(id);
                }
                if (pre.Screen is not null)
                {
                    s.Add(pre.Screen);
                }
            }
            if (step?.Expect is { } ex)
            {
                AddExpectScreens(ex, s);
            }
            switch (step?.Action)
            {
                case ClickAction ca:
                    t.Add(ca.TemplateId);
                    break;
                case DragAction da:
                    t.Add(da.TemplateId);
                    break;
                case ToggleHudAction tg when HudTemplates.ToggleToReach(tg.Target) is { } tid:
                    t.Add(tid);
                    break;
                case RestoreHudAction:
                    t.Add(HudTemplates.ToggleCollapsed);
                    t.Add(HudTemplates.ToggleExpanded);
                    break;
                case ReadAction ra:
                    r.Add(ra.ReaderId);
                    break;
                case NavigateAction na:
                    s.Add(na.ScreenId);
                    break;
                case CallProcedureAction cp:
                    c.Add(cp.ProcedureId);
                    break;
            }
        }
        return new ProcedureRefs([.. t], [.. r], [.. s], [.. c]);
    }

    private static void AddExpectScreens(Expectation e, SortedSet<string> screens)
    {
        if (e.Screen is not null)
        {
            screens.Add(e.Screen);
        }
        foreach (var a in e.Alternatives ?? [])
        {
            if (a is not null)
            {
                AddExpectScreens(a, screens);
            }
        }
    }

    /// <summary>All UI expectations of an expectation and its alternatives.</summary>
    public static IReadOnlyList<UiExpectation> AllUiEvents(Expectation? e)
    {
        var list = new List<UiExpectation>();
        void Walk(Expectation? x, int d)
        {
            if (x is null || d > 3)
            {
                return;
            }
            if (x.UiEvents is not null)
            {
                list.AddRange(x.UiEvents.Where(u => u is not null));
            }
            foreach (var a in x.Alternatives ?? [])
            {
                Walk(a, d + 1);
            }
        }
        Walk(e, 0);
        return list;
    }
}
