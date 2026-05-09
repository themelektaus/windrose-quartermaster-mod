using System.Collections.Generic;

namespace Windrose.Quartermaster.Gui;

// Wire-format for /api/loot-tables. Mirrors the in-pak Vanilla LT JSON
// shape one-to-one but with the long UE asset paths reduced to
// human-readable ids that the frontend can use as lookup keys against
// /api/items and /api/loot-tables itself (sub-table refs).
sealed class LootTableDto
{
    public string id;          // "Mobs/DA_LT_Mob_BlackBeard_Sergeant_Final" (matches LootPatcher's lookup key)
    public string category;    // first segment of id ("Mobs", "Chests", "Foliage", ...)
    public string type;        // LootTableType: "List" / "Weight" / "WeightedOnetime" / "Ordered"
    public List<LootEntryDto> entries;
}

sealed class LootEntryDto
{
    public int index;          // position in vanilla LootData[]; stable as the override key
    public int min;
    public int max;
    public int weight;
    // Exactly one of (lootItemId + lootItemPath) or (lootTableId + lootTablePath)
    // is populated per entry. The "...Path" variant is the raw asset-path the
    // engine consumes; the "...Id" variant is the short form the frontend uses
    // to cross-reference /api/items resp. another /api/loot-tables entry.
    public string lootItemId;       // e.g. "DA_DID_Resource_TeaLeaf_T04"
    public string lootItemPath;     // e.g. "/R5BusinessRules/InventoryItems/.../DA_DID_Resource_TeaLeaf_T04.DA_DID_Resource_TeaLeaf_T04"
    public string lootTableId;      // e.g. "Mobs/Rss/DA_LT_Mob_BlackBeard_Sergeant_BlackbeardSign"
    public string lootTablePath;    // e.g. "/R5BusinessRules/LootTables/Mobs/Rss/DA_LT_..."
    // ItemAttributeModifiers is intentionally omitted from the wire format --
    // Phase 1 is read-only on those, so the frontend doesn't need to see them.
}
