using System.Collections.Generic;

namespace Windrose.Quartermaster.Web;

sealed class BuyerDto
{
    public string id;
    public string faction;
    public string label;
    public string slot;
    public List<BuyerEntryDto> entries;
}

sealed class BuyerEntryDto
{
    public string recipeId;
    public string recipePath;
    public string recipeTag;

    // Each row takes only index 0 of RecipeCost/RecipeResult; extra lines are ignored.
    public string itemId;
    public string itemPath;
    public int    itemCount;
    public string payItemId;
    public string payItemPath;
    public int    payCount;

    public string craftRequirement;

    public bool   resolved;        // false: recipe JSON not found/parsed; row still surfaced
}
