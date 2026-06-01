using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Windrose.Quartermaster.Core
{
    // Shared JSON helpers for the R5BusinessRules recipe/data patchers: tab+CRLF
    // serialization matching the vanilla on-disk format, order-sensitive deep
    // compare, and the trade-field setters.
    static class R5Json
    {
        // Synthesized recipes: both fields always provided, written as a single-entry array.
        public static void SetTradeField(JsonObject root, string key, string itemPath, int count)
        {
            root[key] = new JsonArray(
                new JsonObject
                {
                    ["Item"] = itemPath,
                    ["Count"] = count,
                });
        }

        // Sparse update for edited recipes: null itemPath / null count leaves that leaf alone.
        public static void UpdateTradeField(JsonObject root, string key, string itemPath, int? count)
        {
            if (!(root[key] is JsonArray arr) || arr.Count == 0)
            {
                if (string.IsNullOrEmpty(itemPath) || !count.HasValue) return;
                root[key] = new JsonArray(
                    new JsonObject
                    {
                        ["Item"] = itemPath,
                        ["Count"] = count.Value,
                    });
                return;
            }
            if (!(arr[0] is JsonObject obj)) return;
            if (!string.IsNullOrEmpty(itemPath)) obj["Item"] = itemPath;
            if (count.HasValue) obj["Count"] = count.Value;
        }

        public static string AssetPathToBasename(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return assetPath;
            var s = assetPath;
            var dot = s.LastIndexOf('.');
            var slash = s.LastIndexOf('/');
            var cut = Math.Max(dot, slash);
            return cut >= 0 && cut < s.Length - 1 ? s.Substring(cut + 1) : s;
        }

        // Order-sensitive: object keys must match in the same order too.
        public static bool DeepEquals(JsonNode a, JsonNode b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            if (a is JsonObject oa && b is JsonObject ob)
            {
                if (oa.Count != ob.Count) return false;
                using var ea = oa.GetEnumerator();
                using var eb = ob.GetEnumerator();
                while (ea.MoveNext() && eb.MoveNext())
                {
                    if (ea.Current.Key != eb.Current.Key) return false;
                    if (!DeepEquals(ea.Current.Value, eb.Current.Value)) return false;
                }
                return true;
            }
            if (a is JsonArray aa && b is JsonArray ab)
            {
                if (aa.Count != ab.Count) return false;
                for (int i = 0; i < aa.Count; i++)
                {
                    if (!DeepEquals(aa[i], ab[i])) return false;
                }
                return true;
            }
            if (a is JsonValue va && b is JsonValue vb)
            {
                return va.ToJsonString() == vb.ToJsonString();
            }
            return false;
        }

        public static byte[] SerializeWithTabsAndCrlf(JsonObject root)
        {
            using var ms = new MemoryStream();
            var writerOptions = new JsonWriterOptions
            {
                Indented = true,
                IndentCharacter = '\t',
                IndentSize = 1,
                NewLine = "\r\n",
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            using (var writer = new Utf8JsonWriter(ms, writerOptions))
            {
                root.WriteTo(writer);
            }
            ms.WriteByte((byte)'\r');
            ms.WriteByte((byte)'\n');
            return ms.ToArray();
        }
    }
}
