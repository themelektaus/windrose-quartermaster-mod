using System;
using System.Collections.Generic;
using System.IO;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Microsoft.Toolkit.HighPerformance;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace Windrose.Quartermaster.Core
{
    // Bakes user-supplied PNGs into UE5 Texture2D legacy assets that ship
    // alongside the vanilla item icons. The output is a pair of
    //   <stagingDir>/R5/Content/UI/Icons/Items/Custom/T_QmCustomIcon_<id>.uasset
    //   <stagingDir>/R5/Content/UI/Icons/Items/Custom/T_QmCustomIcon_<id>.uexp
    // for every job, retoc to-zen then packs the whole staging tree as
    // part of the IoStore composite.
    //
    // Design (see Spike 1/2/3 in repo history for the empirical proof):
    //   1. Use vanilla T_ItemIcon_Loot_T02_CoinPiastre_01 as the
    //      template - a 256x256 BC7 9-mip Texture2D.
    //   2. UAssetAPI rewrites the FName table so the new uasset is a
    //      DIFFERENT asset (T_QmCustomIcon_<id> at .../Custom/<id>),
    //      not an override of the vanilla Piastre. The Piastre's icon
    //      stays untouched for any code path that still references it.
    //   3. The uexp's mip-chain bytes get spliced in place: BCnEncoder.Net
    //      produces 9 BC7 blocks of EXACT vanilla sizes
    //      (65536/16384/4096/1024/256/64/16/16/16 bytes), copied at the
    //      same offsets the vanilla file uses (117 / 65669 / ... / 87637).
    //      This keeps the uexp size identical to vanilla (87,677 bytes) so
    //      every other byte in the file (BulkData headers, property stream
    //      tail) stays valid.
    //
    // Coupling with ItemCreatorPatcher:
    //   ItemCreatorPatcher always sets InventoryItemUIData.ItemTexture to
    //   ItemTextureRefFor(item.Id) when item.IconPath is set. The baker
    //   then writes the actual asset at the matching path. They share the
    //   AssetStemFor / AssetPackagePathFor / ItemTextureRefFor helpers so
    //   the two halves stay in lockstep.
    public sealed class IconBakerPatcher
    {
        // Filename stems / virtual paths inside the legacy staging tree.
        // The "T_" prefix matches UE convention for Texture2D assets;
        // "QmCustomIcon_" is our namespace so vanilla naming clashes are
        // impossible.
        public const string CustomAssetStemPrefix = "T_QmCustomIcon_";
        public const string CustomFolderRelative  = "R5/Content/UI/Icons/Items/Custom";
        public const string CustomPackageFolder   = "/Game/UI/Icons/Items/Custom";

        // Vanilla template that retoc to-legacy must extract first. The
        // BuildPipeline composite source uses this stem as its --filter.
        public const string TemplateAssetStem     = "T_ItemIcon_Loot_T02_CoinPiastre_01";
        public const string TemplatePackagePath   = "/Game/UI/Icons/Items/New/T_ItemIcon_Loot_T02_CoinPiastre_01";
        public const string TemplateRelativeUasset = "R5/Content/UI/Icons/Items/New/T_ItemIcon_Loot_T02_CoinPiastre_01.uasset";
        public const string TemplateRelativeUexp   = "R5/Content/UI/Icons/Items/New/T_ItemIcon_Loot_T02_CoinPiastre_01.uexp";

        // Mip layout of the vanilla template. Every bake validates the
        // generated BC7 against these expected sizes before splicing, so
        // a mismatch fails fast with a clear message instead of producing
        // a broken asset.
        //
        // Verified against UAssetAPI parse of the vanilla uexp (see
        // Spike 2 probe output): the chain is 9 mips, 256x256 down to
        // 1x1, BC7 (16 bytes per 4x4 block, minimum 16 bytes per mip).
        static readonly (int Offset, int Length, int Width, int Height)[] MipLayout =
        {
            (   117, 65536, 256, 256),
            ( 65669, 16384, 128, 128),
            ( 82069,  4096,  64,  64),
            ( 86181,  1024,  32,  32),
            ( 87221,   256,  16,  16),
            ( 87493,    64,   8,   8),
            ( 87573,    16,   4,   4),
            ( 87605,    16,   2,   2),
            ( 87637,    16,   1,   1),
        };

        // Total uexp size (vanilla template). After the bake the file
        // MUST still be exactly this many bytes - we splice in place
        // with no length change, and BC7 mip totals match the vanilla
        // chain to the byte.
        const int TemplateUexpSize = 87677;

        public Action<string> Log;

        // Single-item bake job: where the user PNG lives, which custom
        // item id we're baking for. The patcher composes output paths
        // from these via AssetStemFor / CustomFolderRelative.
        public sealed class BakeJob
        {
            public string ItemId;
            public string PngPath;
        }

        public sealed class BakeResult
        {
            public string ItemId;
            public string AssetPath;       // absolute path to the .uasset that landed in staging
            public string UexpPath;
            public string ItemTextureRef;  // "/Game/UI/Icons/Items/Custom/T_QmCustomIcon_<id>.T_QmCustomIcon_<id>"
            public int    PngBytesIn;
            public int    UexpBytesOut;
        }

        // Bakes every job into stagingDir/CustomFolderRelative/. Throws on
        // the first failure so the build pipeline aborts cleanly. The
        // template files are NOT removed by this method; the caller does
        // that explicitly so the cleanup intent stays visible at the
        // call site.
        public List<BakeResult> Bake(string stagingDir, IEnumerable<BakeJob> jobs)
        {
            if (string.IsNullOrEmpty(stagingDir)) throw new ArgumentNullException("stagingDir");
            if (jobs == null) throw new ArgumentNullException("jobs");

            var templateUasset = Path.Combine(stagingDir,
                TemplateRelativeUasset.Replace('/', Path.DirectorySeparatorChar));
            var templateUexp = Path.Combine(stagingDir,
                TemplateRelativeUexp.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(templateUasset))
                throw new FileNotFoundException(
                    "Vanilla icon template not found in staging: " + templateUasset
                    + " - make sure the IoStore composite source uses --filter "
                    + TemplateAssetStem);
            if (!File.Exists(templateUexp))
                throw new FileNotFoundException(
                    "Vanilla icon template uexp not found in staging: " + templateUexp);

            var customAbsDir = Path.Combine(stagingDir,
                CustomFolderRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(customAbsDir);

            var results = new List<BakeResult>();
            foreach (var job in jobs)
            {
                if (job == null) continue;
                if (string.IsNullOrEmpty(job.ItemId))
                    throw new ArgumentException("BakeJob.ItemId is required");
                if (!IsSafeItemId(job.ItemId))
                    throw new ArgumentException(
                        "BakeJob.ItemId '" + job.ItemId + "' contains illegal characters "
                        + "(allowed: A-Z a-z 0-9 _)");
                if (string.IsNullOrEmpty(job.PngPath))
                    throw new ArgumentException("BakeJob.PngPath is required (item '" + job.ItemId + "')");
                if (!File.Exists(job.PngPath))
                    throw new FileNotFoundException(
                        "Source PNG not found for custom item '" + job.ItemId + "': " + job.PngPath);

                results.Add(BakeOne(templateUasset, templateUexp, customAbsDir, job));
            }
            return results;
        }

        // Removes the vanilla template files from the staging tree after
        // baking. Idempotent (no-op if already gone). Called by the
        // composite caller AFTER all jobs are baked so the to-zen step
        // doesn't repackage the (unmodified) Piastre template into the
        // mod pak.
        public void RemoveTemplateFromStaging(string stagingDir)
        {
            if (string.IsNullOrEmpty(stagingDir)) return;
            var templateUasset = Path.Combine(stagingDir,
                TemplateRelativeUasset.Replace('/', Path.DirectorySeparatorChar));
            var templateUexp = Path.Combine(stagingDir,
                TemplateRelativeUexp.Replace('/', Path.DirectorySeparatorChar));
            try { if (File.Exists(templateUasset)) File.Delete(templateUasset); } catch { /* best-effort */ }
            try { if (File.Exists(templateUexp))   File.Delete(templateUexp);   } catch { /* best-effort */ }
        }

        // Public asset-stem helper so ItemCreatorPatcher can derive the
        // matching ItemTexture reference without duplicating the naming
        // rule. "<id>" is the CustomItem.Id (filename-safe alnum +
        // underscore).
        public static string AssetStemFor(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) throw new ArgumentNullException("itemId");
            return CustomAssetStemPrefix + itemId;
        }

        // The "/Game/..." package-path that ends up in NameMap[3] and
        // FolderName when we rename the cloned uasset.
        public static string AssetPackagePathFor(string itemId)
        {
            return CustomPackageFolder + "/" + AssetStemFor(itemId);
        }

        // The full ItemTexture reference (PackagePath.AssetName) the
        // synthesized R5BLInventoryItem JSON puts in
        // InventoryItemUIData.ItemTexture so the engine looks up the
        // right texture at runtime.
        public static string ItemTextureRefFor(string itemId)
        {
            var stem = AssetStemFor(itemId);
            return CustomPackageFolder + "/" + stem + "." + stem;
        }

        // ---- internals --------------------------------------------------

        BakeResult BakeOne(string templateUasset, string templateUexp,
            string customAbsDir, BakeJob job)
        {
            LogLine("Bake " + job.ItemId + " <- " + job.PngPath);
            var assetStem = AssetStemFor(job.ItemId);
            var newPackagePath = AssetPackagePathFor(job.ItemId);

            // 1. Decode + resize PNG into a 256x256 RGBA32 source. We
            //    accept any aspect ratio / size and squash to a square -
            //    Phase 1 UX is "auto-resize, no questions asked".
            //
            //    Sharpness budget:
            //      - Lanczos3 instead of the default Bicubic for the
            //        resize itself: classic gold-standard sharp downscaler.
            //        Visible difference at 1024->256 over Bicubic.
            //      - Light GaussianSharpen (sigma=0.6) on the 256x256
            //        source AFTER resize. Counteracts the slight softening
            //        BC7 compression adds when the engine samples the
            //        tooltip thumb. Sigma is intentionally subtle - higher
            //        values produce visible halo rings on hard edges
            //        (logos, text on icons).
            byte[] pngBytes = File.ReadAllBytes(job.PngPath);
            byte[][] bc7Mips;
            using (var img = Image.Load<Rgba32>(pngBytes))
            {
                if (img.Width != 256 || img.Height != 256)
                {
                    // Pad-with-aspect would be friendlier visually for
                    // non-square inputs, but item icons are always square
                    // in the tooltip frame anyway, so a straight Resize
                    // matches what the user sees in the preview thumb.
                    img.Mutate(ctx => ctx.Resize(256, 256, KnownResamplers.Lanczos3));
                }
                img.Mutate(ctx => ctx.GaussianSharpen(0.6f));
                bc7Mips = EncodeMipChain(img);
            }

            // Sanity-check sizes against the template layout BEFORE we
            // touch the uexp. A wrong-size mip would corrupt later
            // bulk-data headers when spliced.
            for (int i = 0; i < MipLayout.Length; i++)
            {
                if (bc7Mips[i].Length != MipLayout[i].Length)
                {
                    throw new InvalidOperationException(
                        "Mip " + i + " (" + MipLayout[i].Width + "x" + MipLayout[i].Height
                        + ") encoded to " + bc7Mips[i].Length + " bytes; expected "
                        + MipLayout[i].Length + " (BC7 block-size mismatch)");
                }
            }

            // 2. Splice mip chain into a fresh copy of the template uexp.
            var uexpBytes = File.ReadAllBytes(templateUexp);
            if (uexpBytes.Length != TemplateUexpSize)
            {
                throw new InvalidOperationException(
                    "Template uexp size mismatch: got " + uexpBytes.Length
                    + ", expected " + TemplateUexpSize
                    + " - the vanilla Piastre asset has changed shape, regen MipLayout.");
            }
            for (int i = 0; i < MipLayout.Length; i++)
            {
                Buffer.BlockCopy(bc7Mips[i], 0, uexpBytes, MipLayout[i].Offset, MipLayout[i].Length);
            }

            // 3. Load the template uasset, rename its FName slots so the
            //    engine sees a *new* asset instead of an override of the
            //    vanilla Piastre. UAssetAPI re-serializes the file so
            //    offset / length recomputation is automatic - we only
            //    swap the strings.
            var asset = new UAsset(templateUasset, EngineVersion.VER_UE5_6);
            var nameMap = asset.GetNameMapIndexList();

            // Defensive: bail if the name map doesn't look like the vanilla
            // Piastre layout. A mismatch means the template moved (game
            // patch?) and we'd otherwise scribble garbage into the wrong
            // FName slot.
            if (nameMap.Count < 4)
                throw new InvalidOperationException(
                    "Template name map too short (" + nameMap.Count + " entries, expected >=4)");
            if (nameMap[2].Value != TemplateAssetStem)
                throw new InvalidOperationException(
                    "Template NameMap[2] expected '" + TemplateAssetStem + "', got '"
                    + nameMap[2].Value + "' - vanilla template has shifted, baker needs an audit.");
            if (nameMap[3].Value != TemplatePackagePath)
                throw new InvalidOperationException(
                    "Template NameMap[3] expected '" + TemplatePackagePath + "', got '"
                    + nameMap[3].Value + "' - vanilla template has shifted, baker needs an audit.");

            asset.SetNameReference(2, FString.FromString(assetStem));
            asset.SetNameReference(3, FString.FromString(newPackagePath));
            asset.FolderName = FString.FromString(newPackagePath);

            // 4. Write asset (UAssetAPI re-emits .uasset + a sibling .uexp
            //    populated from the raw export bulk-data bytes). We then
            //    overwrite the .uexp with our spliced version so the new
            //    asset reads our pixels, not the template's.
            var outUasset = Path.Combine(customAbsDir, assetStem + ".uasset");
            var outUexp   = Path.Combine(customAbsDir, assetStem + ".uexp");
            asset.Write(outUasset);
            File.WriteAllBytes(outUexp, uexpBytes);

            return new BakeResult
            {
                ItemId = job.ItemId,
                AssetPath = outUasset,
                UexpPath = outUexp,
                ItemTextureRef = ItemTextureRefFor(job.ItemId),
                PngBytesIn = pngBytes.Length,
                UexpBytesOut = uexpBytes.Length,
            };
        }

        // Resize source to each mip resolution and BC7-encode. Quality is
        // CompressionQuality.Fast - the tooltip thumb is small enough that
        // the slower Balanced/BestQuality modes don't show a visible
        // benefit, and Fast keeps a 50-item profile build under a second
        // of icon-bake time.
        //
        // Resampler: Lanczos3 across the whole mip chain. Sharper than the
        // ImageSharp Bicubic default on every step; matters most at 256
        // (the tooltip thumb size) but staying consistent across the chain
        // avoids per-mip aliasing surprises if the engine ever samples a
        // smaller mip for a hotbar slot.
        static byte[][] EncodeMipChain(Image<Rgba32> source)
        {
            var enc = new BcEncoder
            {
                OutputOptions =
                {
                    Format = CompressionFormat.Bc7,
                    Quality = CompressionQuality.Fast,
                    GenerateMipMaps = false, // we generate per mip ourselves
                },
            };

            var sizes = new (int W, int H)[]
            {
                (256, 256), (128, 128), (64, 64), (32, 32),
                (16, 16),   (8, 8),     (4, 4),   (2, 2), (1, 1),
            };
            var mips = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
            {
                var (w, h) = sizes[i];
                using var resized = source.Clone(ctx => ctx.Resize(w, h, KnownResamplers.Lanczos3));
                var pixels = new ColorRgba32[w * h];
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var p = resized[x, y];
                    pixels[y * w + x] = new ColorRgba32(p.R, p.G, p.B, p.A);
                }
                var mem2d = new Memory2D<ColorRgba32>(pixels, h, w);
                var encoded = enc.EncodeToRawBytes(mem2d);
                mips[i] = encoded[0];
            }
            return mips;
        }

        static bool IsSafeItemId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            foreach (var ch in id)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == '_')) return false;
            }
            return true;
        }

        void LogLine(string msg)
        {
            if (Log != null) Log("[IconBaker] " + msg);
        }
    }
}
