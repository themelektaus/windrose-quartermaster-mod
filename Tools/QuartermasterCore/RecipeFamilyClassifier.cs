using System;
using System.Text.Json.Nodes;

namespace Windrose.Quartermaster.Core
{
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
        public static RecipeFamily Classify(JsonObject root, string filename)
        {
            if (root == null) return RecipeFamily.Unclassified;

            string tag = null;
            if (root["RecipeTag"] is JsonObject tagObj
                && tagObj["TagName"] is JsonValue tagVal)
            {
                tag = tagVal.GetValue<string>();
            }

            var fn = filename ?? string.Empty;
            var fnLower = fn.ToLowerInvariant();

            if (fnLower.Contains("furnace"))
                return RecipeFamily.Smelting;
            if (fnLower.Contains("kiln"))
                return RecipeFamily.Kiln;
            if (fnLower.Contains("tannery") || fnLower.Contains("tannin"))
                return RecipeFamily.Tanning;
            if (fnLower.Contains("tanleather"))
                return RecipeFamily.Tanning;
            if (fnLower.Contains("mill") || fnLower.Contains("press"))
                return RecipeFamily.Milling;

            // Order-sensitive: earlier prefix checks win on ambiguous tags.
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

                if (tag.StartsWith("Metal.", StringComparison.OrdinalIgnoreCase)
                    || tag.IndexOf(".Metal.", StringComparison.OrdinalIgnoreCase) >= 0)
                    return RecipeFamily.Smelting;
            }

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
