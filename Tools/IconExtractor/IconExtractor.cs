// IconExtractor: extracts UI icons (UTexture2D) from a Windrose game install
// using CUE4Parse to read the AES-encrypted UE5 IoStore containers, plus
// optionally the localized title + description for each item by walking
// every shipped culture's .locres files.
//
// Originally shipped as a standalone EXE invoked via Process.Start; now a
// plain in-process library called from QuartermasterCore.IconExtractionRunner.
// The on-disk manifest JSON contract is unchanged - the runner still writes
// a temp file and we still parse it - so we can keep evolving the manifest
// shape without coupling the two assemblies through a typed contract.
//
// Public surface:
//   IconExtractor.Run(IconExtractorOptions, Action<string>? log)
//     Throws ArgumentException on bad inputs, InvalidOperationException on
//     runtime failures (provider init, decode errors, etc.). Stdout-style
//     progress is forwarded to the optional log callback (one line per
//     call) so the GUI can stream it over SSE.
//
// Manifest JSON shape (per entry):
//   itemId            (required)
//   texturePath       (required, "/Game/...")
//   nameTable / nameKey         (optional, FText for ItemName)
//   descTable / descKey         (optional, FText for ItemDescription)
//   vanityTable / vanityKey     (optional, FText for VanityText)
//   effects[]                   (optional, list of FText refs)
//   setEffects[]                (optional, list of set-effect entries)
//   descriptionData[]           (optional, curve refs that back the
//                                {0}, {1}, ... placeholders in
//                                description / effects / setEffects).
//                                Each entry:
//                                  curveTable, rowName, curveLevel,
//                                  displayType (RatioToPercent /
//                                  ValueToPercent / SecondsAsMinutes /
//                                  ValueAsValue / None),
//                                  inverse (1 - value when true).
//
// Output:
//   <OutDir>/<itemId>.png   per successfully extracted item.
//   <OutDir>/<itemId>.json  per item that has at least one resolved
//                           localized field; format:
//                             { "<culture>": {
//                                  "name":        "...",
//                                  "description": "...",
//                                  "vanityText":  "...",                   (optional)
//                                  "effects":     ["...", "..."],           (optional)
//                                  "setEffects":  [{ "name": "...",         (optional)
//                                                    "description": "...",
//                                                    "setEffectTag": "...",
//                                                    "activationCount": 2 }]
//                              } }
//                           Optional fields are omitted when empty. Only
//                           cultures with at least one non-empty field are
//                           written.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine.Curves;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Textures.BC;

namespace Windrose.IconExtractor;

// Options for IconExtractor.Run. Constructed by the caller (typically
// IconExtractionRunner) with absolute paths that have already been validated.
public sealed class IconExtractorOptions
{
    public string PaksDir { get; set; } = string.Empty;
    public string AesKey { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string OutDir { get; set; } = string.Empty;
    public string UsmapPath { get; set; } = string.Empty;
    public string GameVersion { get; set; } = "UE5_6";
    public bool NoMeta { get; set; }
}

public static class IconExtractor
{
    // Optional localization fields: when present, the extractor resolves the
    // FText (TableId/Key) -> localized string for every culture shipped in
    // the pak and writes <itemId>.json next to the PNG.
    private sealed class ManifestEntry
    {
        public string ItemId { get; set; } = string.Empty;
        public string TexturePath { get; set; } = string.Empty;
        public string? NameTable { get; set; }
        public string? NameKey { get; set; }
        public string? DescTable { get; set; }
        public string? DescKey { get; set; }
        public string? VanityTable { get; set; }
        public string? VanityKey { get; set; }
        public List<EffectRef>? Effects { get; set; }
        public List<SetEffectRef>? SetEffects { get; set; }
        public List<CurveRef>? DescriptionData { get; set; }
    }

    private sealed class EffectRef
    {
        public string? Table { get; set; }
        public string? Key { get; set; }
    }

