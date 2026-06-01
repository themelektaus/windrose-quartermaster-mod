using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;
using Windrose.Quartermaster.Core.BuildingCreator;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class VanillaMaterialsEndpoint
{
    static readonly object _gate = new object();
    static VanillaMaterialCatalog _catalog;
    static string _retocExe;
    static string _usmapPath;
    static string _paksDir;
    static string _aesKey;
    static string _inspectCacheDir;

    public static void Map(WebApplication app, string repoRoot)
    {
        app.MapGet("/api/vanilla-materials", (string search, int? limit) =>
        {
            try
            {
                EnsureBootstrap(repoRoot);
                var cat = GetCatalog();
                int lim = limit.GetValueOrDefault(50);
                if (lim < 1) lim = 1;
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
                EnsureBootstrap(repoRoot);
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

            _paksDir = SteamLocator.FindVanillaPaksDir();
            _aesKey  = WindroseGameSecrets.AesKey;
            _usmapPath = UsmapLocator.Find(repoRoot);

            var retocResolver = new RetocResolver(repoRoot)
            {
                Log = msg => Console.WriteLine("[vanilla-catalog/retoc] " + msg),
            };
            _retocExe = retocResolver.Resolve();

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

    static MaterialInstanceDto InspectVanillaMaterial(string packagePath)
    {
        string stem;
        int lastSlash = packagePath.LastIndexOfAny(new[] { '/', '\\' });
        stem = lastSlash >= 0 ? packagePath.Substring(lastSlash + 1) : packagePath;
        if (stem.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            stem = stem.Substring(0, stem.Length - ".uasset".Length);

        if (string.IsNullOrWhiteSpace(stem))
            throw new ArgumentException("Could not derive stem from path: " + packagePath);

        var perAssetDir = Path.Combine(_inspectCacheDir, stem);
        string legacyAssetPath = null;

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

        // Both streams must be drained or the child can deadlock on a full pipe.
        var stdoutSb = new System.Text.StringBuilder();
        var stderrSb = new System.Text.StringBuilder();
        var proc = System.Diagnostics.Process.Start(psi);
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stdoutSb) { if (stdoutSb.Length < 4096) stdoutSb.AppendLine(e.Data); }
        };
        proc.ErrorDataReceived  += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderrSb) { if (stderrSb.Length < 4096) stderrSb.AppendLine(e.Data); }
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            string tail;
            lock (stderrSb) { tail = stderrSb.ToString().TrimEnd(); }
            if (string.IsNullOrEmpty(tail)) { lock (stdoutSb) { tail = stdoutSb.ToString().TrimEnd(); } }
            if (tail.Length > 800) tail = "..." + tail.Substring(tail.Length - 800);
            var detail = string.IsNullOrEmpty(tail) ? "" : " - " + tail;
            throw new InvalidOperationException(
                "retoc to-legacy failed for '" + stem + "' (exit " + proc.ExitCode + ")" + detail);
        }
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
