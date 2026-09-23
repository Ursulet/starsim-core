using System.Collections.Immutable;

namespace StarSimCore.Application.Processing;

public sealed record HistoryEntry(PipelineSnapshot Snapshot, string Description, DateTimeOffset Timestamp);

public sealed class ParameterHistory
{
    private readonly IReadOnlyList<IImageProcessor> processorDefinitions;
    private readonly Stack<HistoryEntry> undo = new();
    private readonly Stack<HistoryEntry> redo = new();
    private bool isCoalescing;
    private string? currentCoalesceKey;
    private DateTimeOffset currentTimestamp;

    public ParameterHistory(
        PipelineSnapshot initial,
        string initialDescription = "Initial State",
        IReadOnlyList<IImageProcessor>? processorDefinitions = null)
    {
        this.processorDefinitions = processorDefinitions ?? BuiltInProcessors.All;
        Current = initial;
        CurrentDescription = initialDescription;
        currentTimestamp = DateTimeOffset.UtcNow;
    }

    public PipelineSnapshot Current { get; private set; }
    public string CurrentDescription { get; private set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;
    public int CurrentTimelineIndex => undo.Count;

    /// <summary>
    /// Returns the complete reachable history in chronological order. Reading this
    /// collection never mutates the current snapshot or the undo/redo stacks.
    /// </summary>
    public IReadOnlyList<HistoryEntry> Timeline
    {
        get
        {
            var entries = new List<HistoryEntry>(undo.Count + redo.Count + 1);
            entries.AddRange(undo.Reverse());
            entries.Add(new HistoryEntry(Current, CurrentDescription, currentTimestamp));
            entries.AddRange(redo);
            return entries;
        }
    }

    public void Apply(
        PipelineSnapshot next,
        string description = "Parameter Change",
        bool coalesce = false,
        string? coalesceKey = null)
    {
        if (next == Current) return;

        if (coalesce && isCoalescing && (coalesceKey == null || coalesceKey == currentCoalesceKey))
        {
            Current = next;
            CurrentDescription = description;
            currentTimestamp = DateTimeOffset.UtcNow;
            return;
        }

        undo.Push(new HistoryEntry(Current, CurrentDescription, currentTimestamp));
        Current = next;
        CurrentDescription = description;
        currentTimestamp = DateTimeOffset.UtcNow;
        isCoalescing = coalesce;
        currentCoalesceKey = coalesce ? coalesceKey : null;
        redo.Clear();
    }

    public void EndCoalescing()
    {
        isCoalescing = false;
        currentCoalesceKey = null;
        if (undo.TryPeek(out var previous) && ModulesEqual(previous.Snapshot, Current))
        {
            undo.Pop();
        }
    }

    public bool Undo()
    {
        EndCoalescing();
        if (!undo.TryPop(out var previous)) return false;
        redo.Push(new HistoryEntry(Current, CurrentDescription, currentTimestamp));
        Current = previous.Snapshot;
        CurrentDescription = previous.Description;
        currentTimestamp = previous.Timestamp;
        return true;
    }

    public bool Redo()
    {
        EndCoalescing();
        if (!redo.TryPop(out var next)) return false;
        undo.Push(new HistoryEntry(Current, CurrentDescription, currentTimestamp));
        Current = next.Snapshot;
        CurrentDescription = next.Description;
        currentTimestamp = next.Timestamp;
        return true;
    }

    /// <summary>
    /// Moves to any state currently visible in <see cref="Timeline"/> while
    /// preserving ordinary Undo/Redo behavior around the selected state.
    /// </summary>
    public bool JumpToTimelineIndex(int index)
    {
        EndCoalescing();
        var count = undo.Count + redo.Count + 1;
        if ((uint)index >= (uint)count) return false;

        var changed = false;
        while (undo.Count > index)
            changed |= Undo();
        while (undo.Count < index)
            changed |= Redo();
        return changed;
    }

    public void ResetAll()
    {
        EndCoalescing();
        var modules = processorDefinitions
            .Select(processor => new ProcessorState(
                processor.Id,
                false,
                processor.ParameterSchema.Select(parameter => parameter.DefaultValue).ToImmutableArray()))
            .ToImmutableArray();
        Apply(new PipelineSnapshot(modules, Current.Revision + 1), "Reset All Modules (neutral bypass)");
    }

    public void ResetModule(string id)
    {
        EndCoalescing();
        var definition = processorDefinitions.First(processor => processor.Id == id);
        var modules = Current.Modules.ToBuilder();
        var index = -1;
        for (var candidate = 0; candidate < modules.Count; candidate++)
        {
            if (modules[candidate].Id == id)
            {
                index = candidate;
                break;
            }
        }
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(id));
        modules[index] = new ProcessorState(
            id,
            definition.IsEnabledByDefault,
            definition.ParameterSchema.Select(parameter => parameter.DefaultValue).ToImmutableArray());
        Apply(new PipelineSnapshot(modules.ToImmutable(), Current.Revision + 1), $"Reset Module {id}");
    }

    public void ResetCategory(string category)
    {
        EndCoalescing();
        var definitions = processorDefinitions
            .Where(processor => string.Equals(processor.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(processor => processor.Id, StringComparer.Ordinal);
        if (definitions.Count == 0) return;
        var modules = Current.Modules.ToBuilder();
        for (var index = 0; index < modules.Count; index++)
        {
            var state = modules[index];
            if (!definitions.TryGetValue(state.Id, out var definition)) continue;
            modules[index] = new ProcessorState(
                definition.Id,
                definition.IsEnabledByDefault,
                definition.ParameterSchema.Select(parameter => parameter.DefaultValue).ToImmutableArray());
        }
        Apply(new PipelineSnapshot(modules.ToImmutable(), Current.Revision + 1), $"Reset Category {category}");
    }

    public void Clear(PipelineSnapshot snapshot, string description = "Initial State")
    {
        EndCoalescing();
        undo.Clear();
        redo.Clear();
        Current = snapshot;
        CurrentDescription = description;
        currentTimestamp = DateTimeOffset.UtcNow;
    }

    public static bool ModulesEqual(PipelineSnapshot? a, PipelineSnapshot? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Modules.Length != b.Modules.Length) return false;
        for (var i = 0; i < a.Modules.Length; i++)
        {
            var ma = a.Modules[i];
            var mb = b.Modules[i];
            if (!string.Equals(ma.Id, mb.Id, StringComparison.Ordinal) ||
                ma.Enabled != mb.Enabled ||
                ma.Parameters.Length != mb.Parameters.Length)
                return false;

            for (var p = 0; p < ma.Parameters.Length; p++)
            {
                if (Math.Abs(ma.Parameters[p] - mb.Parameters[p]) > 1e-9)
                    return false;
            }
        }
        return true;
    }
}
