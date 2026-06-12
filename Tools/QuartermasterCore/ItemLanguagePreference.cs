using System;
using System.IO;
using System.Text.Json;

namespace Windrose.Quartermaster.Core
{
    // The Configurator's item display-name language: a language code from the icon
    // metadata (e.g. "en", "de"), chosen in the Web GUI header. Persisted in the data
    // root (next to game-install.json) so the catalog generation in GameDeployer uses
    // the same language the GUI shows - even from paths with no browser attached
    // (mod delete, CLI regeneration).
    public static class ItemLanguagePreference
    {
        const string FileName = "item-language.json";

        // Never throws: any IO/parse error means "no preference set" - callers fall
        // back to English.
        public static string Load(string dataRoot)
        {
            if (string.IsNullOrEmpty(dataRoot)) return null;
            var path = Path.Combine(dataRoot, FileName);
            if (!File.Exists(path)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                if (!doc.RootElement.TryGetProperty("language", out var el)
                    || el.ValueKind != JsonValueKind.String) return null;
                var s = el.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }
            catch
            {
                return null;
            }
        }

        // Empty/null language deletes the file (= back to the English default).
        public static void Save(string dataRoot, string language)
        {
            if (string.IsNullOrEmpty(dataRoot))
                throw new ArgumentNullException(nameof(dataRoot));
            var path = Path.Combine(dataRoot, FileName);
            if (string.IsNullOrWhiteSpace(language))
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                return;
            }
            Directory.CreateDirectory(dataRoot);
            // Hand-built JSON (JsonEncodedText escapes the value): keeps this usable from
            // hosts with reflection-based serialization disabled.
            File.WriteAllText(path,
                "{\n  \"language\": \"" + JsonEncodedText.Encode(language.Trim()) + "\"\n}\n");
        }
    }
}
