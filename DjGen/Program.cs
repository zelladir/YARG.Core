using System.Diagnostics;
using System.Globalization;
using System.Text;
using YARG.Core.Chart;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var roots = new List<string>();
var only = new List<string>();
bool write = false;
string? report = null, output = null;
int jobs = Math.Min(Environment.ProcessorCount, 8);
try
{
    for (int i = 0; i < args.Length; i++)
    {
        string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing value for " + args[i - 1]);
        switch (args[i])
        {
            case "--help": case "-h":
                Console.WriteLine("djgen <songs-root|song-folder>... [--write] [--report report.csv] [--jobs N] [--only <folder>...] [--output scratch-root]\nDry run by default. --output mirrors sidecars into numbered scratch folders; never changes input folders.");
                return 0;
            case "--write": write = true; break;
            case "--report": report = Path.GetFullPath(Value()); break;
            case "--output": output = Path.GetFullPath(Value()); break;
            case "--jobs": jobs = int.Parse(Value()); if (jobs < 1 || jobs > 64) throw new ArgumentException("--jobs must be 1..64."); break;
            case "--only":
                only.Add(Path.GetFullPath(Value()));
                while (i + 1 < args.Length && !args[i + 1].StartsWith("--")) only.Add(Path.GetFullPath(args[++i]));
                break;
            default:
                if (args[i].StartsWith("--")) throw new ArgumentException("Unknown option: " + args[i]);
                roots.Add(Path.GetFullPath(args[i])); break;
        }
    }
    if (roots.Count == 0) throw new ArgumentException("Supply at least one songs root or song folder. Use --help for usage.");
    foreach (string root in roots) if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
    if (output != null && roots.Any(r => Within(output, r))) throw new ArgumentException("--output must be outside the input roots.");
    if (report != null && (Path.GetFileName(report).Equals("notes.mid", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(report).Equals("song.ini", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(report).Equals("dj.chart", StringComparison.OrdinalIgnoreCase)))
        throw new ArgumentException("Report must not replace song source or sidecar files.");
    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
    var folders = roots.SelectMany(r => Directory.EnumerateFiles(r, "notes.mid", options)).Select(p => Path.GetDirectoryName(p)!)
        .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        .Where(p => only.Count == 0 || only.Any(o => Within(p, o))).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    if (folders.Length == 0) throw new ArgumentException("No notes.mid files matched.");
    foreach (string filter in only) if (!folders.Any(p => Within(p, filter))) throw new ArgumentException("--only folder matched no songs: " + filter);
    var rows = new string[folders.Length];
    int failures = 0;
    Parallel.For(0, folders.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, index =>
    {
        string folder = folders[index];
        string destination = Path.Combine(output == null ? folder : Path.Combine(output, index.ToString("D4")), "dj.chart");
        var timer = Stopwatch.StartNew();
        try
        {
            var result = DjBandGenerator.GenerateFolder(folder);
            string status = "dry-run";
            bool protectedFile = File.Exists(destination) && !DjSidecar.IsGeneratorOwned(DjSidecar.ReadText(destination));
            if (protectedFile) status = "protected-hand-authored";
            else if (write)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                status = DjSidecar.WriteGenerated(destination, result.Text) ? "written" : "protected-hand-authored";
            }
            rows[index] = Csv(folder, destination, status, result.Info.SourceHash, result.Info.Quality, result.Info.Hidden,
                result.Info.Flags, result.DurationSeconds.ToString("F3"), timer.Elapsed.TotalMilliseconds.ToString("F3"),
                result.NoteCounts[YARG.Core.Difficulty.Beginner], result.NoteCounts[YARG.Core.Difficulty.Easy],
                result.NoteCounts[YARG.Core.Difficulty.Medium], result.NoteCounts[YARG.Core.Difficulty.Hard], result.NoteCounts[YARG.Core.Difficulty.Expert],
                result.NoteCounts[YARG.Core.Difficulty.Beginner] / result.DurationSeconds,
                result.NoteCounts[YARG.Core.Difficulty.Easy] / result.DurationSeconds,
                result.NoteCounts[YARG.Core.Difficulty.Medium] / result.DurationSeconds,
                result.NoteCounts[YARG.Core.Difficulty.Hard] / result.DurationSeconds,
                result.NoteCounts[YARG.Core.Difficulty.Expert] / result.DurationSeconds, "");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref failures);
            rows[index] = Csv(folder, destination, "error", "", "", true, "InputError", "", timer.Elapsed.TotalMilliseconds.ToString("F3"),
                "", "", "", "", "", "", "", "", "", "", ex.GetType().Name + ": " + ex.Message);
        }
    });
    const string header = "folder,output,status,source_sha256,quality,hidden,flags,duration_seconds,milliseconds,beginner_notes,easy_notes,medium_notes,hard_notes,expert_notes,beginner_nps,easy_nps,medium_nps,hard_nps,expert_nps,error";
    string csv = header + "\n" + string.Join("\n", rows) + "\n";
    if (report == null) Console.Write(csv);
    else { Directory.CreateDirectory(Path.GetDirectoryName(report)!); File.WriteAllText(report, csv, new UTF8Encoding(false)); }
    Console.Error.WriteLine($"{folders.Length} songs; {failures} errors; {(write ? "write" : "dry run")}; jobs={jobs}");
    return failures == 0 ? 0 : 1;
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }

static bool Within(string path, string root)
{
    string relative = Path.GetRelativePath(root, path);
    return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar));
}
static string Csv(params object[] cells) => string.Join(",", cells.Select(c => "\"" + (Convert.ToString(c, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\""));
