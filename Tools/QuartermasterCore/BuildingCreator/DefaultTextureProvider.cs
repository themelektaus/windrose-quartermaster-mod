using System;
using System.Collections.Generic;
using System.IO;

namespace Windrose.Quartermaster.Core.BuildingCreator
{
    // Shipped VT default textures the Building Creator's MI clones can
    // reference for the Albedo / Normal / MTRM params (and any other
    // texture slot the user wants to leave at "shared default" instead
    // of picking a custom).
    //
    // Lives at <ModRoot>/Tools/Templates/DefaultTextures/ as plain
    // .uasset + .uexp + .ubulk triplets cooked once from a UE5.6
    // editor project (4x4 RGBA pages, VT-enabled). The build pipeline
    // copies them into the per-build staging tree once (regardless of
    // which buildings reference them) so the cloned MIs always resolve
    // these stems under the mod's output namespace. The frontend asks
    // the backend for the stem list via /api/buildings/default-textures
    // so the per-slot texture dropdowns can list them as an "always
    // available" group on top of whatever the user-cooked folder
    // contributes.
    //
    // Why ship them and not let the user cook their own:
    //   - User would have to recreate identical 4x4 pages every project,
    //     with the same exact compression + sRGB + VT flags - very easy
    //     to get wrong, and a wrong default kills the material silently
    //     (missing-texture grey).
    //   - "Hey what should the Normal / MTRM look like for a building
    //     I just want to be flat" is a recurring question; embedding
    //     a sane default removes the friction entirely.
    //   - The cooked bytes are tiny (~3 KB per texture), so shipping
    //     three of them adds noise-level overhead to the published EXE.
    public static class DefaultTextureProvider
    {
        // The canonical stem list. Order is the UI dropdown order
        // (Albedo-ish first, then Normal, then MTRM/AO/Roughness).
        // The frontend's "Default textures" optgroup renders them in
        // this exact order so the user has a stable visual anchor.
        public static readonly string[] Stems = new[]
        {
            "T_White",
            "T_NormalFlat",
            "T_MTRMDefault",
            "T_MTRMGlass",
            "T_MTRMZero",
            "T_MTRMOne"
        };

        // File-extension set per stem we copy. Mirrors what the UE5
        // editor produced when it cooked the triplets - .uasset is
        // the package header, .uexp the body, .ubulk the bulk-data
        // sidecar UAssetAPI reads from the same directory automatically.
        static readonly string[] Extensions = new[] { ".uasset", ".uexp", ".ubulk" };

        // Copy every default-texture triplet from the shipped Tools/
        // folder into stagingItemsDir. Skip-if-exists so a user-cooked
        // override (placed under their own CookedFolderPath with the
        // same stem) wins - the per-building stage pass runs before
        // this for the relevant cooked folders, and File.Exists below
        // catches the prior copy. Returns the number of files actually
        // copied; callers can fold the count into their per-build log.
        //
        // Missing-file behaviour is non-fatal: each missing file is
        // reported via log so a fresh checkout that hasn't fetched
        // the LFS/binary triplets yet doesn't silently produce a
        // pak with no defaults staged. Buildings that reference the
        // missing stem will surface a broken-texture in-game; the log
        // line gives the user enough context to pull the file in.
        public static int StageInto(WindrosePaths paths, string stagingItemsDir, string usmapPath, Action<string> log)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (string.IsNullOrEmpty(stagingItemsDir)) throw new ArgumentNullException("stagingItemsDir");
            Directory.CreateDirectory(stagingItemsDir);

            var srcDir = paths.BuildingDefaultTexturesDir;
            if (!Directory.Exists(srcDir))
            {
                if (log != null) log("  warn: default-textures folder missing: " + srcDir
                    + " - buildings that reference " + string.Join(" / ", Stems) + " may render broken textures");
                return 0;
            }

