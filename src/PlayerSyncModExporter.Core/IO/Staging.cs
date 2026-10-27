using System.Globalization;

namespace PlayerSyncModExporter.Core.IO;

public interface IDiskSpaceProbe
{
    /// <summary>Free bytes available to the current user on the volume containing <paramref name="path"/>, or null if unknown.</summary>
    long? GetAvailableBytes(string path);
}

public sealed class DriveInfoDiskSpaceProbe : IDiskSpaceProbe
{
    public long? GetAvailableBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// A temporary working directory next to the final export location. It carries a marker file so cleanup
/// only ever deletes directories this tool created, never user data or PlayerSync/Penumbra folders.
/// </summary>
public sealed class StagingDirectory : IDisposable
{
    public const string Prefix = ".psme-staging-";
    public const string MarkerFileName = ".psme-staging";

    private bool _committed;

    private StagingDirectory(string path) => Path = path;

    public string Path { get; }

    public static StagingDirectory Create(string destinationRoot)
    {
        var path = System.IO.Path.Combine(destinationRoot, Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.WriteAllText(System.IO.Path.Combine(path, MarkerFileName), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return new StagingDirectory(path);
    }

    /// <summary>Marks the directory as moved into place so Dispose leaves it alone.</summary>
    public void MarkCommitted() => _committed = true;

    public void Dispose()
    {
        if (!_committed)
            TryDeleteOwned(Path);
    }

    public static bool IsOwned(string directory)
        => System.IO.Path.GetFileName(directory).StartsWith(Prefix, StringComparison.Ordinal)
           && File.Exists(System.IO.Path.Combine(directory, MarkerFileName));

    public static bool TryDeleteOwned(string directory)
    {
        try
        {
            if (!Directory.Exists(directory) || !IsOwned(directory))
                return false;
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes abandoned staging directories (e.g. after a game crash) older than <paramref name="minAge"/>.</summary>
    public static int CleanupAbandoned(string destinationRoot, TimeSpan minAge)
    {
        if (!Directory.Exists(destinationRoot))
            return 0;

        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(destinationRoot, Prefix + "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (!IsOwned(dir) || DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) < minAge)
                    continue;
                if (TryDeleteOwned(dir))
                    removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Leave it for the next run.
            }
        }

        return removed;
    }
}
