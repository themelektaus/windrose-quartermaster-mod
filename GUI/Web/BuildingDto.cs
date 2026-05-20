using System.Collections.Generic;

namespace Windrose.Quartermaster.Web;

// Wire-format for /api/building-templates. Each entry describes one
// "Buildable" archetype the Building Creator tab can clone.
//
// Etappe G: templates only define gameplay-side properties (DA parent
// for snap/collision/category). Material slots come from the user's
// cooked mesh + per-slot Vanilla MI pick - see the inspect-cooked +
// vanilla-materials endpoints.
sealed class BuildingTemplateDto
{
    // Stable identifier referenced from CustomBuilding.TemplateId.
    public string id;

    // Human-friendly label shown in the template picker (e.g. "Painting").
    public string label;

    // Short description shown next to the picker (one line).
    public string description;

    // Coarse classification ("Decoration" today; "Furniture"/"Floor"/...
    // future). Used by the GUI for grouping.
    public string kind;

    // The category-tag the inject-side DLL filter uses to recognise the
    // build-menu tab this template's clones belong to.
    public string categoryTag;
}

// Wire-format for /api/building-templates/vanilla (Etappe I.1).
// One entry per Vanilla DA_BI_*.uasset that lives under
// /Game/Gameplay/Building/ and is NOT in a BuildingBrushes/Houses
// folder (those carry R5BuildingBrush class which the inject-side
// can't currently clone as Item).
//
// The GUI uses the indexed list to render a searchable + category-
// filterable picker. The picked entry's `id` (= UE /Game/... path)
// is what the profile stores on the building. At build-time the
// patcher resolves the id back to the Vanilla DA via the catalog and
// reads its Mesh/Icon/Recipe refs + FText keys (Etappe I.2 inspector).
sealed class VanillaBuildingTemplateDto
{
    // Stable identifier; = packagePath (kept duplicated so the GUI
    // doesn't have to guess which field to put on the profile).
    public string id;

    // File stem ("DA_BI_Bucket_01") - the picker's user-facing label.
    public string displayName;

    // Parent-folder name ("BuildingDecoration" / "BuildingPoi" / ...).
    // Used by the GUI's category facet filter.
    public string category;

    // UE virtual path, e.g.
    // "/Game/Gameplay/Building/BuildingDecoration/DA_BI_Bucket_01".
    public string packagePath;
}

// Wire-format for /api/building-templates/vanilla/inspect?id=<path>.
// Returns the metadata the BuildingPatcher needs to clone the picked
// Vanilla DA (Mesh + Icon + Recipe asset refs, Name+Description FText
// keys, class name). The frontend uses this to:
//   - Confirm the user-picked DA is actually a R5BuildingItem (sanity
//     gate before starting a long build) - the class field is surfaced
//     in the picker preview pane
//   - Pre-fill the recipe editor with the picked DA's default cost
//     (instead of the previous static templateId="Painting"/"Bucket"
//     lookup)
//   - Show the Mesh / Icon stems so the user knows which files their
//     cook folder needs to overwrite
sealed class VanillaBuildingTemplateInspectDto
{
    public string id;
    public string displayName;
    public string category;
    public string packagePath;
    public string pakRelativePath;

    // "R5BuildingItem" expected. Anything else surfaces as a frontend
    // warning - the BuildPipeline will refuse to clone the DA.
    public string assetClass;

    public string meshStem;
    public string meshPath;

    public string iconStem;
    public string iconPath;

    public string recipeStem;
    public string recipePath;       // /R5BusinessRules/...
    public string recipeJsonPath;   // R5/Plugins/...json (relative)

    public string nameKey;          // FText key for Name
    public string descriptionKey;   // FText key for Description

    public string error;
    public List<string> warnings;
}

// Wire-format for /api/buildings/scan-cooked?path=<absolute>. Listed
// per file in the user's CookedFolderPath, classified by stem prefix
// + extension so the GUI can preview what's there before the user
// commits the path to the profile (and warn about likely-missing
// items: no mesh, no icon, no Albedo image for an image-bearing slot,
// or user-cooked materials that will get skipped at build time).
sealed class CookedFolderScanDto
{
    // Echo of the path the user asked us to scan (absolute, normalized).
    public string path;

    // True if the path exists and is a directory we can read.
    public bool exists;

    // Optional error string when exists=false (or read failed).
    public string error;

    // One entry per file found. Sorted alphabetically by name.
    public List<CookedFolderEntryDto> entries;
}

sealed class CookedFolderEntryDto
{
    // Filename (basename, with extension).
    public string name;

    // Filename without extension.
    public string stem;

    // ".uasset" / ".uexp" / ".ubulk" / ".upage" / ".png" / etc.
    // Lowercase, with leading dot.
    public string extension;

    // Byte size of the file (uncompressed on-disk size).
    public long size;

    // Best-effort classification based on the stem and extension:
    //   "mesh"      - SM_<...>.uasset
    //   "icon"      - T_<...>_Icon.uasset
    //   "texture"   - T_<...>.uasset (non-icon)
    //   "material"  - M_<...>.uasset (will be SKIPPED at build time)
    //   "matinst"   - MI_<...>.uasset (will be SKIPPED at build time)
    //   "blueprint" - BP_<...>.uasset
    //   "data"      - DA_<...>.uasset
    //   "sidecar"   - .uexp / .ubulk / .upage (companion bulk-data)
    //   "other"     - everything else
    public string kind;
}

