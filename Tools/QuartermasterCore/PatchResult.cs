using System.Collections.Generic;

namespace Windrose.Quartermaster.Core
{
    public sealed class PatchResult
    {
        public int Scanned;          // total *.json files walked under vanillaDir
        public int Excluded;         // matched ExcludeRelativePaths (e.g. *\Tests\*)
        public int NoSchema;         // missing "MaxCountInSlot" -> not an inventory item
        public int Skipped;          // vanillaStack <= 1 + non-promotable (no override)
        public int UnchangedSkip;    // computed target == vanillaStack -> nothing to write
        public int Written;          // patched JSONs written to outDir
        public int Promoted;         // of Written: started at stack <= 1
        public int Overridden;       // of Written: had a per-item override (vs. globals)
        public int Capped;           // of Written: hit globals.stackSize.Cap

        // For diagnostics / GUI build-log; capped at a sane upper bound by callers
        // if needed (default = unlimited).
        public List<string> WrittenItems = new List<string>();
    }
}
