using System.Text.RegularExpressions;

namespace TopiaForge.Acceptance.Windows;

/// Game-input grammar is separate from evidence artifacts. Only the bundled root marker may start with a dot.
internal static class ProvisioningGameInputs
{
    internal const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    internal const long MaximumTotalBytes = 8L * 1024 * 1024 * 1024;
    internal static void ValidateRelativePath(string relative)
    {
        if (relative == ".doorstop_version") return;
        if (relative.Length is < 1 or > 255 || relative.Contains('\\'))
            throw new InvalidDataException("Invalid relative game-input path.");
        foreach (var part in relative.Split('/'))
            if (!Regex.IsMatch(part, @"\A[A-Za-z0-9][A-Za-z0-9._ ()-]*\z") || part.EndsWith('.') || part.EndsWith(' ')
                || Regex.IsMatch(part, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Game input contains traversal, an alias or an unreviewed filename.");
    }
    internal static string Child(string root, string relative)
    {
        ValidateRelativePath(relative);
        var child = BoundedJson.PhysicalPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!ProvisioningLaunch.Within(child, BoundedJson.PhysicalPath(root)))
            throw new InvalidDataException("Game input escapes its root.");
        return child;
    }
    internal static long AddLength(long total, long length)
    {
        if (total < 0 || total > MaximumTotalBytes || length < 0 || length > MaximumFileBytes
            || length > MaximumTotalBytes - total)
            throw new InvalidDataException("Game input exceeds its streaming file or aggregate size limit.");
        return total + length;
    }
}
