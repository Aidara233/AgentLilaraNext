using System.Security.Cryptography;
using System.Text;
using ALN.Contracts;
using ALN.Contracts.Prompts;

namespace ALN.Runtime;

/// <summary>
/// Prompts as files: <c>&lt;root&gt;/&lt;group&gt;/&lt;name&gt;.md</c>. A group is a kind of instruction, a file is
/// one version of it, and a Session picks at most one file per group.
///
/// Nothing is cached. A prompt is edited by a person and must take effect on the next turn, so every use
/// reads the file — the same order of cost as reading the session's own state, and it deletes a whole class
/// of "which copy am I actually running" questions.
///
/// Failures are split on purpose: a file that cannot be read is skipped and reported (the rest of the
/// group, the host and every other Session keep working), while a file that was *asked for* and cannot be
/// served fails that turn with the list of what is available. Continuing with a different persona than the
/// one the caller chose is worse than not answering this time.
/// </summary>
public sealed class PromptLibrary : IPromptLibrary
{
    private readonly string[] _roots;

    /// <param name="roots">
    /// Where to look, in order of precedence: the <b>last</b> root wins when two of them have the same
    /// group and file. In the host that is "shipped defaults, then the operator's data root", so copying a
    /// file out of the release and editing it is how you change a prompt.
    /// </param>
    public PromptLibrary(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = roots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(Path.GetFullPath).ToArray();
        if (_roots.Length == 0) throw new ArgumentException("At least one prompt root is required.", nameof(roots));
    }

    /// <summary>The roots in precedence order, for a message that has to say where to look.</summary>
    public IReadOnlyList<string> Roots => _roots;