// -----------------------------------------------------------------------
// Etappe G: Vanilla MI catalog + inspection.
//
// /api/vanilla-materials?search=&limit= -> list of VanillaMaterialDto
// /api/vanilla-materials/inspect?path=  -> single MaterialInstanceDto
//
// The catalog is built once at backend startup (lazy, on first request)
// over all vanilla paks. Inspect uses retoc to-legacy + UAssetAPI to
// surface the MI's parameter blocks the GUI renders dynamically.
// -----------------------------------------------------------------------

sealed class VanillaMaterialDto
{
    // File stem, e.g. "MI_Paintings_01". Stable identifier for the
    // dropdown listing and the picker label.
    public string displayName;

    // UE virtual path, e.g. "/Game/Environment/.../MI_Paintings_01".
    // Stored on the profile as CustomBuildingSlot.VanillaMaterialParentPath.
    public string packagePath;
}

sealed class MaterialInstanceDto
{
    // File stem.
    public string stem;

    // Parent master material the MI inherits from (e.g. "M_Object").
    // Used by the GUI to detect when a user-cooked MI in the cooked
    // folder is compatible with a picked vanilla MI (same parent ->
    // identical param schema -> safe to pre-fill).
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

// -----------------------------------------------------------------------
// /api/buildings/inspect-cooked?path=&meshStem= -> CookedFolderInspectionDto
//
// Reads the mesh's material slot list + all user-cooked MIs in the
// folder. The GUI uses this to render its dynamic slot UI:
//   - per slot, show the slot-name + index from the mesh
//   - if the slot's userMaterialStem matches a user-MI in the dict,
//     surface it as the pre-fill source
//   - when the user picks a vanilla MI parent and the user-MI has the
//     same parent-master, auto-fill the slot's param controls with the
//     user-MI's values
// -----------------------------------------------------------------------

sealed class CookedFolderInspectionDto
{
    public string path;
    public string meshStem;
    public bool   ok;
    public string error;

    public List<MeshMaterialSlotDto> meshSlots;

    // Inspected user-cooked MIs keyed by stem. Frontend reads
    // userMaterialInstances[<meshSlot.userMaterialStem>] to find the
    // pre-fill source for a given slot.
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

// -----------------------------------------------------------------------
// Etappe H2: Vanilla resource catalog + per-building build-cost editor.
//
// /api/vanilla-resources?search=&limit=          -> VanillaResourceDto[]
// /api/buildings/inspect-recipe?templateId=      -> BuildingRecipeInspectionDto
//
// The catalog scans Sources/Vanilla/R5/Plugins/R5BusinessRules/Content/
// InventoryItems/DefaultItems/Resource/DA_DID_Resource_*.json - cheap,
// no CUE4Parse, no extraction. Inspect-recipe parses the template's
// VanillaRecipeJsonPath and surfaces the default RecipeCost list so the
// GUI can pre-fill the cost editor when the user picks a template.
// -----------------------------------------------------------------------

sealed class VanillaResourceDto
{
    // File stem, e.g. "DA_DID_Resource_Hardwood_T02". Unique within the
    // catalog and what the recipe JSON references in RecipeCost[i].Item
    // (after the .Stem suffix is added).
    public string stem;

    // Full UE virtual path the recipe JSON uses verbatim, e.g.
    //   "/R5BusinessRules/InventoryItems/DefaultItems/Resource/
    //    DA_DID_Resource_Hardwood_T02.DA_DID_Resource_Hardwood_T02"
    // Stored on the profile as CustomBuilding.RecipeCost[i].ItemPath.
    public string packagePath;

    // Prettified stem ("Hardwood T02") shown in the resource dropdown.
    public string displayName;

    // Icon UE path (may be empty for resources without thumbnail).
    public string iconPath;

    // Web URL where the local icon PNG is served, e.g.
    // "/Icons/DA_DID_Resource_Hardwood_T02.png". Set only when the file
    // actually exists in the Icons/ folder (Setup extracts these via
    // IconExtractor). Empty otherwise. Lets the recipe picker render
    // icon + name the same way the loot-table picker does for items.
    public string iconUrl;

    // Gameplay-tag identifier, e.g. "ItemData.Resource.Hardwood.T02".
    // Surfaced for power-user filtering but not required by the editor.
    public string itemTag;
}

sealed class RecipeCostEntryDto
{
    // Full packagePath form (matches VanillaResourceDto.packagePath).
    public string itemPath;

    // Quantity required per craft (>= 1, max 999 enforced UI-side).
    public int count;
}

// Returned by /api/buildings/inspect-recipe?templateId=<id>.
// Surfaces the default RecipeCost list the template's Vanilla recipe
// JSON carries, so the GUI can show those values as pre-fill in the
// per-building cost editor. The frontend renders the per-row Resource
// picker against VanillaResourceDto (already enriched with displayName
// + iconPath) once the user starts editing.
sealed class BuildingRecipeInspectionDto
{
    public string templateId;
    public bool   ok;
    public string error;

    // The vanilla RecipeCost entries (item-path + count). Empty if the
    // template has no recipe linkage (would-be future templates with
    // no build cost) - the editor then defaults to "free".
    public List<RecipeCostEntryDto> defaultRecipeCost;

    // The resolved RecipeTag string the vanilla recipe uses, surfaced
    // for diagnostics (read-only in the UI).
    public string vanillaRecipeTag;
}
