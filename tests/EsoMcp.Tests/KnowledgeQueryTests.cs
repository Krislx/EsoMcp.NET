using System.Text.Json;
using EsoData.Accounts;
using EsoData.Catalogs;
using EsoMcp.Core;
using EsoMcp.Import;
using EsoMcp.Server;

namespace EsoMcp.Tests;

public class KnowledgeQueryTests
{
    [Fact]
    public void ActualBuildAndRichInventoryRemainDistinctFromSavedProfiles()
    {
        using var w = new TestWorkspace();
        var character = new EsoCharacter { Id = "1", Name = "Example" };
        character.Build.Attributes = new() { Stamina = 64 };
        var fields = new long[21]; fields[0] = 123; fields[1] = 370; fields[2] = 50; fields[6] = 33;
        character.Storage.Add(new() { Location = "Backpack", Items = [new() {
            ItemId = 123, Trait = 22, Link = new EsoData.Items.ItemLink(fields).ToString(), Count = 1 }] });
        var store = new WorkspaceStore(w.Database.Path);
        store.SaveAccounts([new() { Name = "@Example", Server = "EU", Characters = [character] }]);
        var tools = new AccountTools(new(store, w.Database, new(), offlineDefault: true));
        using var response = JsonDocument.Parse(tools.Inspect(queries: [
            new() { Section = "build", Character = "Example" },
            new() { Section = "inventory", IncludeDetails = true },
            new() { Section = "statistics", Character = "Example" }]));
        var rows = response.RootElement.GetProperty("results");
        Assert.Equal(64, rows[0].GetProperty("rows")[0].GetProperty("attributes").GetProperty("stamina").GetInt32());
        Assert.Equal(33, rows[1].GetProperty("rows")[0].GetProperty("details").GetProperty("effectiveTrait").GetInt32());
        Assert.False(rows[2].GetProperty("available").GetBoolean());
    }

    [Fact]
    public void SavedBuildDetailsExposeBarsAndScribedSkillsOnRequest()
    {
        var character = new EsoCharacter { Id = "1", Name = "Example" };
        character.SavedBuilds.Add(new("3", "Tank", DateTimeOffset.UnixEpoch, new()
        {
            Sections = EsoData.Builds.BuildSections.Bars,
            Bars = new() { Front = [900001, null, null, null, null, null] },
            ScribedSkills = [new(900001, 10, 20, 30)]
        }));
        using var w = new TestWorkspace();
        var store = new WorkspaceStore(w.Database.Path);
        store.SaveAccounts([new EsoAccount { Name = "@Example", Server = "EU", Characters = [character] }]);
        var tools = new AccountTools(new(store, w.Database, new(), offlineDefault: true));

        using var response = JsonDocument.Parse(tools.Inspect(queries:
            [new() { Section = "savedBuilds", Character = "Example", Text = "Tank", IncludeDetails = true }]));
        var build = response.RootElement.GetProperty("results")[0].GetProperty("rows")[0].GetProperty("build");
        Assert.Equal(900001, build.GetProperty("bars").GetProperty("front")[0].GetInt64());
        Assert.Single(build.GetProperty("scribedSkills").EnumerateArray());
    }

    [Fact]
    public void ChampionLookupFallsBackToInstalledCspsNames()
    {
        using var w = new TestWorkspace();
        var addon = Path.Combine(w.Folder, "CarosSkillPointSaver", "data");
        Directory.CreateDirectory(addon);
        File.WriteAllText(Path.Combine(addon, "cpinfo.lua"), """
            [66] = GS(SI_RIDINGTRAINTYPE1) --Steed's Blessing (Speed)

            [265] = string.format("", GS()) --Ironclad

            [46] = string.format("", GS()) --Bastion
            """);
        var options = new ImportOptions { Locations = [new(w.Folder, w.Folder)] };
        var workspace = new AccountWorkspace(new(w.Database.Path), w.Database, options, offlineDefault: true);
        var tools = new AccountTools(workspace, options);

        using var response = JsonDocument.Parse(tools.Resolve("champion", names: ["Ironclad"]));
        var row = response.RootElement.GetProperty("rows")[0];
        Assert.Equal(265, row.GetProperty("id").GetInt64());
        Assert.Equal("Warfare", row.GetProperty("discipline").GetString());
        Assert.Equal("installed-csps", row.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("maximumPoints").ValueKind);
    }

    [Fact]
    public void RefreshedNamesCanBeQueriedWithoutChangingLearnedState()
    {
        using var w = new TestWorkspace();
        var store = new WorkspaceStore(w.Database.Path);
        var character = new EsoCharacter { Id = "1", Name = "Example" };
        character.Progress.Knowledge["scripts"] = new() { [101] = true, [102] = false, [103] = true };
        store.SaveAccounts([new() { Name = "@Example", Server = "EU", Characters = [character] }]);
        var catalog = new GameCatalog();
        catalog.Items[101] = new(101, "Focus Script: Example");
        catalog.Items[102] = new(102, "Affix Script: Example");
        var source = new SourceDocument("names", "uesp-item-metadata", "test", "1", DateTimeOffset.UtcNow, catalog.ToJson(), 20);
        w.Database.Replace(CatalogProjection.Project(catalog, source));
        var workspace = new AccountWorkspace(store, w.Database, new(), offlineDefault: true);
        var tools = new AccountTools(workspace);
        var response = JsonDocument.Parse(tools.Inspect(queries:
            [new() { Section = "knowledge", Character = "Example", Category = "scripts", Text = "Affix", Known = false }]));
        var row = response.RootElement.GetProperty("results")[0].GetProperty("rows")[0];
        Assert.Equal(102, row.GetProperty("itemId").GetInt64());
        Assert.False(row.GetProperty("known").GetBoolean());
        Assert.Equal("Affix Script: Example", row.GetProperty("name").GetString());
        var all = JsonDocument.Parse(tools.Inspect(queries:
            [new() { Section = "knowledge", Character = "Example", Category = "scripts" }]));
        var rows = all.RootElement.GetProperty("results")[0].GetProperty("rows");
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("name").ValueKind);
        Assert.True(rows[2].GetProperty("known").GetBoolean());
    }
}
