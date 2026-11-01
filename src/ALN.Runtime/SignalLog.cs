using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ALN.Contracts;
using ALN.Runtime.Hosting;

namespace ALN.Runtime;

/// <summary>Single source of truth for signal-log level and buffering.</summary>
public sealed record SignalLogOptions(SignalLevel MinLevel = SignalLevel.Info, int Capacity = 8192, int MemoryWindow = 10_000)
{
    public const string FileName = "signalconfig.json";

    /// <summary>
    /// Reads <c>signalconfig.json</c> from the log directory. Missing or malformed configuration
    /// falls back to defaults instead of failing the host — the log must never be a startup barrier.
    /// </summary>
    public static SignalLogOptions Load(string directory)
    {
        var defaults = new SignalLogOptions();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return defaults;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var level = defaults.MinLevel;
            if (root.TryGetProperty("minLevel", out var minLevel) && minLevel.ValueKind == JsonValueKind.String &&
                Enum.TryParse<SignalLevel>(minLevel.GetString(), ignoreCase: true, out var parsed))
                level = parsed;
            else if (root.TryGetProperty("minLevel", out minLevel) && minLevel.TryGetInt32(out var numeric) &&
                Enum.IsDefined(typeof(SignalLevel), numeric))
                level = (SignalLevel)numeric;
            var capacity = root.TryGetProperty("capacity", out var capacityElement) && capacityElement.TryGetInt32(out var parsedCapacity)
                ? Math.Clamp(parsedCapacity, 64, 1_000_000) : defaults.Capacity;
            var window = root.TryGetProperty("memoryWindow", out var windowElement) && windowElement.TryGetInt32(out var parsedWindow)
                ? Math.Clamp(parsedWindow, 0, 1_000_000) : defaults.MemoryWindow;
            return new SignalLogOptions(level, capacity, window);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return defaults;
        }
    }
}

/// <summary>
/// Dispatch-byproduct log: one record per traversed edge or invoked capability.
/// Writes are non-blocking and lossy; the file is a single daily JSONL stream, never one file per module.
/// </summary>
public sealed class SignalLog : ISignalLog, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    private readonly Channel<SignalRecord> _queue;
    private readonly ConcurrentQueue<SignalRecord> _recent = new();
    private readonly string? _directory;
    private readonly int _memoryWindow;
    private readonly Task _writer;
    private readonly CancellationTokenSource _stop = new();
    private FileStream? _stream;
    private DateOnly _streamDate;
    private long _sequence;
    private long _dropped;
    private long _faults;

    private SignalLog(SignalLogOptions options, string? directory)
    {
        MinLevel = options.MinLevel;
        _directory = directory;
        _memoryWindow = options.MemoryWindow;
        _queue = Channel.CreateBounded<SignalRecord>(new BoundedChannelOptions(options.Capacity)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
        _writer = Task.Run(WriteLoopAsync);
    }

    public SignalLevel MinLevel { get; }
    public long DroppedCount => Interlocked.Read(ref _dropped);
    public long FaultCount => Interlocked.Read(ref _faults);

    /// <summary>Creates a log that persists to <paramref name="directory"/>. Pass null for memory only.</summary>
    public static SignalLog Open(string? directory, SignalLogOptions? options = null)
    {
        try
        {
            if (directory is not null)
            {
                SafeFileSystem.EnsureDirectory(directory);
                options ??= SignalLogOptions.Load(directory);
            }
            return new SignalLog(options ?? new SignalLogOptions(), directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SignalLog(options ?? new SignalLogOptions(), null);
        }
    }

    public bool IsEnabled(SignalLevel level) => level >= MinLevel;

    public void Write(SignalRecord record)
    {
        try
        {
            if (record is null || !IsEnabled(record.Level)) return;
            var stamped = record with { Sequence = Interlocked.Increment(ref _sequence) };
            if (_memoryWindow > 0)
            {
                _recent.Enqueue(stamped);
                while (_recent.Count > _memoryWindow) _recent.TryDequeue(out _);
            }
            if (!_queue.Writer.TryWrite(stamped)) Interlocked.Increment(ref _dropped);
        }
        catch
        {
            // Recording must never change the outcome of the dispatch it observes.
            Interlocked.Increment(ref _faults);
        }
    }

    public IReadOnlyList<SignalRecord> Snapshot() => _recent.ToArray();

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var record in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                Persist(record);
        }
        catch (OperationCanceledException) { }
        catch { Interlocked.Increment(ref _faults); }
    }

    private void Persist(SignalRecord record)
    {
        if (_directory is null) return;
        try
        {
            var date = DateOnly.FromDateTime(record.At.UtcDateTime);
            if (_stream is null || date != _streamDate)
            {
                _stream?.Dispose();
                var path = Path.Combine(_directory, $"signals-{date:yyyyMMdd}.jsonl");
                SafeFileSystem.EnsureDirectory(_directory);
                SafeFileSystem.VerifyNewOrRegularFile(path, _directory);
                _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.None);
                _streamDate = date;
            }
            var json = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            _stream.Write(json);
            _stream.WriteByte((byte)'\n');
            _stream.Flush();
        }
        catch
        {
            Interlocked.Increment(ref _faults);
            try { _stream?.Dispose(); } catch { }
            _stream = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _queue.Writer.TryComplete();
            await Task.WhenAny(_writer, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            _stop.Cancel();
            _stream?.Dispose();
        }
        catch { }
        finally { _stop.Dispose(); }
    }
}
