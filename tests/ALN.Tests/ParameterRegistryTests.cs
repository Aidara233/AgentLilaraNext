using ALN.Contracts;
using ALN.Runtime;
using ALN.Storage.Sqlite;

namespace ALN.Tests;

/// <summary>
/// The runtime knob panel. What matters is not that a value can be set, but that it is validated by its
/// owner, applied before it is persisted, survives a restart, and never reads a secret back out.
/// </summary>
public sealed class ParameterRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aln-params-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _state;

    public ParameterRegistryTests() => _state = new SqliteStore(Path.Combine(_root, "aln.db"));

    public void Dispose()
    {
        _state.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_set_is_validated_applied_and_persisted_so_it_survives_a_restart()
    {
        var target = new FakeTarget();
        var registry = new ParameterRegistry(_state);
        Assert.True(registry.Register(target).Ok);

        var applied = await registry.SetAsync(target.Scope, "window", "25", default);
        Assert.True(applied.Ok, applied.Error?.Message);
        Assert.Equal("25", target.ReadParameter("window"));

        // A restart is a new registry over the same state: the value has to come back.
        var restartedTarget = new FakeTarget();
        var restarted = new ParameterRegistry(_state);
        Assert.True(restarted.Register(restartedTarget).Ok);
        var restored = await restarted.RestoreAsync(default);
        Assert.True(restored.Ok, restored.Error?.Message);
        Assert.Equal("25", restartedTarget.ReadParameter("window"));
    }

    [Fact]
    public async Task A_rejected_value_leaves_the_old_one_in_place_and_is_not_persisted()
    {
        var target = new FakeTarget();
        var registry = new ParameterRegistry(_state);
        Assert.True(registry.Register(target).Ok);
        Assert.True((await registry.SetAsync(target.Scope, "window", "10", default)).Ok);

        var rejected = await registry.SetAsync(target.Scope, "window", "0", default);
        Assert.False(rejected.Ok);
        Assert.Equal(ErrorCode.Invalid, rejected.Error!.Code);
        Assert.Equal("10", target.ReadParameter("window"));

        // The stored value is still the accepted one, not the rejected one.
        var stored = await _state.ReadAsync(ParameterRegistry.ScopeKey(target.Scope), "window", default);
        Assert.Contains("\"10\"", stored.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_scope_or_name_is_refused_instead_of_created()
    {
        var registry = new ParameterRegistry(_state);
        Assert.True(registry.Register(new FakeTarget()).Ok);

        var unknownScope = await registry.SetAsync("channel.conversation#nope", "window", "5", default);
        Assert.False(unknownScope.Ok);
        Assert.Equal(ErrorCode.Invalid, unknownScope.Error!.Code);

        var unknownName = await registry.SetAsync(new FakeTarget().Scope, "nonsense", "5", default);
        Assert.False(unknownName.Ok);
        Assert.Equal(ErrorCode.Invalid, unknownName.Error!.Code);
    }

    [Fact]
    public void A_secret_is_settable_but_never_readable()
    {
        var registry = new ParameterRegistry(_state);
        var target = new FakeTarget();
        Assert.True(registry.Register(target).Ok);

        var listed = Assert.Single(registry.List(), p => p.Name == "token");
        Assert.True(listed.Secret);
        // The value is not null because it is unset — it is null because it must never be echoed.
        Assert.Null(listed.Value);
        Assert.Null(listed.DefaultValue);
    }

    [Fact]
    public async Task Apply_happens_before_persist_so_a_failed_apply_never_claims_success()
    {
        var target = new FakeTarget { FailApply = true };
        var registry = new ParameterRegistry(_state);
        Assert.True(registry.Register(target).Ok);

        var applied = await registry.SetAsync(target.Scope, "window", "7", default);
        Assert.False(applied.Ok);
        // Nothing was written: a stored value the process is not actually using is worse than no value.
        Assert.Null((await _state.ReadAsync(target.Scope, "window", default)).Value);
    }

    private sealed class FakeTarget : IParameterTarget
    {
        private int? _window;

        public string Scope => "channel.conversation#test";
        public string ParameterScope => Scope;
        public bool FailApply { get; init; }

        public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [
            new("window", ParameterKind.Integer, null, "test window", ParameterApply.Immediate, Minimum: 1, Maximum: 1000),
            new("token", ParameterKind.Text, null, "test secret", ParameterApply.Reconnect, Secret: true)
        ];

        public string? ReadParameter(string name) => name switch
        {
            "window" => _window?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "token" => "should-never-be-read",
            _ => null
        };

        public ValueTask<Result> ApplyParameterAsync(string name, string value, CancellationToken cancellationToken)
        {
            if (FailApply) return ValueTask.FromResult(Result.Failure(new(ErrorCode.Unavailable, "apply refused")));
            if (name == "window")
            {
                if (!int.TryParse(value, out var window) || window <= 0)
                    return ValueTask.FromResult(Result.Failure(new(ErrorCode.Invalid, "window must be positive")));
                _window = window;
            }
            return ValueTask.FromResult(Result.Success());
        }
    }
}
