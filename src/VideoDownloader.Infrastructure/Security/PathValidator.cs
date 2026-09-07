namespace VideoDownloader.Infrastructure.Security;

public static class PathValidator
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool IsValidSaveDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var expanded = Configuration.PathExpander.Expand(path.Trim());
            if (!Path.IsPathRooted(expanded))
                return false;

            var full = Path.GetFullPath(expanded);
            if (full.Contains("..", StringComparison.Ordinal))
                return false;

            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return false;

            var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(name) && ReservedNames.Contains(name))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }
}
