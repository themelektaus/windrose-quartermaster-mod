using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Windrose.Quartermaster.Web.Endpoints;

// GET /api/item-templates -> the catalog of vanilla items the Item Creator
// tab can clone. Phase 1 ships one hardcoded entry (Piastre, a Default
// Resource item). Future phases can add more entries here without any
// schema migration; the frontend only reads the fields it knows about.
//
// Each template surfaces the baseline values its clones inherit by default
// (MaxCountInSlot, Rarity, KeepOnDeath, ItemTexture path) so the UI can
// preview "this is what your custom item looks like before you edit it"
// without re-loading the source JSON.
public static class ItemTemplatesEndpoint
{
    // Hardcoded catalog. Each entry must reference a real vanilla item -
    // the ItemCreatorPatcher locates the source JSON by walking
    // Sources/Vanilla/InventoryItems/** for a matching basename, so the
    // folder doesn't matter, only the basename does. Adding more here is
    // O(1): list one more entry, the patcher already supports any
    // R5BLInventoryItem template.
    static readonly TemplateDto[] Catalog = new[]
    {
        new TemplateDto
        {
            id = "DA_DID_Misc_CoinPiastre_T02",
            label = "Piastre Coin",
            // Default Resource item: stacks to 9999, Rare rarity, kept on
            // death, no abilities. The simplest possible inventory item -
            // good starting point for the "new item" flow.
            kind = "Resource",
            defaultMaxCountInSlot = 9999,
            defaultRarity = "Rare",
            defaultKeepInInventoryOnDeath = true,
            defaultItemTexture = "/Game/UI/Icons/Items/New/T_ItemIcon_Loot_T02_CoinPiastre_01.T_ItemIcon_Loot_T02_CoinPiastre_01",
        },
    };

    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/item-templates", () =>
        {
            // Return the static catalog as-is. The frontend uses the
            // `id` field to seed CustomItem.TemplateId; the rest powers
            // the "create new" preview card.
            return Results.Json(Catalog);
        });
    }

    sealed class TemplateDto
    {
        public string id;        // basename of the vanilla item, e.g. "DA_DID_Misc_CoinPiastre_T02"
        public string label;     // human-friendly: "Piastre Coin"
        public string kind;      // free-form classification ("Resource", "Consumable", ...)
                                 // - frontend uses it as a coarse filter / display badge.
        public int defaultMaxCountInSlot;
        public string defaultRarity;
        public bool defaultKeepInInventoryOnDeath;
        public string defaultItemTexture;
    }
}
