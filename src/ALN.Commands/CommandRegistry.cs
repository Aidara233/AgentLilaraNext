using System.Text;
using ALN.Contracts;

namespace ALN.Commands;

/// <summary>
/// The instruction table, and the walk from a line of text to the leaf that answers it.
///
/// Two things it deliberately does not do: it does not read a clock of its own (a rate limit you cannot test is a rate
/// limit you do not have), and it does not decide who anybody is — a caller's depth arrives already decided by the
/// identity table, because deciding it here would be a second place where "who is this" gets answered.
/// </summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private const int MaxDepth = 3;
    private const int SeenCapacity = 512;

    private readonly Dictionary<string, CommandNode> _nodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _implicit = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, DateTimeOffset>>> _seen = new(StringComparer.Ordinal);
    private readonly LinkedList<KeyValuePair<string, DateTimeOffset>> _seenOrder = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _throttle = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly ISignalLog? _signals;
    private readonly IAuditSink? _audit;
    private readonly TimeProvider _time;

    public CommandRegistry(CommandOptions options, ISignalLog? signals = null, IAuditSink? audit = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Prefix))
            throw new ArgumentException("An instruction prefix is required: without one every message is an instruction.",
                nameof(options));
        Options = options;
        _signals = signals;
        _audit = audit;
        _time = time ?? TimeProvider.System;
    }

    public CommandOptions Options { get; }

    public bool LooksLike(string? text) =>
        text is not null && text.TrimStart().StartsWith(Options.Prefix, StringComparison.Ordinal);

    public Result Register(CommandNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var segments = node.Path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is 0 or > MaxDepth)
            return Result.Failure(new(ErrorCode.Invalid,
                $"A command path is one to {MaxDepth} segments: '{node.Path}'."));
        foreach (var segment in segments)
            if (!IsSegment(segment))
                return Result.Failure(new(ErrorCode.Invalid,
                    $"'{segment}' is not a command name: a-z, 0-9, '_' and '-', at most 32."));

        var path = string.Join('.', segments).ToLowerInvariant();
        var normalized = node with { Path = path };

        lock (_gate)
        {
            if (_nodes.TryGetValue(path, out _))
            {
                // A namespace that exists only because a leaf needed a parent can be given a real registration later:
                // it is the same node, with something to say this time. Anything else is a conflict.
                if (!_implicit.Remove(path))
                    return Result.Failure(new(ErrorCode.Conflict, $"'{path}' is already registered."));
            }

            // Leaf or namespace, never both (see CommandNode): this is what keeps "//order a" unambiguous.
            if (normalized.IsLeaf && _nodes.Keys.Any(key => key.StartsWith(path + ".", StringComparison.Ordinal)))
                return Result.Failure(new(ErrorCode.Conflict,
                    $"'{path}' already has sub-instructions, so it cannot be a command itself."));

            var parts = path.Split('.');
            for (var i = 1; i < parts.Length; i++)
            {
                var ancestor = string.Join('.', parts[..i]);
                if (_nodes.TryGetValue(ancestor, out var parent) && parent.IsLeaf)
                    return Result.Failure(new(ErrorCode.Conflict,
                        $"'{ancestor}' is a command, so it cannot have sub-instructions ('{path}')."));
            }

            for (var i = 1; i < parts.Length; i++)
            {
                var ancestor = string.Join('.', parts[..i]);
                if (_nodes.ContainsKey(ancestor)) continue;
                _nodes[ancestor] = new CommandNode(ancestor, string.Empty, Owner: normalized.Owner);
                _implicit.Add(ancestor);
            }

            _nodes[path] = normalized;
            return Result.Success();
        }
    }

    public Result Unregister(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return Result.Failure(new(ErrorCode.Invalid, "An owner is required."));
        lock (_gate)
        {
            var doomed = _nodes.Values
                .Where(node => string.Equals(node.Owner, owner, StringComparison.Ordinal))
                .Select(node => node.Path)
                .ToList();
            foreach (var path in doomed)
            {
                _nodes.Remove(path);
                _implicit.Remove(path);
            }
            Sweep();
            return Result.Success();
        }
    }

    public IReadOnlyList<CommandNode> Visible(AccessLevel caller)
    {
        lock (_gate)
            return _nodes.Values
                .Where(node => node.MinLevel <= caller)
                .OrderBy(node => node.Path, StringComparer.Ordinal)
                .ToList();
    }

    public async ValueTask<Result<CommandOutcome>> InvokeAsync(CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var text = invocation.Text.Trim();
        if (!text.StartsWith(Options.Prefix, StringComparison.Ordinal))
            return Result<CommandOutcome>.Failure(new(ErrorCode.Invalid,
                $"Not an instruction: they start with '{Options.Prefix}'."));

        var rest = text[Options.Prefix.Length..].TrimStart();
        if (rest.Length == 0)
            return Result<CommandOutcome>.Failure(new(ErrorCode.Invalid,
                $"There is nothing after the prefix. Try {Options.Prefix}help."));

        if (!TrySplit(rest, out var words, out var splitError))
            return Result<CommandOutcome>.Failure(new(ErrorCode.Invalid, splitError!));

        // A redelivered message must not run twice: the Channel's duplicate check is downstream of here and will never
        // see this message (it does not create an envelope row), so the counter has to live here.
        if (!FirstTime(invocation))
            return Result<CommandOutcome>.Success(CommandOutcome.Silent("duplicate"));

        var caller = invocation.Person?.Level ?? AccessLevel.Agent;
        var typed = Options.Prefix + words[0];

        if (Throttled(invocation))
        {
            // Silence, on purpose: answering every extra message would make the limit the amplifier.
            await RecordAsync(invocation, typed, "throttled", "too many instructions from this sender",
                SignalLevel.Warn, cancellationToken).ConfigureAwait(false);
            return Result<CommandOutcome>.Success(CommandOutcome.Silent("throttled"));
        }

        if (!TryResolve(words, caller, out var node, out var arguments, out var resolveError))
        {
            await RecordAsync(invocation, typed, "invalid", resolveError, SignalLevel.Warn, cancellationToken)
                .ConfigureAwait(false);
            return Result<CommandOutcome>.Failure(new(ErrorCode.Invalid, resolveError!));
        }

        if (node.MinLevel > caller)
        {
            var message = $"{Options.Prefix}{node.Path} 需要 {AccessLevels.Describe(node.MinLevel)}，"
                          + $"你是 {AccessLevels.Describe(caller)}。";
            await RecordAsync(invocation, Options.Prefix + node.Path, "denied", message, SignalLevel.Warn,
                cancellationToken).ConfigureAwait(false);
            return Result<CommandOutcome>.Failure(new(ErrorCode.Denied, message));
        }

        if (!node.IsLeaf)
        {
            // "//order" alone is a question, not a mistake: answering it with the sub-instructions is the help.
            await RecordAsync(invocation, Options.Prefix + node.Path, "listed", null, SignalLevel.Info, cancellationToken)
                .ConfigureAwait(false);
            return Result<CommandOutcome>.Success(CommandOutcome.Said(Render(node.Path, caller)));
        }

        var context = new CommandContext(node.Path, arguments, invocation.Person, caller, invocation.Adapter,
            invocation.PlatformConversationId, invocation.Sender, invocation.SenderName);

        Result<string> ran;
        try
        {
            ran = await node.Body!(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = $"{Options.Prefix}{node.Path} 执行出错：{ex.Message}";
            await RecordAsync(invocation, Options.Prefix + node.Path, "failed", ex.Message, SignalLevel.Warn,
                cancellationToken).ConfigureAwait(false);
            return Result<CommandOutcome>.Failure(new(ErrorCode.Internal, message));
        }

        if (!ran.Ok)
        {
            await RecordAsync(invocation, Options.Prefix + node.Path, ran.Error?.Code.ToString() ?? "failed",
                ran.Error?.Message, SignalLevel.Warn, cancellationToken).ConfigureAwait(false);
            return Result<CommandOutcome>.Failure(ran.Error ?? new(ErrorCode.Internal, "指令失败。"));
        }

        await RecordAsync(invocation, Options.Prefix + node.Path, "ok", null, SignalLevel.Info, cancellationToken)
            .ConfigureAwait(false);
        return Result<CommandOutcome>.Success(CommandOutcome.Said(ran.Value ?? string.Empty));
    }

    /// <summary>The top of the tree, as this caller may see it.</summary>
    public string Render(AccessLevel caller) => Render(string.Empty, caller);

    /// <summary>
    /// What a namespace contains, or what a leaf is for. Instructions this caller could not run are left out entirely:
    /// listing an instruction somebody cannot use only teaches him it exists, which is not a thing help needs to do.
    /// </summary>
    public string Render(string path, AccessLevel caller)
    {
        var children = Children(path, caller);
        if (children.Count == 0)
            return path.Length == 0
                ? "现在没有你能用的指令。"
                : $"{Options.Prefix}{path} 下没有你能用的指令。";

        var lines = new List<string>
        {
            path.Length == 0
                ? $"可用的指令（你是 {AccessLevels.Describe(caller)}）："
                : $"{Options.Prefix}{path} 下有 {children.Count} 条："
        };
        foreach (var child in children.Take(Options.MaxHelpLines))
        {
            var spelled = Options.Prefix + child.Path;
            var tail = child.IsLeaf ? child.Summary : "（有子指令）";
            lines.Add($"  {spelled.PadRight(18)}{tail}");
        }
        if (children.Count > Options.MaxHelpLines)
            lines.Add($"  …还有 {children.Count - Options.MaxHelpLines} 条，用 {Options.Prefix}help <名字> 收窄。");
        return string.Join('\n', lines);
    }

    private IReadOnlyList<CommandNode> Children(string path, AccessLevel caller)
    {
        var depth = path.Length == 0 ? 0 : path.Count(character => character == '.') + 1;
        lock (_gate)
            return _nodes.Values
                .Where(node => path.Length == 0
                    ? !node.Path.Contains('.')
                    : node.Path.StartsWith(path + ".", StringComparison.Ordinal) &&
                      node.Path.Count(character => character == '.') == depth)
                .Where(node => node.MinLevel <= caller)
                .OrderBy(node => node.Path, StringComparer.Ordinal)
                .ToList();
    }

    private bool TryResolve(IReadOnlyList<string> words, AccessLevel caller, out CommandNode node,
        out IReadOnlyList<string> arguments, out string? error)
    {
        node = null!;
        arguments = [];
        error = null;
        var current = string.Empty;
        for (var i = 0; i < words.Count; i++)
        {
            var candidate = current.Length == 0
                ? words[i].ToLowerInvariant()
                : $"{current}.{words[i].ToLowerInvariant()}";
            CommandNode? found;
            lock (_gate) _nodes.TryGetValue(candidate, out found);
            if (found is null)
            {
                if (current.Length == 0)
                {
                    error = $"{Options.Prefix}{words[0]}：没有这条指令。{Options.Prefix}help 看看有什么。";
                    return false;
                }
                var names = Children(current, caller).Select(child => child.Name).ToList();
                error = names.Count == 0
                    ? $"{Options.Prefix}{current} 下没有 {words[i]}。"
                    : $"{Options.Prefix}{current} 下没有 {words[i]}：{string.Join('、', names)}。";
                return false;
            }
            current = candidate;
            if (found.IsLeaf)
            {
                node = found;
                arguments = words.Skip(i + 1).ToList();
                return true;
            }
        }

        CommandNode? last;
        lock (_gate) _nodes.TryGetValue(current, out last);
        if (last is not null)
        {
            node = last;
            return true;
        }
        error = $"{Options.Prefix}{words[0]}：没有这条指令。{Options.Prefix}help 看看有什么。";
        return false;
    }

    /// <summary>
    /// Split on whitespace, keeping a quoted run together. No pipes, no redirection, no nested sub-commands: those are
    /// a shell's shape, and this is not a shell (the shell verb is one of the things this project removed).
    /// </summary>
    private static bool TrySplit(string text, out List<string> words, out string? error)
    {
        words = [];
        error = null;
        var current = new StringBuilder();
        var quoted = false;
        var opened = false;
        foreach (var character in text)
        {
            if (character == '"')
            {
                quoted = !quoted;
                opened |= quoted;
                continue;
            }
            if (!quoted && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }
        if (quoted)
        {
            error = "引号没有闭合。";
            return false;
        }
        if (current.Length > 0) words.Add(current.ToString());
        if (words.Count == 0)
        {
            error = "前缀后面没有指令名。";
            return false;
        }
        if (words.Count > 1 && opened && words[0].Length == 0)
        {
            error = "指令名不能是空的引号。";
            return false;
        }
        return true;
    }

    private static bool IsSegment(string segment)
    {
        if (segment.Length is 0 or > 32) return false;
        if (segment[0] is < 'a' or > 'z') return false;
        foreach (var character in segment)
            if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))
                return false;
        return true;
    }

    private void Sweep()
    {
        bool removed;
        do
        {
            removed = false;
            foreach (var path in _implicit.ToList())
            {
                if (!_nodes.ContainsKey(path)) { _implicit.Remove(path); continue; }
                if (_nodes.Keys.Any(key => key.StartsWith(path + ".", StringComparison.Ordinal))) continue;
                _nodes.Remove(path);
                _implicit.Remove(path);
                removed = true;
            }
        } while (removed);
    }

    /// <summary>True when this platform message has not been seen before. Redelivery is the Adapter's habit, not a bug.</summary>
    private bool FirstTime(CommandInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.PlatformMessageId)) return true;
        var key = $"{invocation.Adapter}\u001f{invocation.PlatformMessageId}";
        lock (_gate)
        {
            if (_seen.TryGetValue(key, out var node))
            {
                _seenOrder.Remove(node);
                _seenOrder.AddLast(node);
                return false;
            }
            var added = _seenOrder.AddLast(new KeyValuePair<string, DateTimeOffset>(key, _time.GetUtcNow()));
            _seen[key] = added;
            while (_seenOrder.Count > SeenCapacity)
            {
                var oldest = _seenOrder.First!;
                _seenOrder.RemoveFirst();
                _seen.Remove(oldest.Value.Key);
            }
            return true;
        }
    }

    private bool Throttled(CommandInvocation invocation)
    {
        if (Options.PerSenderPerMinute <= 0) return false;
        var key = $"{invocation.Adapter}\u001f{invocation.Sender ?? string.Empty}";
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_throttle.TryGetValue(key, out var window) && now - window.Start < TimeSpan.FromMinutes(1))
            {
                if (window.Count >= Options.PerSenderPerMinute) return true;
                _throttle[key] = (window.Start, window.Count + 1);
                return false;
            }
            if (_throttle.Count > 1024) _throttle.Clear();
            _throttle[key] = (now, 1);
            return false;
        }
    }

    private async ValueTask RecordAsync(CommandInvocation invocation, string typed, string outcome, string? detail,
        SignalLevel level, CancellationToken cancellationToken)
    {
        var actor = invocation.Person is { } person ? $"command:{person.Id}" : "command:unknown";
        _signals?.Write(new SignalRecord
        {
            Group = "command",
            Name = typed,
            StepId = invocation.EventId,
            From = actor,
            To = invocation.Adapter.ToString(),
            Mechanism = "command",
            Outcome = outcome,
            Level = level,
            Detail = detail is null ? null : new Dictionary<string, string> { ["message"] = detail }
        });
        if (_audit is not null)
            await _audit.AppendAsync(new AuditRecord(DateTimeOffset.UtcNow, invocation.EventId, actor,
                invocation.Adapter, "command." + typed[Options.Prefix.Length..], outcome, detail), cancellationToken)
                .ConfigureAwait(false);
    }
}
