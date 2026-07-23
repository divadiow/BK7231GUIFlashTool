using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BK7231Flasher
{
    /// <summary>
    /// Evidence-oriented BL602 I/O analyser.
    ///
    /// This is a managed C# port of the useful parts of the standalone Python
    /// BL602 dump analyser.  It deliberately produces a plain-text I/O report;
    /// it does not generate an OpenBeken template and does not infer active-high
    /// or active-low electrical levels.
    /// </summary>
    public static class BL602IoAnalyzer
    {
        private const uint FdtMagic = 0xD00DFEED;
        private static readonly byte[] FdtMagicBytes = { 0xD0, 0x0D, 0xFE, 0xED };
        private const uint FdtBeginNode = 1;
        private const uint FdtEndNode = 2;
        private const uint FdtProp = 3;
        private const uint FdtNop = 4;
        private const uint FdtEnd = 9;
        private const int DefaultUnusedPin = 200;

        public enum Confidence
        {
            Low = 1,
            Medium = 2,
            High = 3,
            VeryHigh = 4,
        }

        public sealed class AnalysisOptions
        {
            public bool DeepApplicationScan { get; set; }
            public bool IncludeGenericPeripheralClues { get; set; } = true;
            public int UnusedPinSentinel { get; set; } = DefaultUnusedPin;
        }

        public sealed class Finding
        {
            public int Pin { get; set; }
            public string Function { get; set; }
            public Confidence ConfidenceLevel { get; set; }
            public string Source { get; set; }
            public string Evidence { get; set; }
            public bool IsGenericClue { get; set; }
        }

        public sealed class DtbSummary
        {
            public int Offset { get; set; }
            public int Size { get; set; }
            public string Schema { get; set; }
            public string Model { get; set; }
            public string PcbaName { get; set; }
            public string Compatible { get; set; }
        }

        public sealed class AnalysisResult
        {
            public string DisplayName { get; set; }
            public int FileSize { get; set; }
            public List<DtbSummary> Dtbs { get; } = new List<DtbSummary>();
            public List<string> ProductIds { get; } = new List<string>();
            public List<string> ApplicationMarkers { get; } = new List<string>();
            public List<Finding> Findings { get; } = new List<Finding>();
            public List<string> Notes { get; } = new List<string>();
            public int ApplicationScanStart { get; set; }
            public int ApplicationScanEnd { get; set; }
            public int DecodedInstructionCount { get; set; }

            public bool FoundAnyDtb => Dtbs.Count != 0;

            public string ToPlainText(bool includeLowConfidence)
            {
                StringBuilder builder = new StringBuilder();
                builder.AppendLine("BL602 I/O findings");
                builder.AppendLine(new string('=', 78));
                builder.AppendLine("File: " + DisplayName);
                builder.AppendLine("Size: " + FileSize.ToString("N0", CultureInfo.InvariantCulture) + " bytes");
                builder.AppendLine("DTB candidates: " + Dtbs.Count.ToString(CultureInfo.InvariantCulture));

                if (ProductIds.Count != 0)
                    builder.AppendLine("Application product ID(s): " + string.Join(", ", ProductIds));
                if (ApplicationMarkers.Count != 0)
                    builder.AppendLine("Application markers: " + string.Join(", ", ApplicationMarkers));
                if (DecodedInstructionCount != 0)
                {
                    builder.AppendLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "Application scan: 0x{0:X}-0x{1:X} ({2:N0} decoded RV32/RVC instructions)",
                        ApplicationScanStart,
                        ApplicationScanEnd,
                        DecodedInstructionCount));
                }

                if (Dtbs.Count != 0)
                {
                    builder.AppendLine();
                    builder.AppendLine("DTB summary");
                    builder.AppendLine(new string('-', 78));
                    for (int index = 0; index < Dtbs.Count; index++)
                    {
                        DtbSummary dtb = Dtbs[index];
                        builder.Append("#").Append(index + 1)
                            .Append(" @ 0x").Append(dtb.Offset.ToString("X8", CultureInfo.InvariantCulture))
                            .Append(", 0x").Append(dtb.Size.ToString("X", CultureInfo.InvariantCulture))
                            .Append(" bytes: ").AppendLine(dtb.Schema ?? "unclassified");
                        if (!string.IsNullOrWhiteSpace(dtb.PcbaName))
                            builder.AppendLine("  PCBA: " + dtb.PcbaName);
                        if (!string.IsNullOrWhiteSpace(dtb.Model))
                            builder.AppendLine("  Model: " + dtb.Model);
                        if (!string.IsNullOrWhiteSpace(dtb.Compatible))
                            builder.AppendLine("  Compatible: " + dtb.Compatible);
                    }
                }

                List<Finding> selected = Findings
                    .Where(item => includeLowConfidence || item.ConfidenceLevel != Confidence.Low)
                    .OrderBy(item => item.Pin)
                    .ThenByDescending(item => item.ConfidenceLevel)
                    .ThenBy(item => item.Function, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                builder.AppendLine();
                builder.AppendLine("I/O table");
                builder.AppendLine(new string('-', 130));
                builder.AppendLine(Fixed("GPIO", 7) + Fixed("Likely function", 40) + Fixed("Confidence", 13) + Fixed("Source", 34) + "Evidence");
                builder.AppendLine(new string('-', 130));

                if (selected.Count == 0)
                {
                    builder.AppendLine("No reportable I/O findings at the selected confidence level.");
                }
                else
                {
                    foreach (Finding item in selected)
                    {
                        builder.Append(Fixed("GPIO" + item.Pin.ToString(CultureInfo.InvariantCulture), 7));
                        builder.Append(Fixed(item.Function, 40));
                        builder.Append(Fixed(ConfidenceLabel(item.ConfidenceLevel), 13));
                        builder.Append(Fixed(item.Source, 34));
                        builder.AppendLine(item.Evidence ?? string.Empty);
                    }
                }

                builder.AppendLine();
                builder.AppendLine("Interpretation notes");
                builder.AppendLine(new string('-', 78));
                builder.AppendLine("- No active-high/active-low electrical level is inferred.");
                builder.AppendLine("- No OpenBeken template is generated; this report is intentionally an I/O evidence table.");
                builder.AppendLine("- Repeated GPIO rows mean independent evidence or competing interpretations were found.");
                builder.AppendLine("- LOW findings are SDK/peripheral clues and may be generic rather than product wiring.");
                foreach (string note in Notes.Distinct())
                    builder.AppendLine("- " + note);

                return builder.ToString();
            }

            private static string Fixed(string value, int width)
            {
                string text = value ?? string.Empty;
                if (text.Length > width - 1)
                    text = text.Substring(0, width - 2) + "…";
                return text.PadRight(width);
            }

            private static string ConfidenceLabel(Confidence value)
            {
                switch (value)
                {
                    case Confidence.VeryHigh: return "VERY HIGH";
                    case Confidence.High: return "HIGH";
                    case Confidence.Medium: return "MEDIUM";
                    default: return "LOW";
                }
            }
        }

        private sealed class DtbProperty
        {
            public object Value;
            public string Kind;
            public byte[] Raw;
        }

        private sealed class DtbHeader
        {
            public int Base;
            public int TotalSize;
            public int StructOffset;
            public int StringsOffset;
            public int ReserveOffset;
            public int Version;
            public int LastCompatibleVersion;
            public int StringsSize;
            public int StructSize;
        }

        private sealed class RvInstruction
        {
            public int Offset;
            public int Size;
            public uint Raw;
            public string Kind = "other";
            public int? Rd;
            public int? Rs1;
            public int? Rs2;
            public int? Imm;
            public int? Target;
            public bool Control;
            public bool Call;
            public int? StoreWidth;
        }

        private sealed class CallRecord
        {
            public int Offset;
            public int Target;
            public int?[] Args;
            public int InstructionIndex;
        }

        private sealed class DirectPwmCandidate
        {
            public List<int> CallSites = new List<int>();
            public int Target;
            public List<int> Pins = new List<int>();
            public List<int?> Frequencies = new List<int?>();
            public List<int?> ThirdArgs = new List<int?>();
            public List<int> Residues = new List<int>();
            public double Score;
            public string Reason;
        }

        private sealed class StackPinTuple
        {
            public List<int> Offsets = new List<int>();
            public List<int> Values = new List<int>();
            public List<int> InstructionOffsets = new List<int>();
        }

        private sealed class DigitalOutputCandidate
        {
            public int Target;
            public List<int> Pins = new List<int>();
            public int CallCount;
            public int ResolvedBinaryCalls;
            public List<PinPairCount> ComplementaryPairs = new List<PinPairCount>();
        }

        private sealed class PinPairCount
        {
            public int First;
            public int Second;
            public int Count;
        }

        private sealed class RecentStore
        {
            public int StackOffset;
            public int Value;
            public int InstructionOffset;
        }

        private static readonly KeyValuePair<string, byte[][]>[] ApplicationMarkers =
        {
            new KeyValuePair<string, byte[][]>("direct PWM lighting", new[] { Bytes("loopset_pwm.c"), Bytes("PWM Trigger") }),
            new KeyValuePair<string, byte[][]>("metering framework", new[] { Bytes("dh_measure_mp"), Bytes("pwr_measure_correct") }),
            new KeyValuePair<string, byte[][]>("CHT8315 sensor", new[] { Bytes("doit_cht8315"), Bytes("CHT8315") }),
            new KeyValuePair<string, byte[][]>("BP5758D LED driver", new[] { Bytes("BP5758D") }),
            new KeyValuePair<string, byte[][]>("persistent output sort", new[] { Bytes("KEY_SORT"), Bytes("store pwm output sort") }),
            new KeyValuePair<string, byte[][]>("RGBWC bulb framework", new[] { Bytes("hs_framework_rgbwc_bulb") }),
            new KeyValuePair<string, byte[][]>("RGB controller framework", new[] { Bytes("hs_framework_rgb_controller") }),
            new KeyValuePair<string, byte[][]>("loopset LED", new[] { Bytes("loopset_led.c") }),
            new KeyValuePair<string, byte[][]>("IR framework", new[] { Bytes("loopset_ir"), Bytes("IR Trigger") }),
            new KeyValuePair<string, byte[][]>("SPI lighting", new[] { Bytes("SPI LED"), Bytes("spi_led"), Bytes("loopset_spi") }),
        };

        public static AnalysisResult Analyze(byte[] data, string displayName, AnalysisOptions options = null)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (options == null)
                options = new AnalysisOptions();

            AnalysisResult result = new AnalysisResult
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? "(buffer)" : displayName,
                FileSize = data.Length,
            };

            List<DtbHeader> headers = FindDtbs(data);
            bool hasStrongProductDtb = false;
            foreach (DtbHeader header in headers)
            {
                Dictionary<string, Dictionary<string, DtbProperty>> nodes;
                try
                {
                    nodes = ParseTree(data, header);
                }
                catch (Exception ex)
                {
                    result.Notes.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "DTB at 0x{0:X8} failed validation: {1}",
                        header.Base,
                        ex.Message));
                    continue;
                }

                string schema = IdentifySchema(nodes);
                DtbSummary summary = new DtbSummary
                {
                    Offset = header.Base,
                    Size = header.TotalSize,
                    Schema = schema,
                    Model = AsString(PropertyValue(nodes, "/", "model")),
                    PcbaName = AsString(PropertyValue(nodes, "/", "pcba_name")),
                    Compatible = AsString(PropertyValue(nodes, "/", "compatible")),
                };
                result.Dtbs.Add(summary);

                if (nodes.ContainsKey("/config_gpio"))
                {
                    hasStrongProductDtb = true;
                    ExtractConfigGpioFindings(result, nodes, options.UnusedPinSentinel);
                }

                if (ExtractVerifiedLightingFindings(result, nodes))
                    hasStrongProductDtb = true;

                ExtractLightingPinUseClues(result, nodes, options.UnusedPinSentinel);
                ExtractGenericGpioClues(result, nodes, options.UnusedPinSentinel);
                if (options.IncludeGenericPeripheralClues)
                    ExtractGenericPeripheralClues(result, nodes, options.UnusedPinSentinel);
            }

            List<string> productIds = DiscoverProductIds(data);
            result.ProductIds.AddRange(productIds);
            List<string> markers = DiscoverMarkers(data);
            result.ApplicationMarkers.AddRange(markers);

            bool shouldDecodeApplication = !hasStrongProductDtb || productIds.Count != 0 ||
                markers.Contains("direct PWM lighting") || markers.Contains("metering framework") ||
                markers.Contains("RGB controller framework") || markers.Contains("SPI lighting");

            if (shouldDecodeApplication)
                AnalyzeApplication(data, headers, productIds, markers, options.DeepApplicationScan, result);

            NormalizeFindings(result);
            if (!result.FoundAnyDtb)
                result.Notes.Add("No valid embedded DTB/FDT blob was found.");
            if (result.Findings.Count == 0)
                result.Notes.Add("No I/O mapping passed the current evidence rules.");
            return result;
        }

        private static byte[] Bytes(string text)
        {
            return Encoding.ASCII.GetBytes(text);
        }

        private static uint ReadBe32(byte[] data, int offset)
        {
            if (offset < 0 || offset + 4 > data.Length)
                throw new EndOfStreamException("BE32 read outside buffer");
            return ((uint)data[offset] << 24) |
                   ((uint)data[offset + 1] << 16) |
                   ((uint)data[offset + 2] << 8) |
                   data[offset + 3];
        }

        private static ushort ReadLe16(byte[] data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        private static uint ReadLe32(byte[] data, int offset)
        {
            return (uint)(data[offset] |
                (data[offset + 1] << 8) |
                (data[offset + 2] << 16) |
                (data[offset + 3] << 24));
        }

        private static int Align4(int value)
        {
            return (value + 3) & ~3;
        }

        private static DtbHeader TryParseHeader(byte[] data, int baseOffset)
        {
            if (baseOffset < 0 || baseOffset + 40 > data.Length)
                return null;
            if (ReadBe32(data, baseOffset) != FdtMagic)
                return null;

            uint totalSizeValue = ReadBe32(data, baseOffset + 4);
            uint structOffsetValue = ReadBe32(data, baseOffset + 8);
            uint stringsOffsetValue = ReadBe32(data, baseOffset + 12);
            uint reserveOffsetValue = ReadBe32(data, baseOffset + 16);
            uint versionValue = ReadBe32(data, baseOffset + 20);
            uint lastCompatibleValue = ReadBe32(data, baseOffset + 24);
            uint stringsSizeValue = ReadBe32(data, baseOffset + 32);
            uint structSizeValue = ReadBe32(data, baseOffset + 36);

            if (totalSizeValue < 40 || totalSizeValue > int.MaxValue)
                return null;
            int totalSize = (int)totalSizeValue;
            if (baseOffset > data.Length - totalSize)
                return null;
            if (structOffsetValue >= totalSizeValue || stringsOffsetValue >= totalSizeValue || reserveOffsetValue >= totalSizeValue)
                return null;
            if (structSizeValue != 0 && structOffsetValue + structSizeValue > totalSizeValue)
                return null;
            if (stringsSizeValue != 0 && stringsOffsetValue + stringsSizeValue > totalSizeValue)
                return null;
            if (versionValue < 16 || lastCompatibleValue > versionValue)
                return null;
            if (structOffsetValue > int.MaxValue || stringsOffsetValue > int.MaxValue || reserveOffsetValue > int.MaxValue ||
                versionValue > int.MaxValue || lastCompatibleValue > int.MaxValue || stringsSizeValue > int.MaxValue || structSizeValue > int.MaxValue)
                return null;

            return new DtbHeader
            {
                Base = baseOffset,
                TotalSize = totalSize,
                StructOffset = (int)structOffsetValue,
                StringsOffset = (int)stringsOffsetValue,
                ReserveOffset = (int)reserveOffsetValue,
                Version = (int)versionValue,
                LastCompatibleVersion = (int)lastCompatibleValue,
                StringsSize = (int)stringsSizeValue,
                StructSize = (int)structSizeValue,
            };
        }

        private static List<DtbHeader> FindDtbs(byte[] data)
        {
            List<DtbHeader> headers = new List<DtbHeader>();
            int position = 0;
            while (position <= data.Length - FdtMagicBytes.Length)
            {
                int found = IndexOf(data, FdtMagicBytes, position, data.Length);
                if (found < 0)
                    break;
                DtbHeader header = TryParseHeader(data, found);
                if (header != null)
                {
                    try
                    {
                        ParseTree(data, header);
                        headers.Add(header);
                        position = found + Math.Max(4, header.TotalSize);
                    }
                    catch
                    {
                        position = found + 4;
                    }
                }
                else
                {
                    position = found + 4;
                }
            }
            return headers;
        }

        private static Dictionary<string, Dictionary<string, DtbProperty>> ParseTree(byte[] data, DtbHeader header)
        {
            byte[] blob = new byte[header.TotalSize];
            Buffer.BlockCopy(data, header.Base, blob, 0, header.TotalSize);
            int offset = header.StructOffset;
            int stringsBase = header.StringsOffset;
            List<string> stack = new List<string>();
            Dictionary<string, Dictionary<string, DtbProperty>> nodes =
                new Dictionary<string, Dictionary<string, DtbProperty>>(StringComparer.Ordinal);
            nodes["/"] = new Dictionary<string, DtbProperty>(StringComparer.Ordinal);
            bool sawEnd = false;

            while (offset + 4 <= blob.Length)
            {
                uint token = ReadBe32(blob, offset);
                offset += 4;
                if (token == FdtBeginNode)
                {
                    string name = ReadCString(blob, offset, out int nextOffset);
                    offset = Align4(nextOffset);
                    if (!string.IsNullOrEmpty(name))
                        stack.Add(name);
                    string path = CurrentPath(stack);
                    if (!nodes.ContainsKey(path))
                        nodes[path] = new Dictionary<string, DtbProperty>(StringComparer.Ordinal);
                }
                else if (token == FdtEndNode)
                {
                    if (stack.Count != 0)
                        stack.RemoveAt(stack.Count - 1);
                }
                else if (token == FdtProp)
                {
                    if (offset + 8 > blob.Length)
                        throw new InvalidDataException("truncated FDT_PROP header");
                    uint lengthValue = ReadBe32(blob, offset);
                    uint nameOffsetValue = ReadBe32(blob, offset + 4);
                    offset += 8;
                    if (lengthValue > int.MaxValue || nameOffsetValue > int.MaxValue)
                        throw new InvalidDataException("oversized FDT property");
                    int length = (int)lengthValue;
                    int nameOffset = (int)nameOffsetValue;
                    if (length < 0 || offset > blob.Length - length)
                        throw new InvalidDataException("property extends beyond DTB");
                    byte[] raw = new byte[length];
                    if (length != 0)
                        Buffer.BlockCopy(blob, offset, raw, 0, length);
                    offset = Align4(offset + length);
                    string propertyName = ReadCString(blob, stringsBase + nameOffset, out _);
                    string path = CurrentPath(stack);
                    if (!nodes.ContainsKey(path))
                        nodes[path] = new Dictionary<string, DtbProperty>(StringComparer.Ordinal);
                    nodes[path][propertyName] = DecodeProperty(raw);
                }
                else if (token == FdtNop)
                {
                    continue;
                }
                else if (token == FdtEnd)
                {
                    sawEnd = true;
                    break;
                }
                else
                {
                    throw new InvalidDataException("unknown FDT token 0x" + token.ToString("X8", CultureInfo.InvariantCulture));
                }
            }

            if (!sawEnd)
                throw new InvalidDataException("DTB structure has no FDT_END token");
            return nodes;
        }

        private static string CurrentPath(List<string> stack)
        {
            return stack.Count == 0 ? "/" : "/" + string.Join("/", stack);
        }

        private static string ReadCString(byte[] data, int offset, out int nextOffset)
        {
            if (offset < 0 || offset >= data.Length)
                throw new InvalidDataException("string offset outside DTB");
            int end = offset;
            while (end < data.Length && data[end] != 0)
                end++;
            if (end >= data.Length)
                throw new InvalidDataException("unterminated DTB string");
            nextOffset = end + 1;
            return Encoding.UTF8.GetString(data, offset, end - offset);
        }

        private static DtbProperty DecodeProperty(byte[] raw)
        {
            if (raw.Length == 0)
                return new DtbProperty { Value = true, Kind = "boolean", Raw = raw };

            if (raw[0] >= 32 && raw[0] < 127 && raw[raw.Length - 1] == 0)
            {
                List<string> parts = new List<string>();
                int start = 0;
                bool valid = true;
                for (int index = 0; index < raw.Length; index++)
                {
                    if (raw[index] != 0)
                        continue;
                    if (index == start || !IsPrintableAscii(raw, start, index - start))
                    {
                        valid = false;
                        break;
                    }
                    parts.Add(Encoding.UTF8.GetString(raw, start, index - start));
                    start = index + 1;
                }
                if (valid && start == raw.Length && parts.Count != 0)
                {
                    return new DtbProperty
                    {
                        Value = parts.Count == 1 ? (object)parts[0] : parts.ToArray(),
                        Kind = parts.Count == 1 ? "string" : "string-list",
                        Raw = raw,
                    };
                }
            }

            if ((raw.Length & 3) == 0)
            {
                uint[] cells = new uint[raw.Length / 4];
                for (int index = 0; index < cells.Length; index++)
                    cells[index] = ReadBe32(raw, index * 4);
                return new DtbProperty
                {
                    Value = cells.Length == 1 ? (object)cells[0] : cells,
                    Kind = cells.Length == 1 ? "u32" : "u32-list",
                    Raw = raw,
                };
            }

            return new DtbProperty
            {
                Value = BitConverter.ToString(raw).Replace("-", string.Empty),
                Kind = "bytes",
                Raw = raw,
            };
        }

        private static bool IsPrintableAscii(byte[] data, int offset, int length)
        {
            if (length <= 0)
                return false;
            for (int index = offset; index < offset + length; index++)
            {
                byte value = data[index];
                if (value != 9 && value != 10 && value != 13 && (value < 32 || value >= 127))
                    return false;
            }
            return true;
        }

        private static object PropertyValue(
            Dictionary<string, Dictionary<string, DtbProperty>> nodes,
            string path,
            string propertyName)
        {
            if (!nodes.TryGetValue(path, out Dictionary<string, DtbProperty> properties))
                return null;
            if (!properties.TryGetValue(propertyName, out DtbProperty property))
                return null;
            return property.Value;
        }

        private static string AsString(object value)
        {
            if (value is string text)
                return text;
            if (value is string[] values)
                return string.Join(",", values);
            return null;
        }

        private static int? ScalarPin(object value, int unusedSentinel)
        {
            int converted;
            if (value is uint unsignedValue && unsignedValue <= int.MaxValue)
                converted = (int)unsignedValue;
            else if (value is int signedValue)
                converted = signedValue;
            else
                return null;
            if (converted < 0 || converted > 63 || converted == unusedSentinel)
                return null;
            return converted;
        }

        private static List<int> IntList(object value)
        {
            if (value is uint[] unsignedValues)
                return unsignedValues.Where(item => item <= int.MaxValue).Select(item => (int)item).ToList();
            if (value is int[] signedValues)
                return signedValues.ToList();
            return new List<int>();
        }

        private static string IdentifySchema(Dictionary<string, Dictionary<string, DtbProperty>> nodes)
        {
            if (nodes.ContainsKey("/config_gpio"))
                return "product-specific /config_gpio";
            if (nodes.ContainsKey("/light_config"))
                return "product lighting configuration";
            if (nodes.ContainsKey("/gpio") && nodes.ContainsKey("/pwm"))
                return "generic SDK peripheral tree";
            return "unclassified DTB";
        }

        private static void ExtractConfigGpioFindings(
            AnalysisResult result,
            Dictionary<string, Dictionary<string, DtbProperty>> nodes,
            int unusedSentinel)
        {
            int? wifiPin = ScalarPin(PropertyValue(nodes, "/config_gpio", "wifi_led_pin"), unusedSentinel);
            if (wifiPin.HasValue)
            {
                AddFinding(result, wifiPin.Value, "Wi-Fi/status LED", Confidence.High,
                    "DTB /config_gpio", "wifi_led_pin", false);
            }

            HashSet<int> retainedChannels = new HashSet<int>();
            if (nodes.TryGetValue("/config_gpio/config_channel", out Dictionary<string, DtbProperty> channelProperties))
            {
                foreach (KeyValuePair<string, DtbProperty> pair in channelProperties.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    Match match = Regex.Match(pair.Key, "^channel(\\d+)_pin$", RegexOptions.CultureInvariant);
                    if (!match.Success)
                        continue;
                    int channel = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    int? pin = ScalarPin(pair.Value.Value, unusedSentinel);
                    if (!pin.HasValue)
                        continue;

                    retainedChannels.Add(channel);
                    AddFinding(result, pin.Value,
                        "Relay/output channel " + (channel + 1).ToString(CultureInfo.InvariantCulture),
                        Confidence.VeryHigh,
                        "DTB /config_gpio/config_channel",
                        pair.Key,
                        false);
                }
            }

            if (nodes.TryGetValue("/config_gpio/config_key", out Dictionary<string, DtbProperty> keyProperties))
            {
                foreach (KeyValuePair<string, DtbProperty> pair in keyProperties.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    Match match = Regex.Match(pair.Key, "^key(\\d+)_pin$", RegexOptions.CultureInvariant);
                    if (!match.Success)
                        continue;
                    int keyIndex = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    int? pin = ScalarPin(pair.Value.Value, unusedSentinel);
                    if (!pin.HasValue)
                        continue;

                    bool paired = retainedChannels.Contains(keyIndex);
                    AddFinding(result, pin.Value,
                        "Key/input " + (keyIndex + 1).ToString(CultureInfo.InvariantCulture),
                        paired || keyIndex == 0 ? Confidence.High : Confidence.Medium,
                        "DTB /config_gpio/config_key",
                        pair.Key + (paired ? "; paired with output channel" : "; precise button/toggle role unresolved"),
                        false);
                }
            }

            ExtractLedGroup(result, nodes, "/config_gpio/config_led", "led", "Indicator LED", unusedSentinel, false);
            ExtractLedGroup(result, nodes, "/config_gpio/config_channel_led", "channel", "Channel indicator LED", unusedSentinel, true);

            AddNamedPin(result, nodes, "/config_gpio", "cf_pin", unusedSentinel,
                "BL0937 CF metering input", Confidence.VeryHigh, "DTB /config_gpio metering");
            AddNamedPin(result, nodes, "/config_gpio", "cf1_pin", unusedSentinel,
                "BL0937 CF1 metering input", Confidence.VeryHigh, "DTB /config_gpio metering");
            AddNamedPin(result, nodes, "/config_gpio", "sel_pin", unusedSentinel,
                "BL0937 SEL output", Confidence.VeryHigh, "DTB /config_gpio metering");

            if (nodes.TryGetValue("/config_gpio/config_lr_channel", out Dictionary<string, DtbProperty> latchingProperties))
            {
                for (int channel = 0; channel < 8; channel++)
                {
                    string onName = "lr_channel" + channel.ToString(CultureInfo.InvariantCulture) + "_on_pin";
                    string offName = "lr_channel" + channel.ToString(CultureInfo.InvariantCulture) + "_off_pin";
                    if (latchingProperties.TryGetValue(onName, out DtbProperty onProperty))
                    {
                        int? pin = ScalarPin(onProperty.Value, unusedSentinel);
                        if (pin.HasValue)
                            AddFinding(result, pin.Value,
                                "Latching relay channel " + (channel + 1).ToString(CultureInfo.InvariantCulture) + " ON coil",
                                Confidence.High,
                                "DTB /config_gpio/config_lr_channel",
                                onName,
                                false);
                    }
                    if (latchingProperties.TryGetValue(offName, out DtbProperty offProperty))
                    {
                        int? pin = ScalarPin(offProperty.Value, unusedSentinel);
                        if (pin.HasValue)
                            AddFinding(result, pin.Value,
                                "Latching relay channel " + (channel + 1).ToString(CultureInfo.InvariantCulture) + " OFF coil",
                                Confidence.High,
                                "DTB /config_gpio/config_lr_channel",
                                offName,
                                false);
                    }
                }
            }

            AddNamedPin(result, nodes, "/config_gpio", "all_on_off_pin", unusedSentinel,
                "All-on/all-off input", Confidence.High, "DTB /config_gpio");
            AddNamedPin(result, nodes, "/config_gpio", "backlight_pin", unusedSentinel,
                "Backlight output", Confidence.High, "DTB /config_gpio");

            AddNamedPin(result, nodes, "/config_gpio/config_log", "log_tx_pin", unusedSentinel,
                "Firmware log UART TX", Confidence.High, "DTB /config_gpio/config_log");
            AddNamedPin(result, nodes, "/config_gpio/config_log", "log_rx_pin", unusedSentinel,
                "Firmware log UART RX", Confidence.High, "DTB /config_gpio/config_log");
        }

        private static void ExtractLedGroup(
            AnalysisResult result,
            Dictionary<string, Dictionary<string, DtbProperty>> nodes,
            string path,
            string prefix,
            string description,
            int unusedSentinel,
            bool channelLed)
        {
            if (!nodes.TryGetValue(path, out Dictionary<string, DtbProperty> properties))
                return;
            foreach (KeyValuePair<string, DtbProperty> pair in properties.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                string pattern = channelLed ? "^channel(\\d+)_led_pin$" : "^led(\\d+)_pin$";
                Match match = Regex.Match(pair.Key, pattern, RegexOptions.CultureInvariant);
                if (!match.Success)
                    continue;
                int index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                int? pin = ScalarPin(pair.Value.Value, unusedSentinel);
                if (!pin.HasValue)
                    continue;
                AddFinding(result, pin.Value,
                    description + " " + (index + 1).ToString(CultureInfo.InvariantCulture),
                    Confidence.High,
                    path,
                    pair.Key,
                    false);
            }
        }

        private static void AddNamedPin(
            AnalysisResult result,
            Dictionary<string, Dictionary<string, DtbProperty>> nodes,
            string path,
            string propertyName,
            int unusedSentinel,
            string function,
            Confidence confidence,
            string source)
        {
            int? pin = ScalarPin(PropertyValue(nodes, path, propertyName), unusedSentinel);
            if (pin.HasValue)
                AddFinding(result, pin.Value, function, confidence, source, propertyName, false);
        }

        private static bool ExtractVerifiedLightingFindings(
            AnalysisResult result,
            Dictionary<string, Dictionary<string, DtbProperty>> nodes)
        {
            string pcba = AsString(PropertyValue(nodes, "/", "pcba_name"));
            string output = AsString(PropertyValue(nodes, "/light_config", "output_chl"));
            List<int> pinConfig = IntList(PropertyValue(nodes, "/water_lamp", "pin_cfg"));

            if (pcba == "CHIP-04" && output == "B,G,R,C,W" && SequenceEqual(pinConfig, 2, 3, 4, 14, 20, 21))
            {
                AddFinding(result, 3, "PWM lighting output 1 (verified family order)", Confidence.VeryHigh,
                    "Verified DTB/PCBA fingerprint", "CHIP-04 + B,G,R,C,W + exact pin_cfg", false);
                AddFinding(result, 14, "PWM lighting output 2 (verified family order)", Confidence.VeryHigh,
                    "Verified DTB/PCBA fingerprint", "CHIP-04 + B,G,R,C,W + exact pin_cfg", false);
                AddFinding(result, 20, "PWM lighting output 3 (verified family order)", Confidence.VeryHigh,
                    "Verified DTB/PCBA fingerprint", "CHIP-04 + B,G,R,C,W + exact pin_cfg", false);
                AddFinding(result, 21, "PWM lighting output 4 (verified family order)", Confidence.VeryHigh,
                    "Verified DTB/PCBA fingerprint", "CHIP-04 + B,G,R,C,W + exact pin_cfg", false);
                AddFinding(result, 2, "PWM lighting output 5 (verified family order)", Confidence.VeryHigh,
                    "Verified DTB/PCBA fingerprint", "CHIP-04 + B,G,R,C,W + exact pin_cfg", false);
                return true;
            }

            if ((pcba == "SM-030" || pcba == "SM-030_01") && output == "W,0,0,C,0" &&
                SequenceEqual(pinConfig, 2, 3, 4, 14, 20, 21))
            {
                AddFinding(result, 21, "PWM lighting output 4 (verified family order)", Confidence.VeryHigh,
                    "Verified DTB/PCBA fingerprint", pcba + " + W,0,0,C,0 + exact pin_cfg", false);
                AddFinding(result, 20, "PWM lighting output 5 (verified family order)", Confidence.VeryHigh,
                    "Verified DTB/PCBA fingerprint", pcba + " + W,0,0,C,0 + exact pin_cfg", false);
                return true;
            }
            return false;
        }

        private static void ExtractLightingPinUseClues(
            AnalysisResult result,
            Dictionary<string, Dictionary<string, DtbProperty>> nodes,
            int unusedSentinel)
        {
            List<int> pins = IntList(PropertyValue(nodes, "/water_lamp", "pin_cfg"));
            if (pins.Count == 0)
                return;
            foreach (int pin in pins.Distinct())
            {
                if (pin >= 0 && pin <= 63 && pin != unusedSentinel)
                {
                    AddFinding(result, pin, "Firmware lighting pin-use list; role unresolved", Confidence.Low,
                        "DTB /water_lamp", "pin_cfg member", true);
                }
            }
            string output = AsString(PropertyValue(nodes, "/light_config", "output_chl"));
            if (!string.IsNullOrWhiteSpace(output))
                result.Notes.Add("light_config output_chl=" + output + "; water_lamp pin_cfg is not treated as a positional output map without a verified fingerprint.");
        }

        private static void ExtractGenericGpioClues(
            AnalysisResult result,
            Dictionary<string, Dictionary<string, DtbProperty>> nodes,
            int unusedSentinel)
        {
            foreach (KeyValuePair<string, Dictionary<string, DtbProperty>> node in nodes.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (!node.Key.StartsWith("/gpio/gpio", StringComparison.Ordinal) || node.Key.Count(character => character == '/') != 2)
                    continue;
                int? pin = ScalarPin(GetProperty(node.Value, "pin"), unusedSentinel);
                if (!pin.HasValue)
                    continue;
                string feature = AsString(GetProperty(node.Value, "feature")) ?? "GPIO";
                string mode = AsString(GetProperty(node.Value, "mode"));
                string status = AsString(GetProperty(node.Value, "status"));
                string function = "Generic SDK " + feature + " declaration";
                string evidence = node.Key + (string.IsNullOrWhiteSpace(mode) ? string.Empty : "; mode=" + mode) +
                    (string.IsNullOrWhiteSpace(status) ? string.Empty : "; status=" + status);
                AddFinding(result, pin.Value, function, Confidence.Low, "Generic DTB /gpio", evidence, true);
            }
        }

        private static void ExtractGenericPeripheralClues(
            AnalysisResult result,
            Dictionary<string, Dictionary<string, DtbProperty>> nodes,
            int unusedSentinel)
        {
            ExtractSimplePinNode(result, nodes, "/ir/rx", "IR receiver DTB declaration", unusedSentinel);
            ExtractSimplePinNode(result, nodes, "/ir/tx", "IR transmitter DTB declaration", unusedSentinel);

            foreach (KeyValuePair<string, Dictionary<string, DtbProperty>> node in nodes.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (!node.Key.EndsWith("/pin", StringComparison.Ordinal))
                    continue;
                string family;
                if (node.Key.StartsWith("/uart/", StringComparison.Ordinal)) family = "UART";
                else if (node.Key.StartsWith("/spi/", StringComparison.Ordinal)) family = "SPI";
                else if (node.Key.StartsWith("/i2c/", StringComparison.Ordinal)) family = "I2C";
                else if (node.Key.StartsWith("/i2s/", StringComparison.Ordinal)) family = "I2S";
                else continue;

                string parent = node.Key.Substring(0, node.Key.Length - 4);
                string status = AsString(PropertyValue(nodes, parent, "status"));
                if (!string.Equals(status, "okay", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (KeyValuePair<string, DtbProperty> property in node.Value)
                {
                    int? pin = ScalarPin(property.Value.Value, unusedSentinel);
                    if (!pin.HasValue)
                        continue;
                    AddFinding(result, pin.Value,
                        family + " " + property.Key.ToUpperInvariant() + " DTB declaration",
                        Confidence.Low,
                        "Generic DTB peripheral",
                        node.Key + ":" + property.Key,
                        true);
                }
            }

            if (nodes.TryGetValue("/pwm", out _))
            {
                foreach (KeyValuePair<string, Dictionary<string, DtbProperty>> node in nodes.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    if (!node.Key.StartsWith("/pwm/pwm@", StringComparison.Ordinal))
                        continue;
                    string status = AsString(GetProperty(node.Value, "status"));
                    if (!string.Equals(status, "okay", StringComparison.OrdinalIgnoreCase))
                        continue;
                    int? pin = ScalarPin(GetProperty(node.Value, "pin"), unusedSentinel);
                    if (!pin.HasValue)
                        continue;
                    object idValue = GetProperty(node.Value, "id");
                    AddFinding(result, pin.Value,
                        "SDK PWM peripheral declaration",
                        Confidence.Low,
                        "Generic DTB /pwm",
                        node.Key + "; id=" + FormatScalar(idValue),
                        true);
                }
            }
        }

        private static void ExtractSimplePinNode(
            AnalysisResult result,
            Dictionary<string, Dictionary<string, DtbProperty>> nodes,
            string path,
            string function,
            int unusedSentinel)
        {
            string status = AsString(PropertyValue(nodes, path, "status"));
            if (!string.Equals(status, "okay", StringComparison.OrdinalIgnoreCase))
                return;
            int? pin = ScalarPin(PropertyValue(nodes, path, "pin"), unusedSentinel);
            if (pin.HasValue)
                AddFinding(result, pin.Value, function, Confidence.Low, "Generic DTB peripheral", path, true);
        }

        private static object GetProperty(Dictionary<string, DtbProperty> properties, string name)
        {
            return properties.TryGetValue(name, out DtbProperty value) ? value.Value : null;
        }

        private static string FormatScalar(object value)
        {
            if (value == null)
                return "?";
            if (value is uint unsignedValue)
                return unsignedValue.ToString(CultureInfo.InvariantCulture);
            if (value is int signedValue)
                return signedValue.ToString(CultureInfo.InvariantCulture);
            return value.ToString();
        }

        private static bool SequenceEqual(List<int> values, params int[] expected)
        {
            return values.Count == expected.Length && values.SequenceEqual(expected);
        }

        private static List<string> DiscoverProductIds(byte[] data)
        {
            List<string> found = new List<string>();
            byte[] directMarker = Bytes("dh_pid");
            int position = 0;
            while (position <= data.Length - directMarker.Length)
            {
                int index = IndexOf(data, directMarker, position, data.Length);
                if (index < 0)
                    break;
                int start = index + directMarker.Length;
                StringBuilder token = new StringBuilder();
                for (int cursor = start; cursor < data.Length && cursor < start + 32; cursor++)
                {
                    byte characterValue = data[cursor];
                    if (!IsProductIdCharacter(characterValue))
                        break;
                    token.Append((char)characterValue);
                }
                string value = token.ToString().TrimEnd('_', '-');
                if (value.Length >= 4 && !found.Contains(value))
                    found.Add(value);
                position = index + directMarker.Length;
            }
            if (found.Count != 0)
                return found.Take(8).ToList();

            byte[] fallbackMarker = Bytes("device_product_id");
            position = 0;
            HashSet<string> rejected = new HashSet<string>(StringComparer.Ordinal)
            {
                "device_product_id", "product_id", "user_token", "firmware_version",
                "restart_reason", "lib_ota", "device_id", "product_key",
            };
            while (position <= data.Length - fallbackMarker.Length)
            {
                int index = IndexOf(data, fallbackMarker, position, data.Length);
                if (index < 0)
                    break;
                int regionEnd = Math.Min(data.Length, index + 256);
                int cursor = index;
                while (cursor < regionEnd)
                {
                    while (cursor < regionEnd && data[cursor] == 0)
                        cursor++;
                    int start = cursor;
                    while (cursor < regionEnd && IsProductIdCharacter(data[cursor]))
                        cursor++;
                    int length = cursor - start;
                    if (length >= 5 && length <= 16 && (cursor == regionEnd || data[cursor] == 0))
                    {
                        string value = Encoding.ASCII.GetString(data, start, length);
                        if (!rejected.Contains(value) && !found.Contains(value))
                            found.Add(value);
                    }
                    cursor++;
                }
                position = index + fallbackMarker.Length;
            }
            return found.Take(8).ToList();
        }

        private static bool IsProductIdCharacter(byte value)
        {
            return (value >= (byte)'A' && value <= (byte)'Z') ||
                   (value >= (byte)'a' && value <= (byte)'z') ||
                   (value >= (byte)'0' && value <= (byte)'9') ||
                   value == (byte)'_' || value == (byte)'-';
        }

        private static List<string> DiscoverMarkers(byte[] data)
        {
            List<string> markers = new List<string>();
            foreach (KeyValuePair<string, byte[][]> entry in ApplicationMarkers)
            {
                if (entry.Value.Any(needle => IndexOf(data, needle, 0, data.Length) >= 0))
                    markers.Add(entry.Key);
            }
            return markers;
        }

        private static int SignExtend(uint value, int bits)
        {
            uint sign = 1u << (bits - 1);
            uint mask = bits == 32 ? uint.MaxValue : ((1u << bits) - 1u);
            value &= mask;
            return unchecked((int)((value ^ sign) - sign));
        }

        private static RvInstruction DecodeRvc(ushort halfword, int offset)
        {
            int quadrant = halfword & 0x3;
            int funct3 = (halfword >> 13) & 0x7;
            RvInstruction instruction = new RvInstruction { Offset = offset, Size = 2, Raw = halfword };

            if (quadrant == 1 && funct3 == 0)
            {
                int rd = (halfword >> 7) & 0x1F;
                uint immediateBits = (uint)((((halfword >> 12) & 1) << 5) | ((halfword >> 2) & 0x1F));
                instruction.Kind = "addi";
                instruction.Rd = rd;
                instruction.Rs1 = rd;
                instruction.Imm = SignExtend(immediateBits, 6);
            }
            else if (quadrant == 1 && funct3 == 2)
            {
                int rd = (halfword >> 7) & 0x1F;
                uint immediateBits = (uint)((((halfword >> 12) & 1) << 5) | ((halfword >> 2) & 0x1F));
                instruction.Kind = "li";
                instruction.Rd = rd;
                instruction.Rs1 = 0;
                instruction.Imm = SignExtend(immediateBits, 6);
            }
            else if (quadrant == 1 && funct3 == 3)
            {
                int rd = (halfword >> 7) & 0x1F;
                if (rd != 0 && rd != 2)
                {
                    uint immediateBits = (uint)((((halfword >> 12) & 1) << 17) | (((halfword >> 2) & 0x1F) << 12));
                    instruction.Kind = "lui";
                    instruction.Rd = rd;
                    instruction.Imm = SignExtend(immediateBits, 18);
                }
                else
                {
                    instruction.Rd = rd;
                }
            }
            else if (quadrant == 2 && funct3 == 4)
            {
                int rd = (halfword >> 7) & 0x1F;
                int rs2 = (halfword >> 2) & 0x1F;
                int bit12 = (halfword >> 12) & 1;
                if (bit12 == 0 && rs2 != 0)
                {
                    instruction.Kind = "mv";
                    instruction.Rd = rd;
                    instruction.Rs1 = rs2;
                }
                else if (bit12 == 1 && rs2 != 0)
                {
                    instruction.Kind = "add";
                    instruction.Rd = rd;
                    instruction.Rs1 = rd;
                    instruction.Rs2 = rs2;
                }
                else if (bit12 == 0 && rs2 == 0)
                {
                    instruction.Kind = "jr";
                    instruction.Rs1 = rd;
                    instruction.Control = true;
                }
                else
                {
                    instruction.Kind = "jalr";
                    instruction.Rs1 = rd;
                    instruction.Control = true;
                    instruction.Call = rd != 0;
                }
            }
            else if (quadrant == 1 && (funct3 == 1 || funct3 == 5))
            {
                uint immediateBits = (uint)(
                    (((halfword >> 12) & 1) << 11) |
                    (((halfword >> 11) & 1) << 4) |
                    (((halfword >> 9) & 0x3) << 8) |
                    (((halfword >> 8) & 1) << 10) |
                    (((halfword >> 7) & 1) << 6) |
                    (((halfword >> 6) & 1) << 7) |
                    (((halfword >> 3) & 0x7) << 1) |
                    (((halfword >> 2) & 1) << 5));
                int immediate = SignExtend(immediateBits, 12);
                instruction.Kind = funct3 == 1 ? "jal" : "j";
                instruction.Target = offset + immediate;
                instruction.Control = true;
                instruction.Call = funct3 == 1;
            }
            else if (quadrant == 1 && (funct3 == 6 || funct3 == 7))
            {
                instruction.Kind = "branch";
                instruction.Control = true;
            }
            else
            {
                if (quadrant == 0 && (funct3 == 2 || funct3 == 3))
                {
                    instruction.Kind = "load";
                    instruction.Rd = 8 + ((halfword >> 2) & 0x7);
                }
                else if (quadrant == 2 && (funct3 == 2 || funct3 == 3))
                {
                    instruction.Kind = "load";
                    instruction.Rd = (halfword >> 7) & 0x1F;
                }
            }
            return instruction;
        }

        private static RvInstruction DecodeRv32(uint word, int offset)
        {
            int opcode = (int)(word & 0x7F);
            int rd = (int)((word >> 7) & 0x1F);
            int funct3 = (int)((word >> 12) & 0x7);
            int rs1 = (int)((word >> 15) & 0x1F);
            int rs2 = (int)((word >> 20) & 0x1F);
            RvInstruction instruction = new RvInstruction { Offset = offset, Size = 4, Raw = word };

            if (opcode == 0x13 && funct3 == 0)
            {
                int immediate = SignExtend((word >> 20) & 0xFFF, 12);
                instruction.Kind = rs1 == 0 ? "li" : "addi";
                instruction.Rd = rd;
                instruction.Rs1 = rs1;
                instruction.Imm = immediate;
            }
            else if (opcode == 0x37)
            {
                instruction.Kind = "lui";
                instruction.Rd = rd;
                instruction.Imm = unchecked((int)(word & 0xFFFFF000));
            }
            else if (opcode == 0x17)
            {
                instruction.Kind = "auipc";
                instruction.Rd = rd;
                instruction.Imm = unchecked((int)(word & 0xFFFFF000));
            }
            else if (opcode == 0x6F)
            {
                uint immediateBits =
                    (((word >> 31) & 1) << 20) |
                    (((word >> 12) & 0xFF) << 12) |
                    (((word >> 20) & 1) << 11) |
                    (((word >> 21) & 0x3FF) << 1);
                int immediate = SignExtend(immediateBits, 21);
                instruction.Kind = "jal";
                instruction.Rd = rd;
                instruction.Target = offset + immediate;
                instruction.Control = true;
                instruction.Call = rd == 1;
            }
            else if (opcode == 0x67)
            {
                instruction.Kind = "jalr";
                instruction.Rd = rd;
                instruction.Rs1 = rs1;
                instruction.Imm = SignExtend((word >> 20) & 0xFFF, 12);
                instruction.Control = true;
                instruction.Call = rd == 1;
            }
            else if (opcode == 0x63)
            {
                instruction.Kind = "branch";
                instruction.Rs1 = rs1;
                instruction.Rs2 = rs2;
                instruction.Control = true;
            }
            else if (opcode == 0x23)
            {
                uint immediateBits = (((word >> 25) & 0x7F) << 5) | ((word >> 7) & 0x1F);
                instruction.Kind = "store";
                instruction.Rs1 = rs1;
                instruction.Rs2 = rs2;
                instruction.Imm = SignExtend(immediateBits, 12);
                if (funct3 == 0) instruction.StoreWidth = 1;
                else if (funct3 == 1) instruction.StoreWidth = 2;
                else if (funct3 == 2) instruction.StoreWidth = 4;
            }
            else if (opcode == 0x03)
            {
                instruction.Kind = "load";
                instruction.Rd = rd;
                instruction.Rs1 = rs1;
            }
            else if (opcode == 0x33 || opcode == 0x1B || opcode == 0x73)
            {
                instruction.Kind = "other";
                instruction.Rd = rd;
                instruction.Rs1 = rs1;
                instruction.Rs2 = rs2;
            }
            return instruction;
        }

        private static List<RvInstruction> DecodeRiscV(byte[] data, int start, int end)
        {
            List<RvInstruction> instructions = new List<RvInstruction>();
            int offset = Math.Max(0, start & ~1);
            end = Math.Min(end, data.Length);
            while (offset + 2 <= end)
            {
                ushort halfword = ReadLe16(data, offset);
                RvInstruction instruction;
                if ((halfword & 0x3) != 0x3)
                {
                    instruction = DecodeRvc(halfword, offset);
                }
                else if (offset + 4 <= end)
                {
                    instruction = DecodeRv32(ReadLe32(data, offset), offset);
                }
                else
                {
                    break;
                }
                instructions.Add(instruction);
                offset += instruction.Size;
            }
            return instructions;
        }

        private static int?[] RecoverCallArguments(List<RvInstruction> instructions, int index, int limit = 36)
        {
            int[] registers = { 10, 11, 12, 13 };
            Dictionary<int, int> sourceRegisters = registers.ToDictionary(item => item, item => item);
            Dictionary<int, int> adjustments = registers.ToDictionary(item => item, item => 0);
            Dictionary<int, int?> values = registers.ToDictionary(item => item, item => (int?)null);
            HashSet<int> dead = new HashSet<int>();

            int first = Math.Max(0, index - limit);
            for (int previous = index - 1; previous >= first; previous--)
            {
                RvInstruction instruction = instructions[previous];
                if (instruction.Control)
                    break;
                if (!instruction.Rd.HasValue)
                    continue;
                int destination = instruction.Rd.Value;
                List<int> dependants = registers
                    .Where(argument => !dead.Contains(argument) && sourceRegisters[argument] == destination)
                    .ToList();
                if (dependants.Count == 0)
                    continue;

                if ((instruction.Kind == "li" || instruction.Kind == "lui") && instruction.Imm.HasValue)
                {
                    foreach (int argument in dependants)
                    {
                        values[argument] = instruction.Imm.Value + adjustments[argument];
                        dead.Add(argument);
                    }
                }
                else if (instruction.Kind == "mv" && instruction.Rs1.HasValue)
                {
                    foreach (int argument in dependants)
                        sourceRegisters[argument] = instruction.Rs1.Value;
                }
                else if (instruction.Kind == "addi" && instruction.Rs1.HasValue && instruction.Imm.HasValue)
                {
                    foreach (int argument in dependants)
                    {
                        if (instruction.Rs1.Value == 0)
                        {
                            values[argument] = instruction.Imm.Value + adjustments[argument];
                            dead.Add(argument);
                        }
                        else
                        {
                            sourceRegisters[argument] = instruction.Rs1.Value;
                            adjustments[argument] += instruction.Imm.Value;
                        }
                    }
                }
                else
                {
                    foreach (int argument in dependants)
                        dead.Add(argument);
                }

                if (dead.Count == registers.Length)
                    break;
            }

            return registers.Select(argument => values[argument]).ToArray();
        }

        private static List<CallRecord> CollectDirectCalls(List<RvInstruction> instructions)
        {
            List<CallRecord> calls = new List<CallRecord>();
            for (int index = 0; index < instructions.Count; index++)
            {
                RvInstruction instruction = instructions[index];
                if (!instruction.Call || !instruction.Target.HasValue)
                    continue;
                calls.Add(new CallRecord
                {
                    Offset = instruction.Offset,
                    Target = instruction.Target.Value,
                    Args = RecoverCallArguments(instructions, index),
                    InstructionIndex = index,
                });
            }
            return calls;
        }

        private static List<DirectPwmCandidate> FindDirectPwmCandidates(List<CallRecord> calls, List<string> markers)
        {
            List<DirectPwmCandidate> candidates = new List<DirectPwmCandidate>();
            if (!markers.Contains("direct PWM lighting"))
                return candidates;

            int index = 0;
            while (index < calls.Count)
            {
                List<CallRecord> run = new List<CallRecord> { calls[index] };
                int cursor = index + 1;
                while (cursor < calls.Count)
                {
                    CallRecord previous = run[run.Count - 1];
                    CallRecord current = calls[cursor];
                    if (current.Target == previous.Target &&
                        current.Offset - previous.Offset <= 40 &&
                        current.InstructionIndex - previous.InstructionIndex <= 18)
                    {
                        run.Add(current);
                        cursor++;
                    }
                    else
                    {
                        break;
                    }
                }

                for (int startAt = 0; startAt < run.Count; startAt++)
                {
                    for (int length = 5; length >= 2; length--)
                    {
                        if (startAt + length > run.Count)
                            continue;
                        List<CallRecord> subset = run.GetRange(startAt, length);
                        if (subset.Any(item => !item.Args[0].HasValue || item.Args[0].Value < 0 || item.Args[0].Value > 31))
                            continue;
                        List<int> pins = subset.Select(item => item.Args[0].Value).ToList();
                        if (pins.Distinct().Count() != length)
                            continue;
                        List<int> residues = pins.Select(pin => pin % 5).ToList();
                        List<int?> frequencies = subset.Select(item => item.Args[1]).ToList();
                        List<int?> thirdArguments = subset.Select(item => item.Args[2]).ToList();
                        bool frequencyEvidence = frequencies.Any(value => value.HasValue && value.Value >= 100 && value.Value <= 1000000);
                        bool safeThirdArguments = thirdArguments.All(value => !value.HasValue || value.Value == 0);
                        bool completeChannels = length == 5 && residues.Distinct().OrderBy(item => item).SequenceEqual(new[] { 0, 1, 2, 3, 4 });

                        double score;
                        string reason;
                        if (completeChannels && frequencyEvidence && safeThirdArguments)
                        {
                            score = 0.88;
                            reason = "five consecutive same-target calls; distinct GPIOs; GPIO%5 covers all BL602 PWM channels; frequency argument present";
                        }
                        else if (length >= 3 && residues.Distinct().Count() == length && frequencyEvidence)
                        {
                            score = 0.76;
                            reason = "repeated same-target GPIO calls with distinct PWM channel residues and frequency evidence";
                        }
                        else
                        {
                            continue;
                        }

                        candidates.Add(new DirectPwmCandidate
                        {
                            CallSites = subset.Select(item => item.Offset).ToList(),
                            Target = subset[0].Target,
                            Pins = pins,
                            Frequencies = frequencies,
                            ThirdArgs = thirdArguments,
                            Residues = residues,
                            Score = score,
                            Reason = reason,
                        });
                    }
                }
                index = Math.Max(cursor, index + 1);
            }

            Dictionary<string, DirectPwmCandidate> unique = new Dictionary<string, DirectPwmCandidate>(StringComparer.Ordinal);
            foreach (DirectPwmCandidate candidate in candidates)
            {
                string key = candidate.Target.ToString(CultureInfo.InvariantCulture) + ":" + string.Join(",", candidate.Pins);
                if (!unique.TryGetValue(key, out DirectPwmCandidate current) || candidate.Score > current.Score)
                    unique[key] = candidate;
            }

            List<DirectPwmCandidate> ordered = unique.Values
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Pins.Count)
                .ThenBy(item => item.CallSites[0])
                .ToList();
            List<DirectPwmCandidate> accepted = new List<DirectPwmCandidate>();
            foreach (DirectPwmCandidate candidate in ordered)
            {
                HashSet<int> candidateSites = new HashSet<int>(candidate.CallSites);
                bool contained = accepted.Any(existing =>
                    existing.Target == candidate.Target &&
                    candidateSites.IsSubsetOf(new HashSet<int>(existing.CallSites)) &&
                    existing.Score >= candidate.Score);
                if (!contained)
                    accepted.Add(candidate);
            }
            return accepted;
        }

        private static List<StackPinTuple> FindStackPinTuples(List<RvInstruction> instructions)
        {
            Dictionary<int, int> registerConstants = new Dictionary<int, int> { [0] = 0 };
            List<RecentStore> recentStores = new List<RecentStore>();
            Dictionary<string, StackPinTuple> candidates = new Dictionary<string, StackPinTuple>(StringComparer.Ordinal);

            Action considerRecent = () =>
            {
                if (recentStores.Count < 3)
                    return;
                for (int length = 5; length >= 3; length--)
                {
                    if (recentStores.Count < length)
                        continue;
                    List<RecentStore> subset = recentStores.Skip(recentStores.Count - length).ToList();
                    List<int> offsets = subset.Select(item => item.StackOffset).ToList();
                    List<int> values = subset.Select(item => item.Value).ToList();
                    List<int> instructionOffsets = subset.Select(item => item.InstructionOffset).ToList();
                    bool consecutive = true;
                    for (int offsetIndex = 1; offsetIndex < offsets.Count; offsetIndex++)
                    {
                        if (offsets[offsetIndex] != offsets[0] + offsetIndex)
                        {
                            consecutive = false;
                            break;
                        }
                    }
                    if (!consecutive || values.Any(value => value < 0 || value > 31) || values.Distinct().Count() != length)
                        continue;
                    if (instructionOffsets[instructionOffsets.Count - 1] - instructionOffsets[0] > 80)
                        continue;
                    string key = string.Join(",", offsets) + "|" + string.Join(",", values);
                    candidates[key] = new StackPinTuple
                    {
                        Offsets = offsets,
                        Values = values,
                        InstructionOffsets = instructionOffsets,
                    };
                    break;
                }
            };

            foreach (RvInstruction instruction in instructions)
            {
                if (instruction.Control)
                {
                    registerConstants = new Dictionary<int, int> { [0] = 0 };
                    recentStores.Clear();
                    continue;
                }

                if ((instruction.Kind == "li" || instruction.Kind == "lui") && instruction.Rd.HasValue && instruction.Imm.HasValue)
                {
                    registerConstants[instruction.Rd.Value] = instruction.Imm.Value;
                }
                else if (instruction.Kind == "mv" && instruction.Rd.HasValue && instruction.Rs1.HasValue)
                {
                    if (registerConstants.TryGetValue(instruction.Rs1.Value, out int value))
                        registerConstants[instruction.Rd.Value] = value;
                    else
                        registerConstants.Remove(instruction.Rd.Value);
                }
                else if (instruction.Kind == "addi" && instruction.Rd.HasValue && instruction.Rs1.HasValue && instruction.Imm.HasValue)
                {
                    if (registerConstants.TryGetValue(instruction.Rs1.Value, out int value))
                        registerConstants[instruction.Rd.Value] = value + instruction.Imm.Value;
                    else
                        registerConstants.Remove(instruction.Rd.Value);
                }
                else if (instruction.Kind == "store" && instruction.StoreWidth == 1 && instruction.Rs1 == 2 &&
                    instruction.Rs2.HasValue && instruction.Imm.HasValue)
                {
                    if (registerConstants.TryGetValue(instruction.Rs2.Value, out int value) && value >= 0 && value <= 31)
                    {
                        recentStores.Add(new RecentStore
                        {
                            StackOffset = instruction.Imm.Value,
                            Value = value,
                            InstructionOffset = instruction.Offset,
                        });
                        while (recentStores.Count > 8)
                            recentStores.RemoveAt(0);
                        considerRecent();
                    }
                }
                else if (instruction.Rd.HasValue)
                {
                    registerConstants.Remove(instruction.Rd.Value);
                }

                if (recentStores.Count != 0 && instruction.Offset - recentStores[recentStores.Count - 1].InstructionOffset > 96)
                    recentStores.Clear();
            }

            List<StackPinTuple> ordered = candidates.Values
                .OrderByDescending(item => item.Values.Count)
                .ThenBy(item => item.InstructionOffsets[0])
                .ToList();
            List<StackPinTuple> accepted = new List<StackPinTuple>();
            foreach (StackPinTuple candidate in ordered)
            {
                bool contained = accepted.Any(existing =>
                    candidate.InstructionOffsets[0] >= existing.InstructionOffsets[0] &&
                    candidate.InstructionOffsets[candidate.InstructionOffsets.Count - 1] <= existing.InstructionOffsets[existing.InstructionOffsets.Count - 1] &&
                    candidate.Values.Count < existing.Values.Count);
                if (!contained)
                    accepted.Add(candidate);
            }
            return accepted.OrderBy(item => item.InstructionOffsets[0]).ToList();
        }

        private static List<DigitalOutputCandidate> FindDigitalOutputCandidates(List<CallRecord> calls)
        {
            Dictionary<int, List<CallRecord>> grouped = new Dictionary<int, List<CallRecord>>();
            foreach (CallRecord call in calls)
            {
                int? pin = call.Args[0];
                if (!pin.HasValue || pin.Value < 0 || pin.Value > 31)
                    continue;
                if (!grouped.TryGetValue(call.Target, out List<CallRecord> records))
                {
                    records = new List<CallRecord>();
                    grouped[call.Target] = records;
                }
                records.Add(call);
            }

            List<DigitalOutputCandidate> results = new List<DigitalOutputCandidate>();
            foreach (KeyValuePair<int, List<CallRecord>> group in grouped)
            {
                List<CallRecord> records = group.Value;
                List<CallRecord> binaryRecords = records.Where(record => record.Args[1] == 0 || record.Args[1] == 1).ToList();
                if (records.Count < 5 || binaryRecords.Count < 4)
                    continue;
                List<int> recurrentPins = records
                    .GroupBy(record => record.Args[0].Value)
                    .Where(pinGroup => pinGroup.Count() >= 2)
                    .Select(pinGroup => pinGroup.Key)
                    .OrderBy(pin => pin)
                    .ToList();
                if (recurrentPins.Count < 2 || recurrentPins.Count > 8)
                    continue;

                Dictionary<string, PinPairCount> pairCounts = new Dictionary<string, PinPairCount>(StringComparer.Ordinal);
                binaryRecords = binaryRecords.OrderBy(record => record.Offset).ToList();
                for (int index = 0; index + 1 < binaryRecords.Count; index++)
                {
                    CallRecord first = binaryRecords[index];
                    CallRecord second = binaryRecords[index + 1];
                    if (second.Offset - first.Offset > 24 || first.Args[0] == second.Args[0] || first.Args[1] == second.Args[1])
                        continue;
                    int firstPin = Math.Min(first.Args[0].Value, second.Args[0].Value);
                    int secondPin = Math.Max(first.Args[0].Value, second.Args[0].Value);
                    string key = firstPin.ToString(CultureInfo.InvariantCulture) + ":" + secondPin.ToString(CultureInfo.InvariantCulture);
                    if (!pairCounts.TryGetValue(key, out PinPairCount count))
                    {
                        count = new PinPairCount { First = firstPin, Second = secondPin };
                        pairCounts[key] = count;
                    }
                    count.Count++;
                }

                results.Add(new DigitalOutputCandidate
                {
                    Target = group.Key,
                    Pins = recurrentPins,
                    CallCount = records.Count,
                    ResolvedBinaryCalls = binaryRecords.Count,
                    ComplementaryPairs = pairCounts.Values.OrderByDescending(item => item.Count).ToList(),
                });
            }

            return results
                .OrderByDescending(item => item.CallCount)
                .ThenByDescending(item => item.ResolvedBinaryCalls)
                .ThenBy(item => item.Target)
                .ToList();
        }

        private static void AnalyzeApplication(
            byte[] data,
            List<DtbHeader> headers,
            List<string> productIds,
            List<string> markers,
            bool deep,
            AnalysisResult result)
        {
            int start = data.Length > 0x12000 ? 0x10000 : 0;
            int firstDtb = headers.Count == 0 ? data.Length : headers.Min(item => item.Base);
            int end = deep ? firstDtb : Math.Min(Math.Min(firstDtb, data.Length), 0x90000);
            if (end <= start)
            {
                start = 0;
                end = Math.Min(firstDtb, data.Length);
            }
            result.ApplicationScanStart = start;
            result.ApplicationScanEnd = end;

            List<RvInstruction> instructions = DecodeRiscV(data, start, end);
            result.DecodedInstructionCount = instructions.Count;
            List<CallRecord> calls = CollectDirectCalls(instructions);
            List<DirectPwmCandidate> pwmCandidates = FindDirectPwmCandidates(calls, markers);
            List<StackPinTuple> stackTuples = FindStackPinTuples(instructions);
            List<DigitalOutputCandidate> digitalOutputs = FindDigitalOutputCandidates(calls);

            bool p93sfgApplied = false;
            if (productIds.Contains("p93sfg") && markers.Contains("direct PWM lighting"))
            {
                DirectPwmCandidate candidate = pwmCandidates.FirstOrDefault(item =>
                    item.Pins.SequenceEqual(new[] { 14, 20, 21, 22, 3 }) &&
                    item.Residues.Distinct().OrderBy(value => value).SequenceEqual(new[] { 0, 1, 2, 3, 4 }));
                if (candidate != null)
                {
                    int[] pins = { 14, 20, 21, 22, 3 };
                    for (int index = 0; index < pins.Length; index++)
                    {
                        AddFinding(result, pins[index],
                            "PWM lighting output " + (index + 1).ToString(CultureInfo.InvariantCulture) + " (verified p93sfg order)",
                            Confidence.VeryHigh,
                            "Application fingerprint p93sfg",
                            "product ID + five-call PWM sequence + complete GPIO%5 channel coverage",
                            false);
                    }
                    p93sfgApplied = true;
                }
            }

            if (!p93sfgApplied)
            {
                foreach (DirectPwmCandidate candidate in pwmCandidates)
                {
                    Confidence confidence = candidate.Score >= 0.85 ? Confidence.High : Confidence.Medium;
                    for (int index = 0; index < candidate.Pins.Count; index++)
                    {
                        int pin = candidate.Pins[index];
                        AddFinding(result, pin,
                            "Direct PWM output candidate (call order " + (index + 1).ToString(CultureInfo.InvariantCulture) + ")",
                            confidence,
                            "Application RV32 analysis",
                            "sequence [" + string.Join(",", candidate.Pins) + "]; GPIO%5=" + pin % 5 + "; " + candidate.Reason,
                            false);
                    }
                }
            }

            bool lAx3ezApplied = false;
            if (productIds.Contains("lAx3ez") && markers.Contains("metering framework") &&
                stackTuples.Any(item => item.Values.SequenceEqual(new[] { 21, 22, 8 })))
            {
                AddFinding(result, 21, "BL0937 CF metering input", Confidence.VeryHigh,
                    "Application fingerprint lAx3ez", "metering marker + consecutive stack pin tuple [21,22,8]", false);
                AddFinding(result, 22, "BL0937 CF1 metering input", Confidence.VeryHigh,
                    "Application fingerprint lAx3ez", "metering marker + consecutive stack pin tuple [21,22,8]", false);
                AddFinding(result, 8, "BL0937 SEL output", Confidence.VeryHigh,
                    "Application fingerprint lAx3ez", "metering marker + consecutive stack pin tuple [21,22,8]", false);
                lAx3ezApplied = true;
            }

            foreach (StackPinTuple tuple in stackTuples)
            {
                if (lAx3ezApplied && tuple.Values.SequenceEqual(new[] { 21, 22, 8 }))
                    continue;
                if (!(markers.Contains("metering framework") || markers.Contains("direct PWM lighting") ||
                      markers.Contains("RGB controller framework") || markers.Contains("SPI lighting") ||
                      markers.Contains("CHT8315 sensor")))
                    continue;
                foreach (int pin in tuple.Values)
                {
                    AddFinding(result, pin,
                        "Compact application pin tuple member; role unresolved",
                        Confidence.Low,
                        "Application RV32 analysis",
                        "tuple [" + string.Join(",", tuple.Values) + "] near 0x" + tuple.InstructionOffsets[0].ToString("X", CultureInfo.InvariantCulture),
                        true);
                }
            }

            foreach (DigitalOutputCandidate candidate in digitalOutputs.Take(8))
            {
                foreach (int pin in candidate.Pins)
                {
                    AddFinding(result, pin,
                        "Application-controlled digital output candidate",
                        Confidence.Medium,
                        "Application RV32 analysis",
                        "recurrent calls to target 0x" + candidate.Target.ToString("X", CultureInfo.InvariantCulture) +
                        "; " + candidate.CallCount.ToString(CultureInfo.InvariantCulture) + " calls",
                        false);
                }
                foreach (PinPairCount pair in candidate.ComplementaryPairs.Where(item => item.Count >= 2).Take(4))
                {
                    string evidence = "paired calls involving GPIO" + pair.First.ToString(CultureInfo.InvariantCulture) +
                        " and GPIO" + pair.Second.ToString(CultureInfo.InvariantCulture) +
                        "; role may be bridge, latching relay or dual indicator";
                    AddFinding(result, pair.First, "Paired application output candidate", Confidence.Medium,
                        "Application RV32 analysis", evidence, false);
                    AddFinding(result, pair.Second, "Paired application output candidate", Confidence.Medium,
                        "Application RV32 analysis", evidence, false);
                }
            }

            if (markers.Contains("persistent output sort"))
                result.Notes.Add("A persistent output-order table (KEY_SORT) exists in the application; its logical colour order is reported as unresolved unless a verified family fingerprint matches.");
            if (markers.Contains("BP5758D LED driver"))
                result.Notes.Add("The application contains a BP5758D LED-driver implementation; driver data/clock pins require caller-specific analysis before assignment.");
            if (markers.Contains("SPI lighting"))
                result.Notes.Add("The application contains an SPI/addressable-lighting framework; an extracted small pin tuple is not promoted to a data pin without a matched driver call.");
        }

        private static void AddFinding(
            AnalysisResult result,
            int pin,
            string function,
            Confidence confidence,
            string source,
            string evidence,
            bool generic)
        {
            if (pin < 0 || pin > 63)
                return;
            result.Findings.Add(new Finding
            {
                Pin = pin,
                Function = function ?? "Unknown I/O",
                ConfidenceLevel = confidence,
                Source = source ?? string.Empty,
                Evidence = evidence ?? string.Empty,
                IsGenericClue = generic,
            });
        }

        private static void NormalizeFindings(AnalysisResult result)
        {
            List<Finding> normalized = new List<Finding>();
            foreach (IGrouping<string, Finding> group in result.Findings.GroupBy(item =>
                item.Pin.ToString(CultureInfo.InvariantCulture) + "|" + item.Function + "|" + item.Source,
                StringComparer.Ordinal))
            {
                Finding best = group.OrderByDescending(item => item.ConfidenceLevel).First();
                string combinedEvidence = string.Join("; ", group.Select(item => item.Evidence)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Distinct(StringComparer.Ordinal));
                normalized.Add(new Finding
                {
                    Pin = best.Pin,
                    Function = best.Function,
                    ConfidenceLevel = best.ConfidenceLevel,
                    Source = best.Source,
                    Evidence = combinedEvidence,
                    IsGenericClue = group.All(item => item.IsGenericClue),
                });
            }
            result.Findings.Clear();
            result.Findings.AddRange(normalized);

            foreach (IGrouping<int, Finding> pinGroup in normalized.GroupBy(item => item.Pin))
            {
                List<Finding> strong = pinGroup.Where(item => (int)item.ConfidenceLevel >= (int)Confidence.High).ToList();
                if (strong.Select(item => item.Function).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                {
                    result.Notes.Add("GPIO" + pinGroup.Key.ToString(CultureInfo.InvariantCulture) +
                        " has more than one high-confidence interpretation; review the evidence rows rather than selecting one automatically.");
                }
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start, int endExclusive)
        {
            if (needle == null || needle.Length == 0)
                return start;
            int final = Math.Min(endExclusive, haystack.Length) - needle.Length;
            for (int index = Math.Max(0, start); index <= final; index++)
            {
                bool equal = true;
                for (int offset = 0; offset < needle.Length; offset++)
                {
                    if (haystack[index + offset] != needle[offset])
                    {
                        equal = false;
                        break;
                    }
                }
                if (equal)
                    return index;
            }
            return -1;
        }
    }
}
