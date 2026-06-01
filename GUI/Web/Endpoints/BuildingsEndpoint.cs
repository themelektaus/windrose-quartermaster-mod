using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class BuildingsEndpoint
{
    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/buildings/scan-cooked", (string path, string profileId) =>
        {
            var dto = ScanCookedFolder(path, profileId, repoRoot);
            return Results.Json(dto);
        });

        app.MapGet("/api/buildings/inspect-cooked", (string path, string meshStem, string profileId) =>
        {
            var dto = InspectCookedFolder(path, meshStem, profileId, repoRoot);
            return Results.Json(dto);
        });

        app.MapGet("/api/buildings/inspect-recipe", (string templateId) =>
        {
            var dto = InspectRecipe(templateId, repoRoot);
            return Results.Json(dto);
        });

        app.MapGet("/api/buildings/default-textures", () =>
        {
            return Results.Json(new
            {
                stems = DefaultTextureProvider.GetStems(),
            });
        });

        Microsoft.AspNetCore.Http.IResult HandlePresets() => Results.Json(new
        {
            presets = ComponentPresetCatalog.GetDtos(),
        });
        app.MapGet("/api/buildings/component-presets", HandlePresets);
        app.MapGet("/api/buildings/flame-presets",     HandlePresets);
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
                try { size = new FileInfo(file).Length; } catch { }

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

        // Order must stay stable across re-scans: GUI card rendering depends on it.
        dto.entries = dto.entries
            .OrderBy(e => e.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return dto;
    }

    static string Classify(string stem, string ext)
    {
        if (string.IsNullOrEmpty(stem)) return "other";

        switch (ext)
        {
            case ".uexp":
            case ".ubulk":
            case ".upage":
                return "sidecar";
        }

        if (ext != ".uasset")
        {
            return "other";
        }

        if (StemStartsWith(stem, "SM_"))   return "mesh";
        if (StemStartsWith(stem, "MI_"))   return "matinst";
        if (StemStartsWith(stem, "M_"))    return "material";
        if (StemStartsWith(stem, "BP_"))   return "blueprint";
        if (StemStartsWith(stem, "DA_"))   return "data";
        if (StemStartsWith(stem, "T_"))
        {
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
