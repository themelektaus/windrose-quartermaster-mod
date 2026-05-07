// IconExtractor: extracts UI icons (UTexture2D) from a Windrose game install
// using CUE4Parse to read the AES-encrypted UE5 IoStore containers, plus
// optionally the localized title + description for each item by walking
// every shipped culture's .locres files.
//
// Invoked by the Stack Size mod's Extract-Icons.ps1. The PowerShell wrapper
// already collected the InventoryItem JSONs from Sources/Vanilla/, parsed
// the ItemTexture paths and the FText (TableId/Key) references for
// ItemName/ItemDescription, and now hands us the list of asset paths +
// localization keys to extract.
//
// Args:
//   --paks-dir <path>    Folder that contains the game's pakchunk0*.pak/.ucas/.utoc. (required)
//   --aes-key  <hex>     AES key for the encrypted IoStore containers. (required)
//   --manifest <path>    JSON array; each entry:
//                          itemId        (required)
//                          texturePath   (required, "/Game/...")
//                          nameTable     (optional, FText TableId for ItemName)
//                          nameKey       (optional, FText Key for ItemName)
//                          descTable     (optional, FText TableId for ItemDescription)
//                          descKey       (optional, FText Key for ItemDescription)
//   --out-dir  <path>    Where the PNGs land. Created if missing. (required)
//   --usmap    <path>    UE5 mappings file (.usmap) for unversioned property layouts.
//                        Without it CUE4Parse cannot deserialize UTexture2D properties
//                        on Windrose builds. Generate via UE4SS Ctrl+Num6 (DumpUSMAP). (required)
//   --game-version <id>  Optional, defaults to UE5_6. One of CUE4Parse's EGame names.
//   --no-meta            Skip metadata extraction (only PNGs).
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
//
// Exit codes:
//   0  success (everything written, even if some items were skipped).
//   2  argument error (bad CLI args, missing files).
//   3  CUE4Parse provider initialization failed (wrong key / wrong game ver).

using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Textures.BC;

namespace WindroseIconExtractor;

