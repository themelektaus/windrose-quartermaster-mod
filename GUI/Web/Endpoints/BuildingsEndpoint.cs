using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Web.Endpoints;

// Helper endpoints for the Building Creator tab. The buildings
// themselves are stored inside Profile.CustomBuildings (no separate
// store), so CRUD goes through the regular GET/PUT /api/profiles/{id}
// path - just like CustomItems. What lives here are the small
// supporting calls the GUI makes to drive the cooked-folder picker
// without exposing a free-form file-system read endpoint to anyone
// who can reach the local Kestrel:
//
//   GET /api/buildings/scan-cooked?path=<raw>&profileId=<id>
//       Lists files in the user's cooked-output folder so the GUI can
//       preview what's there before the user commits the path to the
//       profile. The optional profileId is used to resolve profile-
//       relative folder names (e.g. path="MyPainting" with profileId
//       set resolves to <Profiles>/<profileId>/MyPainting when that
//       folder exists; otherwise path is used as-is). Classifies each
//       file by stem+extension (mesh / icon / texture / material /
//       sidecar / other) so the GUI can warn about likely-missing items
//       (no mesh found, no icon found, ...) and flag user-cooked
//       materials that will get skipped at build time (because they
//       crash shipping - per the spike bisect).
//
// Phase 1 only ships the scan endpoint. Future endpoints could:
//   - validate-cook (sanity check the prefix + slot expectations match
//     the picked template before save)
//   - browse-folder (let the GUI traverse subdirs of a root path)
public static class BuildingsEndpoint
{
    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/buildings/scan-cooked", (string path, string profileId) =>
        {
            var dto = ScanCookedFolder(path, profileId, repoRoot);
            return Results.Json(dto);
        });

        // Deep inspect: read the mesh's material slot list + each
        // user-cooked MI in the folder. The GUI uses this to drive its
        // dynamic per-slot UI (Etappe G). The optional profileId is
        // used the same way as in scan-cooked to resolve profile-
        // relative folder names.
        app.MapGet("/api/buildings/inspect-cooked", (string path, string meshStem, string profileId) =>
        {
            var dto = InspectCookedFolder(path, meshStem, profileId, repoRoot);
            return Results.Json(dto);
        });

        // Etappe H2: surface the template's vanilla RecipeCost list so
        // the GUI can pre-fill the per-building recipe editor when the
        // user picks a template (or first opens a building card that
        // has no user override yet).
        app.MapGet("/api/buildings/inspect-recipe", (string templateId) =>
        {
            var dto = InspectRecipe(templateId, repoRoot);
            return Results.Json(dto);
        });

        // Default-texture stems the Building Creator ships with the
        // app (canonical list: DefaultTextureProvider.Stems). The frontend
        // pulls this list once at tab-open and surfaces it as an
        // "always available" optgroup in every per-slot texture
        // dropdown so the user can reference these stems without
        // cooking copies into their own UE project. The build
        // pipeline copies the matching .uasset/.uexp/.ubulk
        // triplets from Tools/Templates/DefaultTextures/ into the
        // staging tree once per build (see DefaultTextureProvider).
        app.MapGet("/api/buildings/default-textures", () =>
        {
            return Results.Json(new
            {
                stems = DefaultTextureProvider.GetStems(),
            });
        });

        // Etappe J: Flame-FX presets the user can attach to any building.
        // Returns the canonical list (id + displayName + description)
        // the GUI surfaces as a dropdown in each building's editor. When
        // the user picks a preset, the build pipeline clones the
        // corresponding vanilla "fire building" BP once per used preset
        // and patches each opted-in building's DA to use the cloned BP
        // as ItemClass - so the building spawns with Niagara flame FX,
        // a flickering point light, and ambient loop SFX. Default state
        // for any building is "no preset selected" (= no flame).
        app.MapGet("/api/buildings/flame-presets", () =>
        {
            return Results.Json(new
            {
                presets = FlamePresetCatalog.GetDtos(),
            });
        });
    }

    static BuildingRecipeInspectionDto InspectRecipe(string templateId, string repoRoot)
    {
        var dto = new BuildingRecipeInspectionDto
        {
            templateId = templateId ?? "",
            ok = false,
            defaultRecipeCost = new List<RecipeCostEntryDto>(),
        };
        if (string.IsNullOrWhiteSpace(templateId))
        {
            dto.error = "templateId query parameter is required";
            return dto;
        }

        var template = ResolveTemplate(templateId);
        if (template == null)
        {
            dto.error = "Unknown templateId: " + templateId;
            return dto;
        }
        if (string.IsNullOrEmpty(template.VanillaRecipeJsonPath))
        {
            // Template has no recipe linkage - editor defaults to "free".
            dto.ok = true;
            return dto;
        }

        try
        {
            var paths = WindrosePaths.FromModRoot(repoRoot);
            var abs = Path.Combine(paths.Vanilla, template.VanillaRecipeJsonPath);
            if (!File.Exists(abs))
            {
                dto.error = "Vanilla recipe JSON not extracted yet (run Setup): " + template.VanillaRecipeJsonPath;
                return dto;
            }
            var rows = RecipePatcher.ReadDefaultRecipeCost(abs);
            foreach (var (itemPath, count) in rows)
            {
                dto.defaultRecipeCost.Add(new RecipeCostEntryDto
                {
                    itemPath = itemPath,
                    count = count,
                });
            }
            dto.vanillaRecipeTag = RecipePatcher.ReadVanillaRecipeTag(abs);
            dto.ok = true;
            return dto;
        }
        catch (Exception ex)
        {
            dto.error = "Recipe inspection failed: " + ex.Message;
            return dto;
        }
    }

    // Mirrors BuildPipeline.ResolveBuildingTemplate but lives here so
    // the inspect-recipe endpoint stays decoupled from the build path.
    // Accepts a Vanilla DA virtual path ("/Game/Gameplay/Building/.../DA_BI_*")
    // and resolves it via the shared catalog + inspector (Etappe I.2).
    static BuildingTemplate ResolveTemplate(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var trimmed = id.Trim();

        try
        {
            var catalog = BuildingTemplatesEndpoint.GetSharedCatalog();
            var inspector = new VanillaBuildingTemplateInspector
            {
                Catalog = catalog,
                Log     = msg => Console.WriteLine("[building-inspect] " + msg),
            };
            var ins = inspector.Inspect(trimmed);
            if (!string.IsNullOrEmpty(ins.Error)) return null;
            return BuildingTemplate.FromInspection(ins);
        }
        catch
        {
            return null;
        }
    }

    static CookedFolderScanDto ScanCookedFolder(string raw, string profileId, string repoRoot)
    {
        var dto = new CookedFolderScanDto
        {
            path    = raw ?? "",
            exists  = false,
            entries = new List<CookedFolderEntryDto>(),
        };
        if (string.IsNullOrWhiteSpace(raw))
        {
            dto.error = "path query parameter is required";
            return dto;
        }

        // Resolve profile-relative folder names (e.g. raw="MyPainting"
        // + profileId set -> <Profiles>/<profileId>/MyPainting when
        // that folder exists). Absolute paths and unknown profile-
        // relative names fall through to the raw value, which then
        // hits the existing Path.GetFullPath + Directory.Exists check
        // so the user sees the same "Folder does not exist" message
        // as before.
        var resolved = ResolveCookedPath(raw, profileId, repoRoot);

        string normalized;
        try
        {
            normalized = Path.GetFullPath(resolved);
        }
        catch (Exception ex)
        {
            dto.error = "Invalid path: " + ex.Message;
            return dto;
        }
        dto.path = normalized;

        if (!Directory.Exists(normalized))
        {
            dto.error = "Folder does not exist (or is not a directory): " + normalized;
            return dto;
        }

        dto.exists = true;

        try
        {
            foreach (var file in Directory.EnumerateFiles(normalized, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext  = (Path.GetExtension(name) ?? "").ToLowerInvariant();
                long size = 0;
                try { size = new FileInfo(file).Length; } catch { /* best-effort */ }

                dto.entries.Add(new CookedFolderEntryDto
                {
                    name      = name,
                    stem      = stem,
                    extension = ext,
                    size      = size,
                    kind      = Classify(stem, ext),
                });
            }
        }
        catch (Exception ex)
        {
            dto.error = "Read error: " + ex.Message;
            return dto;
        }

        // Stable order: by name, case-insensitive. The GUI relies on
        // this for deterministic card rendering when re-scanning the
        // same folder.
        dto.entries = dto.entries
            .OrderBy(e => e.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return dto;
    }

    // Best-effort classification by stem prefix + extension. Mirrors the
    // BuildingPatcher's expectations so the GUI surfaces the same
    // semantic categories the build pipeline acts on.
    static string Classify(string stem, string ext)
    {
        if (string.IsNullOrEmpty(stem)) return "other";

        // Bulk-data sidecars travel next to their .uasset - we surface
        // them as a distinct kind so the GUI can either hide them or
        // count them next to the parent asset.
        switch (ext)
        {
            case ".uexp":
            case ".ubulk":
            case ".upage":
                return "sidecar";
        }

        if (ext != ".uasset")
        {
            // PNGs / JSONs / random extras in the cook folder. Surface
            // as "other" so the GUI can show them but the build
            // pipeline still ignores them (only .uasset+sidecars get
            // staged).
            return "other";
        }

        // .uasset classification by stem prefix. Keep these aligned
        // with BuildingPatcher's SkipUserCookedMaterialStems logic:
        // "material" and "matinst" entries get filtered out at build
        // time because user-cooked Materials/MIs crash the shipping
        // game (per the spike bisect).
        if (StemStartsWith(stem, "SM_"))   return "mesh";
        if (StemStartsWith(stem, "MI_"))   return "matinst";
        if (StemStartsWith(stem, "M_"))    return "material";
        if (StemStartsWith(stem, "BP_"))   return "blueprint";
        if (StemStartsWith(stem, "DA_"))   return "data";
        if (StemStartsWith(stem, "T_"))
        {
            // Icons by convention end with "_Icon" (the BuildingPatcher
            // and the Painting template both rely on this naming). We
            // surface them separately so the GUI can highlight the icon
            // upload step without having to ask the user to disambiguate
            // texture vs icon roles.
            if (stem.EndsWith("_Icon", StringComparison.OrdinalIgnoreCase))
                return "icon";
            return "texture";
        }
        return "other";
    }

    static bool StemStartsWith(string stem, string prefix)
    {
        return stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // Wraps WindrosePaths.ResolveProfileRelativeFolder for the two
    // GUI helper endpoints (scan-cooked + inspect-cooked). Tolerates
    // missing profileId and any path init failure - falls back to the
    // raw string so the caller's existing "Folder does not exist"
    // error surfaces unchanged. The user-typed CookedFolderPath stays
    // in the profile JSON verbatim; this resolves on the fly only.
    static string ResolveCookedPath(string raw, string profileId, string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        if (string.IsNullOrWhiteSpace(profileId)) return raw;
        try
        {
            var paths = WindrosePaths.FromModRoot(repoRoot);
            return paths.ResolveProfileRelativeFolder(profileId, raw);
        }
        catch
        {
            return raw;
        }
    }

    // -----------------------------------------------------------------
    // Etappe G: deep inspect of the cooked folder. Reads the mesh's
    // material slot list (via UAssetAPI through CookedFolderInspector)
    // + every user-cooked MI in the folder. The GUI feeds this into
    // its dynamic slot UI:
    //   - per mesh slot we know the slot name + index + user-MI ref
    //   - per user-MI we know its parent-master + param defaults
    //   - frontend matches mesh-slot.userMaterialStem against the
    //     user-MI dict to determine the pre-fill source
    // -----------------------------------------------------------------
    static CookedFolderInspectionDto InspectCookedFolder(
        string rawPath, string meshStem, string profileId, string repoRoot)
    {
        var dto = new CookedFolderInspectionDto
        {
            path                  = rawPath ?? "",
            meshStem              = meshStem ?? "",
            ok                    = false,
            meshSlots             = new List<MeshMaterialSlotDto>(),
            userMaterialInstances = new Dictionary<string, MaterialInstanceDto>(),
            warnings              = new List<string>(),
        };
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            dto.error = "path query parameter is required";
            return dto;
        }

        // Mirror ScanCookedFolder: resolve profile-relative folder
        // names via WindrosePaths so "MyPainting" + profileId picks
        // up the per-profile cooked output without the user needing
        // to type an absolute path.
        var resolved = ResolveCookedPath(rawPath, profileId, repoRoot);

        string normalized;
        try
        {
            normalized = Path.GetFullPath(resolved);
        }
        catch (Exception ex)
        {
            dto.error = "Invalid path: " + ex.Message;
            return dto;
        }
        dto.path = normalized;

        if (!Directory.Exists(normalized))
        {
            dto.error = "Folder does not exist: " + normalized;
            return dto;
        }

        string usmapPath;
        try
        {
            usmapPath = UsmapLocator.Find(repoRoot);
            if (!File.Exists(usmapPath))
                throw new FileNotFoundException("Usmap not found: " + usmapPath);
        }
        catch (Exception ex)
        {
            dto.error = "Usmap lookup failed: " + ex.Message;
            return dto;
        }

        try
        {
            var inspector = new CookedFolderInspector
            {
                UsmapPath = usmapPath,
                Log       = msg => Console.WriteLine("[cooked-inspect] " + msg),
            };
            var inspection = inspector.Inspect(normalized, meshStem);

            dto.warnings = inspection.Warnings ?? new List<string>();

            if (inspection.MeshSlots != null)
            {
                foreach (var s in inspection.MeshSlots)
                {
                    dto.meshSlots.Add(new MeshMaterialSlotDto
                    {
                        index            = s.Index,
                        slotName         = s.SlotName,
                        userMaterialStem = s.UserMaterialStem,
                        userMaterialPath = s.UserMaterialPath,
                    });
                }
            }

            if (inspection.UserMaterialInstances != null)
            {
                foreach (var kv in inspection.UserMaterialInstances)
                {
                    dto.userMaterialInstances[kv.Key] = ToMaterialInstanceDto(kv.Value);
                }
            }

            dto.ok = true;
            return dto;
        }
        catch (Exception ex)
        {
            dto.error = ex.GetType().Name + ": " + ex.Message;
            return dto;
        }
    }

    // Reused projection - keep this aligned with the one in
    // VanillaMaterialsEndpoint (same MaterialInstanceDto shape).
    static MaterialInstanceDto ToMaterialInstanceDto(MaterialInstanceData mi)
    {
        var dto = new MaterialInstanceDto
        {
            stem       = mi.AssetStem,
            parentStem = mi.ParentMaterialStem,
            parentPath = mi.ParentMaterialPath,
            scalars    = new List<MIScalarParamDto>(mi.Scalars?.Count ?? 0),
            vectors    = new List<MIVectorParamDto>(mi.Vectors?.Count ?? 0),
            textures   = new List<MITextureParamDto>(mi.Textures?.Count ?? 0),
        };
        foreach (var s in mi.Scalars ?? new List<MIScalarParam>())
            dto.scalars.Add(new MIScalarParamDto { name = s.Name, value = s.Value });
        foreach (var v in mi.Vectors ?? new List<MIVectorParam>())
            dto.vectors.Add(new MIVectorParamDto { name = v.Name, r = v.R, g = v.G, b = v.B, a = v.A });
        foreach (var t in mi.Textures ?? new List<MITextureParam>())
            dto.textures.Add(new MITextureParamDto { name = t.Name, textureStem = t.TextureStem, texturePath = t.TexturePath });
        return dto;
    }
}
