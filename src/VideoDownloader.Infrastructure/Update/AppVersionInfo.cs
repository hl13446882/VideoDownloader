using System.Reflection;

namespace VideoDownloader.Infrastructure.Update;

public static class AppVersionInfo
{
    public static string InformationalVersion { get; } =
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "0.0.0";

    /// <summary>SemVer core used for comparisons (strips +build metadata).</summary>
    public static string SemVer => Normalize(InformationalVersion);

    public static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "0.0.0";

        var trimmed = version.Trim();
        var plus = trimmed.IndexOf('+');
        if (plus >= 0)
            trimmed = trimmed[..plus];
        return trimmed;
    }

    /// <summary>Returns &gt;0 if left is newer than right.</summary>
    public static int CompareSemVer(string? left, string? right)
    {
        var a = Parse(Normalize(left));
        var b = Parse(Normalize(right));
        var core = a.Major.CompareTo(b.Major);
        if (core != 0) return core;
        core = a.Minor.CompareTo(b.Minor);
        if (core != 0) return core;
        core = a.Patch.CompareTo(b.Patch);
        if (core != 0) return core;

        // No pre-release is newer than pre-release (1.0.0 > 1.0.0-beta).
        if (a.Pre.Length == 0 && b.Pre.Length == 0) return 0;
        if (a.Pre.Length == 0) return 1;
        if (b.Pre.Length == 0) return -1;
        return string.CompareOrdinal(a.Pre, b.Pre);
    }

    private static (int Major, int Minor, int Patch, string Pre) Parse(string version)
    {
        var pre = string.Empty;
        var dash = version.IndexOf('-');
        var core = version;
        if (dash >= 0)
        {
            pre = version[(dash + 1)..];
            core = version[..dash];
        }

        var parts = core.Split('.');
        int Read(int index) =>
            index < parts.Length && int.TryParse(parts[index], out var n) ? n : 0;

        return (Read(0), Read(1), Read(2), pre);
    }
}
