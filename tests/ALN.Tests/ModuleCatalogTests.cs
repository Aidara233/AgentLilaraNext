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

    /// <summary>Stands in for a manager: it owns its instances and answers for them, the catalog copies nothing.</summary>
    private sealed class FakeIndex(string category = "fake-instance") : IModuleIndex
    {
        private readonly Dictionary<string, IModule> _held = new(StringComparer.OrdinalIgnoreCase);

        public string Category => category;

        public IModule Hold(string id, string typeId)
        {
            var module = new FakeThing(new ModuleId(id));
            _held[id] = module;
            _types[id] = typeId;
            return module;
        }

        private readonly Dictionary<string, string> _types = new(StringComparer.OrdinalIgnoreCase);

        public void Drop(string id) => _held.Remove(id);

        public IModule? Locate(ModuleId id) => _held.TryGetValue(id.Value, out var module) ? module : null;

        public IReadOnlyList<CatalogEntry> Entries() => _held
            .Select(entry => new CatalogEntry(new ModuleId(entry.Key), _types[entry.Key], Category, ModuleRole.Instance))
            .ToArray();
    }

    [Fact]
    public void An_instance_is_found_through_its_owner_not_through_a_copy()
    {
        var catalog = new ModuleCatalog();
        var index = new FakeIndex();
        Assert.True(catalog.Observe(index).Ok);
        var instance = index.Hold("Fake.Thing#1", "fake.thing");

        Assert.Same(instance, catalog.Find(new ModuleId("Fake.Thing#1")));
        Assert.Single(catalog.List(category: "fake-instance"));
        Assert.Equal("fake.thing", Assert.Single(catalog.List(typeId: "fake.thing")).TypeId);

        // The point of asking instead of copying: a destroyed instance cannot linger as a name that still resolves.
        index.Drop("Fake.Thing#1");
        Assert.Null(catalog.Find(new ModuleId("Fake.Thing#1")));
        Assert.Empty(catalog.List());
    }

    [Fact]
    public void Two_owners_in_one_category_are_refused()
    {
        // Otherwise "which of them has this name" becomes a coin toss at every lookup.
        var catalog = new ModuleCatalog();
        Assert.True(catalog.Observe(new FakeIndex()).Ok);

        var second = catalog.Observe(new FakeIndex());

        Assert.False(second.Ok);
        Assert.Equal(ErrorCode.Conflict, second.Error!.Code);
    }

    [Fact]
    public void The_same_owner_observed_twice_is_not_a_second_owner()
    {
        var catalog = new ModuleCatalog();
        var index = new FakeIndex();
        Assert.True(catalog.Observe(index).Ok);
        Assert.True(catalog.Observe(index).Ok);
        index.Hold("Fake.Thing#1", "fake.thing");

        // One row, not two: asking the same owner twice would double every instance.
        Assert.Single(catalog.List(category: "fake-instance"));
    }

    [Fact]
    public void Two_owners_holding_the_same_name_is_an_error_not_a_coin_toss()
    {
        // Cross-owner duplicates are a bug upstream, and answering with whichever one was asked first is how the
        // wrong object gets called.
        var catalog = new ModuleCatalog();
        var first = new FakeIndex();
        var second = new FakeIndex("another-category");
        Assert.True(catalog.Observe(first).Ok);
        Assert.True(catalog.Observe(second).Ok);
        first.Hold("Fake.Thing#dup", "fake.thing");
        second.Hold("Fake.Thing#dup", "fake.thing");

        Assert.Throws<InvalidOperationException>(() => catalog.Find(new ModuleId("Fake.Thing#dup")));
    }
}
