using System;
using System.Text.Json.Nodes;

namespace Windrose.Quartermaster.Core
{
    // Assigns a recipe (R5BLRecipeData JSON) to one of the production
    // families surfaced in the Stations tab. Classification is best-effort:
    //   1. RecipeTag.TagName prefix is the primary signal (the canonical
    //      vanilla taxonomy). e.g. "Bits.Collection.LogDoor" -> BuildingBits.
    //   2. Filename fallback for the metal/charcoal/tannin families,
    //      because their tags don't cleanly identify the station - a
    //      tannin recipe lives under "Resource.Tannin.*" but a charcoal
    //      recipe lives under "Resource.Coal.*" which collides with raw
    //      coal-ore drop tables. Filename hints (DA_RD_DID_Resource_
    //      Coal_T01_*Kiln*) keep the classification tight.
    //
    // Vanilla recipe tags grouped per family (counts from
    // Sources/Vanilla/.../Recipes/ on 5.6):
    //   Bits.*                  ~194   (workbench / anvil)
    //   TradeOutpost.*          ~148   (NPC order wait)
    //   Deco.*                  ~106   (workbench / deco bench)
    //   Resource.* (mixed)      ~70    (split into Smelting/Kiln/Tanning/Milling/Other)
    //   Armor.* + ItemUpgrade.* ~25
    //   Craft.WeaponTable.*     ~4
    //   Metal.*                 ~10    (furnace - splits into Smelting)
    //   (rest)                  ~50    (catch-all "Other")
    public enum RecipeFamily
    {
        Unclassified = 0,
        Smelting,
        Kiln,
        Tanning,
        Milling,
        BuildingBits,
        Decoration,
        ArmorWeapon,
        TradeOutpost,
        Other,
    }

    public static class RecipeFamilyClassifier
    {
        // Classifies a recipe JSON. `filename` is the basename without
        // extension - the classifier uses it as a secondary signal for
        // the metal/charcoal/tannin/mill families whose RecipeTag alone
        // doesn't disambiguate the station.
        //
        // Returns Unclassified only if the JSON itself is missing the
        // RecipeTag node entirely (shouldn't happen on vanilla data,
        // but the caller should treat it as "skip").
        public static RecipeFamily Classify(JsonObject root, string filename)
        {
            if (root == null) return RecipeFamily.Unclassified;

            // Primary signal: RecipeTag.TagName.
            string tag = null;
            if (root["RecipeTag"] is JsonObject tagObj
                && tagObj["TagName"] is JsonValue tagVal)
            {
                tag = tagVal.GetValue<string>();
            }

            // Filename is reliable even when the tag is None (sometimes
            // vanilla recipes have no recipe tag set, esp. internal helpers).
            var fn = filename ?? string.Empty;
            var fnLower = fn.ToLowerInvariant();

            // Filename-first checks for the station-disambiguation
            // families. These look for the station name embedded in the
            // filename (the canonical naming convention is
            // DA_RD_DID_<Type>_<Stem>_<Station><Tier>.json).
            if (fnLower.Contains("furnace"))
                return RecipeFamily.Smelting;
            if (fnLower.Contains("kiln"))
                return RecipeFamily.Kiln;
            if (fnLower.Contains("tannery") || fnLower.Contains("tannin"))
                return RecipeFamily.Tanning;
            // Tan-leather is made in the tannery. Filename "TanLeather"
            // is unambiguous since vanilla never reuses it elsewhere.
            if (fnLower.Contains("tanleather"))
                return RecipeFamily.Tanning;
            if (fnLower.Contains("mill") || fnLower.Contains("press"))
                return RecipeFamily.Milling;

            // Tag-prefix checks. Earlier wins on ambiguous prefixes,
            // so order matters: TradeOutpost before Resource etc.
            if (!string.IsNullOrEmpty(tag) && !string.Equals(tag, "None", StringComparison.OrdinalIgnoreCase))
            {
                if (tag.StartsWith("TradeOutpost.", StringComparison.OrdinalIgnoreCase)
                    || tag.StartsWith("RecipeData.TradeOutpost.", StringComparison.OrdinalIgnoreCase))
                    return RecipeFamily.TradeOutpost;

                if (tag.StartsWith("Bits.", StringComparison.OrdinalIgnoreCase)
                    || tag.IndexOf(".Bits.", StringComparison.OrdinalIgnoreCase) >= 0)
                    return RecipeFamily.BuildingBits;

                if (tag.StartsWith("Deco.", StringComparison.OrdinalIgnoreCase)
                    || tag.IndexOf(".Deco.", StringComparison.OrdinalIgnoreCase) >= 0)
                    return RecipeFamily.Decoration;

                if (tag.StartsWith("Armor.", StringComparison.OrdinalIgnoreCase)
                    || tag.StartsWith("ItemUpgradeArmor.", StringComparison.OrdinalIgnoreCase)
                    || tag.StartsWith("ItemUpgradeWeapon.", StringComparison.OrdinalIgnoreCase)
                    || tag.IndexOf(".Armor.", StringComparison.OrdinalIgnoreCase) >= 0
                    || tag.IndexOf(".ItemUpgrade", StringComparison.OrdinalIgnoreCase) >= 0
                    || tag.IndexOf("Craft.WeaponTable", StringComparison.OrdinalIgnoreCase) >= 0)
                    return RecipeFamily.ArmorWeapon;

                // Metal.* ingots are smelted in furnaces.
                if (tag.StartsWith("Metal.", StringComparison.OrdinalIgnoreCase)
                    || tag.IndexOf(".Metal.", StringComparison.OrdinalIgnoreCase) >= 0)
                    return RecipeFamily.Smelting;
            }

            // Filename-only catch-alls for things the tag didn't classify.
            // Ingot/charcoal/oil families have very distinct filenames.
            if (fnLower.Contains("ingot") || fnLower.Contains("_ash_"))
                return RecipeFamily.Smelting;
            if (fnLower.Contains("_coal_") || fnLower.Contains("coconutoil"))
                return RecipeFamily.Kiln;
            if (fnLower.Contains("flaxoil") || fnLower.Contains("grapejuice")
                || fnLower.Contains("pineapplejuice") || fnLower.Contains("cornmeal")
                || fnLower.Contains("vinegar"))
                return RecipeFamily.Milling;

            return RecipeFamily.Other;
        }
    }
}
