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
    public sealed class IconBakerPatcher
    {
        public const string CustomAssetStemPrefix = "T_QmCustomIcon_";
        public const string CustomFolderRelative  = "R5/Content/UI/Icons/Items/Custom";
        public const string CustomPackageFolder   = "/Game/UI/Icons/Items/Custom";

        public const string TemplateAssetStem     = "T_ItemIcon_Loot_T02_CoinPiastre_01";
        public const string TemplatePackagePath   = "/Game/UI/Icons/Items/New/T_ItemIcon_Loot_T02_CoinPiastre_01";
        public const string TemplateRelativeUasset = "R5/Content/UI/Icons/Items/New/T_ItemIcon_Loot_T02_CoinPiastre_01.uasset";
        public const string TemplateRelativeUexp   = "R5/Content/UI/Icons/Items/New/T_ItemIcon_Loot_T02_CoinPiastre_01.uexp";

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

        // Mips are spliced in place; the uexp length must stay exactly this so the trailing BulkData/property bytes remain valid.
        const int TemplateUexpSize = 87677;

        public Action<string> Log;

        public sealed class BakeJob
        {
            public string ItemId;
            public string PngPath;
        }

        public sealed class BakeResult
        {
            public string ItemId;
            public string AssetPath;
            public string UexpPath;
            public string ItemTextureRef;
            public int    PngBytesIn;
            public int    UexpBytesOut;
        }

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

        public void RemoveTemplateFromStaging(string stagingDir)
        {
            if (string.IsNullOrEmpty(stagingDir)) return;
            var templateUasset = Path.Combine(stagingDir,
                TemplateRelativeUasset.Replace('/', Path.DirectorySeparatorChar));
            var templateUexp = Path.Combine(stagingDir,
                TemplateRelativeUexp.Replace('/', Path.DirectorySeparatorChar));
            try { if (File.Exists(templateUasset)) File.Delete(templateUasset); } catch { }
            try { if (File.Exists(templateUexp))   File.Delete(templateUexp);   } catch { }
        }

        public static string AssetStemFor(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) throw new ArgumentNullException("itemId");
            return CustomAssetStemPrefix + itemId;
        }

        public static string AssetPackagePathFor(string itemId)
        {
            return CustomPackageFolder + "/" + AssetStemFor(itemId);
        }

        public static string ItemTextureRefFor(string itemId)
        {
            var stem = AssetStemFor(itemId);
            return CustomPackageFolder + "/" + stem + "." + stem;
        }

        BakeResult BakeOne(string templateUasset, string templateUexp,
            string customAbsDir, BakeJob job)
        {
            LogLine("Bake " + job.ItemId + " <- " + job.PngPath);
            var assetStem = AssetStemFor(job.ItemId);
            var newPackagePath = AssetPackagePathFor(job.ItemId);

            byte[] pngBytes = File.ReadAllBytes(job.PngPath);
            byte[][] bc7Mips;
            using (var img = Image.Load<Rgba32>(pngBytes))
            {
                if (img.Width != 256 || img.Height != 256)
                {
                    img.Mutate(ctx => ctx.Resize(256, 256, KnownResamplers.Lanczos3));
                }
                img.Mutate(ctx => ctx.GaussianSharpen(0.6f));
                bc7Mips = EncodeMipChain(img);
            }

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

            // Rename FName slots so the engine indexes a new asset rather than overriding the template.
            var asset = new UAsset(templateUasset, UAssetIo.Ue);
            var nameMap = asset.GetNameMapIndexList();

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

            var outUasset = Path.Combine(customAbsDir, assetStem + ".uasset");
            var outUexp   = Path.Combine(customAbsDir, assetStem + ".uexp");
            asset.Write(outUasset);
            // Must run after Write: it emits its own .uexp from template bulk-data; overwrite it with the spliced pixels.
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

        static byte[][] EncodeMipChain(Image<Rgba32> source)
        {
            var enc = new BcEncoder
            {
                OutputOptions =
                {
                    Format = CompressionFormat.Bc7,
                    Quality = CompressionQuality.Fast,
                    GenerateMipMaps = false,
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
