using System.Text.Json;
using EsoData.Catalogs;
using EsoMcp.Core;

namespace EsoMcp.Import;

/// <summary>
/// Explicitly refreshes selected LibSets item IDs from UESP, then resolves crafting choices from SQLite only.
/// No metadata download runs during ordinary MCP requests.
/// </summary>
public sealed class CraftingCatalogService(Database database) : ICraftingCatalog
{
    private const string Endpoint = "https://esolog.uesp.net/exportJson.php";
    private static readonly HttpClient Client = new();

    public async Task<IReadOnlyList<RefreshEntry>> RefreshDefinitionsAsync(IReadOnlyList<long> itemIds,
        CancellationToken cancellationToken = default)
    {
        if (itemIds.Any(id => id <= 0)) throw new ArgumentException("Item IDs must be positive.", nameof(itemIds));
        var results = new List<RefreshEntry>();
        foreach (var chunk in itemIds.Distinct().Order().Chunk(100))
        {
            var query = $"{Endpoint}?table=minedItemSummary&ids={string.Join(',', chunk)}" +
                "&fields=itemId,name,setId,equipType,armorType,weaponType,trait,defaultEnchantId,enchantName,enchantDesc,abilityDesc,specialType";
            var catalog = UespCatalog.Parse(await Client.GetStringAsync(query, cancellationToken), query);
            var raw = catalog.ToJson();
            var source = new SourceDocument(Identity.Key("uesp-item-metadata", "items", string.Join(',', chunk)),
                "uesp-item-metadata", query, Identity.Key(raw), DateTimeOffset.UtcNow, raw, Priority: 20);
            database.Replace(CatalogProjection.Project(catalog, source), cancellationToken);
            var message = $"{catalog.Items.Count} of {chunk.Length} requested item definitions.";
            database.RecordAttempt(source.Key, source.Label, source.Path, "imported", message);
            results.Add(new(source.Label, "imported", message));
        }
        return results;
    }

    public async Task<IReadOnlyList<RefreshEntry>> RefreshItemMetadataAsync(IReadOnlyList<long> setIds,
        CancellationToken cancellationToken = default)
    {
        if (setIds.Count is < 1 or > 100) throw new ArgumentException("Provide 1..100 set IDs.", nameof(setIds));
        var results = new List<RefreshEntry>();
        foreach (var setId in setIds.Distinct())
        {
            if (setId <= 0) throw new ArgumentOutOfRangeException(nameof(setIds));
            var itemIds = database.CatalogItemIds(setId);
            if (itemIds.Count == 0)
                throw new KeyNotFoundException($"Set {setId} has no local LibSets item membership. Refresh local sources first.");

            var catalogs = new List<GameCatalog>();
            foreach (var chunk in itemIds.Chunk(100))
            {
                var query = $"{Endpoint}?table=minedItemSummary&ids={string.Join(',', chunk)}" +
                    "&fields=itemId,name,setId,equipType,armorType,weaponType,trait,defaultEnchantId,enchantName,enchantDesc,abilityDesc,specialType";
                var json = await Client.GetStringAsync(query, cancellationToken);
                catalogs.Add(UespCatalog.Parse(json, query));
            }
            var catalog = GameCatalog.Merge(catalogs);
            var raw = catalog.ToJson();
            var source = new SourceDocument(Identity.Key("uesp-item-metadata", setId.ToString()), "uesp-item-metadata",
                $"{Endpoint}?table=minedItemSummary&setId={setId}", Identity.Key(raw), DateTimeOffset.UtcNow, raw, Priority: 20);
            database.Replace(CatalogProjection.Project(catalog, source), cancellationToken);
            database.RecordAttempt(source.Key, source.Label, source.Path, "imported", $"{catalog.Items.Count} item definitions for set {setId}.");
            results.Add(new(source.Label, "imported", $"Set {setId}: {catalog.Items.Count} item definitions."));
        }
        return results;
    }

    public IReadOnlyList<long> Resolve(IReadOnlyList<CraftingPlanItem> items)
    {
        if (items.Count is < 1 or > 100) throw new ArgumentException("Provide 1..100 crafting plan items.", nameof(items));
        var parts = new List<GameCatalog>();
        foreach (var document in items.Select(x => x.SetId).Distinct().SelectMany(database.CatalogItemDocuments))
        {
            var item = JsonSerializer.Deserialize<ItemDefinition>(document, DataJson.Options)
                ?? throw new FormatException("Stored item metadata is invalid.");
            var part = new GameCatalog(); part.Items[item.Id] = item; parts.Add(part);
        }
        var catalog = GameCatalog.Merge(parts);
        return items.Select(item => new CraftedItemSelector(item.SetId, item.EquipType, item.ArmorType,
            item.WeaponType, item.Trait).Resolve(catalog).Id).ToArray();
    }
}
