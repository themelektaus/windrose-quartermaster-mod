using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Web.Endpoints;

// Powers the per-slot "Vanilla material parent" dropdown in the
// Building Creator tab.
//
//   GET /api/vanilla-materials?search=<q>&limit=<n>
//       Search the indexed list of MI_*.uasset entries from the vanilla
//       paks. Substring match on displayName + packagePath. Limited to
//       avoid flooding the wire (default 50, max 200). Returns
//       VanillaMaterialDto[].
//
//   GET /api/vanilla-materials/inspect?path=<packagePath>
//       Extract one vanilla MI via retoc-to-legacy and inspect its
//       Scalar/Vector/Texture parameter blocks via UAssetAPI. The GUI
//       calls this when the user picks a material in the dropdown,
//       then renders one editor control per surfaced parameter.
//       Returns MaterialInstanceDto.
//
// The catalog is lazy-built on first request (Index walks the mounted
// CUE4Parse provider's virtual-file list - a few seconds on cold start,
// instant on subsequent requests). The inspect endpoint runs retoc
// once per unique packagePath; a future iteration may cache the
// inspection result but for now a build-menu pick is a one-off click.
public static class VanillaMaterialsEndpoint
{
    // Static singleton catalog. First-touch cost is paid by whichever
    // endpoint call comes in first; subsequent calls return immediately.
    static readonly object _gate = new object();
    static VanillaMaterialCatalog _catalog;
    static string _retocExe;
    static string _usmapPath;
    static string _paksDir;
    static string _aesKey;
    static string _inspectCacheDir;

    public static void Map(WebApplication app, string repoRoot)
    {
        // Resolve shared inputs once at startup so all endpoint paths
        // see the same configured catalog. Failures here become 503s
        // on first request rather than crashing the host.
        EnsureBootstrap(repoRoot);

        app.MapGet("/api/vanilla-materials", (string search, int? limit) =>
        {
            try
            {
                var cat = GetCatalog();
                int lim = limit.GetValueOrDefault(50);
                if (lim < 1) lim = 1;
                // Cap raised to 2000 so the frontend can load the full
                // catalog (~1134 MIs) once and filter client-side using
                // the same UX pattern as the loot-table picker.
                if (lim > 2000) lim = 2000;
                var hits = cat.Search(search ?? "", lim);
                var dtos = new List<VanillaMaterialDto>(hits.Count);
                foreach (var e in hits)
                {
                    dtos.Add(new VanillaMaterialDto
                    {
                        displayName = e.DisplayName,
                        packagePath = e.PackagePath,
                    });
                }
                return Results.Json(dtos);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 503);
            }
        });

        app.MapGet("/api/vanilla-materials/inspect", (string path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.Json(new { error = "path query parameter is required" }, statusCode: 400);

            try
            {
                var dto = InspectVanillaMaterial(path);
                return Results.Json(dto);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });
    }

    static void EnsureBootstrap(string repoRoot)
    {
        if (_catalog != null) return;
        lock (_gate)
        {
            if (_catalog != null) return;

            // Vanilla paks dir: from SteamLocator like everyone else.
            _paksDir = SteamLocator.FindVanillaPaksDir();
            _aesKey  = WindroseGameSecrets.AesKey;
            _usmapPath = UsmapLocator.Find(repoRoot);

            // retoc.exe: same resolver as the build pipeline. Repo root
            // by convention contains retoc.exe at workspace level (see
            // BuildPipeline.cs - retoc is resolved relative to ModRoot).
            _retocExe = ResolveRetocExe(repoRoot);

            _inspectCacheDir = Path.Combine(Path.GetTempPath(),
                "QuartermasterVanillaMiInspect");
            Directory.CreateDirectory(_inspectCacheDir);

            _catalog = new VanillaMaterialCatalog
            {
                PaksDir   = _paksDir,
                AesKey    = _aesKey,
                UsmapPath = _usmapPath,
                Log       = msg => Console.WriteLine("[vanilla-catalog] " + msg),
            };
        }
    }

    static VanillaMaterialCatalog GetCatalog() => _catalog
        ?? throw new InvalidOperationException("Vanilla material catalog not bootstrapped");

    // Resolve retoc.exe relative to the repo root. Mirrors the
    // convention used by BuildPipeline which expects it next to
    // repak.exe at the workspace level.
    static string ResolveRetocExe(string repoRoot)
    {
        var candidates = new[]
        {
            Path.Combine(repoRoot, "retoc.exe"),
            Path.Combine(repoRoot, "Tools", "retoc.exe"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        throw new InvalidOperationException(
            "retoc.exe not found - searched: " + string.Join(", ", candidates));
    }

    // Lazy extract + inspect a vanilla MI by package path. Caches the
    // legacy extract on disk (under temp) keyed by stem so subsequent
    // requests for the same MI are fast.
    static MaterialInstanceDto InspectVanillaMaterial(string packagePath)
    {
        // Strip the optional "/Game/" prefix and trailing ".uasset" if
        // the caller included them. We only need the stem for the
        // retoc --filter and we re-derive the path on the way out.
        string stem;
        int lastSlash = packagePath.LastIndexOfAny(new[] { '/', '\\' });
        stem = lastSlash >= 0 ? packagePath.Substring(lastSlash + 1) : packagePath;
        if (stem.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            stem = stem.Substring(0, stem.Length - ".uasset".Length);

        if (string.IsNullOrWhiteSpace(stem))
            throw new ArgumentException("Could not derive stem from path: " + packagePath);

        var perAssetDir = Path.Combine(_inspectCacheDir, stem);
        string legacyAssetPath = null;

        // If we already have it cached, reuse the extract; otherwise run
        // retoc to-legacy --filter <stem>.
        if (Directory.Exists(perAssetDir))
        {
            var existing = Directory.GetFiles(perAssetDir, stem + ".uasset", SearchOption.AllDirectories);
            if (existing.Length > 0) legacyAssetPath = existing[0];
        }
        if (legacyAssetPath == null)
        {
            Directory.CreateDirectory(perAssetDir);
            RunRetocToLegacy(stem, perAssetDir);
            var found = Directory.GetFiles(perAssetDir, stem + ".uasset", SearchOption.AllDirectories);
            if (found.Length == 0)
                throw new InvalidOperationException(
                    "retoc produced no " + stem + ".uasset under " + perAssetDir);
            legacyAssetPath = found[0];
        }

        var inspector = new MaterialInstanceInspector { UsmapPath = _usmapPath };
        var mi = inspector.Inspect(legacyAssetPath);
        if (mi == null)
            throw new InvalidOperationException(
                "Asset is not a MaterialInstanceConstant: " + packagePath);

        return ToDto(mi);
    }

    static void RunRetocToLegacy(string stem, string outDir)
    {
        var argv = new List<string>
        {
            "--aes-key", _aesKey,
            "to-legacy",
            _paksDir, outDir,
            "--version", "UE5_6",
            "--filter", stem,
        };
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _retocExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);

        var proc = System.Diagnostics.Process.Start(psi);
        proc.OutputDataReceived += (_, e) => { /* swallow */ };
        proc.ErrorDataReceived  += (_, e) => { /* swallow */ };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                "retoc to-legacy failed for '" + stem + "' (exit " + proc.ExitCode + ")");
    }

    static MaterialInstanceDto ToDto(MaterialInstanceData mi)
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
