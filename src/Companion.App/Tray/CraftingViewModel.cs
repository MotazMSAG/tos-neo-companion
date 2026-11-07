using System.Globalization;
using Companion.App.Hosting;
using Companion.App.Jobs;
using Companion.Contracts;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Logs;

namespace Companion.App.Tray;

/// <summary>What the Crafting window needs from the host (a seam for tests). Implementations never throw.</summary>
internal interface ICraftingBackend
{
    /// <summary>The companion's knowledge (one profile for every character) for READ-ONLY use (null when it cannot be opened).</summary>
    ICharacterKnowledge? Knowledge { get; }

    /// <summary>One request through the job channel (the named pipe's validation and job host).</summary>
    PipeResponse Send(PipeRequest request);

    /// <summary>The job the host is running, or null.</summary>
    JobStatusInfo? RunningJob { get; }

    /// <summary>The names of the jobs waiting to run (at most one).</summary>
    IReadOnlyList<string> QueuedJobNames => [];

    /// <summary>The game's auto-collect status from its log (1 = a gathering trip is out), or null.</summary>
    int? AutoCollectStatus => null;

    /// <summary>The crafting automation switch.</summary>
    bool CraftingSwitchOn { get; }

    /// <summary>The slide-click switch.</summary>
    bool SlideClicksOn { get; }

    /// <summary>Latest craft session from the log model.</summary>
    CraftSessionInfo CraftSession { get; }

    /// <summary>Now (UTC).</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// The Crafting window's logic (SPEC 10.3, 11.4): lists the current character's recipes from its knowledge (read
/// only), submits CraftLoop, ScanFavorites and LearnRecipe through the job channel (the same validation, gates and limits as the
/// named pipe), cancels the running job as the CLI's cancel does, follows the job's result and turns every code into
/// plain language. UI thread only.
/// </summary>
internal sealed class CraftingViewModel
{
    private readonly ICraftingBackend _backend;
    private readonly Func<string> _newJobId;
    private readonly CraftProgress _progress = new();
    private bool _loaded;
    private HostStatus? _status;
    private JobStatusInfo? _running;
    private string? _ownJobId;
    private string? _ownJobName;
    private JobState _ownJobState = JobState.Succeeded;

    /// <summary>Creates the view-model (no I/O until <see cref="Refresh"/>).</summary>
    public CraftingViewModel(ICraftingBackend backend, Func<string>? newJobId = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _newJobId = newJobId ?? (() => Guid.NewGuid().ToString("N"));
    }

    /// <summary>The list: the "recipe selected in the game" entry first, then the known recipes.</summary>
    public IReadOnlyList<CraftRecipeRow> Recipes { get; private set; } = [CraftRecipeRow.SelectedInGame];

    /// <summary>Changes whenever <see cref="Recipes"/> is reloaded.</summary>
    public int RecipesVersion { get; private set; }

    /// <summary>The selected row (one of <see cref="Recipes"/>), or null.</summary>
    public CraftRecipeRow? Selected { get; set; }

    /// <summary>Items to make (1..<see cref="CraftingRules.MaxCount"/>), unless <see cref="UntilMaterialsRunOut"/>.</summary>
    public int Count { get; set; } = 1;

    /// <summary>Craft until the materials run out (count 0).</summary>
    public bool UntilMaterialsRunOut { get; set; }

    /// <summary>Batch "max" instead of "1".</summary>
    public bool BatchMax { get; set; }

    /// <summary>What the companion knows ("N recipes known.").</summary>
    public string KnowledgeLine { get; private set; } = "No recipes known yet.";

    /// <summary>What must change in the tray before crafting works, or null when nothing.</summary>
    public string? GateHint { get; private set; }

    /// <summary>The host's last status (null when not reachable).</summary>
    public HostStatus? Host => _status;

    /// <summary>The name of the job the host is running (null when none).</summary>
    public string? RunningJobName => _running?.JobName;

    /// <summary>The id of the job the host is running (null when none).</summary>
    public string? RunningJobId => _running?.JobId;

    /// <summary>The status line (engine state, current job, progress).</summary>
    public string StatusLine { get; private set; } = "Companion: -";

    /// <summary>The one-line health at the top of the window (green / amber / red / grey).</summary>
    public (HealthLevel Level, string Text) Health { get; private set; } = (HealthLevel.Grey, "...");

    /// <summary>The last answer or job end, in plain language.</summary>
    public string Message { get; private set; } = string.Empty;

    /// <summary>True while the job this window submitted is queued, running or parked.</summary>
    public bool OwnJobActive => _ownJobId is not null && !CraftingRules.IsTerminal(_ownJobState);

    /// <summary>
    /// Set by the window: it is the active window or the pointer rests on it. Either way no slide click can be sent
    /// (the cover must be another process's foreground window under the pointer, SPEC 7.5).
    /// </summary>
    public bool WindowBlocksClicks { get; set; }

