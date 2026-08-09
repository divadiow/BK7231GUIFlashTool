using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BK7231Flasher;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: TuyaConfig.CorpusTest <samples-directory> <report-directory>");
    return 2;
}

string samplesRoot = Path.GetFullPath(args[0]);
string reportRoot = Path.GetFullPath(args[1]);
Directory.CreateDirectory(reportRoot);

var files = Directory.Exists(samplesRoot)
    ? Directory.EnumerateFiles(samplesRoot, "*.bin", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToArray()
    : Array.Empty<string>();

var results = new List<CorpusResult>();
int parityFailures = 0;
int deterministicFailures = 0;
int invalidJsonFailures = 0;

foreach (string path in files)
{
    byte[] data = await File.ReadAllBytesAsync(path);
    string relative = Path.GetRelativePath(samplesRoot, path).Replace('\\', '/');
    string platform = relative.Contains('/') ? relative[..relative.IndexOf('/')] : "Unclassified";

    string scratch = Path.Combine(Path.GetTempPath(), "tuya-corpus-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(scratch);
    string priorDirectory = Directory.GetCurrentDirectory();

    TuyaExtractionResult appResult;
    TuyaExtractionResult repeated;
    ReferenceResult reference;

    try
    {
        Directory.SetCurrentDirectory(scratch);
        appResult = TuyaDumpExtractor.Extract(data);
        repeated = TuyaDumpExtractor.Extract(data);
        reference = ExtractLikeCurrentWindowsForm(data);
    }
    finally
    {
        Directory.SetCurrentDirectory(priorDirectory);
        try { Directory.Delete(scratch, recursive: true); } catch { }
    }

    bool parity = appResult.Status == reference.Status &&
                  string.Equals(appResult.Json, reference.Json, StringComparison.Ordinal);
    bool deterministic = appResult.Status == repeated.Status &&
                         string.Equals(appResult.Json, repeated.Json, StringComparison.Ordinal);
    bool validJson = !appResult.Success || IsValidJson(appResult.Json);
    bool windowsNewlines = !appResult.Success || HasOnlyWindowsLineEndings(appResult.Json);

    if (!parity) parityFailures++;
    if (!deterministic) deterministicFailures++;
    if (!validJson) invalidJsonFailures++;

    int topLevelProperties = appResult.Success ? CountTopLevelProperties(appResult.Json) : 0;
    string hash = appResult.Success
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appResult.Json))).ToLowerInvariant()
        : string.Empty;

    results.Add(new CorpusResult(
        platform,
        relative,
        data.LongLength,
        appResult.Status.ToString(),
        appResult.Success,
        parity,
        deterministic,
        validJson,
        windowsNewlines,
        topLevelProperties,
        hash,
        appResult.MagicPosition,
        RedactMessage(appResult.Message)));

    Console.WriteLine($"[{platform}] {Path.GetFileName(path)}: {appResult.Status}; parity={parity}; deterministic={deterministic}; json={validJson}; properties={topLevelProperties}");
}

var synthetic = RunSyntheticTests();
results.AddRange(synthetic);
if (synthetic.Any(x => !x.DesktopParity)) parityFailures++;
if (synthetic.Any(x => !x.Deterministic)) deterministicFailures++;
if (synthetic.Any(x => !x.ValidJson)) invalidJsonFailures++;

var options = new JsonSerializerOptions { WriteIndented = true };
await File.WriteAllTextAsync(
    Path.Combine(reportRoot, "test-results.json"),
    JsonSerializer.Serialize(results, options) + "\n",
    new UTF8Encoding(false));

await File.WriteAllTextAsync(
    Path.Combine(reportRoot, "test-report.md"),
    BuildMarkdown(results, parityFailures, deterministicFailures, invalidJsonFailures),
    new UTF8Encoding(false));

int successCount = results.Count(x => x.Extracted);
Console.WriteLine($"Samples: {results.Count}; extracted: {successCount}; parity failures: {parityFailures}; deterministic failures: {deterministicFailures}; invalid JSON: {invalidJsonFailures}");

return parityFailures == 0 && deterministicFailures == 0 && invalidJsonFailures == 0 ? 0 : 1;

static ReferenceResult ExtractLikeCurrentWindowsForm(byte[] data)
{
    var tc = new TuyaConfig();

    if (tc.fromBytes(data) == false)
    {
        bool classicExtractFailed = tc.extractKeys();
        bool hasEnhancedFallback = classicExtractFailed && tc.hasEnhancedExtractionData();

        if (!classicExtractFailed || hasEnhancedFallback)
        {
            string result;
            try
            {
                result = tc.getEnhancedExtractionText();
            }
            catch
            {
                result = tc.getKeysAsJSON();
            }

            if (string.IsNullOrWhiteSpace(result))
                result = tc.getKeysAsJSON();

            if (!string.IsNullOrWhiteSpace(result))
                return new ReferenceResult(TuyaExtractionStatus.Success, result);

            return new ReferenceResult(TuyaExtractionStatus.ExtractionFailed, string.Empty);
        }

        return new ReferenceResult(TuyaExtractionStatus.ExtractionFailed, string.Empty);
    }

    if (tc.isLastBinaryOBKConfig())
        return new ReferenceResult(TuyaExtractionStatus.OpenBekenConfig, string.Empty);
    if (tc.isLastBinaryFullOf0xff())
        return new ReferenceResult(TuyaExtractionStatus.ErasedFlash, string.Empty);
    return new ReferenceResult(TuyaExtractionStatus.NoTuyaConfigFound, string.Empty);
}

