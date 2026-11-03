using ALN.Contracts;
using ALN.Runtime;

namespace ALN.Tests;

public sealed class ModuleCatalogTests
{
    private sealed class Fake(ModuleId id) : IModule
    {
        public ModuleId Id { get; } = id;
    }

    private static Fake Module(string id) => new(new ModuleId(id));

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
    public void The_catalog_finds_by_name()
    {
        var catalog = new ModuleCatalog();
        var manager = Module(ModuleNames.AdapterManager);
        Assert.True(catalog.Attach(manager, "AdapterManager", "adapter", ModuleRole.Manager).Ok);

        Assert.Same(manager, catalog.Find(new ModuleId(ModuleNames.AdapterManager)));
        Assert.True(catalog.Require(new ModuleId(ModuleNames.AdapterManager)).Ok);
        Assert.False(catalog.Require(new ModuleId("Nobody")).Ok);
    }

    [Fact]
    public void Two_names_differing_only_in_case_are_one_row()
    {
        // The file system does not distinguish them, so neither may the catalog: otherwise the second one
        // quietly takes over the first one's row without anybody noticing.
        var catalog = new ModuleCatalog();
        Assert.True(catalog.Attach(Module("Adapter.QQ#ABCDEF12"), "Adapter.QQ", "adapter", ModuleRole.Instance).Ok);

        var again = catalog.Attach(Module("adapter.qq#abcdef12"), "Adapter.QQ", "adapter", ModuleRole.Instance);

        Assert.False(again.Ok);
        Assert.Equal(ErrorCode.Conflict, again.Error!.Code);
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void An_illegal_name_cannot_be_registered()
    {
        var catalog = new ModuleCatalog();
        var attached = catalog.Attach(Module("a/b"), "Adapter.QQ", "adapter", ModuleRole.Instance);

        Assert.False(attached.Ok);
        Assert.Equal(ErrorCode.Invalid, attached.Error!.Code);
    }

    [Fact]
    public void Listing_by_category_or_by_type_is_how_instances_are_found()
    {
        // An instance id cannot be guessed, so this is the only way to reach one: filtering, not id arithmetic.
        var catalog = new ModuleCatalog();
        catalog.Attach(Module(ModuleNames.AdapterManager), "AdapterManager", "adapter", ModuleRole.Manager);
        catalog.Attach(Module("Adapter.QQ#1"), "Adapter.QQ", "adapter", ModuleRole.Instance);
        catalog.Attach(Module("Adapter.File#1"), "Adapter.File", "adapter", ModuleRole.Instance);
        catalog.Attach(Module(ModuleNames.SessionManager), "SessionManager", "session", ModuleRole.Manager);

        Assert.Equal(4, catalog.List().Count);
        Assert.Equal(3, catalog.List(category: "adapter").Count);
        // The manager is not an adapter: it has its own type, and a type lookup must not return it.
        var qq = Assert.Single(catalog.List(typeId: "Adapter.QQ"));
        Assert.Equal("Adapter.QQ#1", qq.Id.Value);
        Assert.Equal(ModuleRole.Instance, qq.Role);
    }

    [Fact]
    public void A_module_can_be_removed_by_name()
    {
        var catalog = new ModuleCatalog();
        catalog.Attach(Module("Adapter.QQ#1"), "Adapter.QQ", "adapter", ModuleRole.Instance);

        Assert.True(catalog.Detach(new ModuleId("Adapter.QQ#1")).Ok);
        Assert.Empty(catalog.List());
        Assert.False(catalog.Detach(new ModuleId("Adapter.QQ#1")).Ok);
    }
}