    public PromptCatalog List()
    {
        var groups = new Dictionary<string, Dictionary<string, PromptEntry>>(StringComparer.Ordinal);
        var warnings = new List<string>();
        // Walked in precedence order, so a later root replaces an earlier one point by point: the operator can
        // override one prompt without having to reproduce the whole shipped tree.
        foreach (var root in _roots)
        {
            foreach (var groupDirectory in Directories(root))
            {
                var group = Path.GetFileName(groupDirectory);
                if (!IsName(group)) continue;
                if (!groups.TryGetValue(group, out var entries))
                    groups[group] = entries = new Dictionary<string, PromptEntry>(StringComparer.Ordinal);
                foreach (var path in Documents(groupDirectory))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    if (!IsName(name)) continue;
                    var skipped = Read(path, out var bytes, out var fingerprint);
                    if (skipped is not null)
                    {
                        // Skipped, not fatal: one unreadable file must not hide the other prompts or stop the host.
                        warnings.Add($"{group}/{name}{PromptLimits.Extension}: {skipped}");
                        continue;
                    }
                    entries[name] = new PromptEntry(name, bytes, fingerprint, path);
                }
            }
        }
        return new PromptCatalog(
            groups.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new PromptGroup(pair.Key, pair.Value.Values.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray()))
                .ToArray(),
            warnings);
    }

    public async ValueTask<Result<PromptSet>> ResolveAsync(PromptSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.IsEmpty)
            return Result<PromptSet>.Success(new PromptSet(string.Empty, [], EmptyFingerprint));

        var catalog = List();
        var parts = new List<PromptPart>();
        var texts = new List<string>();
        var total = 0L;
        foreach (var (group, name) in selection.Ordered())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var known = catalog.Groups.FirstOrDefault(candidate => string.Equals(candidate.Group, group, StringComparison.Ordinal));
            if (known is null)
                return Result<PromptSet>.Failure(new(ErrorCode.Invalid,
                    $"No prompt group '{group}'. Available groups: {DescribeGroups(catalog)}.",
                    DescribeWarnings(catalog.Warnings)));

            var entry = known.Entries.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
            if (entry is null)
                return Result<PromptSet>.Failure(new(ErrorCode.Invalid,
                    $"No usable prompt '{name}' in group '{group}'. Available: {DescribeEntries(known)}.",
                    DescribeWarnings(catalog.Warnings)));

            string text;
            long bytes;
            string fingerprint;
            try
            {
                var payload = await File.ReadAllBytesAsync(entry.Path, cancellationToken).ConfigureAwait(false);
                bytes = payload.LongLength;
                fingerprint = Convert.ToHexString(SHA256.HashData(payload))[..16].ToLowerInvariant();
                text = Encoding.UTF8.GetString(payload).TrimStart('\uFEFF').Trim();
                if (bytes > PromptLimits.MaxFileBytes)
                    return Result<PromptSet>.Failure(new(ErrorCode.Invalid,
                        $"Prompt is larger than {PromptLimits.MaxFileBytes} bytes: {group}/{name}{PromptLimits.Extension}."));
                if (text.Length == 0)
                    return Result<PromptSet>.Failure(new(ErrorCode.Invalid,
                        $"Prompt is empty: {group}/{name}{PromptLimits.Extension}. An empty file is almost always a half-finished edit."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result<PromptSet>.Failure(new(ErrorCode.Unavailable,
                    $"Prompt could not be read: {entry.Path}.", ex.Message));
            }

            total += bytes;
            if (total > PromptLimits.MaxTotalBytes)
                return Result<PromptSet>.Failure(new(ErrorCode.Invalid,
                    $"Selected prompts exceed {PromptLimits.MaxTotalBytes} bytes in total."));
            parts.Add(new PromptPart(group, name, bytes, fingerprint));
            texts.Add(text);
        }

        // A blank line between groups: each file was written as prose, and gluing two paragraphs together
        // without a boundary is how an instruction ends up reading as part of the previous sentence.
        var set = string.Join("\n\n", texts);
        return Result<PromptSet>.Success(new PromptSet(set, parts, Fingerprint(parts)));
    }

    /// <summary>The same set of files must always give the same fingerprint, so the parts are ordered by group.</summary>
    private static string Fingerprint(IReadOnlyList<PromptPart> parts)
    {
        var material = string.Join("\n", parts.Select(part => $"{part.Group}/{part.Name}:{part.Fingerprint}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant();
    }

    private static string EmptyFingerprint { get; } =
        Convert.ToHexString(SHA256.HashData([]))[..16].ToLowerInvariant();

    /// <summary>Reads one file for the catalog; returns why it was skipped, or null when it is usable.</summary>
    private static string? Read(string path, out long bytes, out string fingerprint)
    {
        bytes = 0;
        fingerprint = string.Empty;
        try
        {
            var info = new FileInfo(path);
            bytes = info.Length;
            if (bytes > PromptLimits.MaxFileBytes) return $"larger than {PromptLimits.MaxFileBytes} bytes";
            var payload = File.ReadAllBytes(path);
            fingerprint = Convert.ToHexString(SHA256.HashData(payload))[..16].ToLowerInvariant();
            if (Encoding.UTF8.GetString(payload).TrimStart('\uFEFF').Trim().Length == 0) return "empty";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static IEnumerable<string> Directories(string root) =>
        Directory.Exists(root)
            ? Directory.GetDirectories(root).Order(StringComparer.Ordinal)
            : [];

    private static IEnumerable<string> Documents(string directory) =>
        Directory.GetFiles(directory, "*" + PromptLimits.Extension).Order(StringComparer.Ordinal);

    /// <summary>Group and file names appear in a spec and in a path, so a separator or a traversal is refused.</summary>
    private static bool IsName(string value) =>
        value is { Length: > 0 and <= 64 } &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        value.IndexOfAny(['/', '\\', ':']) < 0 &&
        value is not "." and not "..";

    private static string DescribeGroups(PromptCatalog catalog) =>
        catalog.Groups.Count == 0 ? "(none)" : string.Join(", ", catalog.Groups.Select(group => group.Group));

    private static string DescribeEntries(PromptGroup group) =>
        group.Entries.Count == 0 ? "(none)" : string.Join(", ", group.Entries.Select(entry => entry.Name));

    private static string? DescribeWarnings(IReadOnlyList<string> warnings) =>
        warnings.Count == 0 ? null : string.Join("; ", warnings);
}
