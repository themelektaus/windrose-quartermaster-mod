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
        // Rum Bottle doubles as the Weather item. Leaving the weather dropdown at
        // "(vanilla)" ships a normal rum (drunk on use, vanilla buff). Picking a
        // weather makes the build clone its ConsumableData per-weather (SpendCount=0
        // so the bottle is NOT consumed, vanilla buff stripped) and the dxgi DLL sets
        // that weather on use. The rum's Food consume-ability spawns nothing and has
        // no cooldown GE - so no boar and no runtime cooldown-strip needed.
        new TemplateDto
        {
            id = "DA_CID_Food_Rum_Bottle_T03",
            label = "Rum Bottle",
            kind = "Consumable",
            defaultMaxCountInSlot = 20,
            defaultRarity = "Rare",
            defaultKeepInInventoryOnDeath = false,
            defaultItemTexture = "/Game/UI/Icons/Items/New/T_ItemIcon_TradeCraft_Alcohol_Rum.T_ItemIcon_TradeCraft_Alcohol_Rum",
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
