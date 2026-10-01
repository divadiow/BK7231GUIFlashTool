# Tuya Dump JSON for Android

An offline Android wrapper around the current `TuyaConfig.cs` extractor from BK7231 Easy Flasher.

## Extraction parity

The project pins the exact upstream file from:

- Repository: `openshwprojects/BK7231GUIFlashTool`
- Commit: `412a42a31ff9d03cd9c0d144ae1197e3352b78b5`
- Path: `BK7231Flasher/Utils/TuyaConfig.cs`
- SHA-256: `05d89d933cf8d2f0a1cb4f866dc96b50d1111d71293f5aa13a0255366285943a`

The Android app follows the current Windows form logic:

1. Load the entire binary.
2. Run `fromBytes()`.
3. Run classic `extractKeys()` to populate the same fallback parameters.
4. Render `getEnhancedExtractionText()`.
5. Fall back to `getKeysAsJSON()` only when the desktop code would do so.

A namespace-local newline shim makes `Environment.NewLine` resolve to CRLF, matching Windows output without editing the pinned extractor file. The app does not parse and reserialise successful output, so key ordering, indentation, escaping and values come from the original extractor.

## App behaviour

- Open a dump from Android's Storage Access Framework or use **Open with** from a file manager.
- View selectable JSON in a monospace field.
- Copy, save or share the exact rendered JSON.
- No Internet permission and no telemetry.
- 64 MiB input safety limit.
- Android 6.0/API 23 or later.

## Sensitive data

Tuya flash dumps and extracted JSON may contain Wi-Fi SSIDs/passwords, UUIDs, authentication keys, PSKs and other identifiers. The app processes files locally. Treat saved/shared JSON as secret-bearing material.

## Validation

The CI workflow uses the same core assembly as the Android app and tests representative complete binaries selected from `divadiow/FlashDumps/IoT` across multiple platform-name families. It checks:

- byte-for-byte parity with an independent simulation of the current Windows form decision path;
- deterministic repeated extraction;
- valid JSON output;
- Windows CRLF output;
- source-file hash pinning.

Reports contain output hashes and structure counts, not extracted JSON or credentials.
