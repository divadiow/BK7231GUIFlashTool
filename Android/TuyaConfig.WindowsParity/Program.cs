using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BK7231Flasher;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: TuyaConfig.WindowsParity <BK7231Flasher.exe> <samples-directory> <report-directory>");
    return 2;
}

string executable = Path.GetFullPath(args[0]);
string samplesRoot = Path.GetFullPath(args[1]);
string reportRoot = Path.GetFullPath(args[2]);

if (!File.Exists(executable))
{
    Console.Error.WriteLine("Windows Easy Flasher executable not found: " + executable);
    return 2;
}
if (!Directory.Exists(samplesRoot))
{
    Console.Error.WriteLine("Samples directory not found: " + samplesRoot);
    return 2;
}

Directory.CreateDirectory(reportRoot);
var rows = new List<ParityRow>();
int mismatches = 0;

foreach (string sample in Directory.EnumerateFiles(samplesRoot, "*.bin", SearchOption.AllDirectories)
                                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
{
    string relative = Path.GetRelativePath(samplesRoot, sample).Replace('\\', '/');
    string platform = relative.Contains('/') ? relative[..relative.IndexOf('/')] : "Unclassified";
    byte[] data = await File.ReadAllBytesAsync(sample);
    TuyaExtractionResult android = TuyaDumpExtractor.Extract(data);
    DesktopResult desktop = await RunDesktopAsync(executable, sample);

    bool exactMatch;
    string difference = string.Empty;

    if (android.Success && desktop.Success)
    {
        exactMatch = string.Equals(android.Json, desktop.Json, StringComparison.Ordinal);
        if (!exactMatch)
            difference = DescribeFirstDifference(android.Json, desktop.Json);
    }
    else
    {
        // The desktop command returns only a generic failure code. Both rejecting a sample is the
        // strongest classification comparison available without modifying Easy Flasher itself.
        exactMatch = android.Success == desktop.Success;
        if (!exactMatch)
            difference = android.Success
                ? "Android core extracted JSON but the Windows command rejected the dump."
                : "Windows command extracted JSON but the Android core rejected the dump.";
    }

    if (!exactMatch)
        mismatches++;

    string androidHash = android.Success ? Sha256(android.Json) : string.Empty;
    string desktopHash = desktop.Success ? Sha256(desktop.Json) : string.Empty;

    rows.Add(new ParityRow(
        platform,
        relative,
        data.LongLength,
        android.Status.ToString(),
        android.Success,
        desktop.ExitCode,
        desktop.Success,
        exactMatch,
        androidHash,
        desktopHash,
        difference));

    Console.WriteLine($"[{platform}] {Path.GetFileName(sample)}: Android={android.Status}; Windows={desktop.ExitCode}; exact={exactMatch}");
}

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
await File.WriteAllTextAsync(
    Path.Combine(reportRoot, "windows-desktop-parity-results.json"),
    JsonSerializer.Serialize(rows, jsonOptions) + "\n",
    new UTF8Encoding(false));

string markdown = BuildMarkdown(rows, mismatches);
await File.WriteAllTextAsync(
    Path.Combine(reportRoot, "windows-desktop-parity-report.md"),
    markdown,
    new UTF8Encoding(false));

Console.WriteLine(markdown);
return mismatches == 0 ? 0 : 1;

static async Task<DesktopResult> RunDesktopAsync(string executable, string sample)
{
    string scratch = Path.Combine(Path.GetTempPath(), "tuya-desktop-ref-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(scratch);
    string output = Path.Combine(scratch, "output.json");

    try
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = scratch,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("tuyaconfig");
        start.ArgumentList.Add(sample);
        start.ArgumentList.Add(output);

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to launch the Windows Easy Flasher command.");
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 || !File.Exists(output))
            return new DesktopResult(process.ExitCode, false, string.Empty);

        // Encoding.UTF8 detects and removes the BOM written by the .NET Framework CLI. The value
        // compared below is therefore the exact JSON string passed to File.WriteAllText by Easy Flasher.
        string json = await File.ReadAllTextAsync(output, Encoding.UTF8);
        return new DesktopResult(process.ExitCode, true, json);
    }
    finally
    {
        try { Directory.Delete(scratch, recursive: true); } catch { }
    }
}

static string Sha256(string text) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

static string DescribeFirstDifference(string expected, string actual)
{
    int common = Math.Min(expected.Length, actual.Length);
    int index = 0;
    while (index < common && expected[index] == actual[index])
        index++;

    if (index == common)
        return $"Text lengths differ at index {index}: Android={expected.Length}, Windows={actual.Length}.";

    (int line, int column) = GetLineColumn(expected, index);
    return $"First text difference at index {index} (line {line}, column {column}); Android length={expected.Length}, Windows length={actual.Length}.";
}

static (int Line, int Column) GetLineColumn(string value, int index)
{
    int line = 1;
    int column = 1;
    for (int i = 0; i < index && i < value.Length; i++)
    {
        if (value[i] == '\n')
        {
            line++;
            column = 1;
        }
        else
        {
            column++;
        }
    }
    return (line, column);
}

static string BuildMarkdown(IReadOnlyList<ParityRow> rows, int mismatches)
{
    var sb = new StringBuilder();
    sb.AppendLine("# Windows Easy Flasher ↔ Android extractor parity");
    sb.AppendLine();
    sb.AppendLine("Each representative dump was passed to the built Windows `BK7231Flasher.exe tuyaconfig` command and to the exact core compiled into the Android application. Successful results were compared as complete .NET strings, including ordering, indentation, escaping and CRLF line endings. This report contains hashes only—not extracted JSON or credentials.");
    sb.AppendLine();
    sb.AppendLine($"- Samples: **{rows.Count}**");
    sb.AppendLine($"- Successful output pairs: **{rows.Count(row => row.AndroidExtracted && row.WindowsExtracted)}**");
    sb.AppendLine($"- Exact matches / mutual rejections: **{rows.Count(row => row.ExactMatch)}**");
    sb.AppendLine($"- Mismatches: **{mismatches}**");
    sb.AppendLine();
    sb.AppendLine("| Platform | Sample | Android result | Windows exit | Exact | SHA-256 prefix |");
    sb.AppendLine("|---|---|---|---:|:---:|---|");

    foreach (ParityRow row in rows)
    {
        string hash = string.IsNullOrEmpty(row.AndroidSha256)
            ? "—"
            : row.AndroidSha256[..Math.Min(16, row.AndroidSha256.Length)];
        sb.Append("| ").Append(Escape(row.Platform))
          .Append(" | ").Append(Escape(row.Sample))
          .Append(" | ").Append(Escape(row.AndroidStatus))
          .Append(" | ").Append(row.WindowsExitCode)
          .Append(" | ").Append(row.ExactMatch ? "✓" : "✗")
          .Append(" | `").Append(hash).AppendLine("` |");
    }

    return sb.ToString();
}

static string Escape(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

internal sealed record DesktopResult(int ExitCode, bool Success, string Json);

internal sealed record ParityRow(
    string Platform,
    string Sample,
    long Bytes,
    string AndroidStatus,
    bool AndroidExtracted,
    int WindowsExitCode,
    bool WindowsExtracted,
    bool ExactMatch,
    string AndroidSha256,
    string WindowsSha256,
    string Difference);
