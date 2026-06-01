using System;

namespace Windrose.Quartermaster.Core
{
    public sealed class RepakResolver
    {
        public const string PinnedVersion = "0.2.3";
        public const string AssetName = "repak_cli-x86_64-pc-windows-msvc.zip";

        readonly string _modRoot;

        public RepakResolver(string modRoot)
        {
            if (string.IsNullOrEmpty(modRoot)) throw new ArgumentNullException("modRoot");
            _modRoot = modRoot;
        }

        public Action<string> Log;

        public string Resolve()
        {
            var url = "https://github.com/trumank/repak/releases/download/v"
                      + PinnedVersion + "/" + AssetName;
            return GitHubReleaseTool.Resolve(
                _modRoot, "repak.exe", "repak", PinnedVersion, AssetName, url, Log);
        }
    }
}
