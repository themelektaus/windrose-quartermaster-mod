using System.Text.Json.Nodes;

namespace Windrose.StackSize.Gui;

sealed class ItemDto
{
    public string id;
    public string name;
    public string icon;
    public int maxCountInSlot;
    public string itemClass;
    public string rarity;
    public string category;
    public string itemType;
    // Canonical UE asset path (e.g. /R5BusinessRules/InventoryItems/Consumables/Food/DA_CID_X.DA_CID_X).
    // Derived from the on-disk source location so the loot-entry picker can emit a valid LootItem
    // for any item, not just those that already appear in some vanilla LootTable.
    public string path;
    public JsonNode meta;
}
