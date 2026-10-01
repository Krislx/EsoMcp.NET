using System.ComponentModel;
using System.Text.Json;
using EsoData.Accounts;
using EsoData.Builds;
using EsoData.Catalogs;
using EsoData.Items;
using EsoMcp.Import;
using ModelContextProtocol.Server;

namespace EsoMcp.Server;

public sealed class AccountQuery
{
    public string Section { get; set; } = "characters";
    public string? Character { get; set; }
    public string? Text { get; set; }
    public long[]? Ids { get; set; }
    public long[]? SetIds { get; set; }
    public string? Location { get; set; }
    public string? Category { get; set; }
    public bool? Known { get; set; }
    public bool UnfinishedOnly { get; set; }
    public string[]? Fields { get; set; }
    public bool Group { get; set; }
    public string? PriceStatus { get; set; }
    public string? Sort { get; set; }
    public bool IncludeDetails { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 20;
}

[McpServerToolType]
public sealed class AccountTools(AccountWorkspace workspace, ImportOptions? options = null)
{
    [McpServerTool(Name = "inspect_account", ReadOnly = true, OpenWorld = false)]
    [Description("Load fresh local account objects and persist them in SQLite. With no queries, list accounts. Sections: characters, summary, skills, skillLines, inventory, equipment, build (actual allocation), statistics, champion, knowledge, research, savedBuilds, sources, collections, prices. Inventory includeDetails=true exposes effective link trait, enchantment and CP160 metadata; missing definitions or observations remain null. savedBuilds includeDetails=true returns a saved profile, not actual application. Inventory supports priceStatus and sort=stackPriceDesc/unitPriceDesc. Exact character name/ID; account key includes server. Default 20 rows, limit 1..100; fields projects properties. offline=true uses stored snapshots.")]
    public string Inspect(string? account = null, AccountQuery[]? queries = null, bool offline = false) => ToolResult.Json(() =>
    {
        var read = workspace.Read(offline);
        if (queries is null || queries.Length == 0) return (object)new { Accounts = read.Data.Accounts.Select(a => new { a.Key, a.Name, a.Server, Characters = a.Characters.Count }), read.Data.Diagnostics };
        if (queries.Length > 20) throw new ArgumentException("At most 20 queries per request.");
        var selected = AccountWorkspace.Select(read, account);
        return new { Account = selected.Key, selected.PriceSource, Results = queries.Select(q => Query(selected, q, read.Catalog)).ToArray(),
            Diagnostics = read.Data.Diagnostics.Concat(selected.Sources.SelectMany(s => s.Diagnostics)).Distinct().ToArray() };
    });

