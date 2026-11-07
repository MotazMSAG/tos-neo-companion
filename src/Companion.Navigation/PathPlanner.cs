using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Logs;
using Companion.Contracts.Navigation;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.Navigation;

/// <summary>
/// Shortest paths over the UI map (SPEC 8.2), pure and thread-safe. BFS over buttons, never through Forbidden or
/// Commit buttons; neighbours are expanded in button-id ordinal order (deterministic tie-break). A path made only of
/// executable hops (Known and clickable / keyed) is preferred; otherwise the shortest path through Provisional or
/// NeedsSurvey hops is returned so the caller can report NeedsSurvey.
/// </summary>
public sealed class PathPlanner : IPathPlanner
{
    private readonly IUiMapCatalog _map;
    private readonly ITemplateCatalog _templates;

    /// <summary>Creates the planner (no side effects).</summary>
    public PathPlanner(IUiMapCatalog map, ITemplateCatalog templates)
    {
        _map = map;
        _templates = templates;
    }

    /// <inheritdoc />
    public NavigationPath? FindPath(string fromScreenId, string toScreenId)
    {
        if (fromScreenId is null || toScreenId is null || _map.GetScreen(fromScreenId) is null || _map.GetScreen(toScreenId) is null)
        {
            return null;
        }
        if (string.Equals(fromScreenId, toScreenId, StringComparison.Ordinal))
        {
            return new NavigationPath(fromScreenId, toScreenId, []);
        }
        return Bfs(fromScreenId, toScreenId, executableOnly: true) ?? Bfs(fromScreenId, toScreenId, executableOnly: false);
    }

    /// <summary>True when a hop can be executed now (Known, navigation/toggle, clickable template or whitelisted key).</summary>
    internal bool IsExecutable(ButtonDefinition b) =>
        b.Status == SurveyStatus.Known
        && b.Safety is ButtonSafety.Navigation or ButtonSafety.Toggle
        && (b.TemplateId is { } t ? _templates.Get(t) is not null && _templates.IsClickable(t) : b.Key is not null);

    private NavigationPath? Bfs(string from, string to, bool executableOnly)
    {
        var buttons = _map.Map.Buttons
            .Where(b => b.LeadsTo is not null && b.Safety is not (ButtonSafety.Forbidden or ButtonSafety.Commit))
            .Where(b => !executableOnly || IsExecutable(b))
            .OrderBy(b => b.Id, StringComparer.Ordinal)
            .ToList();
        var prev = new Dictionary<string, ButtonDefinition>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var b in buttons.Where(b => string.Equals(b.ScreenId, current, StringComparison.Ordinal)))
            {
                var next = b.LeadsTo!;
                if (!visited.Add(next))
                {
                    continue;
                }
                prev[next] = b;
                if (string.Equals(next, to, StringComparison.Ordinal))
                {
                    var hops = new List<ButtonDefinition>();
                    var at = to;
                    while (!string.Equals(at, from, StringComparison.Ordinal))
                    {
                        var hop = prev[at];
                        hops.Add(hop);
                        at = hop.ScreenId;
                    }
                    hops.Reverse();
                    return new NavigationPath(from, to, hops);
                }
                queue.Enqueue(next);
            }
        }
        return null;
    }

    /// <inheritdoc />
    public ProcedureDefinition BuildProcedure(NavigationPath path, bool startWithReset)
    {
        var steps = new List<StepDefinition>();
        if (startWithReset)
        {
            steps.Add(new StepDefinition("reset", StatePredicate.Any, new ResetAction(HardRules.MaxEscPerReset), new Expectation([UiExpectation.HiddenTop(null)])));
        }
        for (var k = 0; k < path.Hops.Count; k++)
        {
            var hop = path.Hops[k];
            var from = _map.GetScreen(hop.ScreenId);
            var to = hop.LeadsTo is null ? null : _map.GetScreen(hop.LeadsTo);
            var pre = new StatePredicate(HudClean: from?.IsHud == true ? true : null, Screen: hop.ScreenId);
            StepAction action;
            Expectation expect;
            if (hop.Safety == ButtonSafety.Toggle && to is { IsHud: true, Hud: { } target })
            {
                action = new ToggleHudAction(target);
                expect = new Expectation(Hud: target);
            }
            else
            {
                action = hop.Key is { } key ? new KeyAction(key) : new ClickAction(hop.TemplateId ?? "missing");
                var ui = hop.ExpectedUi ?? to?.LogUiName;
                var screenCheck = to is not null && to.AnchorTemplateIds.Count > 0 ? to.Id : null;
                if (to is { IsHud: true, Hud: { } h })
                {
                    expect = new Expectation(Hud: h, Screen: to.Id);
                }
                else
                {
                    expect = new Expectation(ui is null ? null : UiExpectation.Opened(ui), Screen: screenCheck);
                }
            }
            var flags = to?.EscCloses == true ? StepFlags.CompensateOnPark : StepFlags.None;
            var timeout = to is not null && to.Status != SurveyStatus.Known ? 6000 : 3000;
            steps.Add(new StepDefinition($"hop{k + 1}:{hop.Id}", pre, action, expect, timeout, flags));
        }
        if (steps.Count == 0)
        {
            steps.Add(new StepDefinition("noop", StatePredicate.Any, new WaitAction(0), Expectation.Nothing));
        }
        return new ProcedureDefinition(
            ProcedureId(path),
            "Navigate " + path.From + " to " + path.To,
            steps,
            ProcedureLimits.Default,
            ProcedureKind.Navigation,
            AutomationSwitch.None);
    }

    private static string ProcedureId(NavigationPath path)
    {
        static string Clean(string s) => new(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
        var id = "nav." + Clean(path.From) + "." + Clean(path.To);
        return id.Length > 64 ? id[..64] : id;
    }
}