internal static class Program
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

    private static int Main(string[] args)
    {
        try
        {
            var parsed = ParseArgs(args);
            return Run(parsed);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"[X] {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[X] {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 3;
        }
    }

    private static int Run(ParsedArgs a)
    {
        Directory.CreateDirectory(a.OutDir);

        // Load manifest first so a malformed file fails before we touch the pak.
        var manifest = LoadManifest(a.ManifestPath);
        Console.WriteLine($"[OK] Manifest entries: {manifest.Count}");

        EnsureOodle();
        EnsureDetex();

        Console.WriteLine($"[..] Initializing CUE4Parse provider ({a.GameVersion})");
        Console.WriteLine($"     PaksDir: {a.PaksDir}");

        var version = ParseGameVersion(a.GameVersion);
        var provider = new DefaultFileProvider(
            a.PaksDir,
            SearchOption.TopDirectoryOnly,
            new VersionContainer(version));
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(a.UsmapPath);
        Console.WriteLine($"[OK] Usmap loaded: {a.UsmapPath}");
        provider.Initialize();
        Console.WriteLine($"[..] Before SubmitKey: UnloadedVfs={provider.UnloadedVfs.Count}, MountedVfs={provider.MountedVfs.Count}");

        // Submit the key for the zero-guid (default), and also for every
        // distinct GUID we see on the unloaded readers. UE5 IoStore can
        // assign different GUIDs per .utoc even when they all use the
        // same AES key.
        var aes = new FAesKey(a.AesKey);
        var seenGuids = new HashSet<FGuid> { new FGuid() };
        foreach (var v in provider.UnloadedVfs) seenGuids.Add(v.EncryptionKeyGuid);
        foreach (var g in seenGuids) provider.SubmitKey(g, aes);

        Console.WriteLine($"     After  SubmitKey: UnloadedVfs={provider.UnloadedVfs.Count}, MountedVfs={provider.MountedVfs.Count}");
        var mounted = provider.Mount();
        Console.WriteLine($"     After  Mount():   UnloadedVfs={provider.UnloadedVfs.Count}, MountedVfs={provider.MountedVfs.Count} (+{mounted})");
        provider.PostMount();
        Console.WriteLine($"[OK] Provider ready: {provider.Files.Count} virtual files mounted");

        // Diagnose any leftover unloaded readers by trying to mount them
        // directly, so we see the underlying error CUE4Parse swallowed.
        if (provider.UnloadedVfs.Count > 0)
        {
            Console.WriteLine($"[..] Diagnosing {provider.UnloadedVfs.Count} unloaded VFS readers:");
            foreach (var v in provider.UnloadedVfs.ToList())
            {
                try
                {
                    v.Mount(provider.PathComparer);
                    Console.WriteLine($"     {v.Name}: direct Mount OK");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"     {v.Name}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        if (Environment.GetEnvironmentVariable("ICONEXTRACTOR_DUMP_VFS") == "1")
        {
            Console.WriteLine("[..] Mounted VFS:");
            foreach (var v in provider.MountedVfs) Console.WriteLine($"     {v.Name} (Encrypted={v.IsEncrypted}, Guid={v.EncryptionKeyGuid})");
            Console.WriteLine("[..] Unloaded VFS (need additional keys):");
            foreach (var v in provider.UnloadedVfs) Console.WriteLine($"     {v.Name} (Encrypted={v.IsEncrypted}, Guid={v.EncryptionKeyGuid})");
            return 0;
        }

        if (Environment.GetEnvironmentVariable("ICONEXTRACTOR_DUMP_PATHS") == "1")
        {
            var byExt = provider.Files.Keys
                .GroupBy(k => Path.GetExtension(k).ToLowerInvariant())
                .OrderByDescending(g => g.Count())
                .Take(15)
                .ToList();
            Console.WriteLine("[..] File counts by extension:");
            foreach (var g in byExt) Console.WriteLine($"     {g.Key,-12} {g.Count(),6}");

            var icons = provider.Files.Keys
                .Where(k => k.Contains("T_ItemIcon", StringComparison.OrdinalIgnoreCase))
                .Take(10).ToList();
            Console.WriteLine($"[..] Files matching T_ItemIcon ({icons.Count} sampled):");
            foreach (var s in icons) Console.WriteLine($"     {s}");

            var uassets = provider.Files.Keys
                .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                .Take(10).ToList();
            Console.WriteLine($"[..] Sample .uasset files ({uassets.Count} of {provider.Files.Keys.Count(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))}):");
            foreach (var s in uassets) Console.WriteLine($"     {s}");
            return 0;
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

        Console.WriteLine();
        Console.WriteLine($"[OK] Extracted: {ok}");
        if (miss > 0) Console.WriteLine($"[!]  Skipped (no/None texture path): {miss}");
        if (fail > 0)
        {
            Console.WriteLine($"[X]  Failed: {fail}");
            int show = Math.Min(failures.Count, 10);
            for (int i = 0; i < show; i++) Console.WriteLine($"     {failures[i]}");
            if (failures.Count > show) Console.WriteLine($"     ... and {failures.Count - show} more");
        }

        if (!a.NoMeta)
        {
            ExtractMetadata(provider, manifest, a.OutDir);
        }

        return 0;
    }

    // For each shipped culture, swap the provider's localization dictionary
    // and resolve every FText (TableId/Key) reference per manifest entry --
    // name, description, vanityText, effects[], setEffects[].
    // We accumulate results in memory and write one <itemId>.json per item
    // at the end, so each item gets a single file with all locales merged
    // (instead of one file per locale).
    private static void ExtractMetadata(DefaultFileProvider provider, List<ManifestEntry> manifest, string outDir)
    {
        // Filter to entries that carry at least one localizable reference.
        var withMeta = manifest.Where(HasAnyLocalization).ToList();
        if (withMeta.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("[!]  Metadata: no manifest entries carry localization keys -- skipped");
            return;
        }

        var cultures = provider.Internationalization.AvailableCultures;
        if (cultures.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("[!]  Metadata: no cultures discovered (DefaultGame.ini parse miss?) -- skipped");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"[..] Metadata: resolving {withMeta.Count} items across {cultures.Count} cultures: {string.Join(", ", cultures)}");

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
                Console.WriteLine($"     {culture}: ChangeCulture failed ({ex.GetType().Name}: {ex.Message}) -- skipping");
                continue;
            }

            int hits = 0;
            foreach (var entry in withMeta)
            {
                var bag = ResolveBag(provider, entry);
                if (bag.IsEmpty) continue;

                if (!perItem.TryGetValue(entry.ItemId, out var byCulture))
                {
                    byCulture = new SortedDictionary<string, LocalizedBag>(StringComparer.Ordinal);
                    perItem[entry.ItemId] = byCulture;
                }
                byCulture[culture] = bag;
                hits++;
            }
            Console.WriteLine($"     {culture}: {hits} item(s) had at least one localized field");
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
        Console.WriteLine($"[OK] Metadata: wrote {written} JSON sidecar(s) to {outDir}");
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
    // culture (caller must have invoked ChangeCulture).
    private static LocalizedBag ResolveBag(DefaultFileProvider provider, ManifestEntry e)
    {
        string name   = LookupOrEmpty(provider, e.NameTable,   e.NameKey);
        string desc   = LookupOrEmpty(provider, e.DescTable,   e.DescKey);
        string vanity = LookupOrEmpty(provider, e.VanityTable, e.VanityKey);

        List<string>? effects = null;
        if (e.Effects is { Count: > 0 })
        {
            foreach (var er in e.Effects)
            {
                var v = LookupOrEmpty(provider, er.Table, er.Key);
                if (v.Length == 0) continue;
                effects ??= new List<string>(e.Effects.Count);
                effects.Add(v);
            }
        }

        List<Dictionary<string, object>>? setEffects = null;
        if (e.SetEffects is { Count: > 0 })
        {
            foreach (var s in e.SetEffects)
            {
                string sn = LookupOrEmpty(provider, s.NameTable, s.NameKey);
                string sd = LookupOrEmpty(provider, s.DescTable, s.DescKey);
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
            Console.WriteLine($"[..] Downloading Oodle DLL ({OodleHelper.OodleFileName})");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            // The HttpClient overload pulls from the OodleUE GitHub release,
            // which is more reliable than the synchronous DownloadOodleDll
            // (that one occasionally fails to resolve its CDN endpoint).
            var ok = OodleHelper.DownloadOodleDllFromOodleUEAsync(http, dllPath)
                .GetAwaiter().GetResult();
            if (!ok || !File.Exists(dllPath))
                throw new InvalidOperationException("Failed to download Oodle DLL from OodleUE.");
            Console.WriteLine($"[OK] Oodle DLL: {dllPath}");
        }
        OodleHelper.Initialize(dllPath);
    }

    // BC7/BC4/BC5/ASTC textures need Detex for decompression. The DLL is shipped
    // as an embedded resource inside CUE4Parse-Conversion -- extract once and
    // hand the path to DetexHelper.Initialize.
    private static void EnsureDetex()
    {
        var here = AppContext.BaseDirectory;
        var dllPath = Path.Combine(here, DetexHelper.DLL_NAME);
        if (!File.Exists(dllPath))
        {
            Console.WriteLine($"[..] Extracting Detex DLL ({DetexHelper.DLL_NAME})");
            if (!DetexHelper.LoadDll(dllPath) || !File.Exists(dllPath))
                throw new InvalidOperationException("Failed to extract embedded Detex DLL.");
            Console.WriteLine($"[OK] Detex DLL: {dllPath}");
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

    private sealed record ParsedArgs(
        string PaksDir,
        string AesKey,
        string ManifestPath,
        string OutDir,
        string UsmapPath,
        string GameVersion,
        bool NoMeta);

    private static ParsedArgs ParseArgs(string[] args)
    {
        string? paks = null, key = null, manifest = null, outDir = null, usmap = null;
        string version = "UE5_6";
        bool noMeta = false;

        for (int i = 0; i < args.Length; i++)
        {
            string Need(string flag)
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{flag} requires a value");
                return args[++i];
            }
            switch (args[i])
            {
                case "--paks-dir": paks = Need("--paks-dir"); break;
                case "--aes-key":  key  = Need("--aes-key");  break;
                case "--manifest": manifest = Need("--manifest"); break;
                case "--out-dir":  outDir = Need("--out-dir"); break;
                case "--usmap":    usmap  = Need("--usmap"); break;
                case "--game-version": version = Need("--game-version"); break;
                case "--no-meta":  noMeta = true; break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(paks))     throw new ArgumentException("--paks-dir is required");
        if (string.IsNullOrWhiteSpace(key))      throw new ArgumentException("--aes-key is required");
        if (string.IsNullOrWhiteSpace(manifest)) throw new ArgumentException("--manifest is required");
        if (string.IsNullOrWhiteSpace(outDir))   throw new ArgumentException("--out-dir is required");
        if (string.IsNullOrWhiteSpace(usmap))    throw new ArgumentException("--usmap is required");
        if (!Directory.Exists(paks))             throw new ArgumentException($"--paks-dir does not exist: {paks}");
        if (!File.Exists(usmap))                 throw new ArgumentException($"--usmap file not found: {usmap}");

        return new ParsedArgs(paks, key, manifest, outDir, usmap, version, noMeta);
    }
}
