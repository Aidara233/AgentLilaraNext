namespace ALN.Contracts;

/// <summary>
/// The state machine every Adapter instance shares: Created → Running → Stopped, with compare-and-swap
/// so two concurrent Start calls can't both believe they won. It lives here rather than inside one
/// adapter package because it is contract behaviour — the second adapter should not reinvent it, and
/// two reinvented versions would disagree about what "start from Stopped" means.
/// </summary>
public abstract class AdapterBase : IChatAdapter
{
    private int _state = (int)AdapterState.Created;

    protected AdapterBase(Address address, AdapterDescriptor descriptor)
    {
        Address = address;
        Descriptor = descriptor;
    }

    public Address Address { get; }
    public AdapterDescriptor Descriptor { get; }
    public AdapterState State => (AdapterState)Volatile.Read(ref _state);

    public virtual ValueTask<Result> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            var state = State;
            if (state == AdapterState.Running)
                return ValueTask.FromResult(Result.Success());
            if (state is not (AdapterState.Created or AdapterState.Stopped))
                return ValueTask.FromResult(Result.Failure(new(ErrorCode.Conflict, $"Adapter cannot start from {state}.")));
            if (Interlocked.CompareExchange(ref _state, (int)AdapterState.Running, (int)state) == (int)state)
                return ValueTask.FromResult(Result.Success());
        }
    }

    public virtual ValueTask<Result> StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            var state = State;
            if (state == AdapterState.Stopped)
                return ValueTask.FromResult(Result.Success());
            if (state != AdapterState.Running && state != AdapterState.Created)
                return ValueTask.FromResult(Result.Failure(new(ErrorCode.Conflict, $"Adapter cannot stop from {state}.")));
            if (Interlocked.CompareExchange(ref _state, (int)AdapterState.Stopped, (int)state) == (int)state)
                return ValueTask.FromResult(Result.Success());
        }
    }

    protected bool IsRunning => State == AdapterState.Running;

    public abstract ValueTask<Result<SendResult>> SendAsync(OutboundMessage message, CancellationToken cancellationToken);
}
