using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

// GET /api/sellers -> all R5BLRecipeList JSONs in Sources/Vanilla/.../RecipeLists/
// whose filename indicates PlayerBuys (the player buys from the NPC = NPC is
// a vendor). Mirrors BuyersEndpoint structurally; the only behavioral
// differences are:
//   1) filename filter is "PlayerBuys" instead of "PlayerSells"
//   2) Cost/Result mapping is swapped (NPC gives Result, player pays Cost)
//   3) the faction label uses "Vendor" / "Trader" depending on whether the
//      list also has a buy-side counterpart, so the user can distinguish
//      pure vendors (Handyman) from full-service traders.
//
// Read-only: phase 1 of the sellers feature only displays the trade rosters.
// CRUD (add/remove items, edit prices) follows in a later iteration.
public static class SellersEndpoint
{
    // Path prefix the recipe references use; everything we resolve sits under
    // the R5BusinessRules plugin tree.
    const string PluginPathPrefix = "/R5BusinessRules/";

    public static void Map(WebApplication app, string repoRoot)
    {
        var paths = WindrosePaths.FromModRoot(repoRoot);

        app.MapGet("/api/sellers", async () =>
        {
            var sellers = await LoadSellers(paths);
            return Results.Json(sellers);
        });
    }

    static async Task<List<SellerDto>> LoadSellers(WindrosePaths paths)
    {
        var result = new List<SellerDto>();
        if (!Directory.Exists(paths.VanillaRecipeLists)) return result;

        // The recipes-root is where we resolve every recipe ref to a file.
        // If it's missing we still surface the lists, just with unresolved
        // entries so the frontend can show "Sources/Vanilla/.../Recipes/
        // is missing - re-run setup".
        var recipesRoot = Directory.Exists(paths.VanillaRecipes)
            ? Path.GetFullPath(paths.VanillaRecipes)
            : null;

        var rootFull = Path.GetFullPath(paths.VanillaRecipeLists);
        foreach (var path in Directory.EnumerateFiles(rootFull, "*.json", SearchOption.AllDirectories))
        {
            // Filename is the cheapest filter: only PlayerBuys lists make
            // it into the Sellers tab. Crafting lists (Furnace_T01, etc.) and
            // PlayerSells lists (= Buyers, separate tab) are skipped here.
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (fileName.IndexOf("PlayerBuys", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            // The four Faction1_PlayerBuys "tier 1" rosters exist on disk but
            // are not wired up in-game - the actually-spawning vendor uses the
            // matching _02 inventory. Hide them so users don't waste effort
            // editing rosters that have no in-game effect. (The Buyers side of
            // these same factions IS active, so this filter lives on the
            // Sellers endpoint only.)
            if (IsHiddenSellerList(fileName)) continue;

            var dto = await TryParseSeller(rootFull, recipesRoot, path);
            if (dto != null) result.Add(dto);
        }

        result.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
        return result;
    }

    static async Task<SellerDto> TryParseSeller(string rootFull, string recipesRoot, string jsonPath)
    {
        try
        {
            using var stream = File.OpenRead(jsonPath);
            using var doc = await JsonDocument.ParseAsync(stream);

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("$type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String
                || typeEl.GetString() != "R5BLRecipeList")
                return null;

            // Build a stable id: path under RecipeLists/, '/'-separated, no ext.
            var rel = jsonPath.Substring(rootFull.Length).TrimStart(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var id = rel
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            if (id.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                id = id.Substring(0, id.Length - 5);

            var dto = new SellerDto { id = id, entries = new List<SellerEntryDto>() };
            DeriveFactionLabel(id, dto);

            if (!root.TryGetProperty("RecipeList", out var listEl)
                || listEl.ValueKind != JsonValueKind.Array)
                return dto; // empty list is still a valid seller DTO

            foreach (var refEl in listEl.EnumerateArray())
            {
                if (refEl.ValueKind != JsonValueKind.String) continue;
                var refStr = refEl.GetString();

                // Skip reputation-token trade lines (BlackbeardSign,
                // FactionSign). In practice these only appear on the
                // PlayerSells side, but we apply the same filter on the
                // PlayerBuys side defensively so a future game update
                // can't sneak rep tokens onto vendor rosters.
                if (IsReputationRecipe(refStr)) continue;

                var entry = ResolveRecipe(recipesRoot, refStr);
                if (entry != null) dto.entries.Add(entry);
            }

            // If filtering left no entries, drop the list entirely so the
            // frontend doesn't render empty cards.
            if (dto.entries.Count == 0) return null;

            return dto;
        }
        catch
        {
            return null;
        }
    }

    // Filename anatomy (vanilla):
    //   DA_RecipeList_Trade_<Faction>1_PlayerBuys             -> slot "1"
    //   DA_RecipeList_Trade_<Faction>1_PlayerBuys_02          -> slot "1_02"
    //   DA_RecipeList_Trade_Handyman_Trader_Animals_PlayerBuys -> slot "Animals"
    // The id we already built carries the faction folder ("TradeBrethren") +
    // filename; derive faction + slot for human-friendly display.
    static void DeriveFactionLabel(string id, SellerDto dto)
    {
        var lastSlash = id.LastIndexOf('/');
        var folder = lastSlash > 0 ? id.Substring(0, lastSlash) : string.Empty;
        var file = lastSlash > 0 ? id.Substring(lastSlash + 1) : id;

        // Folder is "TradeBrethren" / "TradeBucaneers" / "TradeHandyman" / etc.
        var faction = folder.StartsWith("Trade", StringComparison.OrdinalIgnoreCase)
            ? folder.Substring(5)
            : "(other)";
        dto.faction = faction;

        // Slot suffix: everything after "_PlayerBuys".
        const string marker = "_PlayerBuys";
        var markerIdx = file.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var slot = string.Empty;
        if (markerIdx >= 0)
        {
            // Strip leading "DA_RecipeList_Trade_<faction>" prefix to read off
            // the slot ("1", "Animals", ...). For numeric slots we then
            // re-glue any "_02" suffix that lives after "_PlayerBuys".
            var beforeMarker = file.Substring(0, markerIdx);
            var lastUnderscore = beforeMarker.LastIndexOf('_');
            var slotBase = lastUnderscore >= 0 ? beforeMarker.Substring(lastUnderscore + 1) : beforeMarker;

            // Numeric trailing digits (Brethren1 -> "1") vs Handyman naming
            // (Trader_Animals -> "Animals"): trim trailing digits if any,
            // otherwise keep the whole token.
            var digitStart = slotBase.Length;
            while (digitStart > 0 && char.IsDigit(slotBase[digitStart - 1])) digitStart--;
            var slotNum = digitStart < slotBase.Length ? slotBase.Substring(digitStart) : slotBase;

            var afterMarker = file.Substring(markerIdx + marker.Length);
            slot = string.IsNullOrEmpty(afterMarker)
                ? slotNum
                : slotNum + afterMarker; // e.g. "1_02"
        }
        dto.slot = slot;

        // Label heuristics. Handyman traders carry "Trader_<Resource>" in
        // their slot - they're standalone vendors with no Buyer counterpart,
        // so we surface them as "<Faction> Vendor" without a numeric suffix.
        // Faction traders get "Brethren Vendor 1 (Inventory 2)" so the user
        // can correlate them with the matching Buyer-tab card.
        if (faction.Equals("Handyman", StringComparison.OrdinalIgnoreCase))
        {
            // For Handyman the slot looks like "Animals" / "Food" / "Resources"
            // (the digit-stripping above left them intact). Surface that
            // directly so the user sees what kind of vendor it is.
            dto.label = string.IsNullOrEmpty(slot)
                ? "Handyman Vendor"
                : "Handyman " + slot + " Vendor";
        }
        else if (slot.Contains('_'))
        {
            var bits = slot.Split('_');
            dto.label = faction + " Vendor " + bits[0] + " (Inventory " + bits[1].TrimStart('0') + ")";
        }
        else if (!string.IsNullOrEmpty(slot))
        {
            dto.label = faction + " Vendor " + slot;
        }
        else
        {
            dto.label = faction + " Vendor";
        }
    }

    // Tier-1 PlayerBuys rosters that exist on disk but aren't referenced by any
    // in-game vendor spawn. Editing them produces no observable effect, so we
    // hide them from the Sellers tab. Match is exact on the filename (without
    // extension) so the _02 variants - which ARE the active rosters - stay
    // visible.
    static readonly HashSet<string> HiddenSellerLists = new(StringComparer.OrdinalIgnoreCase)
    {
        "DA_RecipeList_Trade_Brethren1_PlayerBuys",
        "DA_RecipeList_Trade_Bucaneers1_PlayerBuys",
        "DA_RecipeList_Trade_Civilian1_PlayerBuys",
        "DA_RecipeList_Trade_Smugglers1_PlayerBuys",
    };

    static bool IsHiddenSellerList(string fileNameNoExt)
        => !string.IsNullOrEmpty(fileNameNoExt) && HiddenSellerLists.Contains(fileNameNoExt);

    // Detects reputation-token recipes (BlackbeardSign / FactionSign). See
    // BuyersEndpoint for the rationale - we mirror the same defensive filter
    // here so a future vanilla change can't surface rep tokens in the editable
    // trade roster.
    static bool IsReputationRecipe(string recipeRef)
    {
        if (string.IsNullOrEmpty(recipeRef)) return false;
        return recipeRef.IndexOf("Reputation_BlackbeardSign", StringComparison.OrdinalIgnoreCase) >= 0
            || recipeRef.IndexOf("Reputation_FactionSign",    StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Resolves "/R5BusinessRules/Recipes/Economy/.../X.X" to the on-disk
    // JSON under Sources/Vanilla/R5/Plugins/R5BusinessRules/Content/Recipes/...
    // Returns a SellerEntryDto with resolved=false (and only the raw ref
    // fields populated) if the recipe file isn't there, so the frontend can
    // still surface a row instead of silently dropping the entry.
    //
    // Note: compared to BuyersEndpoint.ResolveRecipe, this method swaps the
    // Cost/Result -> DTO field mapping so itemId always refers to the "main"
    // item being traded (= the one the NPC delivers on the Sellers side).
    static SellerEntryDto ResolveRecipe(string recipesRoot, string recipeRef)
    {
        if (string.IsNullOrEmpty(recipeRef)) return null;

        var unresolved = new SellerEntryDto
        {
            recipePath = recipeRef,
            recipeId = AssetPathToId(recipeRef),
            resolved = false,
        };

        if (recipesRoot == null) return unresolved;
        if (!recipeRef.StartsWith(PluginPathPrefix, StringComparison.Ordinal))
            return unresolved; // out-of-tree ref - shouldn't happen for trade recipes

        // The recipes-root path we have is .../Content/Recipes/, but the asset
        // ref looks like /R5BusinessRules/Recipes/Economy/.../X.X. Trim the
        // plugin prefix + "Recipes/" segment to land at the sub-path.
        var afterPlugin = recipeRef.Substring(PluginPathPrefix.Length);
        const string recipesSegment = "Recipes/";
        if (!afterPlugin.StartsWith(recipesSegment, StringComparison.Ordinal))
            return unresolved;
        var subRef = afterPlugin.Substring(recipesSegment.Length);

        // Trim the canonical ".AssetName" suffix to get the sub-path of
        // the on-disk JSON, then convert '/' to OS separators.
        var dot = subRef.LastIndexOf('.');
        var slash = subRef.LastIndexOf('/');
        if (dot > slash) subRef = subRef.Substring(0, dot);

        var recipeJsonPath = Path.Combine(recipesRoot, subRef.Replace('/', Path.DirectorySeparatorChar) + ".json");
        if (!File.Exists(recipeJsonPath)) return unresolved;

        try
        {
            using var stream = File.OpenRead(recipeJsonPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return unresolved;
            if (!root.TryGetProperty("$type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String
                || typeEl.GetString() != "R5BLRecipeData")
                return unresolved;

            var entry = new SellerEntryDto
            {
                recipePath = recipeRef,
                recipeId = Path.GetFileNameWithoutExtension(recipeJsonPath),
                resolved = true,
            };

            // SWAP vs BuyersEndpoint:
            //   itemId   <- RecipeResult (the item the NPC delivers)
            //   payItemId <- RecipeCost  (what the player pays in)
            if (root.TryGetProperty("RecipeResult", out var resultEl)
                && resultEl.ValueKind == JsonValueKind.Array)
            {
                FillItemRef(resultEl, out entry.itemId, out entry.itemPath, out entry.itemCount);
            }
            if (root.TryGetProperty("RecipeCost", out var costEl)
                && costEl.ValueKind == JsonValueKind.Array)
            {
                FillItemRef(costEl, out entry.payItemId, out entry.payItemPath, out entry.payCount);
            }
            if (root.TryGetProperty("RecipeTag", out var tagEl)
                && tagEl.ValueKind == JsonValueKind.Object
                && tagEl.TryGetProperty("TagName", out var tagNameEl)
                && tagNameEl.ValueKind == JsonValueKind.String)
            {
                entry.recipeTag = tagNameEl.GetString();
            }
            if (root.TryGetProperty("CraftRequirement", out var reqEl)
                && reqEl.ValueKind == JsonValueKind.String)
            {
                var s = reqEl.GetString();
                if (!string.IsNullOrEmpty(s) && s != "None")
                    entry.craftRequirement = s;
            }

            return entry;
        }
        catch
        {
            return unresolved;
        }
    }

    // Pulls (Item path + Count) from the first entry of a RecipeCost /
    // RecipeResult array. Trade recipes ship exactly one entry per side, but
    // we defensively bail on out-of-shape input rather than throwing.
    static void FillItemRef(JsonElement arr, out string itemId, out string itemPath, out int count)
    {
        itemId = null;
        itemPath = null;
        count = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (el.TryGetProperty("Item", out var itemEl) && itemEl.ValueKind == JsonValueKind.String)
            {
                itemPath = itemEl.GetString();
                itemId = AssetPathToId(itemPath);
            }
            if (el.TryGetProperty("Count", out var cntEl) && cntEl.ValueKind == JsonValueKind.Number)
            {
                cntEl.TryGetInt32(out count);
            }
            return; // first entry only
        }
    }

    // UE asset path -> short id. Matches LootTablesEndpoint.AssetPathToId
    // semantics: takes the rightmost name segment regardless of whether the
    // ref uses the "/Path/Name" or "/Path/Name.Name" canonical form.
    static string AssetPathToId(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return null;
        var s = assetPath;
        var dot = s.LastIndexOf('.');
        var slash = s.LastIndexOf('/');
        var cut = Math.Max(dot, slash);
        return cut >= 0 && cut < s.Length - 1 ? s.Substring(cut + 1) : s;
    }
}