    /// <summary>What the user must do for this window's job to go on, or null (see <see cref="WindowBlocksClicks"/>).</summary>
    public string? ClickHint => OwnJobActive && WindowBlocksClicks ? CraftingMessages.CoverHint : null;

    /// <summary>Enabled controls.</summary>
    public CraftingButtons Buttons => CraftingRules.Buttons(_running is not null, OwnJobActive, Selected, UntilMaterialsRunOut, Count);

    /// <summary>Details of the selected row.</summary>
    public string Details => Selected is { } row ? CraftingRules.Details(row, _backend.UtcNow) : "Select a recipe.";

    /// <summary>Selects the row with this recipe id ("" = the recipe selected in the game), or nothing.</summary>
    public void Select(string? recipeId) =>
        Selected = recipeId is null ? null : Recipes.FirstOrDefault(r => string.Equals(r.RecipeId, recipeId, StringComparison.Ordinal));

    /// <summary>
    /// About once a second: first load, host status, the own job's progress and end. The list is also reloaded when any
    /// job ends (a scan started from the CLI changes the favourites mapping too).
    /// </summary>
    public void Refresh()
    {
        var reload = false;
        if (!_loaded)
        {
            _loaded = true;
            reload = true;
        }

        var status = _backend.Send(new PipeRequest(PipeOp.Status));
        _status = status.Ok ? status.Status : null;
        var previous = _running;
        _running = _backend.RunningJob;
        if (previous is not null && !string.Equals(previous.JobId, _running?.JobId, StringComparison.Ordinal))
        {
            reload = true;
        }

        if (OwnJobActive)
        {
            reload |= FollowOwnJob();
        }

        if (reload)
        {
            ReloadRecipes();
        }

        if (OwnJobActive && string.Equals(_ownJobName, CraftingRules.CraftLoopJob, StringComparison.Ordinal))
        {
            _progress.Observe(_backend.CraftSession);
        }

        KnowledgeLine = BuildKnowledgeLine();
        GateHint = BuildGateHint();
        StatusLine = BuildStatusLine();
    }

    /// <summary>Start: submits CraftLoop for the selected favourite (slot, count, batch), or Craft for any other recipe.</summary>
    public void Start()
    {
        if (!Buttons.Start || Selected is not { } recipe)
        {
            return;
        }

        var id = _newJobId();
        if (recipe.ViaPlan && !(UntilMaterialsRunOut && recipe.Slot > 0))
        {
            Accepted(id, CraftingRules.CraftJob, _backend.Send(CraftingRules.CraftRequest(id, recipe, Count)));
            return;
        }

        var response = _backend.Send(CraftingRules.CraftLoopRequest(id, recipe, UntilMaterialsRunOut, Count, BatchMax));
        Accepted(id, CraftingRules.CraftLoopJob, response);
    }

    /// <summary>Scan favourites: submits ScanFavorites (read-only).</summary>
    public void Scan()
    {
        if (!Buttons.Scan)
        {
            return;
        }

        var id = _newJobId();
        var response = _backend.Send(CraftingRules.ScanRequest(id));
        Accepted(id, CraftingRules.ScanFavoritesJob, response);
    }

    /// <summary>Scan all recipes: submits ScanRecipeList (read-only; walks the whole Make list).</summary>
    public void ScanAll()
    {
        if (!Buttons.Scan)
        {
            return;
        }

        var id = _newJobId();
        var response = _backend.Send(CraftingRules.ScanAllRequest(id));
        Accepted(id, CraftingRules.ScanListJob, response);
    }

    /// <summary>Learn selected recipe: submits LearnRecipe (read-only; the recipe the user selected in the game).</summary>
    public void Learn()
    {
        if (!Buttons.Scan)
        {
            return;
        }

        var id = _newJobId();
        var response = _backend.Send(CraftingRules.LearnRequest(id));
        Accepted(id, CraftingRules.LearnRecipeJob, response);
    }

    /// <summary>Stop: cancels this window's job, or else the running job (the CLI's cancel).</summary>
    public void Stop()
    {
        var target = OwnJobActive ? _ownJobId : _running?.JobId;
        if (target is null)
        {
            Message = "No job is running.";
            return;
        }

        var response = _backend.Send(new PipeRequest(PipeOp.Cancel, target));
        Message = CraftingMessages.ForCancel(response.Code);
        Refresh();
    }

    private void Accepted(string id, string jobName, PipeResponse response)
    {
        Message = CraftingMessages.ForSubmit(jobName, response);
        if (response.Ok)
        {
            _ownJobId = id;
            _ownJobName = jobName;
            _ownJobState = response.Job?.State ?? JobState.Queued;
            if (jobName is CraftingRules.CraftLoopJob or CraftingRules.CraftJob)
            {
                _progress.Begin(_backend.CraftSession);
            }
        }

        Refresh();
    }

