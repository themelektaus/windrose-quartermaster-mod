using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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
}
