using Jint;

namespace ALN.Tests;

/// <summary>
/// What <c>options.LimitMemory(bytes)</c> actually measures, and why the answer matters here.
///
/// A script package declares <c>memoryLimitBytes</c> in its manifest (16 MiB for <c>adapter.channel-intake</c>), and
/// the host reads it as "this script may not allocate more than this". What it really measures is <b>the current
/// thread's allocation since the engine was built</b> — so anything the host allocates while the script is waiting on
/// it is charged to the script. The host does a lot of that on the calls a script package makes: <c>ctx.model.complete()</c>
/// is a whole model request, <c>ctx.session.run()</c> a whole turn (request, history, JSON), and <c>ctx.channel.accept()</c>
/// writes a picture to disk and serialises a message. Waking a Session (<c>ctx.session.activate</c>) no longer runs the
/// turn inside the script at all — it hands it over (docs/08 §5.4) — so it is the one long call that costs the script's
/// budget nothing.
///
/// Two consequences, both measured below:
/// <list type="bullet">
///   <item>Allocation from <em>before</em> the engine existed does not count — the baseline is taken when the engine is
///   built.</item>
///   <item>Allocation a <em>host function</em> makes during the call does count, because it happens on the same thread
///   inside the script's stack. This is the one that fails a working script: an error that reads like a runaway script
///   and is actually the host spending the budget from inside a helper.</item>
/// </list>
///
/// Keep these tests: the budget only means something if what it measures stays understood.
/// </summary>
public sealed class JintMemoryLimitTests
{
    private const long Budget = 16L * 1024 * 1024;

    private static Engine Build() => new(new Options().LimitMemory(Budget));

    /// <summary>
    /// Ballast allocated before the engine is built is not charged. This is the reassuring half: an engine is not
    /// poisoned by whatever else the process did earlier.
    /// </summary>
    [Fact]
    public void Allocation_from_before_the_engine_does_not_count()
    {
        var ballast = new byte[32 * 1024 * 1024];
        GC.KeepAlive(ballast);

        var engine = Build();
        var failure = Record.Exception(() => engine.Evaluate("1 + 1"));

        Assert.Null(failure);
    }

    /// <summary>
    /// A host function that allocates during the call is charged to the script's budget. This is why
    /// <c>adapter.channel-intake</c> can fail its 16 MiB budget while running a 879-byte script: the memory is spent by
    /// the Channel and the Session, on the script's thread, inside the script's own call.
    /// </summary>
    [Fact]
    public void Allocation_a_host_function_makes_during_the_call_counts()
    {
        var engine = Build();
        var spent = new List<byte[]>();
        engine.SetValue("__spend", new Action(() => spent.Add(new byte[24 * 1024 * 1024])));
        try
        {
            var failure = Record.Exception(() => engine.Evaluate("__spend(); 1 + 1"));

            Assert.NotNull(failure);
            Assert.Contains("allocated", failure!.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("limited to", failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            GC.KeepAlive(spent);
        }
    }

    /// <summary>
    /// Note what is <b>not</b> here: a test saying "a picture in the payload blows the budget". That guess was wrong.
    /// An envelope carrying an 8 MiB attachment serialises to about 11 MB of base64 and still parses inside a 16 MiB
    /// budget — so the payload is not what failed in production, however plausible it looks. What fails is
    /// <see cref="Allocation_a_host_function_makes_during_the_call_counts"/>: <c>ctx.channel.accept()</c> writes the
    /// picture to disk and serialises a message on the script's own stack, and those allocations are charged here.
    /// </summary>
}
