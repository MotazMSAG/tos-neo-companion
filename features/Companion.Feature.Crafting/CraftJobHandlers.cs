using Companion.Contracts;
using Companion.Contracts.Features;
using Companion.Contracts.Host;
using Companion.Contracts.Knowledge;
using Companion.Contracts.Planning;
using Companion.Features.Kit;

namespace Companion.Feature.Crafting;

/// <summary>
/// Craft {item, qty}: switch and permission first (R10, KS-02), then knowledge, then the stub check (zero inputs),
/// then the craft flow with the keep-stock minimums reserved. At most 12 crafts per job.
/// </summary>
/// <summary>
/// The pre-2026-10-15 Craft run (CraftFlow: resync, plan, select, quantity, commit procedures), kept unregistered for
/// CraftFlow's tests while KeepStock still uses that flow; the registered Craft job runs <see cref="CraftPlanRun"/>.
/// </summary>
public sealed class CraftFlowGoalJobHandler : IJobHandler
{
    /// <inheritdoc />
    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var refusal = JobKit.CheckCommitGates(context, AutomationSwitch.Crafting)
            ?? JobKit.RequireKnowledge(context);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!parameters.Values.TryGetValue("item", out var itemValue) || itemValue is not ItemKey item
            || !parameters.Values.TryGetValue("qty", out var qtyValue) || qtyValue is not long qty)
        {
            return JobKit.Refused("param.missing");
        }

        refusal = CraftFlow.CheckReady(context);
        if (refusal is not null)
        {
            return refusal;
        }

        return CraftFlow.Run(
            context,
            (_, produced) =>
            {
                var remaining = qty - produced.GetValueOrDefault(item);
                return remaining > 0 ? [new CraftGoal(item, (int)remaining)] : [];
            },
            reserveKeepStock: true,
            new PerJobBudget(CraftJobHandler.MaxCraftsPerJob),
            ct);
    }

    private sealed class PerJobBudget(int max) : ICraftBudget
    {
        private int _crafts;

        public string? BeforeCraft() => _crafts >= max ? "craft.cap.perJob" : null;

        public void AfterCraft() => _crafts++;
    }
}

internal sealed class CraftJobHandler : IJobHandler
{
    internal const int MaxCraftsPerJob = 12;

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var refusal = JobKit.CheckCommitGates(context, AutomationSwitch.Crafting)
            ?? JobKit.RequireKnowledge(context);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!parameters.Values.TryGetValue("item", out var itemValue) || itemValue is not ItemKey item
            || !parameters.Values.TryGetValue("qty", out var qtyValue) || qtyValue is not long qty)
        {
            return JobKit.Refused("param.missing");
        }

        if (qty is < 0 or > 999)
        {
            return JobKit.Refused("param.range:qty"); // 0 = forever (CraftPlanRun.ForeverChunk per round)
        }

        // plan, then the proven live loop per planned craft (CraftPlanRun, 2026-10-15); CraftFlow's stubbed
        // select/quantity procedures stay for KeepStock until recipe selection by the list exists
        return CraftPlanRun.Run(context, item, (int)qty, ct);
    }

    private sealed class PerJobBudget(int max) : ICraftBudget
    {
        private int _crafts;

        public string? BeforeCraft() => _crafts >= max ? "craft.cap.perJob" : null;

        public void AfterCraft() => _crafts++;
    }
}

/// <summary>
/// KeepStock {}: an explicit trigger (TOS-01). Switch, permission and knowledge first; refused while keep-stock is
/// disabled in config. Goals are the keep-stock minimums minus the KNOWN, non-stale count (unknown or stale items are
/// never crafted for; resync or scan first). Caps from <see cref="KeepStockConfig"/>: crafts per hour, crafts per
/// server day (UTC + policy offset) and minutes per job. Counters live in memory for the host session.
/// </summary>
internal sealed class KeepStockJobHandler : IJobHandler
{
    private readonly object _gate = new();
    private readonly List<DateTimeOffset> _craftTimes = [];
    private readonly Dictionary<DateOnly, int> _daily = [];

    public JobResult Execute(IJobContext context, JobParameters parameters, CancellationToken ct)
    {
        var refusal = JobKit.CheckCommitGates(context, AutomationSwitch.Crafting)
            ?? JobKit.RequireKnowledge(context);
        if (refusal is not null)
        {
            return refusal;
        }

        var config = context.Config.KeepStock;
        if (!config.Enabled)
        {
            return JobKit.Refused("keepStock.disabled");
        }

        var rules = context.Knowledge!.GetKeepStockRules();
        if (rules.Count == 0)
        {
            return JobKit.Result(JobState.Succeeded, "keepStock.noRules");
        }

        refusal = CraftFlow.CheckReady(context);
        if (refusal is not null)
        {
            return refusal;
        }

        var budget = new Budget(this, context, config);
        var stop = budget.BeforeCraft();
        if (stop is not null)
        {
            return JobKit.Refused(stop);
        }

        return CraftFlow.Run(context, (stock, _) => Goals(rules, stock), reserveKeepStock: false, budget, ct);
    }

    /// <summary>Minimum minus known count, for known, non-stale, exact counts below the minimum.</summary>
    internal static IReadOnlyList<CraftGoal> Goals(IReadOnlyList<KeepStockRule> rules, StockView stock)
    {
        var goals = new List<CraftGoal>();
        foreach (var rule in rules)
        {
            if (stock.Items.TryGetValue(rule.Item, out var entry) && !entry.Stale && entry.Count is { } count && count < rule.Min)
            {
                goals.Add(new CraftGoal(rule.Item, (int)Math.Min(int.MaxValue, rule.Min - count)));
            }
        }

        return goals;
    }

    private sealed class Budget : ICraftBudget
    {
        private readonly KeepStockJobHandler _owner;
        private readonly IJobContext _context;
        private readonly KeepStockConfig _config;
        private readonly TimeSpan _start;

        public Budget(KeepStockJobHandler owner, IJobContext context, KeepStockConfig config)
        {
            _owner = owner;
            _context = context;
            _config = config;
            _start = context.Clock.Monotonic;
        }

        public string? BeforeCraft()
        {
            var now = _context.Clock.UtcNow;
            if (_context.Clock.Monotonic - _start >= TimeSpan.FromMinutes(Math.Max(0, _config.MaxSessionMinutes)))
            {
                return "keepStock.cap.session";
            }

            lock (_owner._gate)
            {
                _owner._craftTimes.RemoveAll(t => now - t >= TimeSpan.FromHours(1));
                if (_owner._craftTimes.Count >= _config.MaxCraftsPerHour)
                {
                    return "keepStock.cap.hourly";
                }

                if (_owner._daily.GetValueOrDefault(ServerDay(now)) >= _config.MaxDailyCrafts)
                {
                    return "keepStock.cap.daily";
                }
            }

            return null;
        }

        public void AfterCraft()
        {
            var now = _context.Clock.UtcNow;
            lock (_owner._gate)
            {
                _owner._craftTimes.Add(now);
                var day = ServerDay(now);
                _owner._daily[day] = _owner._daily.GetValueOrDefault(day) + 1;
            }
        }

        private DateOnly ServerDay(DateTimeOffset utc) =>
            DateOnly.FromDateTime(utc.UtcDateTime.AddHours(_context.Config.Policy.ServerUtcOffsetHours));
    }
}
