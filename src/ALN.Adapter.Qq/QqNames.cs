using System.Globalization;
using ALN.Contracts;

namespace ALN.Adapter.Qq;

/// <summary>
/// Who people are, as far as this account can tell: the name they are shown under (the group card in a group, the
/// nickname everywhere else) and the remark that belongs to this account rather than to them.
///
/// The whole design here is caching, because the alternative is a platform round trip per message. The friend list is
/// the only carrier of remarks, so it is fetched once and kept for a while — a remark changes when the owner renames
/// somebody, which is not a per-message event. A member name is kept per (place, person) for the same reason.
///
/// The message path never waits for any of this. A message is worth more than its display name, so reading a remark
/// while publishing is a plain dictionary lookup (<see cref="Remembered"/>) and <see cref="Nudge"/> is what asks the
/// platform — once, in the background. The first messages after a start therefore go out without a remark, and every
/// message after that has one. The questions a caller asks explicitly (<see cref="ResolveAsync"/>) do wait, because a
/// caller asking by name wants the answer and not a guess.
/// </summary>
internal sealed class QqNames(OneBotConnection connection, IModuleLogger log)
{
    /// <summary>How long a fetched friend list is believed. Long enough to cost nothing, short enough that a rename
    /// shows up while the conversation is still warm.</summary>
    private static readonly TimeSpan FriendsFresh = TimeSpan.FromMinutes(15);

    /// <summary>After a failed fetch, wait this long before trying again: a bridge that answers "failed" every time
    /// must not be asked once per message.</summary>
    private static readonly TimeSpan FriendsRetry = TimeSpan.FromSeconds(30);

    /// <summary>How long one member/stranger answer is believed.</summary>
    private static readonly TimeSpan LookupFresh = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, string> _remarks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lookup> _lookups = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Task? _friends;
    private DateTimeOffset _attemptedAt = DateTimeOffset.MinValue;
    private bool _loaded;

    private readonly record struct Lookup(string? Name, DateTimeOffset At);

    /// <summary>
    /// The remark for this account id, if the friend list has been fetched and mentions one. Synchronous on purpose:
    /// this is asked while publishing a message, and a message must not wait for a display name.
    /// </summary>
    public string? Remembered(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        lock (_gate) return _remarks.GetValueOrDefault(userId);
    }

    /// <summary>Ask for the friend list if the answer kept is old or missing. Never throws, never blocks the caller.</summary>
    public void Nudge() => _ = LoadFriendsAsync(CancellationToken.None);

    /// <summary>
    /// Fetches the friend list when what is kept is stale, coalescing everyone who asks at the same time into one call.
    /// Returns when the attempt is over — successfully or not, since "the platform would not say" is also an answer.
    /// </summary>
    public async Task LoadFriendsAsync(CancellationToken cancellationToken)
    {
        Task attempt;
        lock (_gate)
        {
            if (_friends is { IsCompleted: false })
            {
                attempt = _friends;
            }
            else
            {
                var waited = DateTimeOffset.UtcNow - _attemptedAt;
                if (waited < (_loaded ? FriendsFresh : FriendsRetry)) return;
                _attemptedAt = DateTimeOffset.UtcNow;
                attempt = _friends = FetchFriendsAsync(cancellationToken);
            }
        }
        await attempt.ConfigureAwait(false);
    }

