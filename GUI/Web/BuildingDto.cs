using System.Collections.Generic;

namespace Windrose.Quartermaster.Web;

sealed class VanillaBuildingTemplateDto
{
    // id duplicates packagePath.
    public string id;
    public string displayName;
    public string category;
    public string packagePath;
}

sealed class VanillaBuildingTemplateInspectDto
{
    public string id;
    public string displayName;
    public string category;
    public string packagePath;
    public string pakRelativePath;

    // Build only proceeds when this equals "R5BuildingItem".
    public string assetClass;

    public string meshStem;
    public string meshPath;

    public string iconStem;
    public string iconPath;

    public string recipeStem;
    public string recipePath;
    public string recipeJsonPath;

    public string nameKey;
    public string descriptionKey;

    public string error;
    public List<string> warnings;
}

sealed class CookedFolderScanDto
{
    public string path;
    public bool exists;
    public string error;

    // Sorted alphabetically by name.
    public List<CookedFolderEntryDto> entries;
}

sealed class CookedFolderEntryDto
{
    public string name;
    public string stem;

    // Lowercase, with leading dot.
    public string extension;
    public long size;

    // One of: mesh, icon, texture, material, matinst, blueprint, data,
    // sidecar, other. material and matinst are skipped at build time.
    public string kind;
}

sealed class VanillaMaterialDto
{
    public string displayName;
    public string packagePath;
}

sealed class MaterialInstanceDto
{
    public string stem;

    // Same parentStem means an identical param schema (safe to pre-fill).
    public string parentStem;
    public string parentPath;

    public List<MIScalarParamDto>  scalars;
    public List<MIVectorParamDto>  vectors;
    public List<MITextureParamDto> textures;
}

sealed class MIScalarParamDto
{
    public string name;
    public float  value;
}

sealed class MIVectorParamDto
{
    public string name;
    public float  r, g, b, a;
}

sealed class MITextureParamDto
{
    public string name;
    public string textureStem;
    public string texturePath;
}

sealed class CookedFolderInspectionDto
{
    public string path;
    public string meshStem;
    public bool   ok;
    public string error;

    public List<MeshMaterialSlotDto> meshSlots;

    // Keyed by stem; look up via meshSlot.userMaterialStem.
    public Dictionary<string, MaterialInstanceDto> userMaterialInstances;

    public List<string> warnings;
}

sealed class MeshMaterialSlotDto
{
    public int    index;
    public string slotName;
    public string userMaterialStem;
    public string userMaterialPath;
}

sealed class VanillaResourceDto
{
    public string stem;
    public string packagePath;
    public string displayName;
    public string iconPath;

    // Set only when the icon PNG exists; empty otherwise.
    public string iconUrl;
    public string itemTag;
}

sealed class RecipeCostEntryDto
{
    public string itemPath;
    public int count;
}

sealed class BuildingRecipeInspectionDto
{
    public string templateId;
    public bool   ok;
    public string error;

    // Empty when the template has no recipe linkage (treated as free).
    public List<RecipeCostEntryDto> defaultRecipeCost;
    public string vanillaRecipeTag;
}