static bool IsValidJson(string json)
{
    try
    {
        using var _ = JsonDocument.Parse(json);
        return true;
    }
    catch
    {
        return false;
    }
}

static bool HasOnlyWindowsLineEndings(string value)
{
    for (int i = 0; i < value.Length; i++)
    {
        if (value[i] == '\n' && (i == 0 || value[i - 1] != '\r'))
            return false;
        if (value[i] == '\r' && (i + 1 >= value.Length || value[i + 1] != '\n'))
            return false;
    }
    return true;
}

static int CountTopLevelProperties(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            ? doc.RootElement.EnumerateObject().Count()
            : 0;
    }
    catch
    {
        return 0;
    }
}

static string RedactMessage(string message)
{
    return message.Length <= 240 ? message : message[..240] + "…";
}

static List<CorpusResult> RunSyntheticTests()
{
    var cases = new (string Name, byte[] Data, TuyaExtractionStatus Expected)[]
    {
        ("empty", Array.Empty<byte>(), TuyaExtractionStatus.InvalidInput),
        ("erased-4k", Enumerable.Repeat((byte)0xff, 4096).ToArray(), TuyaExtractionStatus.ErasedFlash),
        ("obk-header", new byte[] { (byte)'C', (byte)'F', (byte)'G', 0, 0, 0, 0, 0 }, TuyaExtractionStatus.OpenBekenConfig),
        ("random-8k", DeterministicBytes(8192), TuyaExtractionStatus.NoTuyaConfigFound)
    };

    var list = new List<CorpusResult>();
    foreach (var item in cases)
    {
        var one = TuyaDumpExtractor.Extract(item.Data);
        var two = TuyaDumpExtractor.Extract(item.Data);
        bool expected = one.Status == item.Expected;
        bool deterministic = one.Status == two.Status && one.Json == two.Json;
        list.Add(new CorpusResult(
            "Synthetic",
            item.Name,
            item.Data.LongLength,
            one.Status.ToString(),
            one.Success,
            expected,
            deterministic,
            !one.Success || IsValidJson(one.Json),
            !one.Success || HasOnlyWindowsLineEndings(one.Json),
            one.Success ? CountTopLevelProperties(one.Json) : 0,
            one.Success ? one.Sha256 : string.Empty,
            one.MagicPosition,
            RedactMessage(one.Message)));
    }
    return list;
}

static byte[] DeterministicBytes(int length)
{
    var data = new byte[length];
    uint state = 0x31415926;
    for (int i = 0; i < data.Length; i++)
    {
        state = state * 1664525 + 1013904223;
        data[i] = (byte)(state >> 24);
    }
    return data;
}

static string BuildMarkdown(
    IReadOnlyList<CorpusResult> results,
    int parityFailures,
    int deterministicFailures,
    int invalidJsonFailures)
{
    var sb = new StringBuilder();
    sb.AppendLine("# Tuya Dump JSON Android parity test");
    sb.AppendLine();
    sb.AppendLine($"Pinned extractor: `{TuyaDumpExtractor.UpstreamRepository}` commit `{TuyaDumpExtractor.UpstreamCommit}` (`{TuyaDumpExtractor.UpstreamPath}`).");
    sb.AppendLine();
    sb.AppendLine("The report deliberately records hashes and structural counts, not extracted JSON, credentials or device keys.");
    sb.AppendLine();
    sb.AppendLine($"- Samples: **{results.Count}**");
    sb.AppendLine($"- Successful extractions: **{results.Count(x => x.Extracted)}**");
    sb.AppendLine($"- Desktop-wrapper parity failures: **{parityFailures}**");
    sb.AppendLine($"- Determinism failures: **{deterministicFailures}**");
    sb.AppendLine($"- Invalid JSON failures: **{invalidJsonFailures}**");
    sb.AppendLine();
    sb.AppendLine("| Platform | Sample | Size | Result | Desktop parity | Repeat | JSON | Properties | SHA-256 prefix |");
    sb.AppendLine("|---|---|---:|---|:---:|:---:|:---:|---:|---|");

    foreach (var item in results)
    {
        string hash = string.IsNullOrEmpty(item.JsonSha256) ? "—" : item.JsonSha256[..Math.Min(16, item.JsonSha256.Length)];
        sb.Append("| ").Append(Escape(item.Platform))
          .Append(" | ").Append(Escape(item.Sample))
          .Append(" | ").Append(FormatBytes(item.Bytes))
          .Append(" | ").Append(Escape(item.Status))
          .Append(" | ").Append(item.DesktopParity ? "✓" : "✗")
          .Append(" | ").Append(item.Deterministic ? "✓" : "✗")
          .Append(" | ").Append(item.ValidJson ? "✓" : "✗")
          .Append(" | ").Append(item.TopLevelProperties)
          .Append(" | `").Append(hash).AppendLine("` |");
    }

    return sb.ToString();
}

static string FormatBytes(long bytes) => bytes >= 1024 * 1024
    ? $"{bytes / 1024d / 1024d:0.00} MiB"
    : $"{bytes / 1024d:0.0} KiB";

static string Escape(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

internal sealed record ReferenceResult(TuyaExtractionStatus Status, string Json);

internal sealed record CorpusResult(
    string Platform,
    string Sample,
    long Bytes,
    string Status,
    bool Extracted,
    bool DesktopParity,
    bool Deterministic,
    bool ValidJson,
    bool WindowsLineEndings,
    int TopLevelProperties,
    string JsonSha256,
    string MagicPosition,
    string Message);
