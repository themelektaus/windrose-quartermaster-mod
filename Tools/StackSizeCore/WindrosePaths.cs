using System;
using System.IO;

namespace Windrose.StackSize.Core
{
    // Resolves the standard Windrose mod-folder layout from a known root.
    // The root is wherever the .ps1 build scripts (and now the GUI) live --
    // i.e. the directory that contains Sources/, Builds/, Profiles/ etc.
    public sealed class WindrosePaths
    {
        public string ModRoot;
        public string Sources;
        public string Vanilla;
        public string Builds;
        public string Profiles;
        public string ProfilesBuiltin;
        public string BuildTmp;
        public string Tools;

        public static WindrosePaths FromModRoot(string modRoot)
        {
            if (string.IsNullOrEmpty(modRoot)) throw new ArgumentNullException("modRoot");
            modRoot = Path.GetFullPath(modRoot);
            return new WindrosePaths
            {
                ModRoot = modRoot,
                Sources = Path.Combine(modRoot, "Sources"),
                Vanilla = Path.Combine(modRoot, "Sources", "Vanilla"),
                Builds = Path.Combine(modRoot, "Builds"),
                Profiles = Path.Combine(modRoot, "Profiles"),
                ProfilesBuiltin = Path.Combine(modRoot, "Profiles", "_builtin"),
                BuildTmp = Path.Combine(modRoot, ".build-tmp"),
                Tools = Path.Combine(modRoot, "Tools"),
            };
        }
    }
}
