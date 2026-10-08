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
        "TiWorker", "TrustedInstaller", "svchost", "System", "vmmem", "vmmemWSL", "Docker Desktop", "claude",
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
        var scrubbed = PathPattern().Replace(cleaned.ToString(), m => m.Groups["q"] is { Success: true } q ? q.Value + "<path>" + q.Value : "<path>");
        return scrubbed.Length <= MaxErrorChars ? scrubbed : scrubbed[..MaxErrorChars] + "...";
    }

    // Where a path starts: C:\, C:/, C:dir\, \\host, \\?\, a rooted \dir\, or //host/ (not inside a URL).
    private const string PathStart = """(?:(?<![A-Za-z0-9])[A-Za-z]:[\\/]|(?<![A-Za-z0-9])[A-Za-z]:[\w.$-]+\\|\\\\(?:[?.]\\)?|(?<![\w\\])\\(?=[\w$.-]+\\)|(?<![^\s'"(\[=,;])//(?=[^\s/]+/))""";

    // A quoted path runs to the closing quote (one not followed by a letter or digit: O'Brien goes whole).
    // An unquoted path takes the rest of the line up to a character no file name holds. DOMAIN\user goes alone.
    [GeneratedRegex("(?<q>['\"])" + PathStart + """(?:(?!\k<q>(?!\w)).)*(?:\k<q>)?|""" + PathStart + """[^"<>|*?]*|(?<![\w\\.:/-])[A-Za-z][\w.-]*\\[\w.$-]+""")]
    private static partial Regex PathPattern();

    /// <summary>Whether a path names a Windows device (NUL, CON, COM1, ... with or without an extension), which
    /// would swallow the results.</summary>
    public static bool IsDevicePath(string path)
    {
        if (path.Replace('/', '\\').StartsWith(@"\\.\", StringComparison.Ordinal)) return true;
        var name = Path.GetFileName(path);
        var end = name.IndexOfAny(['.', ':']);
        return DeviceName().IsMatch((end < 0 ? name : name[..end]).TrimEnd(' '));
    }

    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|(?:COM|LPT)[1-9\u00B9\u00B2\u00B3]|CONIN\$|CONOUT\$)$", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceName();

    /// <summary>Why <paramref name="path"/> cannot be the results file, or null when a file can be created
    /// there. Checked before any proxy host is started, so the user learns at once, not after the run.</summary>
    public static string? CheckOut(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            if (!Directory.Exists(folder)) return $"--out: the folder {folder} does not exist.";
            // A probe that vanishes on close: the results file itself is only created once the run can start.
            using var probe = new FileStream(Path.Combine(folder, $".piper-bench-{Guid.NewGuid():N}.tmp"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"--out '{path}' cannot be written: {ex.Message}";
        }
    }

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