            int copied = 0;
            int skipped = 0;
            int missing = 0;
            // Per-stem flag: did we just copy a fresh .uasset that needs
            // FolderName normalization? .uexp/.ubulk siblings don't need
            // normalization themselves but the .uasset header drives the
            // package-resolution check, so we run the rewrite once per stem.
            var freshStems = new List<string>();
            foreach (var stem in Stems)
            {
                bool stemFreshlyCopied = false;
                foreach (var ext in Extensions)
                {
                    var srcFile = Path.Combine(srcDir, stem + ext);
                    if (!File.Exists(srcFile))
                    {
                        // .ubulk is sometimes absent when the source
                        // texture has no bulk-data (small inline cooks).
                        // The other two (.uasset, .uexp) are required;
                        // we surface anything missing so the user knows.
                        if (string.Equals(ext, ".ubulk", StringComparison.OrdinalIgnoreCase))
                        {
                            // Not noisy - skip silently. UAssetAPI tolerates
                            // missing .ubulk for textures that don't use it.
                            continue;
                        }
                        if (log != null) log("  warn: default-texture file missing: " + srcFile);
                        missing++;
                        continue;
                    }

                    var dstFile = Path.Combine(stagingItemsDir, stem + ext);
                    if (File.Exists(dstFile))
                    {
                        // Pre-existing staged file wins (user-cooked
                        // override with the same stem). Mirrors the
                        // skip-if-exists pattern the per-building
                        // stage pass uses for shared cooked folders.
                        skipped++;
                        continue;
                    }
                    File.Copy(srcFile, dstFile, overwrite: false);
                    copied++;
                    if (string.Equals(ext, ".uasset", StringComparison.OrdinalIgnoreCase))
                        stemFreshlyCopied = true;
                }
                if (stemFreshlyCopied) freshStems.Add(stem);
            }

            // Normalize FolderName on the freshly staged .uasset files so
            // their internal self-reference matches the top-level staging
            // path (mod-pak output namespace + <stem>). The default
            // textures were cooked under /Content/Quartermaster/, so without this
            // pass the iostore loader silently fails to resolve them at
            // runtime and the MI's texture lookup falls back to the
            // vanilla parent's texture - which is exactly why the user
            // sees the original vanilla material despite picking T_White.
            int normalized = 0;
            int normalizeFailures = 0;
            if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            {
                foreach (var stem in freshStems)
                {
                    var stagedAsset = Path.Combine(stagingItemsDir, stem + ".uasset");
                    if (BuildingPatcher.NormalizeAssetSelfPath(stagedAsset, usmapPath, log, out var err))
                        normalized++;
                    else if (err != null)
                        normalizeFailures++;
                }
            }
            else
            {
                if (log != null) log("  warn: default-texture FolderName normalize SKIPPED (usmapPath missing or not found) - "
                    + string.Join(" / ", Stems) + " may not load in-game; "
                    + "MIs will fall back to the vanilla parent's textures");
            }

            if (log != null)
            {
                if (copied + skipped + missing == 0)
                {
                    log("  (default textures: nothing staged)");
                }
                else
                {
                    log("  default textures: " + copied + " copied"
                        + (skipped > 0 ? ", " + skipped + " pre-existing (user-cooked override)" : "")
                        + (missing > 0 ? ", " + missing + " file(s) MISSING from " + srcDir : "")
                        + (normalized > 0 ? ", " + normalized + " self-path normalized to " + WindrosePaths.ModItemsPackagePath + "<stem>" : "")
                        + (normalizeFailures > 0 ? ", " + normalizeFailures + " normalize FAILURE(s)" : "")
                        + " - " + string.Join(", ", Stems));
                }
            }
            return copied;
        }

        // Returns the list of stems for the frontend dropdown. Keeps
        // the canonical order from the Stems array; the caller may
        // augment with additional stems but should keep these on top
        // so the UX is consistent.
        public static IReadOnlyList<string> GetStems() => Stems;
    }
}
