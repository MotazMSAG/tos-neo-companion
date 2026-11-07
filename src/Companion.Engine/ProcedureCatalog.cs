using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Navigation;
using Companion.Contracts.Perception;
using Companion.Contracts.Vision;
using Companion.Engine.Internal;

namespace Companion.Engine;

/// <summary>
/// Procedures loaded once at host start (core data\procedures.json + features) and sealed before the first job.
/// <see cref="Add"/> validates structure, ids and limits (ceilings) and throws <see cref="InvalidDataException"/> on a
/// broken procedure or a duplicate id. References to templates, readers, screens and other procedures are checked at
/// <see cref="Seal"/> (when every catalog is filled, whatever the load order): a procedure with a dangling reference is
/// removed from the catalog and journaled as "procedure.rejected" (it can never run). Thread-safe.
/// </summary>
public sealed class ProcedureCatalog : IProcedureCatalogBuilder
{
    private readonly ITemplateCatalog _templates;
    private readonly IReaderCatalog _readers;
    private readonly IUiMapCatalog _uiMap;
    private readonly IJournal _journal;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, (ProcedureDefinition Def, string Source)> _items = new(StringComparer.Ordinal);
    private readonly List<string> _rejected = [];
    private bool _sealed;

    /// <summary>Creates an empty catalog (no side effects).</summary>
    public ProcedureCatalog(ITemplateCatalog templates, IReaderCatalog readers, IUiMapCatalog uiMap, IJournal journal, IClock clock)
    {
        _templates = templates;
        _readers = readers;
        _uiMap = uiMap;
        _journal = journal;
        _clock = clock;
    }

    /// <summary>Rejections at seal time ("source|procedure|reference").</summary>
    internal IReadOnlyList<string> Rejected
    {
        get
        {
            lock (_gate)
            {
                return [.. _rejected];
            }
        }
    }

    /// <inheritdoc />
    public ProcedureDefinition? Get(string id)
    {
        lock (_gate)
        {
            return id is not null && _items.TryGetValue(id, out var v) ? v.Def : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ProcedureDefinition> All
    {
        get
        {
            lock (_gate)
            {
                return _items.Values.Select(v => v.Def).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <inheritdoc />
    public void Add(ProcedureDefinition procedure, string sourceCode)
    {
        lock (_gate)
        {
            if (_sealed)
            {
                throw new InvalidOperationException("procedure.catalog.sealed");
            }
            var problems = StepValidation.Validate(procedure);
            if (problems.Count > 0)
            {
                throw new InvalidDataException($"procedure.invalid:{procedure?.Id}:{string.Join(",", problems.Take(5))}");
            }
            if (_items.ContainsKey(procedure.Id))
            {
                throw new InvalidDataException("procedure.duplicate:" + procedure.Id);
            }
            _items[procedure.Id] = (procedure, sourceCode ?? "unknown");
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
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (id, (def, source)) in _items.ToList())
                {
                    var missing = MissingReference(def);
                    if (missing is null)
                    {
                        continue;
                    }
                    _items.Remove(id);
                    _rejected.Add($"{source}|{id}|{missing}");
                    Journal(source, id, missing);
                    changed = true; // callers of a removed procedure must go too
                }
            }
            _sealed = true;
        }
    }

    private string? MissingReference(ProcedureDefinition def)
    {
        var refs = StepValidation.References(def);
        foreach (var t in refs.Templates)
        {
            if (_templates.Get(t) is null)
            {
                return "template:" + t;
            }
        }
        foreach (var r in refs.Readers)
        {
            if (_readers.Get(r) is null)
            {
                return "reader:" + r;
            }
        }
        foreach (var s in refs.Screens)
        {
            if (_uiMap.GetScreen(s) is null)
            {
                return "screen:" + s;
            }
        }
        foreach (var p in refs.Procedures)
        {
            if (!_items.ContainsKey(p))
            {
                return "procedure:" + p;
            }
        }
        return null;
    }

    private void Journal(string source, string id, string missing)
    {
        try
        {
            _journal.Write(new JournalRecord(_clock.UtcNow, JournalKind.FeatureLoad, null, JournalSchema.Code(id), null,
                new Dictionary<string, object?>
                {
                    ["code"] = "procedure.rejected",
                    ["source"] = JournalSchema.Code(source),
                    ["missing"] = JournalSchema.Code(missing),
                }));
        }
        catch
        {
            // journal never throws into control flow
        }
    }
}
