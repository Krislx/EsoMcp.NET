using System.ComponentModel;
using EsoData.Accounts;
using EsoData.Builds;
using EsoData.Formats;
using EsoMcp.Core;
using EsoMcp.Import;
using ModelContextProtocol.Server;

namespace EsoMcp.Server;

[McpServerToolType]
public sealed class BuildTools(AccountWorkspace workspace, WorkspaceStore store)
{
    [McpServerTool(Name = "edit_build", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Persist a named local build/target in SQLite. action=create,copy,update,delete,list,read. Create from current character or supplied native CSPS text. patch.target stores a complete declarative guide setup with named bars/scripts, passives, gear alternatives, CP selections, masteries and consumables; creating a target starts with an empty executable allocation. Exact account/character names or keys; IDs returned for subsequent calls. Mutations use expectedRevision. Omitted patch properties unchanged. No game files change. read returns summary unless section=target,build,requirements,guides,constraints,crafting.")]
    public string Edit(string action, string? id = null, int expectedRevision = 0, string? name = null,
        string? account = null, string? character = null, BuildPatch? patch = null, string? nativeCsps = null,
        string? section = null) => ToolResult.Json(() =>
    {
        var read = workspace.Read();
        if (action == "list") return (object)new { Plans = store.Plans(account is null ? null : AccountWorkspace.Select(read, account).Key) };
        BuildPlan plan;
        if (action == "create")
        {
            var owner = AccountWorkspace.Select(read, account);
            var c = owner.Character(character ?? throw new ArgumentException("Character is required."));
            plan = new() { Id = Guid.NewGuid().ToString("N")[..12], Name = name ?? "Untitled build", AccountKey = owner.Key,
                CharacterId = c.Id, Baseline = c.Build.DeepClone(), Build = nativeCsps is null ? patch?.Target is null ? c.Build.DeepClone() : new() : BuildCodec.Import(CspsBuild.Parse(nativeCsps)) };
            expectedRevision = 0;
        }
        else plan = store.Plan(id ?? throw new ArgumentException("Plan ID is required."));
        if (action == "read") return section switch
        {
            null => Summary(plan), "target" => plan.Target ?? (object)new { Available = false }, "build" => plan.Build, "requirements" => plan.Requirements, "guides" => plan.Guides,
            "constraints" => plan.Constraints, "crafting" => plan.Crafting, _ => throw new ArgumentException("Unknown plan section.")
        };
        if (action == "delete") { store.DeletePlan(plan.Id, expectedRevision); return new { Deleted = plan.Id }; }
        if (action == "copy") { plan.Id = Guid.NewGuid().ToString("N")[..12]; plan.Name = name ?? plan.Name + " copy"; expectedRevision = 0; }
        else if (action != "create" && action != "update") throw new ArgumentException("Unknown action.");
        if (action == "update" && expectedRevision <= 0) throw new ArgumentException("Updating requires the last returned expectedRevision.");
        var before = plan.Build.DeepClone();
        if (name is not null) plan.Name = name;
        if (patch is not null) plan = BuildEditor.Apply(plan, patch, read.Catalog);
        plan = store.SavePlan(plan, expectedRevision);
        var c2 = AccountWorkspace.Select(read, plan.AccountKey).Character(plan.CharacterId);
        if (plan.Target is not null) return new { Plan = Summary(plan), TargetSaved = true };
        var report = BuildAnalysis.Validate(c2, plan.Build, read.Catalog, plan.Constraints);
        var diff = PlanAnalysis.Compare(before, plan.Build, plan.Build.Sections, read.Catalog);
        return new { Plan = Summary(plan), Changes = diff.Take(20), ChangeCount = diff.Count, Validation = Compact(report) };
    });

    [McpServerTool(Name = "analyze_build", ReadOnly = true, OpenWorld = false)]
    [Description("Compare a saved target with the freshly loaded account. section=target,validation,differences,requirements,equipment,setCounts,leveling,crafting. Declarative guide targets use target comparison for validation/differences automatically; targetSection optionally filters paths (abilities,passives,equipment,champion,etc). Results are bounded. Missing metadata remains unknown. Gear set matches alone are partial, not proof of exact traits/enchants. Crafting requires exact catalog recipes.")]
    public string Analyze(string id, string section = "validation", string? crafter = null, int offset = 0, int limit = 20, string? targetSection = null) => ToolResult.Json(() =>
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentException("Invalid page.");
        var read = workspace.Read(); var plan = store.Plan(id);
        var account = AccountWorkspace.Select(read, plan.AccountKey); var character = account.Character(plan.CharacterId);
        if (section == "skillBudget") return GuideSkillBudget.Calculate(character,
            plan.Target ?? throw new ArgumentException("A guide target is required."), read.Catalog);
        if (plan.Target is not null && section is "target" or "validation" or "differences")
        {
            var findings = GuideAnalysis.Compare(account, character.Id, plan.Target, read.Catalog)
                .Where(f => targetSection is null || f.Path.Split('/')[0] == targetSection)
                .Where(f => section != "differences" || f.Status != "satisfied").ToArray();
            return (object)new { plan.Id, plan.Revision, Kind = "guide-target", Total = findings.Length,
                Counts = findings.GroupBy(f => f.Status).ToDictionary(g => g.Key, g => g.Count()),
                HasMore = offset + limit < findings.Length, Rows = findings.Skip(offset).Take(limit) };
        }
        if (section == "validation")
        {
            var validation = BuildAnalysis.Validate(character, plan.Build, read.Catalog, plan.Constraints);
            return (object)new { validation.Valid, validation.ReadyNow, validation.SkillPoints, Total = validation.Findings.Count,
                HasMore = offset + limit < validation.Findings.Count, Findings = validation.Findings.Skip(offset).Take(limit) };
        }
        if (section == "crafting") return PlanAnalysis.Crafting(account, plan.Crafting, crafter ?? plan.CharacterId, read.Catalog);
        IEnumerable<object> values = section switch
        {
            "differences" => PlanAnalysis.Compare(character.Build, plan.Build, plan.Build.Sections, read.Catalog),
            "requirements" => PlanAnalysis.Requirements(account, plan, read.Catalog),
            "setCounts" => PlanAnalysis.SetCounts(plan.Build),
            "equipment" => PlanAnalysis.Equipment(account, plan.Build).Select(e => (object)new { e.Slot, e.Status,
                Count = e.Matches.Count, Matches = e.Matches.Take(5).Select(i => new { i.Reference, i.Name, i.Location, i.CharacterId, i.Quality }) }),
            "leveling" => (character.Progress.Skills ?? []).Where(s => !s.IsPassive && !(s.Morph > 0 && s.Rank >= 4))
                .OrderBy(s => plan.Constraints.CoreLevelingSkills.Any(i => BuildAnalysis.Family(i, read.Catalog) == BuildAnalysis.Family(s.AbilityId, read.Catalog)) ? 0
                    : plan.Constraints.OptionalLevelingSkills.Any(i => BuildAnalysis.Family(i, read.Catalog) == BuildAnalysis.Family(s.AbilityId, read.Catalog)) ? 1 : 2).Cast<object>(),
            _ => throw new ArgumentException("Unknown analysis section.")
        };
        var rows = values.ToArray(); return new { plan.Id, plan.Revision, Total = rows.Length, HasMore = offset + limit < rows.Length, Rows = rows.Skip(offset).Take(limit) };
    });

    [McpServerTool(Name = "export_build", ReadOnly = true, OpenWorld = false)]
    [Description("Export a saved plan as native CSPS (default) or crafting links. Explicit sections: Skills, Bars, Attributes, ChampionPoints, Equipment, Mundus (comma separated). Omitted sections remain untouched in CSPS. Checks the exact exported revision against fresh data. Structural errors block export. requireReady=true also blocks unknown/unmet game requirements. Default allows a future target but returns its limitations. Does not apply anything in game.")]
    public string Export(string id, string format = "csps", BuildSections? sections = null, bool requireReady = false) => ToolResult.Json(() =>
    {
        var read = workspace.Read(); var plan = store.Plan(id);
        var character = AccountWorkspace.Select(read, plan.AccountKey).Character(plan.CharacterId);
        if (plan.Target is not null) throw new ArgumentException("This is a declarative guide target, not a resolved executable allocation. Create a separate working build with resolved IDs and point amounts before exporting.");
        if (format == "crafting") return (object)new { plan.Id, plan.Revision, Format = "Lazy Set Crafter", Text = BuildCodec.Crafting(plan.Crafting) };
        if (format != "csps") throw new ArgumentException("format must be csps or crafting.");
        var parts = sections ?? plan.Build.Sections;
        var report = BuildAnalysis.Validate(character, plan.Build, read.Catalog, plan.Constraints, parts);
        if (!report.Valid || requireReady && !report.ReadyNow) return new { plan.Id, plan.Revision, Exported = false, Validation = Compact(report) };
        return new { plan.Id, plan.Revision, Format = "CSPS", Sections = parts.ToString(),
            Text = BuildCodec.Export(plan.Build, read.Catalog, parts), Validation = Compact(report) };
    });

    [McpServerTool(Name = "verify_build", ReadOnly = true, OpenWorld = false)]
    [Description("After applying a plan in ESO and saving/reloading, compare freshly observed state to that exact plan revision. Returns only differences and unobserved sections; does not equate a saved CSPS profile with application. Optional sections restrict verification to what was exported.")]
    public string Verify(string id, int expectedRevision, BuildSections? sections = null, int offset = 0, int limit = 20) => ToolResult.Json(() =>
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentException("Invalid page.");
        var read = workspace.Read(); var plan = store.Plan(id);
        if (plan.Target is not null) throw new ArgumentException("Use analyze_build to compare a guide target; verify_build requires an exported executable build.");
        if (plan.Revision != expectedRevision) throw new ArgumentException("Plan revision changed; select the exported revision.");
        var account = AccountWorkspace.Select(read, plan.AccountKey); var character = account.Character(plan.CharacterId);
        var differences = PlanAnalysis.Compare(character.Build, plan.Build, sections ?? plan.Build.Sections, read.Catalog);
        return new { plan.Id, plan.Revision, MatchesObserved = differences.Count == 0, Total = differences.Count,
            HasMore = offset + limit < differences.Count, Differences = differences.Skip(offset).Take(limit),
            Observed = account.Sources.SelectMany(s => s.Coverage).Where(c => c.CharacterId == character.Id && c.Section == "character").Select(c => c.ScannedAt).Distinct() };
    });
    internal static object Summary(BuildPlan p) => new { p.Id, p.Name, p.Revision, p.AccountKey, p.CharacterId, Kind = p.Target is null ? "allocation" : "guide-target", Sections = p.Build.Sections.ToString(), ModeledSkillPoints = p.Target is null ? (int?)p.Build.SkillPointCost : null, ChampionPoints = p.Target is null ? (int?)p.Build.ChampionPoints.Values.Sum() : null, Requirements = p.Requirements.Count, Target = p.Target is null ? null : new { p.Target.Variant, Abilities = p.Target.Abilities.Count, Passives = p.Target.Passives.Count, Equipment = p.Target.Equipment.Count, Champion = p.Target.Champion.Count } };
    internal static object Compact(BuildReport report) => new { report.Valid, report.ReadyNow, report.SkillPoints,
        FindingCount = report.Findings.Count, Findings = report.Findings.Take(12), HasMore = report.Findings.Count > 12 };
}
