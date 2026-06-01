using System.Collections.Generic;

namespace Windrose.Quartermaster.Core
{
    public sealed class PatchResult
    {
        public int Scanned;
        public int Excluded;
        public int NoSchema;
        public int Skipped;
        public int UnchangedSkip;
        public int Written;
        public int Promoted;
        public int Overridden;
        public int Capped;

        public List<string> WrittenItems = new List<string>();
    }
}
