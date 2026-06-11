using System.Reflection;

namespace Windrose.Quartermaster.Web;

/// <summary>
/// Single source of truth for the Quartermaster version is the
/// &lt;Version&gt; property in Quartermaster.Web.csproj; the SDK bakes it
/// into this assembly's informational version at build time. All version
/// displays (titlebar, frontend title, issue reports) read it from here.
/// </summary>
public static class AppVersion
{
    /// <summary>Full informational version, e.g. "0.9.1.8+&lt;commit&gt;" (diagnostics).</summary>
    public static string Informational { get; } = Resolve();

    /// <summary>Human display form without build metadata, e.g. "0.9.1.8".</summary>
    public static string Display { get; } = Informational.Split('+', 2)[0];

    private static string Resolve()
    {
        try
        {
            var asm = typeof(AppVersion).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                return info.InformationalVersion;
            return asm.GetName().Version?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }
}
