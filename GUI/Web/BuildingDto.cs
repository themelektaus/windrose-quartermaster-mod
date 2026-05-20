using System.Collections.Generic;

namespace Windrose.Quartermaster.Web;

// Wire-format for /api/building-templates. Each entry describes one
// "Buildable" archetype the Building Creator tab can clone. Phase 1
// ships exactly one entry ("Painting" - a wall painting cloned from
// the Vanilla HighLands painting). Additional templates can be added
// to the catalog in BuildingTemplatesEndpoint without any schema
// migration; the frontend only reads the fields it knows about.
//
// Each template surfaces its slot list (with per-slot flags like
// userAlbedoRequired) so the GUI can render the right "Image" input
// next to image-bearing slots without re-deriving semantics.
sealed class BuildingTemplateDto
{
    // Stable identifier referenced from CustomBuilding.TemplateId. Drives
    // the BuildingTemplate lookup in the patcher pipeline.
    public string id;

    // Human-friendly label shown in the template picker, e.g. "Painting".
    public string label;

    // Short description shown next to the picker (one line).
    public string description;

    // Free-form classification (currently only "Decoration"). Used as a
    // coarse filter / display badge.
    public string kind;

    // The category-tag the inject-side DLL filter uses to recognise the
    // build-menu tab this template's clones belong to (e.g.
    // "BuildingDecoration"). Surfaced to the GUI so the user can see at
    // a glance which tab their building will appear under in-game.
    public string categoryTag;

    // Material slots the template carries, in the same order as on the
    // Vanilla mesh. The frontend renders one "Image" file picker per
    // slot whose userAlbedoRequired is true (currently: the Canvas slot
    // on Painting).
    public List<BuildingTemplateSlotDto> slots;
}

sealed class BuildingTemplateSlotDto
{
    // Slot name (e.g. "Frame", "Canvas"). Used as the key into
    // CustomBuilding.Slots when the user wires up a custom albedo /
    // normal / mtrm.
    public string slotName;

    // True if the slot demands a user-supplied albedo (image-bearing
    // slot). The patcher refuses to build until the corresponding
    // CustomBuildingSlot.CustomAlbedoStem is set. Drives the "required"
    // marker on the GUI's per-slot input.
    public bool userAlbedoRequired;
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
