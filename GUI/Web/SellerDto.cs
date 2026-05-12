using System.Collections.Generic;

namespace Windrose.Quartermaster.Web;

// Wire-format for /api/sellers. Each SellerDto represents one R5BLRecipeList
// JSON whose filename matches the PlayerBuys pattern (the player buys from
// the NPC = NPC is a vendor). Vanilla ships a mix of these: 8 trader rosters
// (Brethren / Bucaneers / Civilians / Smugglers, slots 1 and 1_02) plus the
// 3 Handyman traders (Animals / Food / Resources). The Smugglers PlayerBuys_02
// list is also where the Guinea<->Piastre exchange recipes live.
//
// Read-only - phase 1 only renders the data, no editing yet. CRUD will share
// the same DTO so the wire-format doesn't change between phases.
sealed class SellerDto
{
    public string id;        // path under RecipeLists/, '/'-separated, no extension
                             // e.g. "TradeBrethren/DA_RecipeList_Trade_Brethren1_PlayerBuys"
    public string faction;   // "Brethren" / "Bucaneers" / "Civilians" / "Smugglers" / "Handyman" / "(other)"
    public string label;     // human-friendly: "Brethren Vendor 1 (Inventory 2)"
    public string slot;      // raw slot suffix from the filename, e.g. "1" or "1_02"
    public List<SellerEntryDto> entries;
}

sealed class SellerEntryDto
{
    public string recipeId;        // filename of the R5BLRecipeData JSON
                                   // (e.g. "DA_RD_CID_Food_Rum_Bottle_T03_Buy")
    public string recipePath;      // raw asset path as referenced in the RecipeList
    public string recipeTag;       // RecipeTag.TagName, useful for debugging / future lookups

    // PlayerBuys semantics: Cost = what the player pays (typically coins),
    // Result = what the NPC sells (the actual item). To keep field meanings
    // consistent with BuyerEntryDto for the frontend (itemId = the "main"
    // item being traded, payItemId = the currency/barter good), we swap
    // which JSON field maps to which DTO field compared to BuyerEntryDto:
    //   itemId    <- RecipeResult (= what NPC gives = item being sold)
    //   payItemId <- RecipeCost   (= what player pays = currency)
    public string itemId;          // the item the NPC sells (e.g. "DA_CID_Food_Rum_Bottle_T03")
    public string itemPath;        // raw asset path
    public int    itemCount;       // how many the NPC delivers per trade (typically 1)
    public string payItemId;       // what the player pays in (typically "DA_DID_Misc_CoinPiastre_T02")
    public string payItemPath;
    public int    payCount;        // how many coins per trade

    public string craftRequirement; // optional Brethren/Bucaneers/... reputation gate
                                    // (full asset path, e.g. ".../DA_Requirement_Brethren_3")

    public bool   resolved;        // true if we found the recipe JSON on disk and parsed it;
                                   // false means the RecipeList referenced a recipe whose JSON
                                   // we couldn't find or parse - the frontend still surfaces a row
                                   // so the user knows the entry exists, just without item details
}