    /// <summary>Follows this window's job; true when it has just ended (the recipes must be reloaded).</summary>
    private bool FollowOwnJob()
    {
        var response = _backend.Send(new PipeRequest(PipeOp.Result, _ownJobId));
        if (response.Ok && response.Job is { } job)
        {
            var previous = _ownJobState;
            _ownJobState = job.State;
            if (CraftingRules.IsTerminal(job.State))
            {
                Message = CraftingMessages.ForEnd(job);
                return true;
            }

            if (job.State == JobState.Parked && previous != JobState.Parked)
            {
                Message = CraftingMessages.ForEnd(job);
            }
        }
        else if (string.Equals(response.Code, "notFound", StringComparison.Ordinal))
        {
            _ownJobState = JobState.Cancelled;
            Message = "The job's result is no longer available. [notFound]";
            return true;
        }

        return false;
    }

    private void ReloadRecipes()
    {
        var selectedId = Selected?.RecipeId;
        IReadOnlyList<CraftRecipeRow> rows = [];
        if (_backend.Knowledge is { } knowledge)
        {
            rows = CraftingRules.Map(knowledge.GetRecipes(), knowledge.GetStock(), knowledge.GetIconItems());
        }

        Recipes = [CraftRecipeRow.SelectedInGame, .. rows];
        Select(selectedId);
        RecipesVersion++;
    }

    private string BuildKnowledgeLine()
    {
        var recipes = Recipes.Count - 1;
        return string.Create(CultureInfo.InvariantCulture, $"{recipes} recipe{(recipes == 1 ? string.Empty : "s")} known.");
    }

    private string? BuildGateHint()
    {
        var hints = new List<string>();
        if (!_backend.CraftingSwitchOn)
        {
            hints.Add("Crafting automation is OFF (tray menu: Automation > Crafting).");
        }

        if (!_backend.SlideClicksOn)
        {
            hints.Add("Slide clicks are OFF; crafting and scanning need them (tray menu: Automation > Slide clicks).");
        }

        return hints.Count == 0 ? null : "Before crafting: " + string.Join(" ", hints);
    }

    private string BuildStatusLine()
    {
        Health = CraftingMessages.Health(_status, _running is not null, CraftingMessages.JobActivityText(_running?.Data, DateTimeOffset.UtcNow), _backend.AutoCollectStatus);
        if (_status is not { } st)
        {
            return "Companion: not reachable.";
        }

        var parts = new List<string> { "Companion: " + st.State };
        if (st.Engine is { } e)
        {
            var engine = "engine " + e.State;
            if (e.WaitingFor is { Length: > 0 } wait)
            {
                engine += " (" + CraftingMessages.WaitText(wait) + ")";
            }

            if (e.ProcedureId is { Length: > 0 } procedure)
            {
                engine += ", " + procedure + (e.StepId is { Length: > 0 } step ? " / " + step : string.Empty);
            }

            parts.Add(engine);
        }

        parts.Add(_running is { } job
            ? "job " + job.JobName + " " + (job.JobId.Length >= 8 ? job.JobId[..8] : job.JobId) + " (" + job.State.ToString().ToLowerInvariant() + ")"
            : "no job running");

        if (OwnJobActive && _progress.Started && string.Equals(_ownJobName, CraftingRules.CraftLoopJob, StringComparison.Ordinal))
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"so far: {_progress.Crafts} craft{(_progress.Crafts == 1 ? string.Empty : "s")} started, about {_progress.Items} item{(_progress.Items == 1 ? string.Empty : "s")} (game log)"));
        }

        return string.Join(" | ", parts);
    }
}

/// <summary>The host side of <see cref="ICraftingBackend"/>: the log model, the knowledge store (read only), the config switches and the job channel.</summary>
internal sealed class HostCraftingBackend : ICraftingBackend
{
    private readonly HostServices _s;
    private readonly JobHost _jobs;
    private readonly LocalJobChannel _channel;

    /// <summary>Creates the backend over a started host.</summary>
    public HostCraftingBackend(HostServices services, JobHost jobs)
    {
        _s = services ?? throw new ArgumentNullException(nameof(services));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _channel = new LocalJobChannel(jobs.Handle);
    }

    /// <inheritdoc />
    public ICharacterKnowledge? Knowledge
    {
        get
        {
            try
            {
                return _s.Knowledge.Profile;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public JobStatusInfo? RunningJob => _jobs.RunningJob;

    /// <inheritdoc />
    public IReadOnlyList<string> QueuedJobNames => _jobs.QueuedJobNames;

    /// <inheritdoc />
    public int? AutoCollectStatus
    {
        get
        {
            try
            {
                return _s.Log.State.AutoCollectStatus;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public bool CraftingSwitchOn => _s.Config.IsOn(AutomationSwitch.Crafting);

    /// <inheritdoc />
    public bool SlideClicksOn => _s.Config.SlideClickEnabled;

    /// <inheritdoc />
    public CraftSessionInfo CraftSession
    {
        get
        {
            try
            {
                return _s.Log.State.Craft;
            }
            catch (Exception)
            {
                return CraftSessionInfo.None;
            }
        }
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow => _s.Clock.UtcNow;

    /// <inheritdoc />
    public PipeResponse Send(PipeRequest request) => _channel.Send(request);
}