    /// <summary>
    /// How to name somebody: the card in the group they were seen in, else their nickname, plus the remark when there is
    /// one. Null when this account knows nothing — the caller's own fallback (usually the id) is then the honest answer,
    /// and returning the id dressed up as a resolved name would hide that nothing was found.
    /// </summary>
    public async ValueTask<TargetName?> ResolveAsync(string targetId, string? scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetId)) return null;
        var remark = await RemarkAsync(targetId, cancellationToken).ConfigureAwait(false);
        var name = await NameAsync(targetId, scope, cancellationToken).ConfigureAwait(false);
        if (name is null && remark is null) return null;
        return new TargetName(name ?? targetId, remark);
    }

    private async ValueTask<string?> RemarkAsync(string targetId, CancellationToken cancellationToken)
    {
        if (Remembered(targetId) is { } known) return known;
        // Not a Nudge: an explicit question is worth one wait, and LoadFriendsAsync is a no-op when the list is fresh.
        await LoadFriendsAsync(cancellationToken).ConfigureAwait(false);
        return Remembered(targetId);
    }

    private async ValueTask<string?> NameAsync(string targetId, string? scope, CancellationToken cancellationToken)
    {
        if (!long.TryParse(targetId, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)) return null;

        // The scope is the conversation the person was seen in. A group card only exists inside a group, so the group
        // is what gets asked; anywhere else the account-wide nickname is the best answer there is.
        var inGroup = OneBotProtocol.TryReadConversation(scope, out var placeId, out var platform) &&
                      platform == OneBotProtocol.GroupPlatform;
        var key = inGroup ? $"group:{placeId}/{userId}" : $"user:{userId}";
        lock (_gate)
            if (_lookups.TryGetValue(key, out var kept) && DateTimeOffset.UtcNow - kept.At < LookupFresh)
                return kept.Name;

        string? name;
        if (inGroup)
        {
            name = await AskAsync(OneBotProtocol.ReadMemberName,
                echo => OneBotProtocol.BuildGroupMemberInfo(echo, placeId, userId),
                $"get_group_member_info {placeId}/{userId}", cancellationToken).ConfigureAwait(false);
            // A group that cannot name a member (no such member, a bridge without the action) still has the
            // account-wide name to fall back on.
        }
        else name = null;

        name ??= await AskAsync(OneBotProtocol.ReadStrangerName,
            echo => OneBotProtocol.BuildStrangerInfo(echo, userId),
            $"get_stranger_info {userId}", cancellationToken).ConfigureAwait(false);

        lock (_gate) _lookups[key] = new Lookup(name, DateTimeOffset.UtcNow);
        return name;
    }

    private async ValueTask<string?> AskAsync(Func<System.Text.Json.JsonElement, string?> read,
        Func<string, string> build, string what, CancellationToken cancellationToken)
    {
        var reply = await connection.CallAsync(build, cancellationToken, CallTimeout).ConfigureAwait(false);
        if (!reply.Ok || !reply.Value.Response.Ok)
        {
            log.Warn($"OneBot did not answer {what}", new Dictionary<string, string>
            {
                ["detail"] = reply.Ok ? $"retcode {reply.Value.Response.RetCode}" : reply.Error?.Message ?? string.Empty
            });
            return null;
        }
        return read(reply.Value.Data);
    }

    private async Task FetchFriendsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reply = await connection.CallAsync(OneBotProtocol.BuildFriendList, cancellationToken, CallTimeout)
                .ConfigureAwait(false);
            if (!reply.Ok || !reply.Value.Response.Ok)
            {
                log.Warn("get_friend_list did not succeed, remarks will be missing", new Dictionary<string, string>
                {
                    ["detail"] = reply.Ok ? $"retcode {reply.Value.Response.RetCode}" : reply.Error?.Message ?? string.Empty
                });
                return;
            }

            var friends = OneBotProtocol.ReadFriendList(reply.Value.Data);
            var remarks = 0;
            lock (_gate)
            {
                // The list is the answer: what is no longer in it is no longer a remark of ours. Clearing rather than
                // merging is what makes removing a remark actually remove it.
                _remarks.Clear();
                foreach (var friend in friends)
                {
                    if (friend.Remark is not { } remark) continue;
                    _remarks[friend.UserId.ToString(CultureInfo.InvariantCulture)] = remark;
                    remarks++;
                }
                _loaded = true;
            }
            log.Info("OneBot friend list fetched", new Dictionary<string, string>
            {
                ["friends"] = friends.Count.ToString(CultureInfo.InvariantCulture),
                ["remarks"] = remarks.ToString(CultureInfo.InvariantCulture)
            });
        }
        catch (OperationCanceledException)
        {
            // Shutting down, or the caller's token: the next Nudge tries again.
        }
        catch (Exception ex)
        {
            log.Warn("get_friend_list failed", new Dictionary<string, string> { ["error"] = ex.Message });
        }
    }
}
