# BK7231 GUI Flash Tool — Refactor & Modernization Roadmap

This document proposes a practical modernization plan that improves maintainability, reliability, testability, and contributor experience while preserving device support and user workflows.

## 1) Goals and Constraints

### Primary goals
- Keep existing flashing behavior and device coverage stable.
- Reduce coupling between UI, protocol logic, and hardware I/O.
- Make regressions detectable with repeatable tests.
- Enable safer future changes (new chip families, transport layers, UX updates).

### Non-goals (initially)
- Rewriting all logic at once.
- Dropping Windows Forms immediately.
- Removing Mono/.NET Framework compatibility before migration alternatives exist.

## 2) Current Architecture Risks

Based on repository layout and naming, the project appears to have a single WinForms application with substantial business logic embedded in forms and many protocol-specific classes.

High-risk areas likely include:
- **UI-to-core coupling**: `FormMain*` files likely contain orchestration, protocol branching, and direct hardware calls.
- **Protocol duplication**: Multiple flasher classes can drift in behavior (timeouts, retries, progress reporting, error handling).
- **Cross-cutting concerns**: Logging, cancellation, file checks, and CRC may be inconsistently applied.
- **Operational risk**: Hardware interactions are difficult to test without abstraction/mocking.

## 3) Target Architecture (Incremental)

Adopt a layered architecture:

1. **Domain/Core layer** (`BK7231Flasher.Core`)
   - Flash operation models (`FlashJob`, `BackupJob`, `VerifyJob`).
   - Shared policies: retries, timeout strategy, cancellation, progress events.
   - Validation and preflight checks.

2. **Protocol/Device layer** (`BK7231Flasher.Protocols`)
   - Per-family adapters implementing common interfaces:
     - `IFlashTransport`
     - `IChipFlasher`
     - `IImageVerifier`
   - Keep existing class logic but move into adapter pattern.

3. **Infrastructure layer** (`BK7231Flasher.Infrastructure`)
   - Serial communication wrappers.
   - File system abstractions.
   - Download/HTTP services.
   - Native interop wrappers for EasyFlash.

4. **Application/UI layer** (`BK7231Flasher.WinForms`)
   - Forms only for interaction and state display.
   - No protocol branching in event handlers.
   - Uses application services (`FlashWorkflowService`, `FirmwareDownloadService`, etc.).

## 4) Refactor Sequence (Low-risk Steps)

### Phase 0 — Guardrails first (1–2 weeks)
- Add CI for build + formatting + static analysis.
- Add smoke tests for core helper functions (CRC, bit utils, config parsing).
- Add reproducible sample fixtures from `testDumps` for parser tests.

### Phase 1 — Introduce interfaces (1–2 weeks)
- Define `IProgressReporter`, `ILogger`, `ISerialPort`, `IClock`, `IRandom`.
- Wrap existing static/helpers behind interfaces.
- Introduce dependency injection (e.g., `Microsoft.Extensions.DependencyInjection`) without changing behavior.

### Phase 2 — Extract workflows from forms (2–4 weeks)
- Move flash/backup/download orchestration out of `FormMain` into services.
- Convert form button handlers into thin command invocations.
- Centralize exception-to-user-message translation.

### Phase 3 — Protocol unification pass (2–4 weeks)
- Standardize operation lifecycle across flasher implementations:
  - preflight
  - handshake
  - read/write
  - verify
  - finalize
- Normalize progress and error types for consistent UX.

### Phase 4 — Reliability + observability (1–2 weeks)
- Structured logging with operation IDs.
- Persist operation summaries (chip, baud, success/failure, timings).
- Better diagnostics for common user issues (wrong chip selection, unstable UART).

### Phase 5 — Runtime modernization (optional branch)
- Multi-target project (`net48` + modern .NET, e.g. `net8.0-windows`).
- Gradually adopt nullable reference types and analyzers.
- Evaluate UI migration path (WinForms retained vs WPF/Avalonia).

## 5) Testing Strategy

### Unit tests
- CRC, encryption/decryption helpers, frame parsers, partition utilities.
- Edge cases: partial reads, malformed headers, wrong chip IDs.

### Integration tests
- Fake serial transport for protocol flows.
- Golden-file verification for read/write sequences.

### Hardware-in-loop (later)
- Keep existing batch scripts but add standard result parsing.
- Tag tests by chipset and required hardware.

## 6) Coding Standards to Introduce

- Enable nullable reference types in newly touched projects.
- Enforce analyzer ruleset and stylecop-lite baseline.
- Replace magic numbers with named constants/enums for protocol opcodes and flash layout values.
- Standardize async/cancellation semantics (avoid blocking UI thread).

## 7) UX Improvements Worth Doing During Refactor

- Operation wizard mode (backup only, flash only, backup+flash+verify).
- Device profile presets (chip family + common baud + reset strategy).
- Real-time troubleshooting hints based on error code heuristics.
- Safer defaults for destructive operations (explicit confirmation on full erase).

## 8) Migration KPIs

Track measurable outcomes each phase:
- Build reproducibility in CI.
- Test coverage trend for core services.
- Mean time to diagnose failures (from logs).
- Reduction in bug-fix lead time for protocol-specific changes.
- Number of form event handlers containing protocol logic (target: near zero).

## 9) Suggested First PRs

1. Add CI build + test pipeline.
2. Extract `FlashWorkflowService` skeleton and wire current flow through it.
3. Add serial abstraction and one protocol implementation behind interface.
4. Add parser/CRC unit tests using a small fixture subset.

---

If desired, this roadmap can be followed by a concrete issue breakdown (epics/tasks) with effort estimates per subsystem (`FormMain`, `Flashers`, `Utils`, `Misc`).
