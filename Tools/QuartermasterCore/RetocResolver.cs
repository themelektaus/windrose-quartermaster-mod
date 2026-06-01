using System;

namespace Windrose.Quartermaster.Core
{
    public sealed class RetocResolver
    {
        public const string PinnedVersion = "0.1.5";
        public const string AssetName = "retoc_cli-x86_64-pc-windows-msvc.zip";

        readonly string _modRoot;

        public RetocResolver(string modRoot)
        {
            if (string.IsNullOrEmpty(modRoot)) throw new ArgumentNullException("modRoot");
            _modRoot = modRoot;
        }

        public Action<string> Log;

        public string Resolve()
        {
            var url = "https://github.com/trumank/retoc/releases/download/v"
                      + PinnedVersion + "/" + AssetName;
            return GitHubReleaseTool.Resolve(
                _modRoot, "retoc.exe", "retoc", PinnedVersion, AssetName, url, Log);
        }
    }
}
