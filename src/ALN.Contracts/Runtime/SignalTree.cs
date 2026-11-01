namespace ALN.Contracts;

/// <summary>One record with its children, ordered by sequence.</summary>
public sealed record SignalNode(SignalRecord Record, IReadOnlyList<SignalNode> Children)
{
    /// <summary>True when this node is where the flow split into concurrent lanes.</summary>
    public bool Forks => Children.Count > 1;
}

/// <summary>
/// Assembles the one tree the log describes. Forking is not guessed from timestamps: a node has as
/// many lanes as it has children, and each child is a distinct record id.
/// A child whose parent is absent (filtered by MinLevel, rotated away, or written by another
/// process) becomes a root rather than disappearing — an incomplete picture beats a silent one.
/// </summary>
public static class SignalTree
{
    public static IReadOnlyList<SignalNode> Build(IEnumerable<SignalRecord> records)
    {
        var ordered = records.Where(r => r != null).OrderBy(r => r.Sequence).ToArray();
        var ids = ordered.Select(r => r.Id).ToHashSet();
        var children = new Dictionary<Guid, List<SignalRecord>>();
        var roots = new List<SignalRecord>();
        foreach (var record in ordered)
        {
            if (record.ParentId is { } parent && ids.Contains(parent))
            {
                if (!children.TryGetValue(parent, out var siblings))
                    children[parent] = siblings = [];
                siblings.Add(record);
            }
            else roots.Add(record);
        }

        var built = new Dictionary<Guid, SignalNode>();
        SignalNode Build(SignalRecord record, HashSet<Guid> path)
        {
            if (built.TryGetValue(record.Id, out var existing)) return existing;
            // Defensive: a malformed cycle must not recurse forever.
            if (!path.Add(record.Id)) return new SignalNode(record, []);
            var kids = children.TryGetValue(record.Id, out var siblings)
                ? siblings.Select(child => Build(child, path)).ToArray()
                : [];
            path.Remove(record.Id);
            var node = new SignalNode(record, kids);
            built[record.Id] = node;
            return node;
        }

        return roots.Select(root => Build(root, [])).ToArray();
    }
}
