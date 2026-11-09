# HoardPad — Technical Design (Phase 4)

> **Note:** This is the internal design record written during development, published as-is for transparency. It cites internal working files (the original project brief, brand guide, review reports, spike results and project rules files) that are not included in this repository; those citations are kept for traceability.

| Field | Value |
|---|---|
| **Project** | HoardPad |
| **Document** | `docs/tech-design.md` — Phase-4 technical design (translates the PRD's WHAT into HOW; folds in every measured Phase-4 spike result) |
| **Status** | Draft v1 — authored 2026-06-19 from the five completed §11.1 spikes + the two M18 sub-spikes |
| **Owner** | The maintainer |
| **Supersedes** | The §11.1 "PROVISIONAL pending SPIKE-N" markers in `docs/prd.md` (the PRD reconciliation that flips those markers to measured gates is a separate pass; this doc is the measured-input-of-record for it) |
| **Related docs** | [`docs/prd.md`](prd.md) (the contract), `brief.md`, `CLAUDE.md`, the five spike-results docs + the two sub-spike `RESULTS.md` files (cited inline), future `docs/architecture.md` (Phase 5) |
| **SDLC phase** | Phase 4 — technical design (feeds Phase 5 architecture; capstone the Phase-8 `/code` build executes against) |

This document is binding design. It is read top-to-bottom and references the PRD by stable id (`FR-26`, `§9.4`, `C-9`, `NFR-4`). It does **not** restate the PRD's normative requirements — it pins the numbers and specifies the HOW the PRD defers to tech-design. Where a value was PROVISIONAL in the PRD, the table in §1 traces it to its measured/decided value and the spike that produced it. Where a spike produced an honest deferral (Windows interception runtime, the `MoveFileEx` power-cut), this doc carries the deferral forward and does **not** assert it "Met" (§6).

## How to read this document

- **§1 — Measured-gate summary.** Every PROVISIONAL PRD number flipped to its measured value, each traced to a spike. The "pin every number" deliverable.
- **§2 — C-9 seam interfaces.** The no-platform-dependency .NET domain library and the host-implemented OS-syscall seams, with concrete C# signatures. A `/code` build implements these verbatim.
- **§3 — Deterministic `.hoard` serialization + durability.** The pinned serialization decisions (encoder, unicode rule, overflow-bag-vs-converter) and the append-log durability record format.
- **§4 — Two-archetype AI provider.** The `claude -p` launch contract, the deny-by-default hook protocol, the fail-closed model, the cap model, FR-39 queue, liveness, and the Windows-interception deferral with TD-1…TD-5 carried as design constraints.
- **§5 — Custom controls + M18 effort/risk.** Per-item effort and risk for the custom-control surface PRD §11.1 line 781 names.
- **§6 — Carry-forward deferrals + Phase-5 readiness.** What stays Deferred (not silently upgraded), and the gate to `architecture.md`.
- **§7 — Open questions.** Residual tech-design decisions, each with a proposed default.

Terms are the PRD's §1 Definitions; this doc does not redefine them.

---

## 1. Measured-gate summary — every PROVISIONAL number pinned

Each PRD value that carried a `PROVISIONAL pending SPIKE-N` marker is flipped here to its measured or decided value, traced to the spike that produced it. The PRD-reconciliation pass updates the PRD's §5/§11.1 markers to match this table.

| PRD value | PRD marker | Measured / decided | Verdict vs PRD gate | Source |
|---|---|---|---|---|
| **SC-1 cold start** | PROVISIONAL (SPIKE-1); gate ≤ 1000 ms p90 / median ≤ 800 ms | **843.5 ms p90** (median 787.6 ms), **ReadyToRun ON**; absolute ceiling **1265 ms** (1.5×) | **PASS** on linux-x64. **R2R-OFF FAILS (1309 ms p90)** | SPIKE-1 §B |
| **SC-2 warm file open** | PROVISIONAL (SPIKE-1); gate ≤ 250 ms p90 | **≤ 209 ms p90** (upper-bound proxy — grammar-cold first-open); ceiling **313 ms** | **PASS a fortiori** (true warm ≤ proxy) | SPIKE-1 §C |
| **NFR-5 idle RSS** | PROVISIONAL (SPIKE-1); gate ≤ 400 MB | editor **235.9 MB** (single doc-set); `claude -p` child **~287 MB** (separate, not additive) | **PASS** (164 MB headroom) | SPIKE-1 §D |
| **NFR-1 ReadyToRun** | "may use, measured before committed" | **MANDATORY publish setting** — `PublishReadyToRun=true` | upgraded may-use → **required** | SPIKE-1 §B, §carry-forward |
| **NFR-9 / OQ-5 large-file threshold** | default-proposal 5 MB live-highlight-disable | **split thresholds** — ~100–200 KB preview render / ~500 KB editor highlight (directional, **single-sample**; Phase-8 confirms) | 5 MB too permissive for preview path | SPIKE-1 §F |
| **C-5 / MA7 `.hoard` debounce** | confirm zero-debounce affordable | per-mutation `fsync` **p90 1.43 ms** (~1% of 150 ms coalesce window) — **affordable** | zero-debounce terminal mutations stands; 150 ms item-text coalesce stands | SPIKE-1 §E |
| **Renderer (C-1/FR-15/FR-16)** | renderer library = Phase-4 decision; "VS Code-grade" provisional | **Candidate A — Markdig AST → custom Avalonia control tree** (20/20 FR-16); **zero extra NuGet** | "VS Code-grade" supported **at FR-16 feature-presence level** (honest caveat) | SPIKE-2 §C, §E |
| **FR-11 create cap** | PROVISIONAL (SPIKE-3) | **25** | de-PROVISIONAL | SPIKE-3 §E |
| **FR-38 destructive cap** | PROVISIONAL (SPIKE-3) | **10** | de-PROVISIONAL | SPIKE-3 §E |
| **FR-40 distinct-files-touched cap** | PROVISIONAL (SPIKE-3) | **20** | de-PROVISIONAL | SPIKE-3 §E |
| **FR-38 X% magnitude** | PROVISIONAL (SPIKE-3) | **80%** | de-PROVISIONAL | SPIKE-3 §E |
| **Minimum `claude` CLI version** | TBD in Phase-4 (SPIKE-3) | **2.1.179** (pinned) | de-PROVISIONAL | SPIKE-3 §A1 |

### 1.1 SC-1 cold start — the ReadyToRun ship constraint (binding)

SPIKE-1 re-baselined SC-1 to **843.5 ms p90** (median 787.6 ms) on linux-x64 with the SPIKE-2 Candidate-A renderer and a real TextMate grammar load embedded. This clears the PRD gate (≤ 1000 ms p90, median ≤ 800 ms). The **falsifiable absolute ceiling carried forward is 1265 ms** (1.5× the rebaselined number, per MA15); exceeding it is a fail renegotiated with the maintainer, not a silently-moved goalpost.

The dominant factor is **ReadyToRun**. R2R-on improves p90 by ~466 ms (35.5%); **R2R-off fails the gate outright at 1309 ms p90**. This is a binding design requirement, not optional tuning:

> **Ship constraint (binding): `PublishReadyToRun=true` is MANDATORY for both RIDs.** This upgrades NFR-1's "ReadyToRun may be used, measured before committed" to a **required publish setting**. The Phase-8 build's `.csproj`/publish profile sets `PublishReadyToRun=true` for `win-x64` and `linux-x64`; CI must fail a publish that omits it. (The PRD-reconciliation pass restates NFR-1 accordingly.)

**Trim is optional.** With R2R-on, trim is perf-neutral (+33 ms p90, within trial variance) and shrinks the binary 113 MB → 65 MB; trimming did **not** break the TextMateSharp grammar resources (they pack as managed resources). Trim with R2R-*off* is counter-productive (+358 ms). Therefore: **trim is a safe size win only paired with R2R-on.** If trim is enabled, the `TextMateSharp.Grammars` assembly's trim roots are pinned (the known footgun NFR-1 names), and the supplemental-grammar embedded resources (FR-7) are also pinned as trim roots.

The PRD's enabling mechanisms stand as binding design (lazy per-language grammar registration; deferred subsystem init of the AI provider, the preview renderer, and find-in-folder) — SPIKE-1 measured grammar-first-load at ~150 ms, which is why lazy registration matters for the warm path.

### 1.2 SC-2 warm open — proxy upper-bound and the Phase-8 harness extension

SPIKE-1 measured SC-2 at **209 ms p90**, but on an **upper-bound proxy**: the throwaway harness has no "open a second file into an already-running instance" path, so it measured the slower **grammar-cold first-open** path. The strict already-loaded-grammar warm number is **≤ 209 ms**, so the PASS holds a fortiori. **Layout dominates** (~208 ms); parse (Markdig, ~1.9 ms p90) and the control-tree walk (~15 ms p90) are negligible — consistent with SPIKE-2's hand-off that the construction leg is cheap.

**Carry-forward to Phase 8 (not a blocker):** a strict SC-2 reading (second file into a running instance, grammar already loaded) needs a small harness extension. Expected below 209 ms; the gate passes on the upper bound. The Phase-8 SC-2 harness must add the strict warm-open path and record the already-loaded number separately from the first-grammar-load delta (~150 ms).

### 1.3 NFR-5 idle RSS — the multi-tab carry-forward

Editor idle RSS is **235.9 MB** (single doc-set, R2R-on, 46 KB doc, 5 s settle) — PASS against ≤ 400 MB with ~164 MB headroom. The `claude -p` subprocess peak RSS is **~287 MB**, measured separately (it is a child process launched on demand and short-lived per turn; it is **not additive** with the editor's idle RSS, per NFR-5).

**Carry-forward to Phase 8:** the throwaway app is single-document. The m1 10-doc set was proxied as 10 independent processes (a conservative upper bound that shares no runtime/Avalonia pages and is therefore not gate-comparable). The **gate-comparable figure is the single-instance 235.9 MB**; a real multi-tab implementation's per-tab incremental RSS is expected to be a small fraction of a full process but is **unquantified by the single-instance spike app** — Phase 8 measures multi-tab incremental RSS against the m1 doc-set.

### 1.4 NFR-9 / OQ-5 large-file threshold — split thresholds (directional, single-sample)

SPIKE-1's large-file reading (single-sample, directional) shows layout scaling supra-linearly: 282 ms at 100 KB, 1.5 s at 500 KB, ~12 s at 5 MB. Parse stays cheap and ~linear (84 ms at 5 MB). The PRD's default **5 MB live-highlight-disable threshold is too permissive for the Markdown preview path** — at 5 MB the layout blocks for ~12 s.

**Directional recommendation (to confirm in Phase 8 before final pin):** split the single threshold into two:
- **Preview render threshold ~100–200 KB** — above this, the Markdown rendered-preview (FR-15) does not auto-render the full document (offer a "render anyway" affordance / render a windowed region). The preview is the supra-linear-layout path.
- **Editor syntax-highlight threshold ~500 KB** — above this, the AvaloniaEdit live highlighter degrades (disable live re-tokenize, keep the buffer editable), per NFR-9's existing degrade posture.

> **This is a single-sample reading, not a gate.** Phase 8 must confirm with a proper distribution (multiple samples per size, warm/cold spread) and **separate the preview-layout cost from the editor-highlight cost** before pinning the two thresholds. The folding-recompute cost (SPIKE-FR9: 12.36 ms avg / 33.37 ms worst at 20k lines) is a third, independent large-file cost that lives on the editor path and is governed by the debounce-then-background design in §5.4 — it is not the preview-layout cost.

### 1.5 C-5 / MA7 — `.hoard` durability window confirmed affordable

Per-mutation `fsync` p90 is **1.43 ms** on the ext4 SSD (p50 0.613 ms; 200 trials) — ~1% of the 150 ms item-text coalesce window. **Zero-debounce for terminal `.hoard` mutations** (check/uncheck, reorder, delete, mode change) is affordable with a wide margin; the **150 ms item-text coalesce default stands**. This confirms FR-31's two-class split (zero-debounce terminal, ≤ 150 ms coalesced item-text) is implementable without weakening C-5.

### 1.6 Renderer pin — Candidate A (Markdig AST → custom Avalonia control tree)

> **Decision (binding): the FR-15 preview renderer is Candidate A — a custom walker over Markdig's `MarkdownDocument` AST that emits an Avalonia control tree.** Candidate B (`Markdown.Avalonia 11.0.3`) **hard-fails** two explicit FR-16 acceptance items out of the box — **task-list checkboxes** (renders literal `[x]`/`[ ]`) and **footnotes** (renders literal `[^1]`) — and partially fails links/images/inline-HTML. Candidate A passes **20/20** FR-16 assertions at the feature-presence level. Candidate B beats Candidate A on **no** FR-16 feature; its only edge (code-block syntax coloring via ColorCode) is outside FR-16.

Supporting decisions:
- **Markdig needs zero extra NuGet.** `Markdig.Markdown.Parse(..., pipeline)` with `.UseAdvancedExtensions()` in the core `Markdig` package covers tables, task-lists, footnotes, autolinks, and strikethrough — the full FR-16 GFM set. **No separate `Markdig.Extensions.*` package is pinned.** (SPIKE-2 §E.)
- **"VS Code-grade" is supported at the FR-16 feature-presence level only.** Honest caveat: "fidelity" here means feature presence + correct structure (visual-inspection + control-tree confirmed under Skia-headless), **not pixel-parity** with VS Code's renderer or typographic polish. No claim of byte- or pixel-identical output to any reference renderer is made. The PRD already dropped "VS Code-grade" from the acceptance language (M17); FR-16's per-feature assertions are the acceptance basis.
- **Preview code-block syntax coloring is NOT an FR-16 requirement.** Candidate A shows code blocks in plain mono with the language *label* (which FR-16 requires); it does **not** color the code. Coloring preview code blocks by reusing the editor's TextMate tokenizer is an **optional M18 enhancement** — see §5.1 (+2–4 person-days).
- **No WebView/CEF/HTML control at any layer** (CLAUDE.md §2 / FR-15) — Candidate A is pure managed Avalonia, builds for `net8.0`, and has no Windows-only dependency.

### 1.7 Caps — the four pinned AI-safety numbers + min CLI version

SPIKE-3 measured and documented the basis for each pinned cap. These replace the PRD's PROVISIONAL markers (FR-11/FR-38/FR-40 Done-whens and FR-35's tunable defaults):

| Cap (FR) | Pinned value | Basis (SPIKE-3 §E) | Tunable? |
|---|---|---|---|
| **FR-11 create cap** | **25** | 25 creates in one unconfirmed turn is a mass-create injection signal; covers a one-small-project-scaffold heuristic; 2.5× the destructive cap since creates are lower-risk | yes (FR-35), clamps to a safe range |
| **FR-38 destructive cap** | **10** | ~10 full-file overwrites in one batch flags injection vs a legitimate large refactor; ~1/2.5 of FR-11 (higher-risk ops warrant a lower cap) | yes (FR-35) |
| **FR-40 distinct-files-touched cap** | **20** | Same order of magnitude as FR-11; targets the touch-many-files-sub-threshold attack; 20 distinct files in one turn without confirm is significant scope | yes (FR-35) |
| **FR-38 X% magnitude** | **80%** | Distinguishes whole-file/near-whole replacement (100%, 90%) from a substantial-but-partial edit (a 20-line edit in a 200-line file ≈ 10%); catches injection-style full-rewrites without nagging legitimate large edits | yes (FR-35) |
| **Min CLI version** | **2.1.179** | Live-verified 2026-06-19; the allowlist-only design is version-drift-resilient (the roster drifted 2.1.161 → 2.1.179 and the design absorbed it) | floor, not a pin (≥) |

FR-35's clamp rule (a cap cannot be set to 0/unbounded in a way that disables the injection backstop) applies to all four.

---

## 2. C-9 seam interfaces — the domain/host boundary a `/code` build implements verbatim

PRD §8 C-9 mandates that the checklist, session, file-ops, undo-journal, activity-log models and the two-archetype provider logic live in a **no-platform-dependency .NET domain library**, with every OS syscall pushed behind a host-implemented interface. This section specifies that boundary with concrete C# signatures so a Phase-8 build implements it without reinterpretation. The guiding rule: **the domain stays unit-testable and platform-agnostic; every OS syscall leg lives behind a host interface.** Domain-pure logic is marked **[domain]**; host-syscall legs are marked **[host]**.

> **Phase-5 placement refinement (`architecture.md` §1.2, folded 2026-06-19).** The seam **interface** declarations below use a `HoardPad.Host.Abstractions` namespace, but the interfaces must live **physically in the domain assembly** (`HoardPad.Core`, the Phase-5 rename of `HoardPad.Domain`), under the namespace `HoardPad.Core.Host.Abstractions` — **not** in a `HoardPad.Host` assembly. Declaring them in a `HoardPad.Host` *assembly* would force a `Core → Host` reference cycle that §2.6 forbids (the `senior-csharp-multi-platform` Phase-5 review identified this as a latent defect this annotation corrects). The architecture's ports-and-adapters arrangement — abstractions in `Core`, concretes in `Host` — is the only cycle-free realization. The `Host` token in the namespace is a naming convention, not a project-placement instruction. See `architecture.md` §1.2 / OQ-ARCH-2.

The project layout the seams imply (finalized in Phase 5 `architecture.md`):

- `HoardPad.Domain` — `net8.0`, **no** platform-conditional code, **no** `System.IO` syscalls beyond pure-data shaping, **no** `DateTime.UtcNow`. Owns `SandboxPolicy`, the `.hoard` model + serialization contract, the session-reconciliation table (§9.2), the undo-journal decision logic (§9.4), the cap accounting, and the activity-log/undo-journal record shaping. *(Phase-5 renames this project `HoardPad.Core`; `architecture.md` §1.1.)*
- `HoardPad.Host` — implements the seam interfaces with real OS syscalls (P/Invoke for Windows primitives, POSIX for Linux), guarded by `OperatingSystem.IsWindows()` + `[SupportedOSPlatform("windows")]` so one source compiles for both RIDs (the SPIKE-4/SPIKE-5 pattern — `dotnet build -r win-x64` and `-r linux-x64` both clean from this Linux box).
- `HoardPad.App` — the Avalonia head; wires `HoardPad.Host` into `HoardPad.Domain` via DI.

### 2.1 `IAtomicFileStore` — the NFR-4 atomic-write sequence [host]

The full ordered durability sequence (NFR-4) lives behind one host interface so the domain never touches `fsync`/`MoveFileEx`/`ReplaceFile`. Two write paths per NFR-4: **create-when-absent** (first-ever write of a new file) and **replace-when-present**.

```csharp
namespace HoardPad.Host.Abstractions;

/// Atomic, durable file writes per NFR-4. All OS syscalls live here; the domain
/// calls these and never touches fsync / MoveFileEx / ReplaceFile / rename(2).
public interface IAtomicFileStore
{
    /// First-ever write of a NEW file (new .hoard via FR-34, first recovery snapshot
    /// of a never-saved scratch buffer, first activity-log / undo-journal record).
    /// Sequence: same-directory temp -> fsync(temp data) -> atomic create-rename ->
    /// Linux: fsync(containing dir).
    /// Windows: MoveFileEx(temp, dest, MOVEFILE_WRITE_THROUGH) (fails if dest exists -
    /// correct for create-when-absent). SPIKE-5 compile-confirmed; runtime power-cut DEFERRED (§6).
    /// Throws AtomicWriteUnavailable (caller surfaces the NFR-4 best-effort warning) on EXDEV /
    /// cross-device / synced-target where atomic rename is unavailable.
    Task CreateWhenAbsentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct);

    /// Replace an EXISTING file atomically with write-through.
    /// Sequence: same-directory temp -> fsync(temp data) -> atomic rename-over -> Linux dir fsync.
    /// Windows: P/Invoke ReplaceFile with REPLACEFILE_WRITE_THROUGH (NOT System.IO.File.Replace,
    /// which throws FileNotFoundException when dest is absent and does NOT pass WRITE_THROUGH).
    /// Never delete-then-move (crash window with no valid file).
    Task ReplaceWhenPresentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct);

    /// Append one durable record to an append-log (activity log §9.3, undo journal §9.4).
    /// Writes [4B uint32 LE length][N B payload][4B uint32 CRC-32/ISO-HDLC], then fsync,
    /// BEFORE returning (per-record fsync-before-next-append, §3.3). Returns the new end offset.
    Task<long> AppendRecordAsync(string logPath, ReadOnlyMemory<byte> payload, CancellationToken ct);

    /// On open, truncate-repair an append-log to the last validated record offset (§3.3).
    /// Returns the recovered records and whether a torn tail was truncated.
    Task<AppendLogRepairResult> OpenAndRepairAsync(string logPath, AppendReadMode mode, CancellationToken ct);

    /// Whether destPath's directory supports atomic rename (local same-FS) vs requires the
    /// fsync'd-in-place + warning fallback (EXDEV / SMB / NFS / Syncthing / Dropbox). A-4 / NFR-4.
    Task<AtomicWriteCapability> ProbeAtomicCapabilityAsync(string destPath, CancellationToken ct);
}

public enum AppendReadMode { StopAtFirstBad, ResyncAndContinue } // §3.3: default StopAtFirstBad on local SSD
public enum AtomicWriteCapability { Atomic, BestEffortWithWarning }
public sealed record AppendLogRepairResult(IReadOnlyList<ReadOnlyMemory<byte>> Records, long ValidEndOffset, bool WasTruncated);
public sealed class AtomicWriteUnavailable : Exception { /* carries the path + reason for the NFR-4 warning */ }
```

Notes binding on the implementation:
- The **`EXDEV`/synced-target fallback** (NFR-4 / A-4) is `ProbeAtomicCapabilityAsync` returning `BestEffortWithWarning`; the caller (a domain service) then writes via an `fsync`'d in-place path and surfaces the "zero-loss atomicity is not guaranteed on this location" warning. The temp+rename still runs in-directory; only full atomicity is best-effort.
- Windows create-when-absent is **`MoveFileEx(temp, dest, MOVEFILE_WRITE_THROUGH)`** (SPIKE-5 compile-confirmed, `kernel32!MoveFileExW`); replace-when-present is **P/Invoke `ReplaceFile` with `REPLACEFILE_WRITE_THROUGH`**. The runtime power-cut assertion for the create path is **DEFERRED to real Windows hardware** (§6) — this doc does not assert it "Met."

### 2.2 `IPathResolver` + pure `SandboxPolicy.Evaluate` — the FR-26 escape check

The sandbox escape check (FR-26) splits cleanly: the **pure prefix/`..`/absolute-path/normcase decision is domain** logic; the **symlink/`realpath` syscall leg is host**.

```csharp
namespace HoardPad.Domain.Safety;

/// PURE domain policy [domain]. No syscalls. Decides whether a CANONICAL candidate path is
/// at-or-under a CANONICAL root. The host resolves symlinks to canonical form FIRST (IPathResolver)
/// and passes the resolved pair in. Comparison is over normcased paths (SPIKE-4 case-insensitivity
/// rule) so Windows case/short-name tricks cannot evade the prefix check.
public static class SandboxPolicy
{
    /// root and candidate MUST already be canonical (symlinks resolved) and normcased.
    /// Deny on: candidate not prefixed by root; any unresolved '..'; an absolute path escaping root.
    public static SandboxDecision Evaluate(NormcasedPath root, NormcasedPath candidate);
}

public enum SandboxDecision { Allow, Deny }

/// A path already lowered to the OS's case-fold form (os.path.normcase analogue):
/// no-op on Linux; case-fold + separator-normalize on Windows. Constructed only by the host
/// resolver after canonicalization, so the domain never normcases a raw string itself.
public readonly record struct NormcasedPath(string Value);
```

```csharp
namespace HoardPad.Host.Abstractions;

/// HOST [host]. Resolves a raw, possibly-symlinked, possibly-relative path to a canonical,
/// symlink-resolved, normcased absolute path. This is the realpath/syscall leg the domain must not own.
public interface IPathResolver
{
    /// Resolve symlinks (realpath), make absolute, normcase. On Windows, expand 8.3 short names
    /// (GetLongPathName) so a short-name cannot evade the sandbox (TD-1 hardening). Returns the
    /// canonical normcased path; the domain then calls SandboxPolicy.Evaluate.
    NormcasedPath ResolveCanonical(string rawPath, string rootForRelativeResolution);
}
```

Binding notes:
- **The SPIKE-4 `normcase` case-insensitivity rule is folded:** the policy compares **normcased** paths. On Linux `normcase` is a no-op; on Windows it case-folds + normalizes separators. Without it, `C:\Work\foo.txt` and `c:\work\foo.txt` (i) count as two files in the FR-40 distinct-files set (halving the effective cap) and (ii) let a case/short-name trick evade `relative_to`. The host resolver does the normcase; the domain consumes a `NormcasedPath`.
- **TD-1 is a design constraint:** on Windows the resolver expands 8.3 short names. For the **Python hook side** (the Archetype-A PreToolUse hook, §4), this requires **Python 3.12+** (so `pathlib.resolve()` calls `GetLongPathName`) **or** explicit `GetLongPathName` `ctypes` hardening — below 3.12 the 8.3-short-name sandbox-escape vector is open. The hook's host-wired transport (§2.4) enforces the Python-floor probe.
- The **residual Archetype-A TOCTOU window** (FR-26/M6) is inherent to observe-and-veto: a path component could be swapped to a symlink between the hook's `ResolveCanonical` and the agent's write. This is documented in §4.7, not closed here. Archetype B is TOCTOU-free because HoardPad canonicalizes-then-writes in one step as the writer.

### 2.3 `IAgentBackend` — `claude -p` launch / process-tree kill / stream [host]

```csharp
namespace HoardPad.Host.Abstractions;

/// HOST [host]. Launches the claude -p subprocess, streams stream-json, and kills the WHOLE
/// process tree on cancel (NFR-8). The launch contract (flags) is §4.1; the kill design is below.
public interface IAgentBackend
{
    /// Resolve the launcher per the FR-24 shim order: claude.cmd -> claude.exe -> configured path
    /// (SPIKE-4 ShimResolver). UseShellExecute = false (required for Job-Object assignment).
    LaunchDescriptor ResolveLauncher(string? configuredPath);

    /// Launch a single turn. On Windows: CREATE_SUSPENDED -> AssignProcessToJobObject (while
    /// suspended) -> ResumeThread (the suspend-before-assign ordering closes the race where the
    /// .cmd shim spawns node before the Job assignment). On Linux: setsid() so the child leads
    /// its own process group. WorkingDirectory = resolved open-folder root (NO --cwd flag, §4.1).
    Task<IAgentTurn> StartTurnAsync(LaunchDescriptor launcher, AgentTurnRequest request, CancellationToken ct);
}

/// One in-flight turn. Streams parsed stream-json events; KillTree() terminates the whole tree.
public interface IAgentTurn : IAsyncDisposable
{
    IAsyncEnumerable<AgentStreamEvent> StreamAsync(CancellationToken ct);

    /// Cancel by killing the ENTIRE process tree (the agent may spawn children).
    /// Windows PRIMARY: TerminateJobObject + CloseHandle (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE).
    /// Windows FALLBACK: taskkill /T /F /PID <pid> (breakaway is version-dependent - TD-5).
    /// Linux: kill(-pgid, SIGKILL) on the setsid process group.
    /// This is the SOLE fail-closed enforcer (§4.4) - it must fire before any hook timeout.
    Task KillTreeAsync();
}

public sealed record LaunchDescriptor(string ExecutablePath, bool IsCmd, bool FromPath, string ResolvedVia);
```

**Process-tree kill design (SPIKE-4, binding):**
- **Windows primary — Win32 Job Object.** `CreateJobObject` → `SetInformationJobObject` with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` → launch child `CREATE_SUSPENDED` → `AssignProcessToJobObject` **while suspended** → `ResumeThread`. On cancel: `TerminateJobObject` + `CloseHandle` → the entire tree dies. The `CREATE_SUSPENDED`-before-assign ordering is load-bearing: it closes the race where the `claude.cmd` shim spawns `node` before the Job assignment (which would leave `node` outside the Job and surviving the kill).
- **Windows fallback — `taskkill /T /F /PID <pid>`.** Used when Job assignment fails (the child is already in another Job — possible in CI/containers) or where `CREATE_BREAKAWAY_FROM_JOB` lets a worker escape the Job (**TD-5**: breakaway is version-dependent, so production uses Job Object **AND** `taskkill` regardless).
- **Linux analogue (proven, SPIKE-4).** The shim `setsid()`s to lead its own process group; cancel is `kill(-pgid, SIGKILL)`. SPIKE-4's 3-level `shim → child → grandchild` stand-in confirmed 0 survivors over 2 runs.
- **Shim resolver (SPIKE-4 `ShimResolver`).** PATH probe `claude.cmd` → `claude.exe` → configured explicit path, returning `LaunchDescriptor { ExecutablePath, IsCmd, FromPath, ResolvedVia }`. `IsCmd` tells the host the child started via `cmd.exe` (extra process level for Job assignment). Launch with `UseShellExecute = false` (required for Job-Object assignment).

**Deferral (carried, not asserted Met):** the Windows Job-Object **runtime** legs (no-orphan-after-kill, timed-out-hook-no-leak on Windows) are **win-x64 compile-confirmed only**; the runtime is **DEFERRED to Windows hardware** (§6). The Linux analogues PASS; the Windows runtime does not.

### 2.4 Host-wired tool-permission hook transport

The domain exposes the **pure `SandboxPolicy`** and the pure cap-accounting logic; the **host wires the PreToolUse hook** (the OS process the CLI launches, its `--settings` injection, its stdin/stdout transport). The seam:

```csharp
namespace HoardPad.Host.Abstractions;

/// HOST [host]. Materializes the deny-by-default PreToolUse hook the CLI invokes, wires its
/// --settings injection, and bridges its stdin event JSON to the domain's pure decision logic.
public interface IToolPermissionHookHost
{
    /// Produce the --settings JSON wiring the production hook as the PreToolUse '*'-matcher hook
    /// with a no/long timeout (>= the FR-39 no-reply timer + margin; default 600s) so the hook's
    /// own timeout never wins the race (§4.4 fail-open). Probe the Python floor (TD-1: 3.12+ on
    /// Windows, or GetLongPathName hardening); pick the interpreter (python / py launcher).
    HookSettings BuildHookSettings(HookConfig config);

    /// Run the fail-closed self-test (FR-24 §3) before write agency is enabled: launch claude -p
    /// in a scratch dir with the production hook, prompt a Write/Edit the hook is configured to DENY,
    /// pass only if (a) no file appears AND (b) permission_denials is populated. Returns the result.
    Task<SelfTestResult> RunFailClosedSelfTestAsync(LaunchDescriptor launcher, CancellationToken ct);
}
```

The hook process itself (the Python script SPIKE-3/SPIKE-4 prototyped) calls into the domain's `SandboxPolicy.Evaluate` and cap accounting for its allow/deny decision; the host owns the process lifecycle, the `--settings` wiring, and the self-test. The cap-state is owned by HoardPad and keyed to the conversation (§4.5) — the hook reads/writes it through a host-provided exclusive-locked file (the SPIKE-4 `O_EXCL` SpinLock, portable to Windows `CREATE_NEW`), not `fcntl` (which has no Windows equivalent).

### 2.5 `IAppDataPaths` + injected `TimeProvider`

```csharp
namespace HoardPad.Host.Abstractions;

/// HOST [host]. Per-user data dir + permission restriction (NFR-7). The recovery subdirectory
/// is MACHINE-LOCAL and NOT synced (§9.2 / R4-MA1).
public interface IAppDataPaths
{
    string AppDataRoot { get; }                 // per-user; restricted to the user where the OS supports it
    string RecoveryDirectory { get; }           // machine-local, non-synced
    string SettingsFilePath { get; }            // FR-36 single human-readable settings file
    string ActivityLogPath(string sessionId);   // §9.3 per-session NDJSON
    string UndoJournalRoot { get; }             // §9.4
    /// Restrict a just-created file/dir to the current user (chmod 600 / Windows ACL) where supported.
    void RestrictToCurrentUser(string path);
}
```

The domain takes an injected **`System.TimeProvider`** for every timestamp (`savedAt`, the `seq`-paired clock, activity-log `ts`, undo-journal `preOpMtime` reads via the host). **The domain never calls `DateTime.UtcNow`** (C-9) — this keeps the session-reconciliation table (§9.2) and the undo decision logic deterministically unit-testable with a fake clock.

### 2.6 Why the seam split holds

Every OS syscall (atomic rename, `fsync`, `MoveFileEx`/`ReplaceFile`, `realpath`/symlink resolution, `GetLongPathName`, Job-Object kill, `setsid`/`kill(-pgid)`, the hook process lifecycle, per-user permission restriction, the wall-clock) is behind a `HoardPad.Host` interface. The domain holds: `SandboxPolicy.Evaluate`, the `.hoard` model + the §3 serialization contract, the §9.2 reconciliation table (every cell unit-tested with a fake clock + injected hashes), the §9.4 undo decision logic (injected current hash/mtime), the cap accounting (FR-11/FR-38/FR-40 set + counters, conversation-keyed), and the activity-log/undo-journal record shaping. This is what makes the §1 spike measurements re-runnable and the safety logic provable without a live OS or a live agent.

---

## 3. Deterministic `.hoard` serialization contract + durability (folds SPIKE-5)

This section pins the two serialization decisions SPIKE-5 left to tech-design and specifies the append-log durability record format. It is the HOW for PRD §9.1, §9.3, §9.4.

### 3.1 The MA2 invariant (folded from SPIKE-5)

SPIKE-5 re-confirmed MA2 against the .NET floor. The §9.1 byte-stability invariant is:

> Serialization is **byte-identical for files already in HoardPad's canonical form**. The first re-save of a foreign/older-form file canonicalizes it (an **accepted one-time diff**); every re-save thereafter is byte-identical.

What **is** byte-stable (SPIKE-5 Evidence 1, 4):
- **Numbers in the overflow bag** (`JsonElement` raw-text preservation): `1.50`, `1E10`, `9007199254740993`, `-0.0` all round-trip verbatim.
- **Typed fields** (`int order`, `int schemaVersion`) are normalized by STJ's number writer — fine, because HoardPad controls their encoding and emits them canonically.
- **Overflow-bag within-bag insertion order** is preserved.

What is **NOT** byte-stable (SPIKE-5 Evidence 2, 3):
- **Unicode escaping** — the default `JavaScriptEncoder` escapes `<`, `é`, etc.; `UnsafeRelaxedJsonEscaping` emits them literal. Two builds disagreeing on the encoder produce non-identical bytes for the same logical content.
- **Cross-version key ordering** — `[JsonExtensionData]` forces all unknown keys *after* all known keys, so a key a newer build interleaves between known properties relocates to the tail on an older build's re-save.

### 3.2 The two tech-design decisions SPIKE-5 left open — pinned here

**Decision (a) — pinned encoder + unicode canonicalization rule:**

> **Pin `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` and emit literal non-ASCII**, stable across all HoardPad builds and both RIDs.

Rationale: `.hoard` files are the maintainer's personal notes synced between two machines by their own means; they are read in `git diff`, in other editors, and re-saved by both builds. Emitting literal `é`/`<` rather than `é`/`<` produces the smallest, most human-legible diffs and the most natural content for a notes file that is never embedded in an HTML attribute context (the XSS-escaping the default encoder defends against is irrelevant to a local JSON checklist file). The alternative (always-escape via the default encoder) is also internally consistent but yields noisier diffs and uglier content; **`UnsafeRelaxedJsonEscaping` is pinned.** All HoardPad builds use this one encoder so escaping never differs build-to-build. (The name "Unsafe" refers to HTML-injection contexts, which do not apply to a local `.hoard` file; this is documented so a later reader does not mistake it for a security relaxation.)

**Decision (b) — overflow-bag mechanism vs custom converter:**

> **Pin `[JsonExtensionData]` overflow bag and accept the one-time known/unknown-boundary relocation diff.** Do **not** ship the custom raw-key-order converter in v1.

Tradeoff (stated plainly): `[JsonExtensionData]` is the simpler, lower-risk mechanism — it is a single STJ attribute on the model, it preserves unknown keys (the required cross-version data-loss defense), and it preserves within-bag insertion order. Its only cost is that an interleaved unknown key written by a *newer* build **relocates to the tail** on an *older* build's first re-save — a one-time canonicalizing diff, byte-identical on every save thereafter. The custom raw-key-order converter would eliminate that relocation (zero relocation) but is materially more code, must round-trip arbitrary key order across the known/unknown boundary, and is a new serialization surface to test and maintain. For a single-user personal tool where the relocation is a one-time, recoverable, non-data-losing diff on first cross-build restore, **the converter is not worth its complexity in v1.** This decision is coupled to the §9.2 no-spurious-conflict scope: with `[JsonExtensionData]`, two *mismatched* builds canonicalize a foreign-authored file differently, so the first cross-build restore of an interleaved-key `.hoard` is **one accepted one-time conflict** (the same diff §9.1 accepts), not a spurious conflict on every sync. (If a future version wants the absolute no-spurious-conflict guarantee, the converter is the documented upgrade path; OQ-TD-2 in §7.)

### 3.3 Append-log durability record format (folds SPIKE-5)

The activity log (§9.3) and the undo journal (§9.4) are append-mostly. SPIKE-5 implemented and crash-tested the record format; it is binding:

```
[4 bytes]  uint32 payload length (little-endian)
[N bytes]  UTF-8 JSON payload (no trailing newline)
[4 bytes]  uint32 CRC-32/ISO-HDLC checksum of the payload bytes only
```

- **Per-record overhead: 8 bytes** (4 length + 4 checksum). Checksum polynomial `0xEDB88320` (zlib/Ethernet/PNG CRC-32).
- **Per-record `fsync` before the next append begins** (§9.3-a). This is what makes "torn tail == first invalid record" *provable* rather than assumed: on a write-ordering FS, each record is durable before the next starts, so the only record that can ever be invalid is the final in-flight one. SPIKE-5 fixture 1 + 1b confirmed this across all three torn-record shapes (partial length, mid-payload, full-payload-no-CRC).
- **On-open truncate-repair** to the last validated record offset before the first new append (§9.3-b). SPIKE-5 confirmed truncate-repair on torn tails and the degenerate cases (empty, partial length prefix, all-torn).

**Reader mode — v1 local SSD:** `StopAtFirstBad` is **correct** for the v1 local-SSD target, because per-record `fsync` makes interior holes impossible (the first invalid record is always the torn trailing one). SPIKE-5 fixture 2 demonstrated that the naive stop-at-first-bad reader *would* drop intact records #3–#6 after a forced interior corruption — but that interior corruption is **unreachable on a write-ordering FS with per-record fsync**.

**Named fallback — non-ordering FS:** `resync-and-continue` is the documented fallback (R4-mi4) for a target FS that does **not** honor `fsync` write-ordering (a lying disk cache, NFS, Syncthing, SMB). It scans forward byte-by-byte past a bad interior record to recover intact later records. SPIKE-5 boundary fixtures B1–B3 confirmed it recovers correctly past corrupt length prefixes, corrupt checksums, and two consecutive corrupt records.

**Honest B4 false-positive characterization (carried, not fabricated away):** resync-and-continue has an **inherent** false-positive risk — a corrupt outer record whose payload happens to contain a valid inner record frame (length + payload + matching CRC) emits a ghost record. The CRC-32 gate bounds this to **~0.0004% per corrupt record per 64 KB payload** (~16,384 candidate positions / 2³²). It is **unreachable in normal operation on a write-ordering FS** (where resync-and-continue is never taken); it is a named, accepted risk only on the non-ordering-FS fallback path, and the worst case is a ghost record, never silent data loss — the genuine tail record is always recovered.

**Throughput (SPIKE-5):** per-record append (serialize + write + CRC + fsync) is **p50 ~0.6–0.7 ms, p90 ~1.1–1.5 ms** on local ext4 SSD (within the variance of the bare-fsync baseline; framing overhead is negligible, fsync dominates). A 25-op AI turn costs **~14–49 ms** on local ext4 SSD (p50-best to p90-worst). **Named caveat:** on a slow/network/synced target, per-`fsync` latency can be 10–100× higher, pushing a 25-op turn into **seconds** — a named caveat, not a design change. The `IAtomicFileStore.AppendRecordAsync` seam (§2.1) is where this cost is paid.

This same record format, per-record-`fsync`, truncate-repair, and `StopAtFirstBad`-default-with-`ResyncAndContinue`-fallback applies to **both** the activity log (§9.3) and the undo journal (§9.4) when its entries are appended; the undo-journal before-image blobs (`before/…bin`) are whole-file create+`fsync` per NFR-4's create-when-absent path.

### 3.4 Fixed property ordering + known-key-promotion migration

- **Fixed property ordering (v2, ODQ-11):** known properties are emitted in a fixed declared order (the model's declaration order: `schemaVersion`, `title`, `settings`, `items`; per item **`id`, `text`, `checked`, `parentId`, `order`, `collapsed`**); overflow-bag keys follow, in preserved within-bag insertion order. This (with the pinned encoder) is what makes a canonical-form file byte-identical on re-save. The two new v2 fields (`parentId` string|null, `collapsed` bool) are **ordinary typed fields** — the pinned `UnsafeRelaxedJsonEscaping` encoder, the `[JsonExtensionData]` overflow bag at both object levels, and the byte-stable-when-canonical invariant are **unaffected** by their addition.
- **Known-key-promotion migration rule:** when a key previously carried in the overflow bag becomes a **known property** at a later `schemaVersion`, the migration **promotes** the bag value into the typed property and **removes it from the bag** on read of the older file — so the value is never duplicated across both the typed field and the bag. Phase 8 defines the per-key promotion mapping at each `schemaVersion` bump. **v1 ships `schemaVersion: 2`** (ODQ-11); the **v1→v2 migration** adds `parentId`/`collapsed` as known fields by the default-on-absence rule (PRD §9.1: a `schemaVersion: 1` file loads as all-items-`parentId=null`/`collapsed=false` and first re-save emits v2) — no overflow-bag promotion is needed since v1 never carried those keys.

### 3.5 Scoped round-trip fidelity test (required)

The serialization contract carries two required tests (owned by `unit-test-engineer`, A-6):
1. **Canonical round-trip:** parse → re-emit is **byte-identical for a file already in canonical form**, over a corpus covering integers, large/64-bit integers, decimals, nulls, nested objects, and unicode strings at **both** object levels (root + per-item bag). **The corpus is extended for ODQ-11 (v2) with:** (a) a **nesting** fixture — a three-tier `.hoard` exercising `parentId` chains and per-sibling-group contiguous `order`; (b) a **completion-lock** fixture — a `checked: true` parent over an unchecked-child subtree (the locked-but-state-retained block: children keep their own real `checked`/`text`/`order`, asserting the parent's check never mutated them); (c) a **collapsed-state** fixture — parents with `collapsed: true`/`false` round-tripping intact, including a **collapsed-but-unchecked parent** whose children are **present in the file but absent from the visible-row sequence** (Gap B): the fixture asserts both that the descendants serialize intact (`parentId`/`order`/`checked` unmutated by collapse) and that the display-mapping's visible-row sequence excludes them while the collapsed parent's own row remains. Each asserts byte-identical re-emission of the new fields in the fixed v2 property order.
2. **First-re-save canonicalization:** the **first** re-save of a non-canonical (foreign/older-form, interleaved-unknown-key) file produces the documented **one-time canonicalizing diff** (key relocation to tail), not a data-loss — and is byte-identical on every save thereafter. **Extended for ODQ-11:** a `schemaVersion: 1` (flat, no `parentId`/`collapsed`) file re-saves as `schemaVersion: 2` with every item at tier 0 (`parentId: null`, `collapsed: false`) per the v1→v2 migration rule (PRD §9.1) and is byte-identical thereafter; a foreign file with a **dangling/cyclic `parentId`** re-saves with the offending item repaired to `parentId: null` (logged), and an **over-deep** import re-saves clamped to tier 2 — neither rejects the file. The over-deep clamp fixture uses the **pinned worked example in PRD §9.1** (depth-3 cousins `D`/`E` under a tier-2 grandparent `C` reparent to their deepest legal ancestor `B` at tier 1, becoming tier-2 siblings of `C`) as its expected result.

---

## 4. Two-archetype AI provider (folds SPIKE-3 + SPIKE-4)

This section specifies the AI provider HOW for PRD §6.6 (FR-22…FR-29, FR-10/11/38/39/40) + C-8. The architecture is the PRD's: **Archetype A** (`claude -p`, default, delegated-agent, observe-and-veto) and **Archetype B** (Anthropic API / local LLM via Ollama/LiteLLM, driven-completion, HoardPad executes ops). Both route through **one shared file-ops service** that delivers **uniform logging + undo-journal structure**; **enforcement strength differs** (per-op-atomic/TOCTOU-free in B; observe-and-veto with a residual TOCTOU window in A — C-8). This doc does not weaken the safety model; it pins its mechanics.

### 4.1 `claude -p` launch contract (SPIKE-3, binding)

The launch command for one Archetype-A turn. Every flag was confirmed present at 2.1.179 (SPIKE-3 §A). The min CLI version floor is **2.1.179**.

- **`-p` / `--print`** — non-interactive, single-shot per turn.
- **`--output-format stream-json --verbose --include-partial-messages`** — streaming partial output into the panel. **`stream-json` requires `--verbose`** (omitting `--verbose` is a CLI error — SPIKE-3 §A2).
- **`--max-budget-usd <ceiling>`** — coarse cost ceiling (a runaway bound, not a precise per-op budget).
- **`--settings <json>` (and/or `--setting-sources`)** — injects the production hook config explicitly. HoardPad does **not** rely on ambient user settings; the FR-24 §3 self-test confirms the injected config was honored rather than silently ignored.
- **`--tools "Write,Read,Edit"`** — constrains the model's **offered roster** to exactly these three built-in tools. **Use `--tools`, NOT `--allowedTools`** — `--allowedTools` is a *permission* list (what the user *allows*) and does **not** shrink the offered roster (SPIKE-3 §A16). `--tools` reduces the attack surface at the roster level; the deny-by-default hook is the backstop for anything the roster still offers.
- **`--strict-mcp-config`** — strips ambient MCP servers (from `~/.mcp.json` / project `.mcp.json`) from the offered roster, so no `mcp__*` tool is even offered. The `*` hook matcher remains the backstop regardless (SPIKE-3 §B).
- **No `--cwd`** — there is no `--cwd` flag for `-p` (SPIKE-3 §A2). Set the OS process `WorkingDirectory` to the resolved open-folder root.
- **No `--max-turns`, no `--permission-prompt-tool`** — both **absent** at 2.1.179 (SPIKE-3 §A2). The runaway bound is `--max-budget-usd` + HoardPad's wall-clock timeout (which kills the process tree, §2.3) + hook-side per-turn cap counting.
- **Never `bypassPermissions` / `--dangerously-skip-permissions`.**

`session_id` is extractable from the first stream event (SPIKE-3 §H8) but is **fresh per invocation** and holds no cross-call hook memory — so it is a per-op correlation aid, **not** the cap-state key (§4.5).

### 4.2 Deny-by-default hook protocol (SPIKE-3, binding)

The PreToolUse hook matches **`*`** (every tool) and is **allowlist-only**: it allows canonicalized `Read` / `Edit` / `Write` (Write/Edit only when the FR-26 sandbox check passes, and Write only in-sandbox/under-cap) and **denies every other tool by default**. This is an allowlist *principle*, not a fixed denylist — the roster drifted 2.1.161 → 2.1.179 (adding `ToolSearch`, `AskUserQuestion`, `TodoWrite`, `EnterPlanMode`/`ExitPlanMode`, and more), and the allowlist absorbed every addition automatically (SPIKE-3 §B confirmed `AskUserQuestion`, `TodoWrite`, `ToolSearch`, and `SuperFutureToolV99` all denied).

**Mechanism — nested-JSON `hookSpecificOutput` (preferred), exit-2 fallback:** SPIKE-3 confirmed two valid deny mechanisms.

> **Use the nested-JSON `hookSpecificOutput` mechanism** (exit 0 + the nested object below). It is richer than exit-2 because it adds **`updatedInput`** (rewrite the tool's `tool_input` before execution) and **`additionalContext`**, and supports `permissionDecision` values `allow | deny | ask | defer`. Exit code 2 (deny; stderr shown to the model) is the minimal fallback.

```json
{
  "hookSpecificOutput": {
    "hookEventName": "PreToolUse",
    "permissionDecision": "deny",
    "permissionDecisionReason": "reason shown to the model"
  }
}
```

- **The deprecated top-level `{"permissionDecision":"deny"}` is silently ignored** (SPIKE-3 §"What does NOT work") — only exit code 2 or the correctly-**nested** `hookSpecificOutput` form denies. A Phase-8 build must emit the nested form.
- **`Task` → `Agent` hook-name alias** (documented constraint): the hook fires as `tool_name="Agent"` for both `Task` and `Agent` invocations. The allowlist `{Read, Edit, Write}` denies both correctly; a Phase-8 build must not key an allowlist on `Task` and miss `Agent`.

### 4.3 Agency → permission mapping

- **`auto-apply`** → `acceptEdits` permission-mode; the hook still gates each write against the sandbox **and** applies the FR-10 smart-downgrade rules and the FR-11/FR-38/FR-40 caps.
- **`approve-each`** → the PreToolUse hook **blocks synchronously** while HoardPad shows its confirm/reject dialog, then returns nested-JSON `permissionDecision: "allow"` (proceed) or `"deny"` (reject). A `sleep`-then-`allow` blocking hook is live-confirmed to let the write succeed *after* the block (SPIKE-3 C1) — so `approve-each` is buildable this way. (`permissionDecision:"ask"` is **superseded** — it auto-denies in non-interactive `-p`.)
- **`read-only`** → a plan / Read-only posture; the matcher allows no write tools.

**Phase-8 review flag (do not redesign here):** SPIKE-3 surfaced the nested-JSON **`defer` + `updatedInput`** fields as candidates for the approve-each / FR-39 design — `defer` could hold a tool call while the approval dialog is open without fully blocking the hook process, and `updatedInput` could sanitize a `tool_input` before execution. **Carry these as Phase-8 candidates; do not redesign the approve-each flow on them now.** The v1 design is the synchronously-blocking allow/deny hook above; `defer`/`updatedInput` are a possible refinement to evaluate in Phase 8.

### 4.4 Fail-closed model (SPIKE-3 C2, corrected — binding)

This is the load-bearing safety correction. The CLI's hook `timeout` is in **seconds** (default 600), and **on timeout the CLI kills the hook and the tool PROCEEDS — FAIL-OPEN** (SPIKE-3 C2). Therefore:

> **The approve hook runs with NO/LONG `timeout`** (≥ the FR-39 no-reply timer + margin, e.g. the 600 s default), so the hook's own timeout can never win the race and leak a write. **HoardPad's process-tree kill on the confirm-dialog no-reply deadline is the SOLE fail-closed enforcer** (§2.3 `KillTreeAsync`). It must always fire before any hook timeout could elapse.

The "timed-out approve hook must not leak a write" property holds **because of HoardPad's kill** (the process-group/Job-Object kill terminates the gating hook before it can return `allow`), **not** because the CLI fails closed (it does not). SPIKE-3 C2 and SPIKE-4's `hook-leak-model` both confirmed: a HoardPad kill at the no-reply deadline (500 ms in the test) beats a 5 s blocking-hook sleeper — the marker was never written.

**Binding consequences:**
- The hook `timeout` is set to the 600 s default (or longer than the no-reply timer + margin) — never equal to or shorter than the no-reply timer (a short hook timeout that wins the race fail-opens and leaks a write).
- HoardPad owns the no-reply timer (default ~60 s, FR-24/FR-39, injectable as a short test value).
- The fail-closed self-test (FR-24 §3) is a hard gate before write agency is enabled — version/auth/PATH checks are necessary but not sufficient, because `-p` **silently ignores an invalid hook config** (SPIKE-3 D3). The self-test (deny a Write, assert no file + `permission_denials` populated) is the only guard against a silently-disarmed sandbox.

### 4.5 Cap model (SPIKE-3, binding)

The four pinned numbers (§1.7): FR-11 = 25, FR-38 = 10, FR-40 = 20, FR-38 X% = 80%.

**Accumulation and no-re-arm:** the per-turn counters **accumulate across the agent's internal tool loop** within one invocation (SPIKE-3 E1 — with `FR11_CAP=3`, creates #1–#3 allowed, #4–#5 denied) and **do not re-arm after a confirm** (E2 — the counter does not reset; every further create of that class confirms). This prevents an injection from chaining N-at-a-time through confirm-fatigue.

**Cap-state ownership and lifetime (R5-BL1, live-confirmed — binding):**

> The cap-state (the FR-40 distinct-files set + the FR-11/FR-38/FR-40 no-re-arm tripped state) is **owned by HoardPad and keyed to the user-initiated conversation / logical turn**, NOT the per-invocation `session_id`. Because each `claude -p` invocation gets a **fresh `session_id`** with **no cross-call hook memory** (SPIKE-3 H8, live-confirmed: two invocations → two distinct UUIDs), an invocation-scoped counter would **re-arm on every HoardPad-initiated continuation** and degrade the per-attack bound from *N distinct files* to *N files per one-click confirm*.

The mechanism (SPIKE-3 H5, deterministically confirmed): HoardPad mints a durable conversation/logical-turn id when the user initiates an AI conversation and persists the cap-state under it in a HoardPad-owned, exclusive-locked file (the SPIKE-4 `O_EXCL` SpinLock, portable to Windows `CREATE_NEW`; never `fcntl`). A HoardPad-initiated **continuation** (a fresh invocation / `session_id`) **resumes the same cap-state** (SPIKE-3 H5: file #4 was DENIED across the continuation — the set did not reset). **Only a new user-initiated conversation resets it** (H6: after deleting the cap-state file, file #4 is allowed). This is the deterministically-confirmed cross-continuation no-re-arm property.

**Create-vs-overwrite split — on BOTH `Write` and `Edit` (SPIKE-3 F):** the hook does a `stat`/existence check on the target of **both** `Write` and `Edit`. A `Write`/`Edit` whose target does **not** exist is a **create** (→ FR-11). A `Write` whose target **exists**, or an `Edit` whose `new_string` is **≥ 80%** of the prior file bytes, is a **destructive overwrite** (→ FR-38). An `Edit` with empty `old_string` on a non-existent path is the agent's file-create idiom (→ FR-11, and increments FR-40). This closes the `Edit`-create bypass (SPIKE-3 F3, F5, F6) — the FR-38 cap fires on Edit-routed full-file overwrites, not only on Write-keyed ones; the bypass is closed by the **magnitude predicate**, not by a Write-keyed counter.

**FR-40 increments on both paths, counts distinct files, is misroute-invariant (SPIKE-3 H):** FR-40 maintains a **set of distinct canonical paths** touched this turn, incremented on **every** create and **every** overwrite (regardless of per-op X% magnitude — H2: 5 sub-threshold overwrites trip FR-40 before FR-38 ever fires). A file touched many times counts **once** (H3). A `stat`-race misroute (overwrite misrouted to the create bucket) still increments FR-40 once (H4) — closing the stat-race escape.

**Substitution coverage (SPIKE-3 G, H7) — the cap is only as strong as the matcher:** a capped agent *will* attempt substitution (`Bash`, `AskUserQuestion`, `Edit`-create, `ToolSearch`). The deny-by-default matcher denies **all** of them after a cap (G1 Bash, G2 AskUserQuestion, G3 Edit-create, H7 AskUserQuestion + ToolSearch after an FR-40 pause). A Phase-8 build that weakens the matcher to a denylist defeats every cap.

**Counting disjointness (§9.3 mi3 rule, binding):** an op increments exactly one *per-op* cap (FR-11 **or** FR-38, disjoint) **and** separately the FR-40 set. Caps count **applied** ops, not attempted — a denied/blocked op does not burn the cap. A clobbering move/copy (Archetype-B-only) is governed by FR-10(b)(ii)'s individual confirm and is **not** double-counted toward FR-38. A clobbering full-overwrite under the size cap gets **no individual confirm** (FR-10(b)(ii) is move/copy only) — it is governed only by FR-38 + FR-40 (the deliberate asymmetry vs a clobbering move).

### 4.6 FR-39 queue (SPIKE-3 C3, binding)

When an `approve-each` confirm goes unanswered past the no-reply timeout, HoardPad **fails closed**: denies the op, kills the `claude` process tree (§2.3), and **parks the captured `tool_input`** (the full PreToolUse event JSON from the hook's stdin — it fully describes the proposed create/overwrite/edit) in the FR-39 pending-proposals surface with apply/discard actions. Every queued op is logged.

> **Replay is via the shared file-ops service (the Archetype-B execution path), NOT by re-launching `claude` or re-invoking the hook.** Applying a queued Archetype-A proposal later runs the captured `tool_input` through the shared service's full FR-26/FR-28/FR-29 path as an Archetype-B op. This is safe because a PreToolUse-denied/killed op left **no partial on-disk state** — there is nothing half-written to reconcile (SPIKE-3 C3).

### 4.7 Liveness (MA14, SPIKE-3 I — binding)

Every hook-failure mode (timeout, malformed config, missing command) is a **silent allow with no negative CLI signal** — so liveness cannot be detected by listening for an error. It must be tracked **positively**:

> Match each `stream-json` write tool-use event the agent emits against an observed per-op hook callback, **keyed by `tool_use_id`** (present in the hook's stdin). A write tool-use event with **no matching hook invocation** is the degrade signal (the hook armed at startup — proven by the FR-24 §3 self-test — but is no longer being honored per op). SPIKE-3 I confirmed hook fires match 1:1 with tool-use events.

On a detected mid-turn degrade (an absent expected callback, or a hook invocation that fails/times out), FR-10(c) applies: HoardPad blocks/confirms the remaining ops or halts the turn; ops already applied before detection are logged.

The **residual Archetype-A TOCTOU window** (FR-26/M6) is documented honestly: a path component could be swapped to a symlink between the hook's canonicalize check and the agent's write. Archetype A is observe-and-veto, so the hook (against a writer it does not execute) cannot fully close it. Archetype B is TOCTOU-free (HoardPad canonicalizes-then-writes in one step as the writer). This is a documented limitation, not a bug.

### 4.8 Windows interception = DEFERRED (Decision 2) — folded honestly

SPIKE-4's run shape is the Decision-2 explicit deferral: no Windows surface was available, so all Linux-side build/compile/portability/harness work was done and the Windows **runtime** legs are classified **Deferred — not fabricated.** A Deferred result **renegotiates** the SC-5/SC-8 win-x64 acceptance basis; it does **not** fail the project.

| Leg | Status (carried forward) |
|---|---|
| Hook OS-portability (`fcntl` → `O_EXCL` SpinLock; `normcase` case-insensitivity) | **Met (Linux-side)** — ported, smoke-tested, Linux behaviour unchanged |
| Windows `--settings` package + `python` resolution | **Met (authored)** — ready to run unchanged on Windows |
| Hook-side assertions (hooks-fire / deny-on-Write+Edit / matcher parity) | **Authored, Deferred (Windows runtime)** — turnkey `Run-Assertions.ps1` |
| Win32 Job-Object kill + `taskkill /T /F` fallback | **Met (win-x64 compile-confirmed)**; Windows runtime **Deferred** |
| No-orphan-after-kill | **Linux-analogue PASS**; Windows Job-Object runtime **Deferred** |
| Timed-out-hook-must-not-leak-a-write | **Linux-analogue PASS**; Windows runtime **Deferred** |
| `claude.cmd`/`.exe` shim resolver | **Met (compile-confirmed + live Linux probe)**; real Windows install shape **Deferred to confirm** |

> The Windows **runtime** interception legs are **Deferred to Windows hardware. This doc does NOT assert them "Met."** SC-5/SC-8 "Met" on win-x64 remains subject to the Decision-2 renegotiation clause.

**Portability fixes folded as design constraints (carry into the Phase-8 hook):**
- `fcntl` → cross-platform `O_EXCL` SpinLock (atomic on POSIX `O_CREAT|O_EXCL` and Win32 `CREATE_NEW`; 5 ms spin, 10 s hard timeout, unlinked in `finally`). Preserves the FR-40 cap-state exclusive-lock semantics.
- `os.path.normcase` on both sides of every path comparison; the version-dependent `relative_to` replaced with an explicit normcased string-prefix match (a documented no-op on Linux).

**TD-1…TD-5 carried as design constraints (SPIKE-4):**

| Flag | Constraint (binding on the Phase-8 hook/host) |
|---|---|
| **TD-1** | **Require Python 3.12+ on Windows** for the hook (so `pathlib.resolve()` calls `GetLongPathName` and expands 8.3 short names — a sandbox-escape vector on 3.10/3.11), **or** add explicit `GetLongPathName` `ctypes` hardening. The host's hook-settings builder (§2.4) probes the interpreter and the Python floor. |
| **TD-2** | UNC paths (`\\server\share\…`) are handled correctly by the normcased prefix check; attention needed only if the sandbox itself is a UNC share. |
| **TD-3** | Drive-relative paths (`C:foo`) are handled by `resolve()`; no extra hardening. |
| **TD-4** | Log whether Windows `claude -p` sends `tool_input.file_path` with forward/back/mixed slashes (no behaviour impact — `pathlib` accepts both — but record it). |
| **TD-5** | `CREATE_BREAKAWAY_FROM_JOB` is version-dependent — tech-design specifies **Job-Object-primary AND `taskkill /T /F`-fallback regardless** (already pinned in §2.3). |

The turnkey Windows-runtime checklist is `spike-4-results-2026-06-19.md` §"What a future Windows-hardware session must do" — the remaining work: probe `python`/`py`, run `Run-Assertions.ps1`, run the live `claude -p` assertions (deny prevents real Write **and** Edit; exit-2 fallback; `Task`→`Agent`; timed-out-hook-no-leak), `dotnet publish` the harness and assert 0 survivors after `TerminateJobObject`, record `where claude` + the `.cmd` structure + the post-launch tree + the breakaway behaviour, then re-classify the Deferred legs to Met-on-win-x64 (or Degraded) and update SC-5/SC-8.

---

## 5. Custom controls + M18 effort/risk estimates

The M18 tech-design deliverable: per-item effort and risk for the custom-control surface PRD §11.1 line 781 names (FR-1, FR-6, FR-7, FR-9, FR-15, FR-30/FR-32, FR-37). Two of these (FR-37, FR-9) were de-risked by the M18 sub-spikes; their estimates are grounded in measured results. Estimates are person-days, solo. Layout-cost claims are grounded in SPIKE-1's layout-dominates finding.

| Item | Control | Effort (person-days) | Risk | Sub-spiked? |
|---|---|---|---|---|
| **FR-15** | Markdig-AST → Avalonia render tree | **6–9** (+2–4 optional preview code-coloring) | Low–Medium | SPIKE-2 (M17) |
| **FR-37** | Brand TextMate theme + lockstep switch | **2–3** | Low–Medium | SPIKE-FR37 |
| **FR-9** | Indentation folding (full FR-9) | **7.5** | Low–Medium | SPIKE-FR9 |
| **FR-1** | Tab drag-reorder behavior | **1.5–2.5** | Low | — |
| **FR-6** | Move/duplicate-line + encoding detection | **2–3** | Low–Medium | — |
| **FR-7** | Custom `IRegistryOptions` + supplemental grammars | **2–3** | Low–Medium | — |
| **FR-30/FR-32** (flat) + **FR-41/FR-42** (nesting, ODQ-11) | `.hoard` checklist control (items + drag-reorder) **+ nesting + completion-lock gating + collapse + indent/outdent** | **~7–11** (was 4–6 for flat; ODQ-11 adds **+3–5**) | Low–Medium | — |

### 5.1 FR-15 render tree — 6–9 person-days (SPIKE-2 M17, grounded)

SPIKE-2's Candidate-A walker already reaches all 20 FR-16 assertions at prototype fidelity in ~a day. The M17 estimate is **~6–9 person-days** to production FR-16 fidelity inside HoardPad. Risk **Low–Medium** (the AST→control mapping is mechanical and proven end-to-end; the only genuine unknowns are large-table layout and the inline-HTML styling-vs-passthrough policy, both bounded). Breakdown (SPIKE-2 M17): harden the walker + theme-aware brushes (light/dark, FR-37 lockstep) ~2 d; link click-nav + footnote-anchor scroll ~1 d; table column-width strategy + nested-block-in-cell ~1–1.5 d; image loading (local-relative, async, broken-image, no-network) ~0.5–1 d; inline-HTML policy + raw-HTML-block display ~0.5–1 d; large-doc behavior + FR-15 toggle wiring + SC-4 round-trip + read-only enforcement ~1 d; FR-16 fixture-driven test pass + visual QA on both RIDs ~1 d.

> **Optional preview code-block syntax coloring: +2–4 person-days.** Candidate A shows code blocks plain-mono with the language label (FR-16-sufficient). Coloring them by reusing the editor's TextMate tokenizer is a discretionary enhancement, **not** an FR-16 requirement. Phase 8 sizes it as an explicit option; it is the only place the preview visibly differs from VS Code / Candidate B.

The FR-15/FR-30/FR-32 layout cost is grounded in SPIKE-1's finding that **layout dominates** (parse + control-tree walk are negligible) — the render-tree build is cheap (~13.5 ms for ~20 KB, SPIKE-2 §D); the cost is the Avalonia layout pass, which is governed by the §1.4 preview threshold (~100–200 KB).

### 5.2 FR-37 brand TextMate theme + lockstep switch — 2–3 person-days (SPIKE-FR37, grounded)

SPIKE-FR37 proved the mechanism: `TextMate.Installation.SetTheme(registryOptions.LoadTheme(...))` swaps the editor's syntax theme live without recreating the editor or losing document/scroll state; the call returns synchronously and the editor re-colors on the next render pass. Lockstep atomicity was proven — `ThemeService.Switch(palette)` performs all four mutations (TextMate `SetTheme`, editor background, preview-palette swap, `Application.Current.RequestedThemeVariant`) in **one synchronous UI-thread call with no yield**, so the render loop never observes a partial state (dark→light→dark round-trip PNGs are pixel-identical in the preview pane). Switch latency (synchronous data-swap) is **p50 ~2 ms, p90 ~3 ms**.

**Estimate: ~2–3 person-days. Risk Low–Medium.** Breakdown (SPIKE-FR37 Q5): production `ThemePalette` record with HoardPad brand dark/light (requires `brand.md` / Phase 7) ~0.5 d; wire `ThemeService` into the app shell (toolbar toggle + system-theme detect) ~0.5 d; confirm re-tokenize behaviour on large docs + `SuspendUpdate` if a flash is seen + Windows round-trip ~0.5 d; system-theme auto-detect + user-override setting ~0.5 d; settings persistence (last-chosen / follow-system) ~0.5 d.

**Residual (Medium factors, carried):**
- **Large-doc re-tokenize flash** is not directly observable headless (AvaloniaEdit's syntax layer needs a live GL surface). At ~50 KB re-tokenize measured under 2 ms — a single-frame re-color is plausible but not guaranteed; **needs a real-GPU validation pass**, with `SuspendUpdate` as the documented mitigation if a flash appears.
- **Windows 10 title-bar chrome:** `RequestedThemeVariant` follows the system title bar on Windows 11 (App SDK bridge) but **not Windows 10** — chrome/title-bar may not switch in lockstep on Win10; +0.5 d for a Win10-specific fallback if needed (carried as a deferral — win-x64 not run from this Linux box).
- **Cache `IRawTheme` at startup:** `registryOptions.LoadTheme()` deserializes the embedded TextMate JSON on every switch (~1–2 ms); caching one `IRawTheme` per palette at startup drops the data-swap under 1 ms. Minor optimization, worth doing.

### 5.3 FR-9 indentation folding — 7.5 person-days (SPIKE-FR9, grounded)

SPIKE-FR9 proved the mechanism: a pure `IndentationFoldingStrategy` over a `TextDocument` returns `List<NewFolding>` for `FoldingManager.UpdateFoldings()`, **display-only** (never mutates the document — byte-identical round-trip after expand, EC-10 + Q3 confirmed), 19/19 tests pass (Python + YAML 3-level nesting + all named edge cases). Recompute is **O(n)**: 1.98 ms / 5.45 ms / 12.36 ms avg at 1k / 5k / 20k lines (worst 33.37 ms at 20k).

**Estimate: 7.5 person-days for full FR-9. Risk Low–Medium.** Breakdown (SPIKE-FR9 Q5): `IndentationFoldingStrategy` production (Python/YAML/TOML/Makefile/Dockerfile + per-language indent rules + continuation-line exclusions) 2 d; `BraceFoldingStrategy` (C/C++/C#/Java/JS/TS/Rust) + `FoldingStrategySelector` by TextMate scope 2 d; `FoldingService` (debounced background recompute + UI-thread commit, wired to `TextChanged`, cancellation on document replace) 1 d; per-language edge-case validation + regression fixtures 1.5 d; integration (settings panel enable/disable per language, fold/unfold shortcuts) 1 d.

**Binding design from the sub-spike (the perf shape):**

> Folding recompute runs **debounced (200–400 ms after the last change) on a background thread** — the strategy is thread-safe (it reads a `TextDocument.GetText()` snapshot and builds a fresh list, touching no UI-thread Avalonia primitive). `FoldingManager.UpdateFoldings()` is called on the UI thread (O(folds), typically sub-ms). This mirrors Rider/VS Code (background analysis, UI-thread commit) and keeps the supra-linear-at-20k worst case (33 ms) off the critical render path SPIKE-1 identified as the bottleneck. At 20k lines the average (12 ms) is inside a 16 ms frame budget but the worst case is not — hence background, not synchronous-on-keystroke.

**Residual (Medium factors, carried):**
- **Per-language indent quirks:** Python continuation lines (backslash/implicit-paren), YAML block scalars (`|`, `>`), Makefile recipe TABs — none are algorithmic blockers but each adds testing surface + a per-language fold-exclusion hook.
- **TextMate-scope-aware fold suppression:** in production, querying the TextMate scope per line to skip fold regions inside string literals / multi-line comments (and to choose indent-vs-brace strategy) adds complexity + a dependency on the tokenizer's async lifecycle not explored in the sub-spike.
- **`UpdateFoldings()` cost at scale unbenchmarked:** at ~6,843 folds (20k-line doc) the FoldingManager's internal BST update is not free; **benchmark it before finalizing the debounce interval.**

### 5.4 FR-1 tab drag-reorder — 1.5–2.5 person-days, Low risk

Avalonia's `TabControl` provides no drag-reorder out of the box (FR-1 build note). The control is a custom reorder behavior over the tab strip's `ItemsControl`: pointer-press/drag/drop with a drop-indicator, computing the new tab index and reordering the bound `ObservableCollection`. No measured layout pressure (the tab strip is a small, flat item set). Effort **1.5–2.5 d** (the drag mechanics + the drop-indicator visual + keyboard-reorder parity); **risk Low** — a well-trodden Avalonia behavior pattern, no measured unknowns. This is cut-order item (1) in A-8 if the AI host overruns (keep keyboard tab-switch + close; drop the custom drag).

### 5.5 FR-6 move/duplicate-line + encoding detection — 2–3 person-days, Low–Medium risk

Two pieces (FR-6 build note): (a) **move-line up/down + duplicate-line** are custom `TextDocument` commands not built into AvaloniaEdit — straightforward document-mutation commands with multi-line-selection handling (~1 d, Low risk); (b) **encoding detection on open** — BOM sniffing + a charset detector (e.g. `UTF.Unknown`) with detect-then-preserve-on-save (NFR-9) (~1–2 d, Low–Medium risk — the unknown is detector accuracy on ambiguous files and the preserve-on-save round-trip). Bundled offline (A-7/NFR-7). Encoding detection is cut-order item (4) in A-8 (ship UTF-8-only, defer detection) — reaching it is the escalate-to-maintainer floor.

### 5.6 FR-7 custom `IRegistryOptions` + supplemental grammars — 2–3 person-days, Low–Medium risk

`TextMateSharp.Grammars` covers ~18 of the 20 languages out of the box; **TOML is likely absent and Rust may lag** (FR-7 build note). The work: a custom `IRegistryOptions` that overlays bundled supplemental `.tmLanguage.json` grammars (TOML, and Rust if it lags) on top of the stock registry, registered **lazily** per-language (NFR-1, the SC-1 enabling mechanism), surfaced through the language auto-detect/override (FR-8). All grammars bundled as embedded resources (offline, A-7/NFR-7). Effort **2–3 d**; risk **Low–Medium** — the unknown is sourcing/validating two supplemental grammars and the lazy-registration wiring (already validated in the SPIKE-1 perf app's lazy-on-first-open grammar load). If trim is enabled (§1.1), the supplemental-grammar resources are pinned as trim roots alongside `TextMateSharp.Grammars`.

### 5.7 FR-30 / FR-32 (+ FR-41 / FR-42, ODQ-11) checklist control — ~7–11 person-days, Low–Medium risk

The `.hoard` checklist is a custom Avalonia MVVM control — an `ItemsControl` + drag-reorder behavior — that does **not** involve AvaloniaEdit (FR-30 build note, applies FR-30…FR-34, FR-41, FR-42). The control is extended with **tree ARIA semantics** (`AutomationProperties` level/position-in-set/expanded/disabled, ODQ-11 D11) rather than re-hosted on a stock `TreeView` (the `TreeView` would forfeit the FR-32 drag-reorder + FR-33 display-mode behavior the custom control owns).

**Flat-checklist base (the original FR-30/FR-32 estimate, ~4–6 d):** the checkable/editable item control; the drag-reorder behavior (sharing the FR-1 drag pattern); the three checked-item display modes with the §9.1 display-index→stored-`order` mapping (a pure domain function, unit-tested per §9.1); the durable per-mutation auto-save through the serialized write queue (FR-31, zero-debounce terminal mutations confirmed affordable §1.5).

**Nesting + completion-lock + collapse + indent/outdent (the ODQ-11 addition, rough order +3–5 d, ODQ-11 risk 3):** `parentId`-derived tree rendering (indent guides + the ▸/▾ twisty); `Alt+Shift+←/→` indent/outdent carrying the whole subtree (D9) with depth-cap enforcement (the no-op past tier 2); the `effectiveEnabled` ancestor-chain gating that greys/disables a locked subtree while children keep their real marks (D2/D3a) — **`effectiveEnabled(item)` = every strict ancestor has `checked == false`; a tier-0 item is always enabled; the item itself stays toggleable** (so a completed parent can always be unchecked to reopen), identical contract wording to PRD §9.1 and architecture §5.1/§6.5; the `collapsed` terminal mutation (FR-42); the delete-parent prompt (delete-subtree vs promote-children, D8, both branches undoable + re-persisted); and the tree ARIA announcements. The display-mode mapping rises in complexity — it gains the **per-sibling-group partition dimension** (a completed parent block moves/hides as one contiguous unit within its sibling group) — but **stays a pure, property-tested domain function** in `Core.Checklist`; the property test must **sweep drag-and-mode behavior within nested sibling groups, including a checked-parent-block move/hide, under each mode**. The `effectiveEnabled` gating and `depth`/sibling-group-renormalize/depth-cap are likewise pure C-9 domain functions (the control reads them; it embeds no gating or partition arithmetic).

**Total effort ~7–11 d; risk Low–Medium** — the complexity is concentrated in the now-hierarchy-aware display-mode mapping and the gating, both pure domain logic with defined §9.1 contracts and property tests; layout cost stays negligible (a flat, small item set — layout-dominates does not bite). The pure-function placement (`HoardPad.Core.Checklist`) is what keeps the `senior-csharp-multi-platform` implementability check tractable.

> **Known keymap risk — `Alt+Shift+←/→` on linux-x64 (C-2a; binding KEPT).** The decided indent/outdent chord is `Alt+Shift+←/→` (ODQ-11 D5, unchanged). On stock GNOME/IBus + KDE, `Alt+Shift` is a frequent layout-switch modifier and the WM/IME may consume the chord before Avalonia sees it. Mitigation is the existing user-rebindable shortcut (ODQ-13) + the always-available `Checklist ▸ Indent/Outdent` menu/context-menu path (design §5.2/§8.1), so nesting never depends solely on the chord. **Final binding confirmation under the target Linux compositors is a Phase-8 build-time check** — do not change the decided primary binding; if a common environment consumes it, surface a default-rebind suggestion.

> **A-8 cut-order question (open, for the maintainer at scope-pressure time).** The flat checklist is **protected** in A-8's cut order (the editor absorbs the slip, not the `.hoard` product). **Is the ODQ-11 nesting layer (FR-41/FR-42 + the indent/outdent/collapse/gating additions) equally protected, or is it a v1 cut candidate if the build slips?** If the +3–5 d ODQ-11 increment is *not* protected, a defined fallback is to ship the flat checklist (FR-30/FR-32/FR-33 at v1 baseline, no nesting) and defer nesting to a fast-follow — the v2 schema already loads a flat file as all-tier-0, so cutting the nesting layer is a UI/interaction cut, not a data-model rollback. **Proposed default: flag to the maintainer; the data model ships v2 either way** (forward-compatible), so the cut is reversible and low-cost. Surfaced as the ODQ-11 effort-growth decision (ODQ-11 risk 3).

---

## 6. Carry-forward / honest deferrals + Phase-5 readiness

### 6.1 What stays Deferred (NOT silently upgraded to "Met")

These legs were honestly deferred by the spikes and are carried forward as deferrals. A Phase-8 build (or a future Windows-hardware session) closes them; this doc does **not** assert any of them "Met."

| Deferral | Why | Closes when |
|---|---|---|
| **win-x64 perf (SC-1/SC-2/NFR-5)** | All SPIKE-1 numbers are linux-x64; the win-x64 publish compiles clean but was not run on the Linux box | Re-measured on real Windows hardware (SPIKE-1 non-confirmation 1) |
| **Native Wayland synthetic input + Wayland SC-1/SC-2 leg** | SPIKE-1 measured the genuine input-stack path under **X11/Xvfb** (the fallback was not needed); no Wayland compositor was present (`WAYLAND_DISPLAY` empty). The input *methodology* is resolved; the Wayland-specific leg is outstanding | Validated on real Wayland hardware (`libei`/portal or `wlroots` virtual-keyboard); Wayland SC-1/SC-2 numbers re-measured (R3-mi10) |
| **Windows interception runtime (SPIKE-4)** | No Windows surface; the runtime Job-Object kill / hook-fire / deny-on-Write+Edit / no-leak legs are compile-confirmed + Linux-analogue-PASS only | A Windows-hardware session runs the turnkey checklist (SPIKE-4 §"What a future Windows-hardware session must do"); SC-5/SC-8 win-x64 re-classified |
| **Windows `MoveFileEx` first-ever-write power-cut (SPIKE-5)** | The `MoveFileEx(MOVEFILE_WRITE_THROUGH)` create path is **compile-confirmed only**; the runtime absent-or-complete (never-torn) assertion is not runtime-testable on Linux | A Windows-hardware power-cut test asserts absent-or-complete (SPIKE-5 non-confirmation 1). **NOT asserted "Met" here.** |
| **Strict SC-2 warm-open path** | SPIKE-1's 209 ms is a grammar-cold upper-bound proxy; the strict second-file-into-running-instance path needs a small harness extension | Phase 8 harness extension (expected < 209 ms; not a blocker) |
| **Multi-tab idle RSS** | SPIKE-1's gate-comparable figure is single-instance 235.9 MB; per-tab incremental RSS is unquantified | Phase 8 multi-tab measurement against the m1 doc-set |
| **NFR-9 split thresholds** | The ~100–200 KB preview / ~500 KB editor split is a **single-sample** directional reading | Phase 8 confirms with a proper distribution, separating preview-layout from editor-highlight cost, before final pin |
| **FR-37 re-tokenize flash + Win10 title-bar; FR-9 `UpdateFoldings()` at scale** | Sub-spike residuals (headless could not render the editor syntax layer; Win10 chrome not run; 6,843-fold BST cost unbenchmarked) | Real-GPU validation pass + Win10 fallback (FR-37); `UpdateFoldings()` benchmark before finalizing the debounce interval (FR-9) |

The AI file-op safety model (sandbox + undo + log + caps + process-tree kill) is specified in full in §2/§4 and is **non-negotiable** — none of the deferrals above weaken it. The Linux interception story is proven (SPIKE-3); only the Windows *runtime* re-confirmation is deferred.

### 6.2 Readiness to proceed to Phase 5 (`architecture.md`)

Every §11.1 spike is complete and folded. Every PROVISIONAL number is pinned (§1) and traced to its spike. The C-9 seam interfaces (§2), the deterministic serialization contract (§3), the two-archetype provider mechanics (§4), and the M18 effort/risk estimates (§5) are specified to the level a Phase-8 `/code` build executes against. The honest deferrals (§6.1) are carried forward, not silently upgraded.

This document is the measured-input-of-record for the PRD-reconciliation pass (which flips the PRD's §5/§11.1 provisional markers to measured gates via `requirements-architect`, never renumbering) and is **ready to feed Phase 5 (`architecture.md`)**, which finalizes the `HoardPad.Domain` / `HoardPad.Host` / `HoardPad.App` project structure the §2 seams imply, the DI wiring, and the module/assembly boundaries.

---

## 7. Open questions

Residual tech-design decisions, each with a proposed default so none blocks Phase 5. These are HOW-level, not scope-level (scope is locked by the PRD).

- **OQ-TD-1 (preview code-block syntax coloring).** Ship the optional +2–4 d preview code-coloring (§5.1) in v1, or defer? **Proposed default: defer to a fast-follow** — it is not an FR-16 requirement and the build budget is already heavy with the AI host (A-8). Adopt unless the maintainer wants the VS-Code-parity look in v1.
- **OQ-TD-2 (overflow-bag vs custom converter, §3.2(b)).** v1 pins `[JsonExtensionData]` + accepted one-time relocation diff. The custom raw-key-order converter (zero relocation, absolute no-spurious-conflict §9.2 guarantee) is the documented upgrade path. **Proposed default: ship `[JsonExtensionData]` in v1**; revisit only if the maintainer sees real two-machine sync-conflict friction on `.hoard` files in practice.
- **OQ-TD-3 (FR-9 `UpdateFoldings()` debounce interval, §5.3).** The 200–400 ms debounce is a range; the exact value depends on the unbenchmarked `UpdateFoldings()` BST cost at ~6,843 folds. **Proposed default: 300 ms, confirmed by the Phase-8 `UpdateFoldings()` benchmark** before finalizing.
- **OQ-TD-4 (FR-37 Windows-10 title-bar fallback, §5.2).** Whether a Win10-specific title-bar fallback is needed is unverified from this Linux box. **Proposed default: implement the lockstep switch for chrome+editor+preview as specified; add the Win10 title-bar fallback only if the deferred win-x64 validation shows the title bar does not follow `RequestedThemeVariant` on Win10** (+0.5 d if needed).
- **OQ-TD-5 (`defer`/`updatedInput` in approve-each, §4.3).** SPIKE-3 flagged the nested-JSON `defer`/`updatedInput` fields as candidates for the approve-each / FR-39 design. **Proposed default: keep the v1 synchronously-blocking allow/deny hook; evaluate `defer`/`updatedInput` as a Phase-8 refinement** — do not redesign on them pre-build.
- **Telemetry — deferred per project privacy constraint.** Per PRD §14, telemetry / measurable-success-signal instrumentation (the `telemetry-analyst` surface) is deliberately deferred for HoardPad v1: no telemetry is a hard privacy constraint (C-6, NFR-7) and the product is personal-first with no remote adoption metrics to validate. Success is validated locally against the §5 testable criteria. No `telemetry-analyst` engagement for v1. *(Recorded here so the decision is visible at tech-design time, not only in the PRD.)*

---

## 8. Reviewers required — ALL RESOLVED (Phase-5 review, 2026-06-19)

> **Status: all six lines RESOLVED → confirmed by the Phase-5 combined specialist review (2026-06-19), zero objections; reports at `docs/review-phase5/`.** These lines were deferred from the Phase-4 session (the spikes were the Phase-4 verification gate) and folded into the Phase-5 architecture review round, since the same specialists review `architecture.md` against the same §2/§3/§4 decisions. Each is confirmed with no change to the pinned design; the substance is retained below for traceability, not re-opened.

Per the pre-publication review protocol, the following specialist reviews validate the sections that touch their domains. (Each is annotated with its Phase-5 resolution.)

```
REVIEW_REQUEST: senior-csharp-desktop-developer | §2 Seams + §5 Custom controls | Confirm the C-9 seam interface signatures (IAtomicFileStore, IPathResolver, IAgentBackend) and the FR-15/FR-37/FR-9/FR-1/FR-6/FR-7/FR-30 effort estimates are implementable as specified against Avalonia 11.x + AvaloniaEdit + .NET 8, and that the §2.3 Job-Object/setsid kill design and §5 layout-cost groundings are sound.
  → RESOLVED (Phase-5 review 2026-06-19, CONFIRMED; docs/review-phase5/senior-csharp-desktop-developer.md — TD line 629 CONFIRMED, seam signatures + kill design + M18 estimates implementable as specified)
REVIEW_REQUEST: senior-csharp-multi-platform | §2 Domain/Host split + §3 Serialization | Confirm the HoardPad.Domain / HoardPad.Host boundary keeps every OS syscall out of the domain, the SandboxPolicy.Evaluate pure-domain split is correct, and the §3 deterministic .hoard serialization contract (pinned UnsafeRelaxedJsonEscaping + [JsonExtensionData] + fixed ordering + known-key promotion) is sound for cross-version round-trip.
  → RESOLVED (Phase-5 review 2026-06-19, CONFIRMED; docs/review-phase5/senior-csharp-multi-platform.md — TD line 630 CONFIRMED; the latent seam-namespace defect is corrected by §2's Phase-5 placement-refinement note above + architecture.md §1.2)
REVIEW_REQUEST: llm-integration-engineer | §4 Two-archetype AI provider | Confirm the §4.1 launch contract, §4.2 deny-by-default nested-JSON hook protocol, §4.4 fail-closed model (HoardPad-kill-as-sole-enforcer), §4.5 conversation-keyed cap model + create/overwrite split, §4.6 FR-39 replay, §4.7 liveness, and the §4.8 Windows-deferral + TD-1..TD-5 are correctly folded from SPIKE-3/SPIKE-4 and do not weaken the safety model.
  → RESOLVED (Phase-5 review 2026-06-19, CONFIRMED; docs/review-phase5/llm-integration-engineer.md — TD line 631 CONFIRMED, every clause traces faithfully to SPIKE-3/SPIKE-4; safety model not weakened)
REVIEW_REQUEST: sqlite-dba | §3.3 Append-log durability | Confirm the length-prefixed + CRC-32/ISO-HDLC record format, per-record-fsync-before-next-append, on-open truncate-repair, StopAtFirstBad-default / resync-and-continue-fallback, and the carried B4 false-positive characterization are the correct durable-append design for the activity log + undo journal (SQLite was considered and rejected in §9.2; confirm a flat append-log remains the right call).
  → RESOLVED (Phase-5 review 2026-06-19, CONFIRMED; docs/review-phase5/sqlite-dba.md — TD line 632 CONFIRMED, crash-grounded in SPIKE-5; SQLite rejection independently concurred — keep it rejected)
REVIEW_REQUEST: unit-test-engineer | §1 Gates + §3.5 Round-trip test + §6.1 Deferrals | Confirm the pinned gates (SC-1/SC-2/NFR-5/caps), the §3.5 scoped round-trip fidelity tests, and the A-6 fixture dependencies are testable as specified, and that the §6.1 carried deferrals (strict SC-2, multi-tab RSS, Windows runtime, MoveFileEx power-cut) are correctly NOT asserted Met.
  → RESOLVED (Phase-5 review 2026-06-19, CONFIRMED; docs/review-phase5/unit-test-engineer.md — TD line 633 CONFIRMED; an explicit unknown-key-survives-mutation assertion was added to the §3.5 corpus at architecture.md §5.1)
REVIEW_REQUEST: project-manager | §5 M18 estimates + §6 Readiness | Confirm the §5 effort/risk estimates and the A-8 cut-order mapping are internally consistent with the PRD scope, and that §6.2 readiness to proceed to Phase 5 holds given the carried deferrals.
  → RESOLVED (Phase-5 review 2026-06-19, CONFIRMED; consistency confirmed in the Phase-5 round, zero blockers)
```

## Reviewed — Phase-5 round complete

All six lines above were dispatched and confirmed in the Phase-5 combined review (2026-06-19) — zero objections, no change to any pinned tech-design decision. Reports at `docs/review-phase5/`. Per the council-review protocol, each section was drafted against the measured spike inputs; the specialist reviews were the pre-publication validation layer, now complete. The tech-design is confirmed and the architecture (`docs/architecture.md` v2) folds the round's SHOULD-FIX items.
