using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class NpcSpawnersEndpoint
{
    // Quest spawners are never patchable; hide them from the catalog too.
    const string QuestSegment = "/AI_POI_ForQuest/";

    public static void Map(WebApplication app, string repoRoot)
    {
        var paths = WindrosePaths.FromModRoot(repoRoot);

        app.MapGet("/api/npc-spawners", async () =>
        {
            var list = await LoadSpawners(paths.VanillaAiSpawners);
            return Results.Json(list);
        });
    }

    static async Task<List<NpcSpawnerDto>> LoadSpawners(string spawnersDir)
    {
        var result = new List<NpcSpawnerDto>();
        if (!Directory.Exists(spawnersDir)) return result;

        var rootFull = Path.GetFullPath(spawnersDir);
        foreach (var path in Directory.EnumerateFiles(rootFull, "*.json", SearchOption.AllDirectories))
        {
            var dto = await TryParse(rootFull, path);
            if (dto != null) result.Add(dto);
        }

        result.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
        return result;
    }

    static async Task<NpcSpawnerDto> TryParse(string rootFull, string jsonPath)
    {
        try
        {
            var rel = jsonPath.Substring(rootFull.Length).TrimStart(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var idSlash = rel
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');

            if (("/" + idSlash).IndexOf(QuestSegment, StringComparison.OrdinalIgnoreCase) >= 0)
                return null;

            var id = idSlash.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? idSlash.Substring(0, idSlash.Length - 5)
                : idSlash;

            using var stream = File.OpenRead(jsonPath);
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string type = root.TryGetProperty("$type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : "";

            // Collect Amount blocks + mobs from both shapes.
            int min = int.MaxValue, max = int.MinValue, blocks = 0;
            bool isNpc = false;
            // Unique = single named character / town citizen: actor under
            // /Character/AI/NPC/ or any spawner beneath /Tortuga/. Mirrors
            // NpcSpawnPatcher.IsUniqueNpcSpawner so the UI projection matches the build.
            bool isUnique = ("/" + idSlash).IndexOf("/Tortuga/", StringComparison.OrdinalIgnoreCase) >= 0;
            var mobs = new List<string>();
            var mobSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void ScanCollection(JsonElement coll)
            {
                if (coll.ValueKind != JsonValueKind.Array) return;
                foreach (var c in coll.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    if (c.TryGetProperty("Amount", out var amt) && amt.ValueKind == JsonValueKind.Object)
                    {
                        int aMin = amt.TryGetProperty("Min", out var mn) && mn.TryGetInt32(out var mv) ? mv : 0;
                        int aMax = amt.TryGetProperty("Max", out var mx) && mx.TryGetInt32(out var xv) ? xv : 0;
                        if (aMin < min) min = aMin;
                        if (aMax > max) max = aMax;
                        blocks++;
                    }
                    if (c.TryGetProperty("Assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in assets.EnumerateArray())
                        {
                            if (a.ValueKind != JsonValueKind.String) continue;
                            var ap = a.GetString();
                            if (ap != null)
                            {
                                if (!isNpc
                                    && (ap.IndexOf("/Character/", StringComparison.OrdinalIgnoreCase) >= 0
                                     || ap.IndexOf("/A2_Spawners/AI_", StringComparison.OrdinalIgnoreCase) >= 0))
                                    isNpc = true;
                                if (!isUnique
                                    && ap.IndexOf("/Character/AI/NPC/", StringComparison.OrdinalIgnoreCase) >= 0)
                                    isUnique = true;
                            }
                            var stem = StemOf(ap);
                            if (!string.IsNullOrEmpty(stem) && mobSeen.Add(stem)) mobs.Add(stem);
                        }
                    }
                }
            }

            if (root.TryGetProperty("Variants", out var variants) && variants.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in variants.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("Collection", out var coll))
                        ScanCollection(coll);
            }
            if (root.TryGetProperty("Collection", out var topColl))
                ScanCollection(topColl);

            bool hasRespawn = false;
            int respawnMinutes = 0;
            if (root.TryGetProperty("RespawnInterval", out var ri) && ri.ValueKind == JsonValueKind.Object
                && ri.TryGetProperty("Min", out var riMin) && riMin.TryGetInt32(out var riv))
            {
                hasRespawn = true;
                respawnMinutes = (int)Math.Round(riv / 60.0);
            }

            // Files with neither Amount nor RespawnInterval (mutator presets) aren't tunable.
            if (blocks == 0 && !hasRespawn) return null;

            var slash = id.IndexOf('/');
            var category = slash < 0 ? "(root)" : id.Substring(0, slash);
            var nameStart = id.LastIndexOf('/');
            var name = nameStart < 0 ? id : id.Substring(nameStart + 1);

            return new NpcSpawnerDto
            {
                id = id,
                name = name,
                category = category,
                kind = isNpc ? "npc" : "other",
                isUniqueNpc = isNpc && isUnique,
                type = type,
                hasRespawn = hasRespawn,
                respawnMinutes = respawnMinutes,
                countMin = min == int.MaxValue ? 0 : min,
                countMax = max == int.MinValue ? 0 : max,
                amountBlocks = blocks,
                mobs = mobs,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // "/Game/.../BP_Mob_Wolf.BP_Mob_Wolf_C" -> "BP_Mob_Wolf"
    static string StemOf(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return null;
        var s = assetPath;
        var dot = s.IndexOf('.');
        if (dot >= 0) s = s.Substring(0, dot);
        var slash = s.LastIndexOf('/');
        if (slash >= 0) s = s.Substring(slash + 1);
        return s;
    }
}
