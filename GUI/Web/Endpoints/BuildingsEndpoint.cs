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
//   GET /api/buildings/scan-cooked?path=<absolute>
//       Lists files in the user's cooked-output folder so the GUI can
//       preview what's there before the user commits the path to the
//       profile. Classifies each file by stem+extension (mesh / icon /
//       texture / material / sidecar / other) so the GUI can warn about
//       likely-missing items (no mesh found, no icon found, ...) and
//       flag user-cooked materials that will get skipped at build time
//       (because they crash shipping - per the spike bisect).
//
// Phase 1 only ships the scan endpoint. Future endpoints could:
//   - validate-cook (sanity check the prefix + slot expectations match
//     the picked template before save)
//   - browse-folder (let the GUI traverse subdirs of a root path)
public static class BuildingsEndpoint
{
    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/buildings/scan-cooked", (string path) =>
        {
            var dto = ScanCookedFolder(path);
            return Results.Json(dto);
        });

        // Deep inspect: read the mesh's material slot list + each
        // user-cooked MI in the folder. The GUI uses this to drive its
        // dynamic per-slot UI (Etappe G).
        app.MapGet("/api/buildings/inspect-cooked", (string path, string meshStem) =>
        {
            var dto = InspectCookedFolder(path, meshStem, repoRoot);
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

    // Mirrors the static template factory list in BuildingTemplatesEndpoint.
    // Keeps the two endpoints decoupled (each only knows about the
    // templates that matter for its own response shape).
    static BuildingTemplate ResolveTemplate(string id)
    {
        if (string.Equals(id, "Painting", StringComparison.OrdinalIgnoreCase))
            return BuildingTemplate.Painting();
        if (string.Equals(id, "Bucket", StringComparison.OrdinalIgnoreCase))
            return BuildingTemplate.Bucket();
        return null;
    }

    static CookedFolderScanDto ScanCookedFolder(string raw)
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

        string normalized;
        try
        {
            normalized = Path.GetFullPath(raw);
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
        string rawPath, string meshStem, string repoRoot)
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

        string normalized;
        try
        {
            normalized = Path.GetFullPath(rawPath);
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
