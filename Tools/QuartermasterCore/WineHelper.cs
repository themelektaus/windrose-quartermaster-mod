using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Windrose.Quartermaster.Core
{
    // On Linux, Windows .exe binaries must be run via Wine.
    // Call ApplyWine() on any ProcessStartInfo targeting a .exe before Process.Start.
    static class WineHelper
    {
        public static void ApplyWine(ProcessStartInfo psi)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi.ArgumentList.Insert(0, psi.FileName);
                psi.FileName = "wine";
            }
        }
    }
}
