using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Windrose.Quartermaster.Core
{
    // Edits the per-user UE config that controls the global UI scale:
    //   %LOCALAPPDATA%\R5\Saved\Config\Windows\Engine.ini
    //   [/Script/Engine.UserInterfaceSettings]
    //   ApplicationScale=<float>
    //
    // This is a direct local-config edit - not a pak, not profile-bound: the
    // value is global to the install, so we read / write the live file rather
    // than persist it on a profile. UE rewrites this file on exit, so the game
    // must be closed for an edit to stick.
    //
    // UE also rewrites Engine.ini on launch and would revert ApplicationScale,
    // so after writing we mark the file read-only to make the value stick. We
    // clear the read-only flag before every write (otherwise the write itself
    // would throw) so re-edits from this tool keep working.
    //
    // The writer is surgical: it preserves every other line, the section
    // ordering, and the file's newline style. If the section or key is absent
    // it adds just that section / key; the rest of the file is untouched.
    public static class UiScalePatcher
    {
        public const double VanillaScale = 1.0;
        public const double MinScale = 0.5;
        public const double MaxScale = 1.1;

        const string SectionName = "/Script/Engine.UserInterfaceSettings";
        const string KeyName = "ApplicationScale";

        public sealed class UiScaleResult
        {
            public bool Written;
            public double Scale;
            public bool FileExisted;
            public bool SectionExisted;
            public bool KeyExisted;
            public bool ReadOnlySet;
            public string Path;
        }

        public static string EngineIniPath()
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(local)) return null;
            return Path.Combine(local, "R5", "Saved", "Config", "Windows", "Engine.ini");
        }

        public static double ClampScale(double scale)
        {
            if (double.IsNaN(scale)) return VanillaScale;
            if (scale < MinScale) return MinScale;
            if (scale > MaxScale) return MaxScale;
            return scale;
        }

        // "0.0##" keeps at least one decimal (UE expects a float literal) and
        // trims trailing zeros: 1.0 -> "1.0", 0.5 -> "0.5", 0.85 -> "0.85".
        static string Format(double scale)
            => scale.ToString("0.0##", CultureInfo.InvariantCulture);

        // Current ApplicationScale from the live Engine.ini, or null when the
        // file / section / key isn't present (caller treats null as vanilla).
        public static double? ReadCurrentScale()
        {
            var path = EngineIniPath();
            if (path == null || !File.Exists(path)) return null;

            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch { return null; }

            bool inSection = false;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length >= 2 && line[0] == '['
                    && line[line.Length - 1] == ']')
                {
                    var name = line.Substring(1, line.Length - 2).Trim();
                    inSection = string.Equals(name, SectionName, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                if (!string.Equals(key, KeyName, StringComparison.OrdinalIgnoreCase)) continue;
                var val = line.Substring(eq + 1).Trim();
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return d;
                return null;
            }
            return null;
        }

        public static UiScaleResult Apply(double scale)
        {
            var path = EngineIniPath();
            if (path == null)
                throw new InvalidOperationException(
                    "LOCALAPPDATA is not set; cannot locate Engine.ini.");

            scale = ClampScale(scale);
            var result = new UiScaleResult { Scale = scale, Path = path };

            // Default to CRLF (Windows UE config); preserve an existing style.
            string newline = "\r\n";
            List<string> lines;
            if (File.Exists(path))
            {
                result.FileExisted = true;
                var text = File.ReadAllText(path);
                if (text.IndexOf("\r\n", StringComparison.Ordinal) < 0
                    && text.IndexOf('\n') >= 0)
                    newline = "\n";
                lines = SplitLines(text);
            }
            else
            {
                lines = new List<string>();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }

            var keyLine = KeyName + "=" + Format(scale);

            // Locate our section header and the next header after it (or EOF).
            int sectionStart = -1;
            int sectionEnd = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.Length < 2 || t[0] != '[' || t[t.Length - 1] != ']') continue;
                var name = t.Substring(1, t.Length - 2).Trim();
                if (sectionStart < 0)
                {
                    if (string.Equals(name, SectionName, StringComparison.OrdinalIgnoreCase))
                        sectionStart = i;
                }
                else
                {
                    sectionEnd = i;
                    break;
                }
            }

            if (sectionStart >= 0)
            {
                result.SectionExisted = true;
                int keyIdx = -1;
                for (int i = sectionStart + 1; i < sectionEnd; i++)
                {
                    var t = lines[i].Trim();
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = t.Substring(0, eq).Trim();
                    if (string.Equals(key, KeyName, StringComparison.OrdinalIgnoreCase))
                    { keyIdx = i; break; }
                }
                if (keyIdx >= 0)
                {
                    result.KeyExisted = true;
                    lines[keyIdx] = keyLine;
                }
                else
                {
                    lines.Insert(sectionStart + 1, keyLine);
                }
            }
            else
            {
                // Append the section block; separate from prior content.
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length != 0)
                    lines.Add(string.Empty);
                lines.Add("[" + SectionName + "]");
                lines.Add(keyLine);
            }

            // Clear an existing read-only flag (set by a prior Apply, or the
            // user) so the write below doesn't throw. Best effort.
            ClearReadOnly(path);

            // Join + single trailing newline (idempotent against SplitLines).
            File.WriteAllText(path, string.Join(newline, lines) + newline,
                new UTF8Encoding(false));
            result.Written = true;

            // Lock the file so the game can't rewrite ApplicationScale on its
            // next launch. Best effort - the value is written regardless.
            result.ReadOnlySet = SetReadOnly(path);
            return result;
        }

        // True when the live Engine.ini exists and carries the read-only flag.
        public static bool IsReadOnly()
        {
            var path = EngineIniPath();
            if (path == null || !File.Exists(path)) return false;
            try { return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0; }
            catch { return false; }
        }

        static void ClearReadOnly(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
            catch { /* best effort */ }
        }

        static bool SetReadOnly(string path)
        {
            try
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                return true;
            }
            catch { return false; }
        }

        // Splits on CRLF / LF, dropping the single trailing empty entry a
        // terminal newline produces, so a read -> write round-trip stays
        // byte-stable instead of growing a blank line each time.
        static List<string> SplitLines(string text)
        {
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var parts = normalized.Split('\n');
            int count = parts.Length;
            if (count > 0 && parts[count - 1].Length == 0) count--;
            var list = new List<string>(count);
            for (int i = 0; i < count; i++) list.Add(parts[i]);
            return list;
        }
    }
}
