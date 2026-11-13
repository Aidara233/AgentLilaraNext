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
        // 2026-11-12 改。原来是"造东西、改东西的那几格都是 operator，因为它们改的是这个进程的形状"。改的理由与
        // 其说是信任变了，不如说是把两件事分开了：改形状已经是可逆的（销毁走回收站 C-25、包能换回旧版、
        // 等级授不出比自己深的），而真正危险的那几条**作为例外**钉在各自的描述符上（下一个测试 + docs/07 §9.1）。
        // 冻结这张表，是为了让"下次只改一边"这件事红在测试里。
        foreach (var control in new[]
                 {
                     Capabilities.AdaptersControl, Capabilities.ChannelsControl, Capabilities.ModelsControl,
                     Capabilities.SessionsControl, Capabilities.ModulesControl, Capabilities.GraphControl,
                     Capabilities.ParamsWrite, Capabilities.PromptsWrite, Capabilities.ArchiveControl,
                     Capabilities.SchedulesControl, Capabilities.NetControl
                 })
            Assert.Equal(AccessLevel.Agent, AccessLevels.Of(control));

        // 唯一没有跟着降的一格：给人定级。它决定的不是这个进程长什么样，而是以后谁的话算数。
        Assert.Equal(AccessLevel.Operator, AccessLevels.Of(Capabilities.IdentityControl));

        // Reading is the agent's half of the job, and stays where it was.
        Assert.Equal(AccessLevel.Agent, AccessLevels.Of(Capabilities.ModulesRead));
        Assert.Equal(AccessLevel.Agent, AccessLevels.Of(Capabilities.MessagesWrite));
    }

    [Fact]
    public void The_sessions_own_verbs_are_an_agents_including_making_one()
    {
        // 2026-11-12 新增：造一个会话原来没有动词，只能由挂载脚本 ctx.session.open 顺手造出来。现在
        // sessions.create 与会话自己的其它动词同级，而它授出去的等级仍然受委派不变式管（AssignsLevel 指向 level），
        // 所以"能造"不等于"能造一个比自己深的"。
        Assert.Equal(AccessLevel.Agent, AccessLevels.Of(Capabilities.SessionsControl));

        var creates = new ActionDescriptor(SessionActions.Create, "Make a Session.", Capabilities.SessionsControl, true,
            AssignsLevel: "level");
        Assert.Equal("level", creates.AssignsLevel);
        Assert.Equal(AccessLevel.Agent, creates.RequiredLevel);
        // Handing a level down is the same one place for both verbs, and it is still the only place.
        Assert.Equal("level", new ActionDescriptor(SessionActions.SetLevel, "Raise one.", Capabilities.SessionsControl,
            true, AssignsLevel: "level").AssignsLevel);
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
