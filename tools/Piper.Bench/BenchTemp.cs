using System.Text.RegularExpressions;

namespace Piper.Bench;

/// <summary>The temporary folders a benchmark run makes. Each holds a throwaway certificate authority
/// (a root key with a fixed password) that a run deleted at its end; a hard kill leaves it behind.</summary>
internal static partial class BenchTemp
{
    // piper-bench-<32 hex digits>, the name BenchRunner gives its folder, and nothing else.
    [GeneratedRegex("^piper-bench-[0-9a-f]{32}$")]
    private static partial Regex RunFolder();

    /// <summary>Deletes the run folders in <paramref name="root"/> older than <paramref name="age"/>.
    /// Folders that are links, files, or named anything else are left alone. Returns how many went.</summary>
    public static int DeleteStaleFolders(string root, TimeSpan age)
    {
        var deleted = 0;
        try
        {
            foreach (var path in Directory.EnumerateDirectories(root, "piper-bench-*"))
            {
                var info = new DirectoryInfo(path);
                // A junction or symlink is never followed: only a real folder this tool made is removed.
                if (!RunFolder().IsMatch(info.Name) || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (DateTime.UtcNow - info.CreationTimeUtc < age) continue;
                try { info.Delete(recursive: true); deleted++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use by another run, or not ours to delete: leave it for the next start.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable temp folder: nothing to clean.
        }
        return deleted;
    }
}
