using System.Text;

namespace PlayerSyncModExporter.Core.IO;

public static class PathSafety
{
    private static readonly char[] WindowsInvalidFileNameChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public const int MaxFileNameLength = 80;
    public const int MaxGamePathLength = 260;

    public static StringComparison PathComparison
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Produces a single path segment that is valid on Windows (where the game runs) and every other OS:
    /// no separators, no reserved device names, no control characters, no trailing dots or spaces.
    /// </summary>
    public static string SanitizeFileName(string? name, string fallback = "export")
    {
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(c) || Array.IndexOf(WindowsInvalidFileNameChars, c) >= 0 || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
                sb.Append('_');
            else
                sb.Append(c);
        }

        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        while (result.StartsWith('.'))
            result = result[1..];

        if (result.Length > MaxFileNameLength)
            result = result[..MaxFileNameLength].TrimEnd('.', ' ');

        if (result.Length == 0 || result.All(ch => ch == '_'))
            return fallback;

        var stem = result.Split('.')[0];
        if (WindowsReservedNames.Contains(stem))
            result = "_" + result;

        return result;
    }

    public static bool IsUnderRoot(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>Combines <paramref name="relative"/> under <paramref name="root"/> and rejects results that escape it.</summary>
    public static bool TryCombineUnderRoot(string root, string relative, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            return false;

        try
        {
            var combined = Path.GetFullPath(Path.Combine(root, relative));
            if (!IsUnderRoot(root, combined))
                return false;
            fullPath = combined;
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Validates and normalizes an FFXIV game path ("chara/equipment/e0001/texture/x.tex").
    /// Rejects anything that could be interpreted as a file system path or escape an archive folder.
    /// </summary>
    public static bool TryNormalizeGamePath(string? gamePath, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(gamePath))
            return false;

        var p = gamePath.Trim().Replace('\\', '/').ToLowerInvariant();
        if (p.Length > MaxGamePathLength || p.StartsWith('/') || p.Contains(':') || p.Contains("//", StringComparison.Ordinal))
            return false;

        foreach (var c in p)
            if (c < 0x20 || c > 0x7E || c is '<' or '>' or '"' or '|' or '?' or '*')
                return false;

        var segments = p.Split('/');
        if (segments.Length < 2 || segments.Any(s => s.Length == 0 || s == "." || s == ".."))
            return false;

        var last = segments[^1];
        var dot = last.LastIndexOf('.');
        if (dot <= 0 || dot == last.Length - 1)
            return false;

        normalized = p;
        return true;
    }

    public static string GetExtension(string gamePath)
    {
        var dot = gamePath.LastIndexOf('.');
        return dot < 0 ? string.Empty : gamePath[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>Returns <paramref name="name"/>, or "name (2)", "name (3)", ... for the first unused path in <paramref name="parent"/>.</summary>
    public static string GetUniquePath(string parent, string name)
    {
        var candidate = Path.Combine(parent, name);
        for (var i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
            candidate = Path.Combine(parent, $"{name} ({i})");
        return candidate;
    }
}