    internal static object Query(EsoAccount account, AccountQuery query, GameCatalog? catalog = null)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 100) throw new ArgumentException("Use offset >= 0 and limit 1..100.");
        var character = query.Character is null ? null : account.Character(query.Character);
        EsoCharacter NeedCharacter() => character ?? throw new ArgumentException("Select a character for this section.");
        bool Text(string? value) => query.Text is null || value?.Contains(query.Text, StringComparison.OrdinalIgnoreCase) == true;
        IEnumerable<object> rows = query.Section switch
        {
            "characters" => account.Characters.Where(c => Text(c.Name)).Select(c => (object)new { c.Id, c.Name, c.Level, c.Class, c.Race }),
            "summary" => [new { NeedCharacter().Name, NeedCharacter().Level, NeedCharacter().Class, NeedCharacter().Race,
                NeedCharacter().Progress.TotalSkillPoints, NeedCharacter().Progress.UnspentSkillPoints, NeedCharacter().Progress.TotalChampionPoints,
                NeedCharacter().Progress.ChampionBudgets, Sections = NeedCharacter().Build.Sections.ToString() }],
            "skills" => (NeedCharacter().Progress.Skills ?? []).Where(s => Text(s.Name) && (query.Ids is null || query.Ids.Contains(s.AbilityId))
                && (!query.UnfinishedOnly || !s.IsPassive && !(s.Morph > 0 && s.Rank >= 4))).Cast<object>(),
            "skillLines" => (NeedCharacter().Progress.SkillLines ?? []).Where(s => Text(s.Key)).Select(s => (object)new { Name = s.Key, Rank = s.Value }),
            "inventory" => Inventory(),
            "prices" => [new { account.PriceSource, Stacks = account.Inventory.Count(),
                StatusCounts = account.Inventory.GroupBy(i => i.Price.Status).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                PricedStackEstimate = account.Inventory.Any(i => i.EstimatedStackPrice.HasValue)
                    ? account.Inventory.Sum(i => i.EstimatedStackPrice ?? 0) : (decimal?)null,
                Note = "Partial market estimate, not liquidatable wealth. Unpriced items excluded; binding/tradability is not established." }],
            "equipment" => NeedCharacter().Build.Equipment.Select(p => (object)new { Slot = p.Key, Item = p.Value }),
            "build" => [NeedCharacter().Build],
            "statistics" => NeedCharacter().RecordedStatistics is { } statistics ? [statistics] : [],
            "champion" => [new { NeedCharacter().Build.ChampionPoints, NeedCharacter().Build.ChampionSlots, NeedCharacter().Progress.ChampionBudgets }],
            "research" => [new { NeedCharacter().Progress.Research, NeedCharacter().Progress.ResearchKnowledge }],
            "knowledge" => NeedCharacter().Progress.Knowledge.Where(k => query.Category is null || k.Key == query.Category).SelectMany(k =>
                k.Value.Where(e => (query.Ids is null || query.Ids.Contains(e.Key)) && (query.Known is null || e.Value == query.Known))
                    .Select(e => new { Category = k.Key, ItemId = e.Key, Name = catalog?.Items.GetValueOrDefault(e.Key)?.Name, Known = e.Value })
                    .Where(e => Text(e.Name)).Cast<object>()),
            "savedBuilds" => SavedBuilds(),
            "sources" => account.Sources.Cast<object>(),
            "collections" => (account.SetCollections ?? []).Where(c => query.SetIds is null || query.SetIds.Contains(c.Key))
                .Select(c => (object)new { SetId = c.Key, SlotMask = c.Value }),
            _ => throw new ArgumentException("Unknown account section.")
        };
        var all = rows.ToArray();
        var available = query.Section switch
        {
            "skills" => NeedCharacter().Progress.Skills is not null,
            "skillLines" => NeedCharacter().Progress.SkillLines is not null,
            "knowledge" => query.Category is null ? NeedCharacter().Progress.Knowledge.Count > 0 : NeedCharacter().Progress.Knowledge.ContainsKey(query.Category),
            "equipment" => NeedCharacter().Build.Sections.HasFlag(BuildSections.Equipment),
            "statistics" => NeedCharacter().RecordedStatistics is not null,
            "champion" => NeedCharacter().Build.Sections.HasFlag(BuildSections.ChampionPoints),
            "collections" => account.SetCollections is not null,
            "prices" => account.PriceSource is not null,
            _ => true
        };
        return new { query.Section, Available = available, Total = all.Length, query.Offset,
            HasMore = query.Offset + query.Limit < all.Length, Rows = all.Skip(query.Offset).Take(query.Limit).Select(r => Project(r, query.Fields)).ToArray() };

