using System.Collections.Generic;

namespace Windrose.Quartermaster.Web;

sealed class LootTableDto
{
    public string id;
    public string category;
    public string type;
    public List<LootEntryDto> entries;
}

sealed class LootEntryDto
{
    public int index;          // position in vanilla LootData[]; stable override key
    public int min;
    public int max;
    public int weight;
    // Per entry, exactly one of the lootItem* pair or the lootTable* pair is populated.
    public string lootItemId;
    public string lootItemPath;
    public string lootTableId;
    public string lootTablePath;
}
