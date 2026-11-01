using System.Globalization;
using ALN.Contracts;
using Microsoft.Data.Sqlite;

namespace ALN.Storage.Sqlite;

/// <summary>
/// One file for everything a program writes down: component state, message history, audit records.
///
/// The reason it is a database and not a tree of files: these rows are interrogated, not read by eye.
/// "Has this owner already recorded this platform message?" is a unique index instead of a directory scan;
/// "append the message and write the pending marker" is one transaction instead of two chances to fail in
/// between. Specs (adapters, channels, models, scripts) stay files — those are written by a person and
/// reviewed by a person.
///
/// Durability: <c>journal_mode=WAL</c> with <c>synchronous=NORMAL</c>, which survives a killed process (the
/// case this whole state story is about) and can lose only the last commit on a power cut.
/// </summary>
public sealed class SqliteStore : IStateStore, IMessageStore, IAuditSink, IAtomicWork, IDisposable
{
    private const int SchemaVersion = 1;
    private const int SqliteConstraint = 19;
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // How deep the current async flow is inside AtomicallyAsync: an operation either owns the connection or
    // joins somebody else's transaction.
    private readonly AsyncLocal<int> _depth = new();
    private SqliteTransaction? _transaction;
    private bool _disposed;

    public SqliteStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path, Environment.CurrentDirectory);
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new ArgumentException("Database path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA busy_timeout=5000;");
        CreateSchema();
    }

    public string Path { get; }

    // ---- IStateStore: what a component remembers ------------------------------------------------

    public ValueTask<Result<string?>> ReadAsync(string scope, string key, CancellationToken cancellationToken) =>
        RunAsync(cancellationToken, Result<string?>.Failure, "State could not be read.", () =>
        {
            if (!IsToken(scope) || !IsToken(key))
                return Result<string?>.Failure(new(ErrorCode.Invalid, "State scope and key must be tokens."));
            using var command = Command("SELECT value FROM state WHERE scope=$scope AND key=$key;");
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$key", key);
            // Absent comes back as Ok with a null value; Result<string?>.Ok is false in that case, which every
            // caller already has to read off Error.
            return Result<string?>.Success(command.ExecuteScalar() as string);
        });

    public ValueTask<Result> WriteAsync(string scope, string key, string json, CancellationToken cancellationToken) =>
        RunAsync(cancellationToken, Result.Failure, "State could not be written.", () =>
        {
            if (!IsToken(scope) || !IsToken(key))
                return Result.Failure(new(ErrorCode.Invalid, "State scope and key must be tokens."));
            if (json is null) return Result.Failure(new(ErrorCode.Invalid, "State value must not be null."));
            using var command = Command(
                "INSERT INTO state(scope, key, value, updated_at) VALUES($scope, $key, $value, $at) " +
                "ON CONFLICT(scope, key) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at;");
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", json);
            command.Parameters.AddWithValue("$at", Now());
            command.ExecuteNonQuery();
            return Result.Success();
        });

    public ValueTask<Result> DeleteAsync(string scope, string key, CancellationToken cancellationToken) =>
        RunAsync(cancellationToken, Result.Failure, "State could not be deleted.", () =>
        {
            if (!IsToken(scope) || !IsToken(key))
                return Result.Failure(new(ErrorCode.Invalid, "State scope and key must be tokens."));
            using var command = Command("DELETE FROM state WHERE scope=$scope AND key=$key;");
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
            // Deleting a key that is not there is not an error: components say "forget this" without asking.
            return Result.Success();
        });

    // ---- IMessageStore: what an owner said and heard -------------------------------------------

    public ValueTask<Result> AppendAsync(StoredMessage message, CancellationToken cancellationToken) =>
        RunAsync(cancellationToken, Result.Failure, "Message could not be recorded.", () =>
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Id) || message.Text is null)
                return Result.Failure(new(ErrorCode.Invalid, "Stored message requires an id and text."));
            if (!message.Owner.IsInstance)
                return Result.Failure(new(ErrorCode.Invalid, $"Message owner is not a valid instance address: {message.Owner}"));
            using var command = Command(
                "INSERT INTO message(id, owner, conversation, platform_message_id, sender, text, at, direction) " +
                "VALUES($id, $owner, $conversation, $platform, $sender, $text, $at, $direction);");
            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$owner", message.Owner.ToString());
            command.Parameters.AddWithValue("$conversation", message.PlatformConversationId);
            command.Parameters.AddWithValue("$platform", (object?)message.PlatformMessageId ?? DBNull.Value);
            command.Parameters.AddWithValue("$sender", (object?)message.Sender ?? DBNull.Value);
            command.Parameters.AddWithValue("$text", message.Text);
            command.Parameters.AddWithValue("$at", Timestamp(message.At));
            command.Parameters.AddWithValue("$direction", (int)message.Direction);
            try
            {
                command.ExecuteNonQuery();
                return Result.Success();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
            {
                // The unique index on (owner, platform message id) said no: the same message arriving a second
                // time, which is what the transport does after a crash.
                return Result.Failure(new(ErrorCode.Conflict,
                    "This platform message is already recorded for this owner.",
                    message.PlatformMessageId));
            }
        });

    public ValueTask<Result<IReadOnlyList<StoredMessage>>> ReadAsync(Address owner, CancellationToken cancellationToken) =>
        RunAsync(cancellationToken, Result<IReadOnlyList<StoredMessage>>.Failure, "Message history could not be read.", () =>
        {
            if (!owner.IsInstance)
                return Result<IReadOnlyList<StoredMessage>>.Failure(new(ErrorCode.Invalid, $"Not an instance address: {owner}"));
            using var command = Command(
                "SELECT id, conversation, platform_message_id, sender, text, at, direction FROM message " +
                "WHERE owner=$owner ORDER BY at, id;");
            command.Parameters.AddWithValue("$owner", owner.ToString());
            var messages = new List<StoredMessage>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                messages.Add(new StoredMessage(
                    reader.GetString(0),
                    owner,
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    (MessageDirection)reader.GetInt32(6)));
            }
            return Result<IReadOnlyList<StoredMessage>>.Success(messages);
        });

    /// <summary>"Did this owner already record this platform message?" — the unique index asked directly.</summary>
    public ValueTask<Result<bool>> ContainsAsync(Address owner, string platformMessageId, CancellationToken cancellationToken) =>
        RunAsync(cancellationToken, Result<bool>.Failure, "Message history could not be read.", () =>
        {
            using var command = Command("SELECT 1 FROM message WHERE owner=$owner AND platform_message_id=$platform LIMIT 1;");
            command.Parameters.AddWithValue("$owner", owner.ToString());
            command.Parameters.AddWithValue("$platform", platformMessageId);
            return Result<bool>.Success(command.ExecuteScalar() is not null);
        });

    /// <summary>Audit records in the order they were written. Small enough to read whole; used by tests and the UI.</summary>
    public IReadOnlyList<AuditRecord> ReadAudit()
    {
        _gate.Wait();
        try
        {
            using var command = Command("SELECT at, event_id, actor, action, outcome, detail FROM audit ORDER BY id;");
            var records = new List<AuditRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                records.Add(new AuditRecord(
                    DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    default,
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
            return records;
        }
        finally { _gate.Release(); }
    }

    /// <summary>A page of history, newest last. Used by the WebUI; the delta contributor reads in bulk.</summary>
    public ValueTask<Result<IReadOnlyList<StoredMessage>>> ReadRecentAsync(Address owner, int limit, CancellationToken cancellationToken) =>
        RunAsync(cancellationToken, Result<IReadOnlyList<StoredMessage>>.Failure, "Message history could not be read.", () =>
        {
            using var command = Command(
                "SELECT id, conversation, platform_message_id, sender, text, at, direction FROM message " +
                "WHERE owner=$owner ORDER BY at DESC, id DESC LIMIT $limit;");
            command.Parameters.AddWithValue("$owner", owner.ToString());
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
            var messages = new List<StoredMessage>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                messages.Add(new StoredMessage(
                    reader.GetString(0), owner, reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    (MessageDirection)reader.GetInt32(6)));
            }
            messages.Reverse();
            return Result<IReadOnlyList<StoredMessage>>.Success(messages);
        });

    // ---- IAuditSink ----------------------------------------------------------------------------

    public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken) =>
        // IAuditSink cannot report failure, so this cannot either: an audit row that fails to land must not
        // take the action it describes down with it.
        new(WriteAuditAsync(record, cancellationToken));

    private async Task WriteAuditAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await RunAsync(cancellationToken, Result.Failure, "Audit record could not be written.", () =>
        {
            using var command = Command("INSERT INTO audit(at, event_id, actor, action, outcome, detail) VALUES($at, $event, $actor, $action, $outcome, $detail);");
            command.Parameters.AddWithValue("$at", Timestamp(record.At));
            command.Parameters.AddWithValue("$event", record.EventId.ToString("D"));
            command.Parameters.AddWithValue("$actor", record.Actor);
            command.Parameters.AddWithValue("$action", record.Action);
            command.Parameters.AddWithValue("$outcome", record.Outcome);
            command.Parameters.AddWithValue("$detail", (object?)record.Detail ?? DBNull.Value);
            command.ExecuteNonQuery();
            return Result.Success();
        }).ConfigureAwait(false);
    }

    // ---- IAtomicWork ---------------------------------------------------------------------------

    public async ValueTask<Result> AtomicallyAsync(Func<CancellationToken, ValueTask<Result>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Nested work joins the enclosing transaction instead of opening a second one.
        if (_depth.Value > 0) return await body(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction();
            _transaction = transaction;
            _depth.Value++;
            try
            {
                var result = await body(cancellationToken).ConfigureAwait(false);
                if (result.Ok) transaction.Commit();
                else transaction.Rollback();
                return result;
            }
            catch
            {
                try { transaction.Rollback(); } catch (SqliteException) { /* already gone with the connection */ }
                throw;
            }
            finally
            {
                _depth.Value--;
                _transaction = null;
            }
        }
        finally { _gate.Release(); }
    }

    // ---- counts for the startup receipt --------------------------------------------------------

    public long CountMessages() => Count("message");
    public long CountStateKeys() => Count("state");
    public long CountAuditRecords() => Count("audit");

    private long Count(string table)
    {
        _gate.Wait();
        try
        {
            using var command = Command($"SELECT COUNT(*) FROM {table};");
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
        _gate.Dispose();
    }

    // ---- plumbing ------------------------------------------------------------------------------

    private async ValueTask<T> RunAsync<T>(CancellationToken cancellationToken, Func<Error, T> onError,
        string failureMessage, Func<T> work)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ownsGate = _depth.Value == 0;
        if (ownsGate) await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return work();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteBusy or SqliteLocked)
        {
            // Another writer has the file: retryable, and the caller is told so.
            return onError(new(ErrorCode.Unavailable, failureMessage, ex.Message, Retryable: true));
        }
        catch (SqliteException ex)
        {
            return onError(new(ErrorCode.Unavailable, failureMessage, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return onError(new(ErrorCode.Unavailable, failureMessage, ex.Message));
        }
        finally
        {
            if (ownsGate) _gate.Release();
        }
    }

    private SqliteCommand Command(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (_transaction is not null) command.Transaction = _transaction;
        return command;
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void CreateSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS state(
                scope TEXT NOT NULL,
                key TEXT NOT NULL,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(scope, key));

            CREATE TABLE IF NOT EXISTS message(
                id TEXT NOT NULL PRIMARY KEY,
                owner TEXT NOT NULL,
                conversation TEXT NOT NULL,
                platform_message_id TEXT NULL,
                sender TEXT NULL,
                text TEXT NOT NULL,
                at TEXT NOT NULL,
                direction INTEGER NOT NULL DEFAULT 0);

            CREATE INDEX IF NOT EXISTS message_owner_at ON message(owner, at);
            -- One platform message is one row per owner: the transport handing it over twice is not news.
            CREATE UNIQUE INDEX IF NOT EXISTS message_owner_platform
                ON message(owner, platform_message_id) WHERE platform_message_id IS NOT NULL;

            CREATE TABLE IF NOT EXISTS audit(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                at TEXT NOT NULL,
                event_id TEXT NOT NULL,
                actor TEXT NOT NULL,
                action TEXT NOT NULL,
                outcome TEXT NOT NULL,
                detail TEXT NULL);

            PRAGMA user_version = {SchemaVersion};
            """;
        command.ExecuteNonQuery();
    }

    private static string Now() => Timestamp(DateTimeOffset.UtcNow);

    private static string Timestamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool IsToken(string? value) =>
        value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
