using System.Reflection;
using ALN.Contracts;
using ALN.Contracts.Sessions;

namespace ALN.Tests;

/// <summary>
/// The asymmetry, written down where it can fail: an interface's level is a fact about the code, so nothing a running
/// process can call changes it; a caller's level is a decision about this deployment, so exactly one verb changes it.
/// If both were mutable, "who may do what" would be a value anything could rewrite.
/// </summary>
public sealed class AccessLevelTests
{
    [Fact]
    public void The_table_of_interface_levels_has_no_way_to_be_edited_at_runtime()
    {
        // Reflection rather than prose: the day somebody adds Set(...) "just for a test", this fails. An interface
        // that could rewrite the table would be the only interface that mattered.
        var mutators = typeof(AccessLevels).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Where(method => method.DeclaringType == typeof(AccessLevels))
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["Describe", "Of"], mutators);
        Assert.DoesNotContain(typeof(AccessLevels).GetProperties(BindingFlags.Public | BindingFlags.Static),
            property => property.SetMethod is { IsPublic: true });
        // The one property it does expose is a view, not a handle to the dictionary behind it.
        Assert.IsAssignableFrom<IReadOnlyCollection<string>>(AccessLevels.Known);
    }

    [Fact]
    public void The_verbs_that_could_move_the_boundary_sit_where_the_document_says()
    {
        // Each of these can change what this process is or what it will say next, so none of them is an agent's.
        Assert.Equal(AccessLevel.Operator, AccessLevels.Of(Capabilities.ParamsWrite));
        Assert.Equal(AccessLevel.Operator, AccessLevels.Of(Capabilities.PromptsWrite));
        Assert.Equal(AccessLevel.Operator, AccessLevels.Of(Capabilities.ModelsControl));
        Assert.Equal(AccessLevel.Operator, AccessLevels.Of(Capabilities.SessionsControl));
        Assert.Equal(AccessLevel.Operator, AccessLevels.Of(Capabilities.ModulesControl));
        // Reading is the agent's half of the job.
        Assert.Equal(AccessLevel.Agent, AccessLevels.Of(Capabilities.ModulesRead));
        Assert.Equal(AccessLevel.Agent, AccessLevels.Of(Capabilities.MessagesWrite));
    }

    [Fact]
    public void Only_a_session_verb_declares_a_level_it_hands_out()
    {
        // AssignsLevel is the one declared way to hand a level to something else, and the control plane refuses anything
        // above the caller's own. Kept narrow on purpose: a second verb that hands out levels is a second place the
        // delegation rule has to hold.
        static ActionDescriptor Sitting(AccessLevel? min) =>
            new("demo", "Demo.", Capabilities.SessionsRead, true, MinLevel: min);

        var hands = new ActionDescriptor(SessionActions.SetLevel, "Raise a Session.", Capabilities.SessionsControl, true,
            AssignsLevel: "level");
        Assert.Equal("level", hands.AssignsLevel);
        // A verb that merely sits deep does not hand anything out; the two fields answer different questions.
        Assert.Null(Sitting(AccessLevel.Full).AssignsLevel);
        Assert.Equal(AccessLevel.Full, Sitting(AccessLevel.Full).RequiredLevel);
    }
}