    private sealed class SetEffectRef
    {
        public string? NameTable { get; set; }
        public string? NameKey { get; set; }
        public string? DescTable { get; set; }
        public string? DescKey { get; set; }
        public string? SetEffectTag { get; set; }
        public int ActivationCount { get; set; }
    }

    // Curve-table reference that backs a {0}/{1}/... placeholder in the
    // localized description / effects / set-effects strings. The field names
    // mirror the JSON shape produced by QuartermasterCore.IconManifestBuilder.
    private sealed class CurveRef
    {
        public string? CurveTable { get; set; }
        public string? RowName { get; set; }
        public int CurveLevel { get; set; }
        public string? DisplayType { get; set; }
        public bool Inverse { get; set; }
    }

    // Tiny wrapper around the optional log callback so the rest of the file
    // can keep its old Console.WriteLine cadence (one line per call,
    // separators between logical sections via Out("")).
    [ThreadStatic]
    private static Action<string>? _log;
    private static void Out(string msg)
    {
        if (_log != null) _log(msg);
    }

    // Entry point. Throws ArgumentException for bad inputs, InvalidOperationException
    // for CUE4Parse / IO failures during the actual run. Output PNG / JSON files
    // land under opts.OutDir.
    public static void Run(IconExtractorOptions opts, Action<string>? log = null)
    {
        if (opts is null) throw new ArgumentNullException(nameof(opts));
        ValidateOptions(opts);

        _log = log;
        try
        {
            RunCore(opts);
        }
        finally
        {
            _log = null;
        }
    }

