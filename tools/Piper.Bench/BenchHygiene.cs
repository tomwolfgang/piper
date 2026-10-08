using System.Text;
using System.Text.RegularExpressions;

namespace Piper.Bench;

/// <summary>What may end up in a results file, which is meant to be shared: no local path, no user or
/// machine name, no list of what else the machine was running. Pure, so the smoke tests link it.</summary>
internal static partial class BenchHygiene
{
    private const int MaxErrorChars = 200;

    // Processes worth naming because they explain a noisy run: scanners, indexers, browsers, builds,
    // updaters. Anything else is reported as "other", never by name.
    private static readonly HashSet<string> NoisyProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ekrn", "egui", "MsMpEng", "MpDefenderCoreService", "NisSrv", "MsSense", "avp", "AvastSvc", "avgsvc",
        "mbamservice", "SentinelAgent", "CSFalconService", "SearchIndexer", "SearchHost", "OneDrive", "Teams",
        "ms-teams", "chrome", "msedge", "firefox", "Code", "devenv", "MSBuild", "dotnet", "VBCSCompiler",
        "TiWorker", "TrustedInstaller", "svchost", "System", "vmmem", "vmmemWSL", "Docker Desktop",
    };

    /// <summary>The name a background process is recorded under: its own when it is on the short list
    /// of known noisy ones, otherwise "other".</summary>
    public static string ProcessLabel(string processName) => NoisyProcesses.Contains(processName) ? processName : "other";

    /// <summary>An error as it is written to a results file: the text on one line with every Windows
    /// or UNC path replaced (the rest of the line after a path goes with it, since a path may hold
    /// spaces), cut to <see cref="MaxErrorChars"/> characters.</summary>
    public static string Scrub(string text)
    {
        var cleaned = new StringBuilder(text.Length);
        foreach (var c in text) cleaned.Append(char.IsControl(c) ? ' ' : c);
        var scrubbed = PathPattern().Replace(cleaned.ToString(), "<path>");
        return scrubbed.Length <= MaxErrorChars ? scrubbed : scrubbed[..MaxErrorChars] + "...";
    }

    // A drive path (C:\ or C:/) or a UNC path (\\host\share), up to the next quote or the end of the line.
    [GeneratedRegex("""(?:[A-Za-z]:[\\/]|\\\\)[^"'<>|*?]*""")]
    private static partial Regex PathPattern();

    /// <summary>Creates the results file, which must not exist: a benchmark that is re-run into an old
    /// file name must not destroy the recording that was there.</summary>
    public static FileStream CreateResultsFile(string path)
    {
        try { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch (IOException) when (File.Exists(path))
        {
            throw new IOException($"{path} already exists and is not overwritten; choose another --out or delete it.");
        }
    }
}
