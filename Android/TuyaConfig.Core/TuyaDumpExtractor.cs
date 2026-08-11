using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BK7231Flasher
{
    public enum TuyaExtractionStatus
    {
        Success,
        ErasedFlash,
        OpenBekenConfig,
        NoTuyaConfigFound,
        ExtractionFailed,
        InvalidInput,
        Exception
    }

    public sealed record TuyaExtractionResult(
        TuyaExtractionStatus Status,
        string Json,
        string Message,
        string HumanReadable,
        string MagicPosition,
        IReadOnlyList<string> LogLines)
    {
        public bool Success => Status == TuyaExtractionStatus.Success;
        public string Sha256 => string.IsNullOrEmpty(Json)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json))).ToLowerInvariant();
    }

    /// <summary>
    /// Reproduces the current FormMain_TuyaConfigExtractor.cs decision path. TuyaConfig.cs itself
    /// is the pinned upstream file; the Android layer does not reimplement any extraction logic.
    /// </summary>
    public static class TuyaDumpExtractor
    {
        public const string UpstreamRepository = "openshwprojects/BK7231GUIFlashTool";
        public const string UpstreamCommit = "412a42a31ff9d03cd9c0d144ae1197e3352b78b5";
        public const string UpstreamPath = "BK7231Flasher/Utils/TuyaConfig.cs";
        public const string UpstreamSha256 = "05d89d933cf8d2f0a1cb4f866dc96b50d1111d71293f5aa13a0255366285943a";

        public static TuyaExtractionResult Extract(byte[]? data)
        {
            if (data == null || data.Length == 0)
            {
                return Failure(TuyaExtractionStatus.InvalidInput, "The selected file is empty.");
            }

            var logs = new List<string>();
            FormMain? previousLogger = FormMain.Singleton;
            FormMain.Singleton = new FormMain(message =>
            {
                if (!string.IsNullOrWhiteSpace(message))
                    logs.Add(message.TrimEnd('\r', '\n'));
            });

            try
            {
                var tc = new TuyaConfig();

                // fromBytes() returns false when it located/decrypted a supported Tuya config.
                if (tc.fromBytes(data) == false)
                {
                    // extractKeys() likewise returns false on classic extraction success.
                    bool classicExtractFailed = tc.extractKeys();
                    bool hasEnhancedFallback = classicExtractFailed && tc.hasEnhancedExtractionData();

                    if (!classicExtractFailed || hasEnhancedFallback)
                    {
                        string output;
                        try
                        {
                            output = tc.getEnhancedExtractionText();
                        }
                        catch
                        {
                            output = tc.getKeysAsJSON();
                        }

                        if (string.IsNullOrWhiteSpace(output))
                            output = tc.getKeysAsJSON();

                        if (string.IsNullOrWhiteSpace(output))
                            return Failure(TuyaExtractionStatus.ExtractionFailed, "The Tuya config was decrypted, but no JSON could be rendered.", logs);

                        // The namespace-local Environment shim makes TuyaConfig emit CRLF exactly as the
                        // Windows application does. Validate without reserialising or changing ordering.
                        try
                        {
                            using var _ = JsonDocument.Parse(output);
                        }
                        catch (Exception ex)
                        {
                            return Failure(TuyaExtractionStatus.ExtractionFailed, "Extractor output was not valid JSON: " + ex.Message, logs);
                        }

                        string humanReadable = string.Empty;
                        try
                        {
                            humanReadable = tc.getKeysHumanReadableEnhanced();
                        }
                        catch
                        {
                            // Human-readable pin hints are auxiliary and never replace the JSON result.
                        }

                        return new TuyaExtractionResult(
                            TuyaExtractionStatus.Success,
                            output,
                            "Tuya configuration extracted successfully.",
                            humanReadable,
                            tc.getMagicPositionDecAndHex(),
                            logs);
                    }

                    return Failure(TuyaExtractionStatus.ExtractionFailed, "The Tuya vault was found, but key extraction failed.", logs);
                }

                if (tc.isLastBinaryOBKConfig())
                    return Failure(TuyaExtractionStatus.OpenBekenConfig, "This is an OpenBeken CFG file, not a Tuya flash dump.", logs);

                if (tc.isLastBinaryFullOf0xff())
                    return Failure(TuyaExtractionStatus.ErasedFlash, "The selected data is entirely 0xFF and appears erased.", logs);

                return Failure(TuyaExtractionStatus.NoTuyaConfigFound, "No supported Tuya configuration was found in this binary.", logs);
            }
            catch (Exception ex)
            {
                return Failure(TuyaExtractionStatus.Exception, ex.ToString(), logs);
            }
            finally
            {
                FormMain.Singleton = previousLogger;
            }
        }

        private static TuyaExtractionResult Failure(
            TuyaExtractionStatus status,
            string message,
            IReadOnlyList<string>? logs = null) =>
            new(status, string.Empty, message, string.Empty, string.Empty, logs ?? Array.Empty<string>());
    }
}