        IEnumerable<object> Inventory()
        {
            var items = account.Inventory.Where(i => (character is null || i.CharacterId == character.Id) && Text(i.Name)
                && (query.Ids is null || query.Ids.Contains(i.ItemId)) && (query.SetIds is null || i.SetId.HasValue && query.SetIds.Contains(i.SetId.Value))
                && (query.Location is null || string.Equals(i.Location, query.Location, StringComparison.OrdinalIgnoreCase)));
            if (query.PriceStatus is not null)
            {
                if (!Enum.TryParse<EsoData.Pricing.PriceMatchStatus>(query.PriceStatus, true, out var status) || !Enum.IsDefined(status))
                    throw new ArgumentException("Unknown priceStatus.");
                items = items.Where(i => i.Price.Status == status);
            }
            items = query.Sort switch
            {
                null => items,
                "stackPriceDesc" => items.OrderByDescending(i => i.EstimatedStackPrice).ThenBy(i => i.Reference),
                "unitPriceDesc" => items.OrderByDescending(i => i.Price.EstimatedUnitPrice).ThenBy(i => i.Reference),
                _ => throw new ArgumentException("sort must be stackPriceDesc or unitPriceDesc.")
            };
            if (query.Group && query.Sort is not null) throw new ArgumentException("Price sorting applies to individual stacks; omit group.");
            if (query.Group) return items.GroupBy(i => (i.SetId, i.Location, i.Quality, i.CharacterId)).Select(g => (object)new
            { g.Key.SetId, g.Key.Location, g.Key.Quality, g.Key.CharacterId, Count = g.Sum(i => i.Count), Stacks = g.Count(),
                PricedStacks = g.Count(i => i.EstimatedStackPrice.HasValue),
                PricedStackEstimate = g.Any(i => i.EstimatedStackPrice.HasValue) ? g.Sum(i => i.EstimatedStackPrice ?? 0) : (decimal?)null });
            return items.Select(i => query.IncludeDetails
                ? (object)new { i.Reference, i.ItemId, i.Name, i.Count, i.Location, i.CharacterId, i.Quality, i.SetId,
                    i.Trait, i.ArmorType, i.WeaponType, i.EquipType, Details = OwnedItemDetails.Read(i, catalog), i.Price }
                : new { i.Reference, i.ItemId, i.Name, i.Count, i.Location, i.CharacterId, i.Quality, i.SetId, i.Trait, i.ArmorType, i.WeaponType,
                    i.Price, i.EstimatedStackPrice });
        }
        IEnumerable<object> SavedBuilds() => NeedCharacter().SavedBuilds
            .Where(b => query.Text is null || b.Id.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
                || b.Name?.Contains(query.Text, StringComparison.OrdinalIgnoreCase) == true)
            .Select(b => query.IncludeDetails
                ? (object)new { b.Id, b.Name, b.SavedAt, Sections = b.Build.Sections.ToString(), b.Build }
                : new { b.Id, b.Name, b.SavedAt, Sections = b.Build.Sections.ToString() });
    }
    internal static object Project(object row, string[]? fields)
    {
        if (fields is null) return row;
        var json = JsonSerializer.SerializeToElement(row, AccountJson.Options);
        return fields.ToDictionary(f => f, f => json.TryGetProperty(f, out var value) ? value : throw new ArgumentException($"Unknown projected field '{f}'."));
    }

    [McpServerTool(Name = "resolve_definitions", ReadOnly = true, OpenWorld = false)]
    [Description("Batch exact or substring lookups against loaded local catalogs. kind: skills, sets, items, champion. Champion lookup also reads names and disciplines from an installed CSPS data file; that fallback does not claim caps, prerequisites or slottability. Names and IDs are definitions, not ownership. Returns bounded rows; resolve exact skill names in edit_build without a separate lookup. No network request.")]
    public string Resolve(string kind, string[]? names = null, long[]? ids = null, int limit = 20, int offset = 0) => ToolResult.Json(() =>
    {
        if (limit is < 1 or > 100 || offset < 0) throw new ArgumentException("Invalid page.");
        var catalog = workspace.Read().Catalog;
        bool Match(long id, string? name) => (ids is null || ids.Contains(id)) && (names is null || names.Any(n => name?.Contains(n, StringComparison.OrdinalIgnoreCase) == true));
        object[] rows = kind switch
        {
            "skills" => catalog.Skills.Values.Where(s => Match(s.Id, s.Name)).Cast<object>().ToArray(),
            "items" => catalog.Items.Values.Where(s => Match(s.Id, s.Name)).Cast<object>().ToArray(),
            "sets" => catalog.Sets.Values.Where(s => Match(s.Id, s.Names.GetValueOrDefault("en"))).Select(s => (object)new { s.Id, Name = s.Names.GetValueOrDefault("en") }).ToArray(),
            "champion" => ChampionDefinitions().Where(s => Match(s.Id, s.Name)).Cast<object>().ToArray(),
            _ => throw new ArgumentException("kind must be skills, sets, items or champion.")
        };
        return new { Total = rows.Length, HasMore = offset + limit < rows.Length, Rows = rows.Skip(offset).Take(limit) };

        IEnumerable<ChampionLookup> ChampionDefinitions()
        {
            var definitions = catalog.ChampionStars.Values.ToDictionary(s => s.Id, s => new ChampionLookup(s.Id,
                s.Name, s.Discipline, s.MaximumPoints, s.Slottable, s.MinimumSlottablePoints,
                s.Prerequisites, "catalog"));
            foreach (var addons in (options?.Locations ?? []).Select(x => x.AddonsPath)
                         .Where(x => x is not null).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var directory = Path.Combine(addons!, "CarosSkillPointSaver");
                if (!File.Exists(Path.Combine(directory, "data", "cpinfo.lua"))) continue;
                foreach (var definition in CspsChampionCatalog.Read(directory))
                    definitions.TryAdd(definition.Id, new(definition.Id, definition.Name, definition.Discipline,
                        null, null, null, null, "installed-csps"));
            }
            return definitions.Values.OrderBy(x => x.Id);
        }
    });

    private sealed record ChampionLookup(long Id, string Name, string Discipline, int? MaximumPoints,
        bool? Slottable, int? MinimumSlottablePoints, IReadOnlyDictionary<long, int>? Prerequisites, string Source);
}
