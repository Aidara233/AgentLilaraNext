using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

public sealed class ModuleCatalogTests
{
    [Module("Fake.Manager", Category = "fake", Role = ModuleRole.Manager, Singleton = true)]
    private sealed class FakeManager(ModuleId id) : IModule
    {
        public ModuleId Id { get; } = id;
    }

    [Module("Fake.Thing", Category = "fake")]
    private sealed class FakeThing(ModuleId id) : IModule
    {
        public ModuleId Id { get; } = id;
    }

    /// <summary>A module that says nothing about itself. The catalog must refuse it rather than invent a type.</summary>
    private sealed class Undeclared(ModuleId id) : IModule
    {
        public ModuleId Id { get; } = id;
    }

    private static IModule Manager(string id) => new FakeManager(new ModuleId(id));
    private static IModule Thing(string id) => new FakeThing(new ModuleId(id));

    [Theory]
    [InlineData("AdapterManager")]
    [InlineData("Adapter.QQ#ABCDEF12")]
    [InlineData("session.chat#a_b-1")]
    [InlineData("x")]
    public void A_legal_id_is_a_name_and_nothing_more(string id)
    {
        Assert.True(ModuleId.IsLegal(id));
        Assert.True(ModuleId.TryParse(id, out var parsed));
        Assert.Equal(id, parsed.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a b")]
    [InlineData("con")]
    [InlineData("LPT9")]
    public void An_illegal_id_is_refused_rather_than_rewritten(string id)
    {
        // Refused, never sanitized: rewriting "a/b" into "a_b" would let two different names collide on disk.
        Assert.False(ModuleId.IsLegal(id));
        Assert.Throws<ArgumentException>(() => ModuleId.Parse(id));
    }

    [Fact]
    public void A_name_longer_than_the_limit_is_refused()
    {
        Assert.False(ModuleId.IsLegal(new string('a', 97)));
        Assert.True(ModuleId.IsLegal(new string('a', 96)));
    }

    [Fact]
    public void The_catalog_finds_a_singleton_by_name()
    {
        var catalog = new ModuleCatalog();
        var manager = Manager("Fake.Manager");
        Assert.True(catalog.Attach(manager).Ok);

        Assert.Same(manager, catalog.Find(new ModuleId("Fake.Manager")));
        Assert.True(catalog.Require(new ModuleId("Fake.Manager")).Ok);
        Assert.False(catalog.Require(new ModuleId("Nobody")).Ok);
    }

    [Fact]
    public void What_a_module_is_comes_from_its_own_declaration()
    {
        // Not from the registration call: the host used to pass type, category and role, which made every call
        // site a second place the truth could be wrong.
        var catalog = new ModuleCatalog();
        Assert.True(catalog.Attach(Manager("Fake.Manager")).Ok);
        Assert.True(catalog.Attach(Thing("Fake.Thing#1")).Ok);

        var manager = Assert.Single(catalog.List(category: "fake", typeId: "Fake.Manager"));
        Assert.Equal(ModuleRole.Manager, manager.Role);
        var thing = Assert.Single(catalog.List(typeId: "Fake.Thing"));
        Assert.Equal(ModuleRole.Instance, thing.Role);
    }

    [Fact]
    public void A_class_that_declares_nothing_is_refused()
    {
        var catalog = new ModuleCatalog();

        var attached = catalog.Attach(new Undeclared(new ModuleId("Mystery")));

        Assert.False(attached.Ok);
        Assert.Equal(ErrorCode.Invalid, attached.Error!.Code);
        Assert.Contains("[Module]", attached.Error.Message);
    }

    [Fact]
    public void A_singleton_must_be_named_after_its_own_type()
    {
        // Its name is part of the contract, so it cannot be arbitrary; an instance name is just a name, so only
        // singletons are checked.
        var catalog = new ModuleCatalog();

        var wrong = catalog.Attach(Manager("SomethingElse"));
        Assert.False(wrong.Ok);
        Assert.Contains("Fake.Manager", wrong.Error!.Message);

        Assert.True(catalog.Attach(Thing("whatever-anyone-likes")).Ok);
    }

    [Fact]
    public void Two_names_differing_only_in_case_are_one_row()
    {
        // The file system does not distinguish them, so neither may the catalog: otherwise the second one
        // quietly takes over the first one's row without anybody noticing.
        var catalog = new ModuleCatalog();
        Assert.True(catalog.Attach(Thing("Fake.Thing#ABCDEF12")).Ok);

        var again = catalog.Attach(Thing("fake.thing#abcdef12"));

        Assert.False(again.Ok);
        Assert.Equal(ErrorCode.Conflict, again.Error!.Code);
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void An_illegal_name_cannot_be_registered()
    {
        var catalog = new ModuleCatalog();
        var attached = catalog.Attach(Thing("a/b"));

        Assert.False(attached.Ok);
        Assert.Equal(ErrorCode.Invalid, attached.Error!.Code);
    }

    [Fact]
    public void Listing_by_category_or_by_type_is_how_instances_are_found()
    {
        // An instance id cannot be guessed, so this is the only way to reach one: filtering, not id arithmetic.
        var catalog = new ModuleCatalog();
        catalog.Attach(Manager("Fake.Manager"));
        catalog.Attach(Thing("Fake.Thing#1"));
        catalog.Attach(Thing("Fake.Thing#2"));

        Assert.Equal(3, catalog.List().Count);
        Assert.Equal(3, catalog.List(category: "fake").Count);
        // The manager is not an instance of the thing: it has its own type, and a type lookup must not return it.
        Assert.Equal(2, catalog.List(typeId: "Fake.Thing").Count);
    }

    [Fact]
    public void A_module_can_be_removed_by_name()
    {
        var catalog = new ModuleCatalog();
        catalog.Attach(Thing("Fake.Thing#1"));

        Assert.True(catalog.Detach(new ModuleId("Fake.Thing#1")).Ok);
        Assert.Empty(catalog.List());
        Assert.False(catalog.Detach(new ModuleId("Fake.Thing#1")).Ok);
    }
}
