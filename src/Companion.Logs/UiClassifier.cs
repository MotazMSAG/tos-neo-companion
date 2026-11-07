using System.Collections.Concurrent;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;

namespace Companion.Logs;

/// <summary>
/// UI-name classifier (SPEC 6.2): hard-coded <see cref="UiCatalog"/> seeds plus Stack names learned from
/// '[ui mgr]hide top ui X' lines. Precedence Forbidden &gt; Social &gt; Busy &gt; Station &gt; Stack (seed or learned) &gt;
/// Overlay (seed) &gt; Unknown; a learned Stack therefore overrides the Overlay seed. Names are matched exactly
/// (ordinal); a name needs no "UI_" prefix. Thread-safe: the learned set is a concurrent set that only grows for the
/// lifetime of the host (it survives log rotations). Constructor has no side effects.
/// </summary>
public sealed class UiClassifier : IUiClassifier
{
    private static readonly HashSet<string> ForbiddenExact = new(UiCatalog.ForbiddenExact, StringComparer.Ordinal);
    private static readonly HashSet<string> SocialExact = new(UiCatalog.SocialExact, StringComparer.Ordinal);
    private static readonly HashSet<string> BusyExact = new(UiCatalog.BusyExact, StringComparer.Ordinal);
    private static readonly HashSet<string> StationExact = new(UiCatalog.StationExact, StringComparer.Ordinal);
    private static readonly HashSet<string> StackSeed = new(UiCatalog.StackSeed, StringComparer.Ordinal);
    private static readonly HashSet<string> OverlaySeed = new(UiCatalog.OverlaySeed, StringComparer.Ordinal);
    private static readonly HashSet<string> EscSafe = new(UiCatalog.EscSafe, StringComparer.Ordinal);
    private static readonly HashSet<string> PrefixExempt = new(UiCatalog.ForbiddenPrefixExempt, StringComparer.Ordinal);
    private static readonly HashSet<string> NeverLearned = new(UiCatalog.NeverLearnedStack, StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte> _learned = new(StringComparer.Ordinal);

    /// <summary>Creates the classifier (no side effects).</summary>
    public UiClassifier()
    {
    }

    /// <inheritdoc />
    public UiKind Classify(string uiName)
    {
        ArgumentNullException.ThrowIfNull(uiName);
        if (IsForbidden(uiName))
        {
            return UiKind.Forbidden;
        }

        if (SocialExact.Contains(uiName))
        {
            return UiKind.Social;
        }

        if (IsBusy(uiName))
        {
            return UiKind.Busy;
        }

        if (StationExact.Contains(uiName))
        {
            return UiKind.Station;
        }

        if (StackSeed.Contains(uiName) || _learned.ContainsKey(uiName))
        {
            return UiKind.Stack;
        }

        return OverlaySeed.Contains(uiName) ? UiKind.Overlay : UiKind.Unknown;
    }

    /// <inheritdoc />
    public bool IsEscSafe(string uiName)
    {
        ArgumentNullException.ThrowIfNull(uiName);
        return EscSafe.Contains(uiName);
    }

    /// <inheritdoc />
    public bool IsLearnedStack(string uiName)
    {
        ArgumentNullException.ThrowIfNull(uiName);
        return _learned.ContainsKey(uiName);
    }

    /// <summary>Records a name seen in '[ui mgr]hide top ui X' (called by the log tracker; not part of the contract).</summary>
    internal void LearnStack(string uiName)
    {
        if (!string.IsNullOrEmpty(uiName) && !NeverLearned.Contains(uiName))
        {
            _learned.TryAdd(uiName, 0);
        }
    }

    private static bool IsForbidden(string name)
    {
        if (ForbiddenExact.Contains(name))
        {
            return true;
        }

        foreach (var p in UiCatalog.ForbiddenPrefixes)
        {
            if (name.StartsWith(p, StringComparison.Ordinal) && !PrefixExempt.Contains(name))
            {
                return true;
            }
        }

        var upper = name.ToUpperInvariant();
        foreach (var c in UiCatalog.ForbiddenContains)
        {
            if (upper.Contains(c, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBusy(string name)
    {
        if (BusyExact.Contains(name))
        {
            return true;
        }

        foreach (var p in UiCatalog.BusyPrefixes)
        {
            if (name.StartsWith(p, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
