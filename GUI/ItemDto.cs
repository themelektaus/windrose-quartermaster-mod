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
    public JsonNode meta;
}