    private static void ValidateOptions(IconExtractorOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.PaksDir))     throw new ArgumentException("PaksDir is required");
        if (string.IsNullOrWhiteSpace(o.AesKey))      throw new ArgumentException("AesKey is required");
        if (string.IsNullOrWhiteSpace(o.ManifestPath)) throw new ArgumentException("ManifestPath is required");
        if (string.IsNullOrWhiteSpace(o.OutDir))      throw new ArgumentException("OutDir is required");
        if (string.IsNullOrWhiteSpace(o.UsmapPath))   throw new ArgumentException("UsmapPath is required");
        if (!Directory.Exists(o.PaksDir))             throw new ArgumentException($"PaksDir does not exist: {o.PaksDir}");
        if (!File.Exists(o.UsmapPath))                throw new ArgumentException($"Usmap file not found: {o.UsmapPath}");
        if (!File.Exists(o.ManifestPath))             throw new ArgumentException($"Manifest not found: {o.ManifestPath}");
    }

    private static void RunCore(IconExtractorOptions a)
    {
        Directory.CreateDirectory(a.OutDir);

        // Load manifest first so a malformed file fails before we touch the pak.
        var manifest = LoadManifest(a.ManifestPath);
        Out($"[OK] Manifest entries: {manifest.Count}");

        EnsureOodle();
        EnsureDetex();

        Out($"[..] Initializing CUE4Parse provider ({a.GameVersion})");
        Out($"     PaksDir: {a.PaksDir}");

        var version = ParseGameVersion(a.GameVersion);
        var provider = new DefaultFileProvider(
            a.PaksDir,
            SearchOption.TopDirectoryOnly,
            new VersionContainer(version));
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(a.UsmapPath);
        Out($"[OK] Usmap loaded: {a.UsmapPath}");
        provider.Initialize();
        Out($"[..] Before SubmitKey: UnloadedVfs={provider.UnloadedVfs.Count}, MountedVfs={provider.MountedVfs.Count}");

        // Submit the key for the zero-guid (default), and also for every
        // distinct GUID we see on the unloaded readers. UE5 IoStore can
        // assign different GUIDs per .utoc even when they all use the
        // same AES key.
        var aes = new FAesKey(a.AesKey);
        var seenGuids = new HashSet<FGuid> { new FGuid() };
        foreach (var v in provider.UnloadedVfs) seenGuids.Add(v.EncryptionKeyGuid);
        foreach (var g in seenGuids) provider.SubmitKey(g, aes);

        Out($"     After  SubmitKey: UnloadedVfs={provider.UnloadedVfs.Count}, MountedVfs={provider.MountedVfs.Count}");
        var mounted = provider.Mount();
        Out($"     After  Mount():   UnloadedVfs={provider.UnloadedVfs.Count}, MountedVfs={provider.MountedVfs.Count} (+{mounted})");
        provider.PostMount();
        Out($"[OK] Provider ready: {provider.Files.Count} virtual files mounted");

        // Diagnose any leftover unloaded readers by trying to mount them
        // directly, so we see the underlying error CUE4Parse swallowed.
        if (provider.UnloadedVfs.Count > 0)
        {
            Out($"[..] Diagnosing {provider.UnloadedVfs.Count} unloaded VFS readers:");
            foreach (var v in provider.UnloadedVfs.ToList())
            {
                try
                {
                    v.Mount(provider.PathComparer);
                    Out($"     {v.Name}: direct Mount OK");
                }
                catch (Exception ex)
                {
                    Out($"     {v.Name}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        int ok = 0, miss = 0, fail = 0;
        var failures = new List<string>(capacity: 16);

        foreach (var entry in manifest)
        {
            var assetPath = NormalizeAssetPath(entry.TexturePath);
            if (string.IsNullOrEmpty(assetPath))
            {
                miss++;
                continue;
            }

            try
            {
                var tex = provider.LoadPackageObject<UTexture2D>(assetPath);
                var bitmap = tex.Decode();
                if (bitmap is null)
                {
                    fail++;
                    failures.Add($"{entry.ItemId}: Decode() returned null for {assetPath}");
                    continue;
                }
                var pngBytes = bitmap.Encode(ETextureFormat.Png, saveHdrAsHdr: false, out _);
                var outPath = Path.Combine(a.OutDir, $"{SafeFileName(entry.ItemId)}.png");
                File.WriteAllBytes(outPath, pngBytes);
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                failures.Add($"{entry.ItemId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Out("");
        Out($"[OK] Extracted: {ok}");
        if (miss > 0) Out($"[!]  Skipped (no/None texture path): {miss}");
        if (fail > 0)
        {
            Out($"[X]  Failed: {fail}");
            int show = Math.Min(failures.Count, 10);
            for (int i = 0; i < show; i++) Out($"     {failures[i]}");
            if (failures.Count > show) Out($"     ... and {failures.Count - show} more");
        }

        if (!a.NoMeta)
        {
            ExtractMetadata(provider, manifest, a.OutDir);
        }
    }

    // For each shipped culture, swap the provider's localization dictionary
    // and resolve every FText (TableId/Key) reference per manifest entry
    // - name, description, vanityText, effects[], setEffects[].
    // We accumulate results in memory and write one <itemId>.json per item
    // at the end, so each item gets a single file with all locales merged
    // (instead of one file per locale).
    private static void ExtractMetadata(DefaultFileProvider provider, List<ManifestEntry> manifest, string outDir)
    {
        // Filter to entries that carry at least one localizable reference.
        var withMeta = manifest.Where(HasAnyLocalization).ToList();
        if (withMeta.Count == 0)
        {
            Out("");
            Out("[!]  Metadata: no manifest entries carry localization keys - skipped");
            return;
        }

        var cultures = provider.Internationalization.AvailableCultures;
        if (cultures.Count == 0)
        {
            Out("");
            Out("[!]  Metadata: no cultures discovered (DefaultGame.ini parse miss?) - skipped");
            return;
        }

        Out("");
        Out($"[..] Metadata: resolving {withMeta.Count} items across {cultures.Count} cultures: {string.Join(", ", cultures)}");

        // Curve refs are culture-independent (they always produce the same
        // numeric string), so resolve them once per item up front.
        // itemId -> values[] (one entry per descriptionData[] index, null
        // means "lookup failed - leave the {N} literal in place").
        CurveResolver.Verbose = Environment.GetEnvironmentVariable("ICONEXTRACTOR_DEBUG_CURVES") == "1";
        var curveResolver = new CurveResolver(provider);
        var resolvedCurves = new Dictionary<string, string?[]>(StringComparer.Ordinal);
        int curveItems = 0, curveOk = 0, curveMiss = 0;
        foreach (var entry in withMeta)
        {
            if (entry.DescriptionData is not { Count: > 0 }) continue;
            curveItems++;
            var arr = new string?[entry.DescriptionData.Count];
            for (int i = 0; i < entry.DescriptionData.Count; i++)
            {
                var s = curveResolver.Resolve(entry.DescriptionData[i]);
                arr[i] = s;
                if (s is null) curveMiss++; else curveOk++;
            }
            resolvedCurves[entry.ItemId] = arr;
        }
        if (curveItems > 0)
        {
            Out($"     Curve placeholders: resolved {curveOk}, missed {curveMiss} across {curveItems} item(s)");
        }

        // itemId -> culture -> bag of resolved fields
        var perItem = new Dictionary<string, SortedDictionary<string, LocalizedBag>>(withMeta.Count);

        foreach (var culture in cultures)
        {
            try
            {
                provider.ChangeCulture(culture);
            }
            catch (Exception ex)
            {
                Out($"     {culture}: ChangeCulture failed ({ex.GetType().Name}: {ex.Message}) - skipping");
                continue;
            }

            int hits = 0;
            foreach (var entry in withMeta)
            {
                resolvedCurves.TryGetValue(entry.ItemId, out var values);
                var bag = ResolveBag(provider, entry, values);
                if (bag.IsEmpty) continue;

                if (!perItem.TryGetValue(entry.ItemId, out var byCulture))
                {
                    byCulture = new SortedDictionary<string, LocalizedBag>(StringComparer.Ordinal);
                    perItem[entry.ItemId] = byCulture;
                }
                byCulture[culture] = bag;
                hits++;
            }
            Out($"     {culture}: {hits} item(s) had at least one localized field");
        }

        // Write one JSON per item. Optional fields are omitted when empty,
        // so a banana stays { name, description } and a weapon gets
        // { name, description, vanityText, effects } etc.
        var jsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        int written = 0;
        foreach (var (itemId, byCulture) in perItem)
        {
            var shaped = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            foreach (var (culture, bag) in byCulture)
            {
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                if (bag.Name.Length > 0) dict["name"] = bag.Name;
                if (bag.Description.Length > 0) dict["description"] = bag.Description;
                if (bag.VanityText.Length > 0) dict["vanityText"] = bag.VanityText;
                if (bag.Effects is { Count: > 0 }) dict["effects"] = bag.Effects;
                if (bag.SetEffects is { Count: > 0 }) dict["setEffects"] = bag.SetEffects;
                shaped[culture] = dict;
            }

            var outPath = Path.Combine(outDir, $"{SafeFileName(itemId)}.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(shaped, jsonOpts));
            written++;
        }
        Out($"[OK] Metadata: wrote {written} JSON sidecar(s) to {outDir}");
    }

    private static bool HasAnyLocalization(ManifestEntry e)
    {
        if (!string.IsNullOrEmpty(e.NameTable) && !string.IsNullOrEmpty(e.NameKey)) return true;
        if (!string.IsNullOrEmpty(e.DescTable) && !string.IsNullOrEmpty(e.DescKey)) return true;
        if (!string.IsNullOrEmpty(e.VanityTable) && !string.IsNullOrEmpty(e.VanityKey)) return true;
        if (e.Effects is { Count: > 0 }) return true;
        if (e.SetEffects is { Count: > 0 }) return true;
        return false;
    }

    // Resolve every FText reference for a single item under the *current*
    // culture (caller must have invoked ChangeCulture). Placeholder values
    // (when present) substitute {0}/{1}/... in description / effects /
    // setEffects[].description; unresolved placeholders stay literal.
    private static LocalizedBag ResolveBag(DefaultFileProvider provider, ManifestEntry e, string?[]? values)
    {
        string name   = LookupOrEmpty(provider, e.NameTable,   e.NameKey);
        string desc   = SubstitutePlaceholders(LookupOrEmpty(provider, e.DescTable, e.DescKey), values);
        string vanity = LookupOrEmpty(provider, e.VanityTable, e.VanityKey);

        List<string>? effects = null;
        if (e.Effects is { Count: > 0 })
        {
            foreach (var er in e.Effects)
            {
                var v = LookupOrEmpty(provider, er.Table, er.Key);
                if (v.Length == 0) continue;
                effects ??= new List<string>(e.Effects.Count);
                effects.Add(SubstitutePlaceholders(v, values));
            }
        }

        List<Dictionary<string, object>>? setEffects = null;
        if (e.SetEffects is { Count: > 0 })
        {
            foreach (var s in e.SetEffects)
            {
                string sn = LookupOrEmpty(provider, s.NameTable, s.NameKey);
                string sd = SubstitutePlaceholders(LookupOrEmpty(provider, s.DescTable, s.DescKey), values);
                if (sn.Length == 0 && sd.Length == 0) continue;

                var entry = new Dictionary<string, object>(StringComparer.Ordinal);
                if (sn.Length > 0) entry["name"] = sn;
                if (sd.Length > 0) entry["description"] = sd;
                if (!string.IsNullOrEmpty(s.SetEffectTag)) entry["setEffectTag"] = s.SetEffectTag;
                if (s.ActivationCount > 0) entry["activationCount"] = s.ActivationCount;
                setEffects ??= new List<Dictionary<string, object>>(e.SetEffects.Count);
                setEffects.Add(entry);
            }
        }

        return new LocalizedBag(name, desc, vanity, effects, setEffects);
    }

    private static string LookupOrEmpty(DefaultFileProvider provider, string? table, string? key)
    {
        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(key)) return string.Empty;
        return provider.Internationalization.SafeGet(table, key, string.Empty) ?? string.Empty;
    }

    // Replace every {N} in `text` with values[N], leaving the literal {N} in
    // place when the index is out of range or the lookup failed.
    private static readonly Regex PlaceholderRegex = new(@"\{(\d+)\}", RegexOptions.Compiled);
    private static string SubstitutePlaceholders(string text, string?[]? values)
    {
        if (string.IsNullOrEmpty(text) || values is null || values.Length == 0) return text;
        if (text.IndexOf('{') < 0) return text;
        return PlaceholderRegex.Replace(text, m =>
        {
            if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)
                && idx >= 0 && idx < values.Length)
            {
                var v = values[idx];
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return m.Value;
        });
    }

    // Loads UCurveTables on demand and evaluates a CurveRef to a display
    // string ("5", "5%", "5.5%" - per the DisplayType formatter).
    // The provider is shared with the rest of the run, so cached assets are
    // reused. Misses (table not found, row not found) cache as null and the
    // caller leaves the {N} placeholder literal.
    private sealed class CurveResolver
    {
        // Per-table cache: original UCurveTable + a string-keyed RowMap mirror.
        // The mirror is needed because UCurveTable.TryFindCurve hashes by
        // FName.Index when keys were loaded from a binary package, so
        // RowMap.TryGetValue(new FName(textRowName)) misses even when the
        // text matches. We rebuild a case-insensitive string map once per
        // table and look up by string instead.
        private sealed class CachedTable
        {
            public UCurveTable Table;
            public Dictionary<string, FStructFallback> ByName;
            public CachedTable(UCurveTable t, Dictionary<string, FStructFallback> map) { Table = t; ByName = map; }
        }

        private readonly DefaultFileProvider _provider;
        private readonly Dictionary<string, CachedTable?> _cache = new(StringComparer.OrdinalIgnoreCase);

        public CurveResolver(DefaultFileProvider provider) { _provider = provider; }

        public static bool Verbose = false;

        public string? Resolve(CurveRef r)
        {
            if (r is null || string.IsNullOrEmpty(r.CurveTable) || string.IsNullOrEmpty(r.RowName))
                return null;

            var cached = LoadTable(r.CurveTable);
            if (cached is null) return null;

            if (!cached.ByName.TryGetValue(r.RowName, out var rowStruct))
            {
                if (Verbose) Out($"     [curve-miss] row '{r.RowName}' not in {r.CurveTable}");
                return null;
            }

            FRealCurve? curve = cached.Table.CurveTableMode switch
            {
                ECurveTableMode.SimpleCurves => new FSimpleCurve(rowStruct),
                ECurveTableMode.RichCurves   => new FRichCurve(rowStruct),
                _                            => null,
            };
            if (curve is null) return null;

            float value;
            try { value = curve.Eval(r.CurveLevel); }
            catch (Exception ex)
            {
                if (Verbose) Out($"     [curve-eval] {r.RowName} @{r.CurveLevel}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }

            // bInverseValue flips the sign for display. The Windrose data
            // convention stores damage/stamina-reduction curves as negative
            // multipliers (e.g. -0.2 for "20% less"); inverting to +0.2
            // gives the human-friendly "20%" the description text expects.
            float displayed = r.Inverse ? -value : value;
            return Format(displayed, r.DisplayType);
        }

        private CachedTable? LoadTable(string assetPath)
        {
            if (_cache.TryGetValue(assetPath, out var existing)) return existing;
            CachedTable? built = null;
            string? path = null;
            try
            {
                path = NormalizeAssetPath(assetPath);
                if (!string.IsNullOrEmpty(path))
                {
                    var t = _provider.LoadPackageObject<UCurveTable>(path);
                    if (t?.RowMap is not null)
                    {
                        var byName = new Dictionary<string, FStructFallback>(t.RowMap.Count, StringComparer.OrdinalIgnoreCase);
                        foreach (var kvp in t.RowMap)
                        {
                            // Last write wins on duplicate text keys - in practice
                            // RowMap is text-unique, this is just defensive.
                            byName[kvp.Key.Text] = kvp.Value;
                        }
                        built = new CachedTable(t, byName);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Verbose) Out($"     [curve-load] {path ?? assetPath}: {ex.GetType().Name}: {ex.Message}");
                built = null;
            }
            _cache[assetPath] = built;
            return built;
        }

        private static string Format(float v, string? displayType)
        {
            // DisplayTypes seen in Sources/Vanilla (counts in parens):
            //   RatioToPercent (288): 0.05 -> "5%"
            //   ValueAsValue   (217): 5.0  -> "5"
            //   SecondsAsMinutes (62): 300 -> "5"
            //   ValueToPercent  (24): 5    -> "5%"
            //   None            (1):  fallback to ValueAsValue
            return displayType switch
            {
                "RatioToPercent"   => FormatNumber(v * 100f) + "%",
                "ValueToPercent"   => FormatNumber(v) + "%",
                "SecondsAsMinutes" => FormatNumber(v / 60f),
                _                  => FormatNumber(v),
            };
        }

        private static string FormatNumber(float v)
        {
            // Round to 2 decimals max, drop trailing zeros: 5.0 -> "5",
            // 5.50 -> "5.5", 5.123 -> "5.12". Invariant culture so "%"-style
            // strings stay locale-neutral (Eng. dot vs. continental comma).
            var rounded = (float)Math.Round(v, 2);
            return rounded.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    private readonly record struct LocalizedBag(
        string Name,
        string Description,
        string VanityText,
        List<string>? Effects,
        List<Dictionary<string, object>>? SetEffects)
    {
        public bool IsEmpty =>
            Name.Length == 0 &&
            Description.Length == 0 &&
            VanityText.Length == 0 &&
            (Effects is null || Effects.Count == 0) &&
            (SetEffects is null || SetEffects.Count == 0);
    }

    // UE5 IoStore (.utoc/.ucas) usually uses Oodle compression. Windrose ships
    // without an oo2core DLL so we let CUE4Parse fetch one from the official
    // OodleUE distribution on first run and cache it next to our exe.
    private static void EnsureOodle()
    {
        var here = AppContext.BaseDirectory;
        var dllPath = Path.Combine(here, OodleHelper.OodleFileName);
        if (!File.Exists(dllPath))
        {
            Out($"[..] Downloading Oodle DLL ({OodleHelper.OodleFileName})");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            // The HttpClient overload pulls from the OodleUE GitHub release,
            // which is more reliable than the synchronous DownloadOodleDll
            // (that one occasionally fails to resolve its CDN endpoint).
            var ok = OodleHelper.DownloadOodleDllFromOodleUEAsync(http, dllPath)
                .GetAwaiter().GetResult();
            if (!ok || !File.Exists(dllPath))
                throw new InvalidOperationException("Failed to download Oodle DLL from OodleUE.");
            Out($"[OK] Oodle DLL: {dllPath}");
        }
        OodleHelper.Initialize(dllPath);
    }

    // BC7/BC4/BC5/ASTC textures need Detex for decompression. The DLL is shipped
    // as an embedded resource inside CUE4Parse-Conversion - extract once and
    // hand the path to DetexHelper.Initialize.
    private static void EnsureDetex()
    {
        var here = AppContext.BaseDirectory;
        var dllPath = Path.Combine(here, DetexHelper.DLL_NAME);
        if (!File.Exists(dllPath))
        {
            Out($"[..] Extracting Detex DLL ({DetexHelper.DLL_NAME})");
            if (!DetexHelper.LoadDll(dllPath) || !File.Exists(dllPath))
                throw new InvalidOperationException("Failed to extract embedded Detex DLL.");
            Out($"[OK] Detex DLL: {dllPath}");
        }
        DetexHelper.Initialize(dllPath);
    }

    private static List<ManifestEntry> LoadManifest(string path)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"Manifest not found: {path}");

        using var stream = File.OpenRead(path);
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        var entries = JsonSerializer.Deserialize<List<ManifestEntry>>(stream, opts)
            ?? throw new ArgumentException("Manifest could not be parsed as a JSON array.");
        return entries;
    }

    // CUE4Parse expects "Game/..." (no leading slash) and no ".AssetName" suffix.
    // Vanilla JSONs ship paths like "/Game/UI/Icons/.../Foo.Foo".
    private static string NormalizeAssetPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        if (raw.Equals("None", StringComparison.OrdinalIgnoreCase)) return string.Empty;

        var s = raw.TrimStart('/');
        // Strip the ".AssetName" trailing duplicate ("Foo.Foo" -> "Foo").
        var lastDot = s.LastIndexOf('.');
        var lastSlash = s.LastIndexOf('/');
        if (lastDot > lastSlash) s = s[..lastDot];
        return s;
    }

    private static EGame ParseGameVersion(string raw)
    {
        // Accept "UE5_4" or "GAME_UE5_4" or "5.4".
        var s = raw.Trim().ToUpperInvariant();
        if (s.StartsWith("GAME_")) s = s[5..];
        if (s.StartsWith("UE")) s = "GAME_" + s;
        else if (s.Length > 0 && char.IsDigit(s[0])) s = "GAME_UE" + s.Replace('.', '_');
        else s = "GAME_" + s;
        if (Enum.TryParse<EGame>(s, ignoreCase: true, out var v)) return v;
        throw new ArgumentException($"Unknown EGame value: {raw} (tried '{s}')");
    }

    private static string SafeFileName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        Span<char> buf = stackalloc char[s.Length];
        for (int i = 0; i < s.Length; i++)
            buf[i] = Array.IndexOf(bad, s[i]) >= 0 ? '_' : s[i];
        return new string(buf);
    }
}
