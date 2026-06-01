using System.Collections.Generic;

namespace Windrose.Quartermaster.Web;

sealed class SellerDto
{
    public string id;
    public string faction;
    public string label;
    public string slot;
    public List<SellerEntryDto> entries;
}

sealed class SellerEntryDto
{
    public string recipeId;
    public string recipePath;
    public string recipeTag;

    // PlayerBuys inverts the JSON->DTO mapping relative to BuyerEntryDto:
    // itemId comes from RecipeResult, payItemId from RecipeCost.
    public string itemId;
    public string itemPath;
    public int    itemCount;
    public string payItemId;
    public string payItemPath;
    public int    payCount;

    public string craftRequirement;

    public bool   resolved;        // false: recipe JSON not found/parsed; row still surfaced
}
