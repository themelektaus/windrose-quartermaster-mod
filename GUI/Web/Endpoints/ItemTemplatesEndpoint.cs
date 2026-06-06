using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class ItemTemplatesEndpoint
{
    // Each entry's id must be the basename of a real vanilla item.
    static readonly TemplateDto[] Catalog = new[]
    {
        new TemplateDto
        {
            id = "DA_DID_Misc_CoinPiastre_T02",
            label = "Piastre Coin",
            kind = "Resource",
            defaultMaxCountInSlot = 9999,
            defaultRarity = "Rare",
            defaultKeepInInventoryOnDeath = true,
            defaultItemTexture = "/Game/UI/Icons/Items/New/T_ItemIcon_Loot_T02_CoinPiastre_01.T_ItemIcon_Loot_T02_CoinPiastre_01",
        },
        new TemplateDto
        {
            id = "DA_CID_Food_Rum_Bottle_T03",
            label = "Rum Bottle",
            kind = "Consumable",
            defaultMaxCountInSlot = 20,
            defaultRarity = "Rare",
            defaultKeepInInventoryOnDeath = false,
            defaultItemTexture = "/Game/UI/Icons/Items/New/T_ItemIcon_TradeCraft_Alcohol_Rum.T_ItemIcon_TradeCraft_Alcohol_Rum",
        },
        // Weather Whistle: cloned from the L2 boar whistle (no boar spawn). With a
        // weather picked, the build clones its ConsumableData per-weather and the
        // dxgi DLL sets that weather on use; "(vanilla)" ships an inert whistle.
        new TemplateDto
        {
            id = "DA_CID_Misc_SpawnerBoar_L2_T02",
            label = "Weather Whistle",
            kind = "Consumable",
            defaultMaxCountInSlot = 1,
            defaultRarity = "Legendary",
            defaultKeepInInventoryOnDeath = true,
            defaultItemTexture = "/Game/UI/Icons/Items/New/T_ItemIcon_Consumables_T02_SpawnerBoar.T_ItemIcon_Consumables_T02_SpawnerBoar",
            supportsWeather = true,
        },
    };

    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/item-templates", () =>
        {
            return Results.Json(Catalog);
        });
    }

    sealed class TemplateDto
    {
        public string id;
        public string label;
        public string kind;
        public int defaultMaxCountInSlot;
        public string defaultRarity;
        public bool defaultKeepInInventoryOnDeath;
        public string defaultItemTexture;
        // When true the Item Creator shows a weather-effect dropdown for this
        // template (Weather Whistle). Maps to CustomItem.WeatherId on build.
        public bool supportsWeather;
    }
}
