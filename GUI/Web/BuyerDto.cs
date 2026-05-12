using System.Collections.Generic;

namespace Windrose.Quartermaster.Web;

// Wire-format for /api/buyers. Each BuyerDto represents one R5BLRecipeList
// JSON whose filename matches the PlayerSells pattern (the NPC buys from
// the player). Trade Rum Bottles ships exactly one such file augmented with
// a Rum entry; vanilla has 8 of them (Brethren / Bucaneers / Civilians /
// Smugglers, slots 1 and 1_02). Read-only - phase 1 only renders the data,
// no editing yet.
sealed class BuyerDto
{
    public string id;        // path under RecipeLists/, '/'-separated, no extension
                             // e.g. "TradeBrethren/DA_RecipeList_Trade_Brethren1_PlayerSells_02"
    public string faction;   // "Brethren" / "Bucaneers" / "Civilians" / "Smugglers" / "(other)"
    public string label;     // human-friendly: "Brethren Trader 1 (Inventory 2)"
    public string slot;      // raw slot suffix from the filename, e.g. "1" or "1_02"
    public List<BuyerEntryDto> entries;
}

sealed class BuyerEntryDto
{
    public string recipeId;        // filename of the R5BLRecipeData JSON
                                   // (e.g. "DA_RD_CID_Food_Rum_Bottle_T03_Buy")
    public string recipePath;      // raw asset path as referenced in the RecipeList
    public string recipeTag;       // RecipeTag.TagName, useful for debugging / future lookups

    // PlayerSells semantics: Cost = what the player gives up (the item),
    // Result = what the NPC pays (usually coins). We surface both sides so
    // a future Buyer-editor can change either column without re-deriving
    // semantics. Each row is the *first* entry from RecipeCost / RecipeResult -
    // trade recipes always have a single cost + single result line; if a
    // pathological recipe has more we just take index 0 and ignore the rest.
    public string itemId;          // the item the player sells (e.g. "DA_CID_Food_Rum_Bottle_T03")
    public string itemPath;        // raw asset path
    public int    itemCount;       // how many the player gives per trade (typically 1)
    public string payItemId;       // what the NPC pays in (typically "DA_DID_Misc_CoinPiastre_T02")
    public string payItemPath;
    public int    payCount;        // how many coins per trade

    public string craftRequirement; // optional Brethren/Bucaneers/... reputation gate
                                    // (full asset path, e.g. ".../DA_Requirement_Brethren_3")

    public bool   resolved;        // true if we found the recipe JSON on disk and parsed it;
                                   // false means the RecipeList referenced a recipe whose JSON
                                   // we couldn't find or parse - the frontend can still show the
                                   // ref so the user knows it's there, just without item details
}
