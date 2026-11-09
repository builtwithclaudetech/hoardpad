# HoardPad — System Architecture (Phase 5)

> **Note:** This is the internal design record written during development, published as-is for transparency. It cites internal working files (the original project brief, brand guide, review reports, spike results and project rules files) that are not included in this repository; those citations are kept for traceability.

| Field | Value |
|---|---|
| **Project** | HoardPad |
| **Document** | `docs/architecture.md` — Phase-5 system architecture (the blueprint the Phase-8 `/code` build scaffolds the entire codebase from) |
| **Status** | Draft v2 — authored 2026-06-19; v2 folds the completed Phase-5 combined specialist review (reports at `docs/review-phase5/`) |
| **Owner** | The maintainer |
| **Realizes** | [`docs/tech-design.md`](tech-design.md) (the Phase-4 design of record) and [`docs/prd.md`](prd.md) (the signed-off contract). This doc makes their HOW concrete; it does **not** re-decide what they pinned. |
| **Related docs** | `brief.md`, `CLAUDE.md`, the five spike-results docs + the two M18 sub-spike `RESULTS.md` files (cited inline), future `docs/design.md` (Phase 6, user-facing UX), future `brand.md` (Phase 7, palette) |
| **SDLC phase** | Phase 5 — system architecture (feeds Phase 6 design; the Phase-8 `/code` build scaffolds the solution from this doc) |

This document is binding architecture. It is read top-to-bottom and references the tech-design and PRD by stable id (`TD §2.1`, `FR-26`, `C-9`, `NFR-4`, `SPIKE-3 §B`). It does **not** restate the PRD's normative requirements or re-derive the tech-design's pinned decisions — it realizes them as a concrete .NET solution, a dependency graph, a threading model, a persistence layout, and a build topology. Where the tech-design carried an honest deferral (win-x64 interception runtime, the `MoveFileEx` first-write power-cut, win-x64 / Wayland perf), this doc carries it forward and does **not** assert it "Met" (§7.6, §10).

## Changelog

- **2026-06-19 — v2 (folds the completed Phase-5 combined specialist review; per-reviewer reports at `docs/review-phase5/`).** Tightening fixes only; no pinned decision re-decided (renderer, caps, encoder, min-CLI, gates unchanged).
  - **§1.2 / §2.1 / §2.2 / §6 / OQ-ARCH-4 (the one convergent 3-reviewer item).** The FR-6 charset detector (`UTF.Unknown`) now lives behind an `IEncodingDetector` Host/App seam at the I/O edge — **never inside `HoardPad.Core`**. The §2.1 "BCL + `System.Text.Json` only" invariant stays absolute; only the pure detect-then-preserve *decision* is in `Core`.
  - **§1.6.** Added `HoardPad.App.Tests` (owns SC-3 20-language token read-back, SC-4 toggle, the FR-16 renderer-fidelity fixture); named the interior-bad-record `resync` fixture in `Host.Tests`; flagged the Windows-first-create power-cut fixture authored-but-Deferred (not asserted on Linux); assigned NFR-5 idle-RSS to `HoardPad.Bench` and the MA14 liveness-match test to `Core.Ai.Tests`; drew the harness dependency edges.
  - **§2.1.** Scoped the layering invariants to **product** projects (test/harness legitimately reference `Host`/`Core`).
  - **§2.2 / §2.3.** Acknowledged the Avalonia 11.3.6 / AvaloniaEdit 11.4.1 minor-line split (runtime-compatible per SPIKE-1) + the CPM transitive-pin strategy; acknowledged `CommunityToolkit.Mvvm` (App-only); added `Directory.Build.targets` with the R2R-on-publish `<Error>` guard; added the banned-symbols analyzer + architecture-test fixture for mechanical C-9 enforcement.
  - **§3.3.** Added the `ITextSourceVersion` snapshot-version guard (drop+reschedule the `UpdateFoldings()` commit on stale document version); pinned the debounce-timer kind; stated the FR-9 scope selector falls back to indentation folding pre-tokenize.
  - **§3.7.** Carried SPIKE-FR37's `RequestedThemeVariant` one-frame chrome-desync residual; scoped pixel-identity to the three captured surfaces.
  - **§3 / §8.1.** Stated all enforcement timers are `TimeProvider`-driven (only the named-pipe stop-instant uses `Stopwatch`).
  - **§3.5 / §5.1.** Added the "unknown overflow-bag keys survive a *mutation*" round-trip assertion.
  - **§4.2 / §4.6.** Named the host↔hook decision transport for the synchronously-blocking `approve-each` hook; specified FR-39 user-initiated-replay accounting (normal Archetype-B per-op confirm, not the per-turn injection caps).
  - **§6.** Surfaced FR-16 in the FR column.
  - **OQ-ARCH-2.** Added the Phase-8 hazard note (the `Host` token in `HoardPad.Core.Host.Abstractions` is not a project-placement instruction).
  - **§11 / Awaiting review.** Flipped to record the completed review round (7 reviewers + 6 folded TD lines, zero blockers; SQLite rejection independently concurred).
  - Tech-design annotations (`docs/tech-design.md`): the six §8 `REVIEW_REQUEST:` lines marked RESOLVED; a §2 note pointing here for the seam-abstraction placement refinement.

## How to read this document

- **§1 — Solution / project structure.** The concrete .NET solution and its projects, realizing the C-9 domain/host split. The single most load-bearing section.
- **§2 — Dependency graph + layering + package manifest.** `App → Host → Core`, the pinned packages, the solution-level scaffolding files.
- **§3 — Threading model.** Which work runs on the UI thread vs off-thread, and how results marshal back.
- **§4 — AI-provider architecture.** The two-archetype model, hook deployment, the conversation-keyed cap-state store, the append paths.
- **§5 — Data / persistence architecture.** The `.hoard` store, the machine-local session/recovery store, the append-log activity/undo journals, the on-disk layout.
- **§6 — Custom-control architecture (the M18 surface).** Where each custom control lives and how it composes.
- **§7 — Build / publish topology.** Per-RID self-contained publish, the mandatory R2R setting, trim, hook bundling.
- **§8 — Cross-cutting.** DI/composition, settings, logging (no telemetry), the error model, the no-network constraint.
- **§9 — Traceability.** Each architecture section mapped back to the tech-design / PRD / spike it realizes.
- **§10 — Open architecture questions.** Each with a proposed default.

Terms are the PRD's §1 Definitions and the tech-design's §2 project names (`HoardPad.Core` ≡ the tech-design's `HoardPad.Domain` — see §1.1 naming note); this doc does not redefine them.

---

## 1. Solution / project structure

### 1.1 The realized layout

The C-9 contract (PRD §8) and TD §2 mandate a no-platform-dependency domain library, every OS syscall behind a host-implemented seam, and an Avalonia head that composes them. This solution realizes that boundary as five product projects plus seven test/harness projects.

```
HoardPad.sln
├── src/
│   ├── HoardPad.Core/            net8.0   — domain; NO platform dependency
│   ├── HoardPad.Host/            net8.0   — C-9 seam implementations; ALL OS-conditional code
│   ├── HoardPad.App/             net8.0   — Avalonia head: UI, MVVM, composition root
│   ├── HoardPad.Core.Ai/         net8.0   — AI archetype logic (separate assembly — see §1.5)
│   └── HoardPad.Core.Checklist/  net8.0   — `.hoard` model + serialization (separate assembly — see §1.5)
├── tests/
│   ├── HoardPad.Core.Tests/            — domain unit tests with fakes + fake TimeProvider; the arch-test fixture (§2.3)
│   ├── HoardPad.Core.Checklist.Tests/  — serialization round-trip (TD §3.5), display-mode mapping property + edge-case tests
│   ├── HoardPad.Core.Ai.Tests/         — cap-accounting, SandboxPolicy, classifier predicate, MA14 liveness-match tests
│   ├── HoardPad.Host.Tests/            — seam impls against a temp FS (atomic-write, append-log, repair, resync); Win power-cut authored-Deferred
│   └── HoardPad.App.Tests/             — Skia-headless: SC-3 token read-back (20 langs) + SC-4 toggle + FR-16 fidelity fixture
├── harness/
│   ├── HoardPad.Bench/                 — external SC-1/SC-2/NFR-5 launcher + named-pipe echo channel (SPIKE-1); process-launches published App
│   └── HoardPad.AcceptanceHarness/     — SC-5 Archetype-A hook gate + Archetype-B shared-service gate + A-6 fixtures (refs Core.Ai+Host)
├── hooks/
│   └── deny_by_default.py              — the production PreToolUse hook (bundled; §4.3)
├── Directory.Build.props
├── Directory.Build.targets             — the R2R-on-publish guard (§2.3, §7.2)
├── Directory.Packages.props
└── global.json
```

**Naming note (binding).** TD §2 calls the domain project `HoardPad.Domain`. This architecture renames it **`HoardPad.Core`** to match the Phase-5 plan and the CLAUDE.md §2 "shared logic = plain .NET class libraries" wording; the two names refer to the same project and the same C-9 contract. Wherever TD §2 writes `HoardPad.Domain.*` (e.g. `HoardPad.Domain.Safety.SandboxPolicy`), read `HoardPad.Core.*`. This is the only rename; every seam interface, namespace suffix, and signature in TD §2 is otherwise carried verbatim. The Phase-8 build uses `HoardPad.Core`.

### 1.2 `HoardPad.Core` — the domain (NO platform dependency)

`HoardPad.Core` is `net8.0`, references **no** Avalonia, **no** `HoardPad.Host`, and contains **no** OS syscall, **no** `System.Runtime.InteropServices` P/Invoke, **no** RID-conditional code, and **no** `DateTime.UtcNow` (realizes C-9 / TD §2). It depends only on the BCL and `System.Text.Json`. It owns:

| Concern | Realizes |
|---|---|
| The pure `SandboxPolicy.Evaluate(NormcasedPath root, NormcasedPath candidate) → Allow\|Deny` | TD §2.2, FR-26, C-9 |
| `NormcasedPath` value type (constructed only by the host resolver) | TD §2.2 |
| The cap accounting — the FR-11/FR-38/FR-40 counters + the FR-40 distinct-files set + the no-re-arm tripped state, **conversation-keyed** | TD §4.5, FR-11/FR-38/FR-40, C-4 |
| The FR-38 counted-op classifier predicate over `(tool_name, target_pre_op_bytes, proposed_bytes)` + the create-vs-overwrite routing | TD §4.5, FR-38, FR-11 |
| The session/recovery 3-dimensional reconciliation table (every cell pure, fake-clock-testable) | PRD §9.2, FR-19 |
| The undo-journal reverse-decision logic (injected current hash/mtime) | PRD §9.4, FR-28 |
| The file-ops / undo-journal / activity-log **record models** (the data shapes, not the I/O) | PRD §9.3, §9.4 |
| The seam **abstractions** (`IAtomicFileStore`, `IPathResolver`, `IAgentBackend`, `IToolPermissionHookHost`, `IAppDataPaths`, `IEncodingDetector`) | TD §2 (see §1.3 placement note); `IEncodingDetector` keeps `UTF.Unknown` out of `Core` (FR-6, OQ-ARCH-4) |
| The pure encoding **detect-then-preserve decision** (over the `IEncodingDetector` candidate+confidence) — NOT the byte-sniff itself | FR-6, OQ-ARCH-4 |
| The injected `System.TimeProvider` consumed for every timestamp + all enforcement timers (no-reply, SpinLock, debounce/coalesce) drive off it | TD §2.5, C-9 |

**The `UTF.Unknown` charset package is NOT a `Core` dependency (binding — resolves the §2.1-invariant contradiction).** FR-6's encoding *detection* (the byte-sniff/BOM heuristic that needs the `UTF.Unknown` charset package) is an I/O-edge concern, **not** domain logic. It lives behind an `IEncodingDetector` seam (interface in `HoardPad.Core.Host.Abstractions`, concrete impl referencing `UTF.Unknown` in `HoardPad.Host` or `HoardPad.App` — §6 / OQ-ARCH-4). Only the pure **detect-then-preserve *decision*** (given a candidate encoding + confidence, decide the buffer's effective encoding and the on-save preserve rule) stays in `Core` — a pure function over the seam's returned classification, unit-testable with a fixture corpus, no package. This keeps the §2.1 invariant 1 ("`HoardPad.Core` references nothing but the BCL + `System.Text.Json`") **absolute**: the `UTF.Unknown` NuGet never enters `Core`. (The v1-draft proposal that put the detector package in `Core` is superseded — see OQ-ARCH-4.)

**Seam-abstraction placement (binding).** TD §2 declares the seam interfaces in a `HoardPad.Host.Abstractions` namespace and the pure policy in `HoardPad.Domain.Safety`. To keep the dependency arrow `App → Host → Core` (and never `Core → Host`), the **seam interfaces live in `HoardPad.Core`** (e.g. `HoardPad.Core.Host.Abstractions`), so the domain depends only on the abstractions it calls and the host depends on the domain to implement them. The TD §2 namespace `HoardPad.Host.Abstractions` is therefore realized as `HoardPad.Core.Host.Abstractions` (an `Abstractions` namespace physically inside `HoardPad.Core`); the concrete classes are in `HoardPad.Host`. This is the standard ports-and-adapters arrangement and is the only safe way to satisfy "Host implements Core's interfaces" without a dependency cycle. (`SandboxPolicy` stays in `HoardPad.Core` as TD §2.2 requires.) The namespace's `Host` token is a naming convention only — **it does not place the abstractions in the `HoardPad.Host` assembly** (taking it literally is the single most likely way a build agent reintroduces the forbidden `Core → Host` cycle — OQ-ARCH-2).

### 1.3 `HoardPad.Host` — the C-9 seam implementations (ALL OS-conditional code)

`HoardPad.Host` is `net8.0`, references `HoardPad.Core` (to implement its abstractions), and is the **only** project that contains OS syscalls, P/Invoke, `OperatingSystem.IsWindows()` branches, and `[SupportedOSPlatform("windows")]` attributes. One source compiles for both RIDs (the SPIKE-4 / SPIKE-5 pattern — `dotnet build -r win-x64` and `-r linux-x64` both clean from a Linux box). It implements:

| Seam impl | Realizes |
|---|---|
| `AtomicFileStore : IAtomicFileStore` — Linux `temp→fsync→rename→dir-fsync`; Windows `MoveFileEx(MOVEFILE_WRITE_THROUGH)` create-when-absent / P/Invoke `ReplaceFile(REPLACEFILE_WRITE_THROUGH)` replace-when-present; the `AppendRecordAsync` per-record-`fsync` path; `OpenAndRepairAsync` truncate-repair; `ProbeAtomicCapabilityAsync` for the `EXDEV`/synced-target fallback | TD §2.1, NFR-4, PRD §9.3 |
| `PathResolver : IPathResolver` — `realpath`/symlink resolution + `normcase`; on Windows `GetLongPathName` 8.3-short-name expansion (TD-1); returns a `NormcasedPath` the domain consumes | TD §2.2, FR-26, TD-1 |
| `AgentBackend : IAgentBackend` — `claude -p` launch (the FR-24 shim resolver order; Windows `CREATE_SUSPENDED → AssignProcessToJobObject → ResumeThread`; Linux `setsid`); `stream-json` parse; `IAgentTurn.KillTreeAsync` (Windows `TerminateJobObject` primary + `taskkill /T /F` fallback; Linux `kill(-pgid, SIGKILL)`) | TD §2.3, FR-24, NFR-8, TD-5 |
| `ToolPermissionHookHost : IToolPermissionHookHost` — the PreToolUse hook lifecycle: `--settings` JSON build, Python-floor probe (TD-1), the FR-24 §3 fail-closed self-test | TD §2.4, FR-24, FR-26 |
| `AppDataPaths : IAppDataPaths` — per-user data dir + `RestrictToCurrentUser` (chmod 600 / Windows ACL); the machine-local non-synced recovery subdir | TD §2.5, NFR-7, PRD §9.2 |
| `EncodingDetector : IEncodingDetector` — the FR-6 byte-sniff/BOM heuristic (references `UTF.Unknown`); returns a candidate encoding + confidence the `Core` decision consumes | FR-6, OQ-ARCH-4 (may instead live in `App` — both keep `UTF.Unknown` out of `Core`) |
| `SystemTimeProvider : TimeProvider` — the concrete wall clock (the domain never reads it directly) | TD §2.5, C-9 |

The hook **process** itself (the Python script) is a deployed artifact, not C# — it calls back into `HoardPad.Core`'s pure `SandboxPolicy.Evaluate` and cap-accounting logic via the host-owned cap-state file (§4.4). `HoardPad.Host` owns its lifecycle, the `--settings` wiring, and the self-test; it never embeds the decision logic.

### 1.4 `HoardPad.App` — the Avalonia head (UI + MVVM + composition root)

`HoardPad.App` is the `net8.0` Avalonia executable. It references `HoardPad.Core`, `HoardPad.Host`, `HoardPad.Core.Ai`, and `HoardPad.Core.Checklist`, and is the **only** project that references Avalonia / AvaloniaEdit. It owns:

- The MVVM UI: the editor surface (AvaloniaEdit + TextMate), the Candidate-A Markdown preview render tree (§6), the `.hoard` checklist control (§6), the AI-panel in-window overlay (FR-22 — an Avalonia `Panel`/`AdornerLayer` over the editor, never a separate OS window), the settings UI, the activity-log + FR-39 pending-proposals surface.
- The FR-37 lockstep theme switch (`ThemeService`, §3.7, §6.6) — the one synchronous UI-thread `Switch(palette)` call.
- The **composition root** — the DI container wiring `HoardPad.Host`'s concrete seams into the domain services and the view-models (§8.1). This is the single place the platform implementations meet the domain.

### 1.5 The AI-provider and checklist split — separate assemblies (justified)

The Phase-5 brief asks an explicit decision: are the AI provider and the checklist **separate assemblies** or **namespaces within `HoardPad.Core`**? CLAUDE.md §2 mandates "keep editor/AI/checklist domains separable."

**Decision: separate assemblies — `HoardPad.Core.Ai` and `HoardPad.Core.Checklist` — both `net8.0`, both platform-free (the same C-9 contract as `HoardPad.Core`).**

Rationale (stated plainly so the Phase-8 build does not collapse them into one project):

1. **"Separable" is enforced by the compiler, not by convention.** Namespaces inside one assembly do not stop the checklist code from `using` the AI cap-accounting types, or the AI code from reaching into checklist internals — the separability rule erodes silently as the codebase grows. Separate assemblies make a cross-domain reference a deliberate project reference a reviewer sees, which is exactly what CLAUDE.md §2 is asking for.
2. **The dependency graph is acyclic and shallow.** `HoardPad.Core.Checklist → HoardPad.Core` (it needs the seam abstractions + `IAtomicFileStore` + the serialization contract). `HoardPad.Core.Ai → HoardPad.Core` (it needs `SandboxPolicy`, the cap models, the record models, the seam abstractions). The two domain-extension assemblies **do not reference each other** — the checklist never needs the AI loop, and the AI never needs the `.hoard` model. This independence is the structural proof that the domains are separable; collapsing them into one assembly would forfeit the proof.
3. **Testability is sharper.** `HoardPad.Core.Ai.Tests` and `HoardPad.Core.Checklist.Tests` are independent unit suites — the AI cap/classifier/sandbox tests do not drag the serialization corpus, and the serialization round-trip + display-mode property tests do not drag the AI fakes. This keeps the TD §3.5 round-trip corpus and the SPIKE-3 cap fixtures in separable, fast suites.
4. **`HoardPad.Core` stays the genuine shared kernel.** What remains in `HoardPad.Core` is what *both* extensions and the host share: the seam abstractions, `SandboxPolicy`, `NormcasedPath`, the record models, the session/recovery reconciliation, the undo decision logic, and the injected `TimeProvider`. `SandboxPolicy` and the activity-log/undo-journal record models live in `HoardPad.Core` (not `Core.Ai`) because session-restore (PRD §9.2) and the shared file-ops service (§4.5) both touch them and neither is AI-specific.

Cost (acknowledged): three domain assemblies instead of one is marginally more project plumbing (one extra `.csproj` each, two extra test projects). For a build the Phase-8 `/code` run scaffolds once, the compiler-enforced separability is worth the plumbing. If a future maintainer finds the split is pure friction with no separability benefit, OQ-ARCH-1 (§10) is the documented collapse path — but v1 ships the split.

**The two-archetype provider logic** (Archetype A `claude -p` host-orchestration logic + Archetype B driven-completion loop + the shared file-ops service) lives in `HoardPad.Core.Ai`. The OS-syscall legs it needs (`claude -p` launch, process-tree kill, the hook transport, atomic writes) are the `HoardPad.Host` seams it calls through the abstractions — so even the AI assembly stays platform-free.

### 1.6 Test + harness projects

| Project | Maps to | Realizes |
|---|---|---|
| `HoardPad.Core.Tests` | `HoardPad.Core` | SandboxPolicy fakes, reconciliation-table cells, undo decision logic — all with a fake `TimeProvider` (C-9 testability story) |
| `HoardPad.Core.Checklist.Tests` | `HoardPad.Core.Checklist` | TD §3.5 canonical round-trip + first-re-save canonicalization (incl. ODQ-11 nesting + completion-lock + collapsed fixtures); the §9.1 hierarchy-aware display-mode mapping property test (FR-33, all three modes, sweeping nested sibling groups incl. a checked-parent-block move/hide); the `effectiveEnabled` ancestor-chain gating + depth-cap (FR-41) |
| `HoardPad.Core.Ai.Tests` | `HoardPad.Core.Ai` | cap accumulation + no-re-arm + conversation-keyed lifetime, the FR-38 classifier predicate, the create-vs-overwrite split, FR-40 misroute-invariance, and the **MA14 liveness-match** test (correlate a synthetic `stream-json` tool-use list against a synthetic hook-callback list, assert the unmatched-event degrade fires — pure, no harness, §4.2 step 6) |
| `HoardPad.Core.Checklist.Tests` (also) | `HoardPad.Core.Checklist` | the §9.1 named edge cases as **example-based** tests alongside any property sweep (reverse-ordered-neighbors / no-upper / no-lower / partition-boundary, PRD §9.1) — a pure sweep can pass while never hitting the anchor rule |
| `HoardPad.App.Tests` (Skia-headless) | SC-3 / SC-4 / FR-16 | the App-layer gates with no other test home: **SC-3** TextMate token read-back across all 20 languages (drives the real `TextMateSharp` tokenizer headless, asserts coarse token classes at marked regions, A-6 prerequisite); **SC-4** Markdown-toggle byte-identical-buffer round-trip; the **FR-16 renderer-fidelity fixture** (the per-feature assertions against the rendered Candidate-A control tree under Skia-headless) |
| `HoardPad.Host.Tests` | `HoardPad.Host` | `IAtomicFileStore` against a temp FS (atomic write, append-record, truncate-repair); the **interior-bad-record `resync-and-continue` fixture** (forge a corrupt interior record — linux-constructible, TD §3.3 B1–B3); the EXDEV/synced fallback probe. The **Windows-first-create power-cut fixture (MoveFileEx absent-or-complete)** is authored here but **Deferred to Windows hardware — NOT asserted on Linux** (§7.6); a Phase-8 Linux run must not attempt it and mis-mark it. |
| `HoardPad.Bench` (harness, not a test) | SC-1/SC-2/NFR-5 | the external cold/warm launcher + the named-pipe echo channel (SPIKE-1 `bench/`); the start instant owner; reads HoardPad's one `TextChanged` stop timestamp; also owns the **single-instance idle-RSS measurement (NFR-5)** since it already owns the cold/warm process. **Measures a *published* App via process-launch — no `ProjectReference` to App** (preserves the SPIKE-1 external-observer design). |
| `HoardPad.AcceptanceHarness` | SC-5 | the Archetype-A deterministic hook gate (drives the production hook with crafted `tool_input` + applies allowed ops via the shared service), the Archetype-B shared-service gate, the cross-continuation no-re-arm step, the A-6 fixture set, the durability crash fixtures. **Project-references `Core.Ai` and `Host`** (it drives the production hook + the shared file-ops service). |

The `HoardPad.Bench`, `HoardPad.AcceptanceHarness`, and `HoardPad.App.Tests` are **named build artifacts** (PRD SC-1/SC-3/SC-4/SC-5 require the launcher and the gates to be runnable), not throwaway spike code — they are the production analogues of the SPIKE-1 `bench/`, the SPIKE-2 fidelity harness, and the SPIKE-3/SPIKE-5 harnesses.

---

## 2. Dependency graph + layering + package manifest

### 2.1 The layering rule (binding)

```
                         ┌─────────────────────────────┐
                         │        HoardPad.App         │  Avalonia head; composition root
                         │  (the ONLY Avalonia ref)    │  references Core, Host, Core.Ai, Core.Checklist
                         └───────────────┬─────────────┘
                                         │ DI: wires Host impls into Core abstractions
              ┌──────────────────────────┼──────────────────────────┐
              ▼                          ▼                          ▼
   ┌──────────────────┐      ┌────────────────────┐      ┌──────────────────────┐
   │  HoardPad.Host   │      │  HoardPad.Core.Ai  │      │ HoardPad.Core.Checklist│
   │  (OS syscalls,   │      │ (two-archetype     │      │ (.hoard model +        │
   │   P/Invoke, RID  │      │  provider logic;   │      │  serialization;        │
   │   conditionals)  │      │  platform-free)    │      │  platform-free)        │
   └────────┬─────────┘      └─────────┬──────────┘      └──────────┬───────────┘
            │                          │                            │
            └──────────────┬──────────┴────────────┬───────────────┘
                           ▼                        ▼
                    ┌──────────────────────────────────────┐
                    │            HoardPad.Core              │
                    │  domain kernel: SandboxPolicy,        │
                    │  seam ABSTRACTIONS, record models,    │
                    │  session/recovery reconciliation,     │
                    │  undo decision logic, injected        │
                    │  TimeProvider. NO platform dependency.│
                    └──────────────────────────────────────┘
```

**Invariants the architecture review (`senior-csharp-multi-platform`) gates. These scope to the *product* projects (`src/`); test and harness projects legitimately reference `Host`/`Core`/`Core.Ai` seams and are expected additional consumers that sit outside the shipped graph (they do not violate the layering).**

1. **`HoardPad.Core` references nothing but the BCL + `System.Text.Json`.** No Avalonia, no `HoardPad.Host`, no `System.Runtime.InteropServices` P/Invoke, no `OperatingSystem.IsWindows()`, no `DateTime.UtcNow`, no `UTF.Unknown` (FR-6 detection is behind the `IEncodingDetector` seam, §1.2). Enforced mechanically (§2.3): the Windows-platform analyzer, a banned-symbols analyzer (`DateTime.UtcNow`/`DateTime.Now`/`DllImportAttribute`/`System.IO.File`/`System.IO.Directory` banned in `Core`/`Core.Ai`/`Core.Checklist`), an architecture-test fixture asserting no `Core → Host`/Avalonia reference, and the unit suite running on both RIDs.
2. **The arrow never reverses (product projects).** `Core.Ai` and `Core.Checklist` depend on `Core`; `Host` depends on `Core`; `App` depends on all four. Among **product** projects, nothing depends on `App`, and nothing depends on `Host` except `App`. There is **no `Core → Host`** edge — that is why the seam interfaces live in `Core` (§1.2). (Test/harness projects may reference `Host`/`Core.Ai` — see §1.6: `Host.Tests → Host`, `AcceptanceHarness → Core.Ai`+`Host`; `Bench` process-launches a *published* App with no `ProjectReference`.)
3. **`Core.Ai` and `Core.Checklist` do not reference each other** (§1.5 rationale 2).
4. **Only `HoardPad.App` (among product projects) references Avalonia / AvaloniaEdit.** The render tree, the checklist control, and the theme switch are App concerns; the domain models they bind to are in `Core` / `Core.Checklist`. (`HoardPad.App.Tests` also references Avalonia for the Skia-headless SC-3/SC-4/FR-16 gates — a test consumer, not a product violation.)

### 2.2 Pinned package manifest (binding — realizes the SPIKE-measured stack)

Every version is the spike-measured pin (SPIKE-1 §Environment, SPIKE-2, SPIKE-5). The Phase-8 build uses **central package management** (`Directory.Packages.props`), so versions are declared once.

| Package | Version | Used by | Source |
|---|---|---|---|
| `Avalonia` | **11.3.6** | App | SPIKE-1, SPIKE-FR37 |
| `Avalonia.Desktop` | 11.3.6 | App (desktop lifetime) | — |
| `Avalonia.Themes.Fluent` | 11.3.6 | App (chrome `ThemeVariant`) | SPIKE-FR37 |
| `Avalonia.AvaloniaEdit` | **11.4.1** | App | SPIKE-1, SPIKE-FR9 |
| `AvaloniaEdit.TextMate` | **11.4.1** | App | SPIKE-1 |
| `TextMateSharp.Grammars` | **2.0.4** | App | SPIKE-1 |
| `Markdig` | **0.45.0** (`.UseAdvancedExtensions()`) | App (parse) | SPIKE-1, SPIKE-2 |

**Avalonia minor-line split (acknowledged, runtime-compatible).** Avalonia is pinned at **11.3.6** but AvaloniaEdit 11.4.1 / AvaloniaEdit.TextMate 11.4.1 depend transitively on Avalonia **11.4.x** packages. **SPIKE-1 proved this combination builds and runs** (it is the measured stack — four linux-x64 publish variants launched and echoed correctly), so the 11.3↔11.4 line is empirically runtime-compatible for the surface AvaloniaEdit uses. This is stated explicitly so it is not a silent first-build surprise; the CPM transitive-pin handling is in §2.3.

**Binding manifest constraints:**

- **Zero extra Markdig extension packages.** `Markdig 0.45.0`'s core `.UseAdvancedExtensions()` covers the full FR-16 GFM set (tables, task-lists, footnotes, autolinks, strikethrough). No `Markdig.Extensions.*` package is added (TD §1.6, SPIKE-2 §E). A `/code` build that pulls a separate Markdig extension package is wrong.
- **No WebView / CEF / Chromium / HTML-control package at any layer.** The Candidate-A renderer is pure managed Avalonia (TD §1.6, FR-15, CLAUDE.md §2). The manifest contains no browser-runtime dependency.
- **An encoding-detection package (FR-6) — anticipated, and it lives in `Host`/`App`, NEVER `Core`.** FR-6's encoding detection needs a charset detector (TD §5.5 names `UTF.Unknown` as the candidate). It is `net8.0`, offline, and platform-free, but it is referenced **only by the `IEncodingDetector` concrete impl in `HoardPad.Host` (or `HoardPad.App`)** — never by `HoardPad.Core` (the §2.1 invariant 1 stays absolute; §1.2, OQ-ARCH-4). Bundled offline (A-7 / NFR-7).
- **`CommunityToolkit.Mvvm` — the view-model package, App-only.** The view-model layer (source-generated `ObservableObject`/`[ObservableProperty]`/`[RelayCommand]`, with `IAsyncRelayCommand` + `CanExecute` for the cancellable AI turn) uses `CommunityToolkit.Mvvm`. It is `net8.0`, platform-free, and referenced **only by `HoardPad.App`** (view-models are App-layer). Acknowledged here, the same courtesy the FR-6 package gets, so it is not surprise scope at Phase 8.

### 2.3 Solution-level scaffolding (binding)

The Phase-8 build scaffolds these four files at the solution root before any `.csproj`:

- **`global.json`** — pins the SDK to the .NET 8.0 floor: `{ "sdk": { "version": "8.0.0", "rollForward": "latestMinor" } }`. (`rollForward: latestMinor` lets the build use the 8.0.128 measured in the spikes or any later 8.0.x, but never silently jumps to .NET 10.) This guarantees the spike-measured runtime is the build runtime.
- **`Directory.Build.props`** — solution-wide MSBuild defaults: `<TargetFramework>net8.0</TargetFramework>`, `<Nullable>enable</Nullable>`, `<LangVersion>latest</LangVersion>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<ImplicitUsings>enable</ImplicitUsings>`, and the C-9-boundary enforcement (next bullet). Functional-leaning, nullable-enabled style per the project's coding standards.
  - **Mechanical C-9 boundary enforcement (not reviewer-vigilance).** The Windows-platform analyzer (flags Windows API use outside `[SupportedOSPlatform]`) alone does **not** catch an accidental `Core → Host` `ProjectReference`, a non-OS-gated `[DllImport]`, or a stray `DateTime.UtcNow`. The build therefore adds, scoped to `Core`/`Core.Ai`/`Core.Checklist`: (1) **`Microsoft.CodeAnalysis.BannedApiAnalyzers`** with `DateTime.UtcNow`, `DateTime.Now`, `System.Runtime.InteropServices.DllImportAttribute`, `System.IO.File`, `System.IO.Directory` banned (a banned symbol is a build error under warnings-as-errors); and (2) an **architecture-test fixture** in `Core.Tests` (e.g. NetArchTest) asserting `Core` references no `Host`/Avalonia assembly. The dual-RID unit run (§2.1 invariant 1) is the runtime backstop. The invariant is then enforced at build time, not by review.
- **`Directory.Packages.props`** — central package management (CPM): `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` plus one `<PackageVersion>` per row in the §2.2 manifest. Project `.csproj` files use `<PackageReference Include="..." />` with no version attribute. One pin, one place — so a version drift is a one-line diff a reviewer catches.
  - **CPM transitive-pin footgun (pre-empted).** Under CPM + `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, a transitive dependency resolving above any central `<PackageVersion>` becomes a **build-breaking error** (NU1605/NU1608-class). The Avalonia 11.3.6 / AvaloniaEdit 11.4.1 minor-line split (§2.2) surfaces exactly this — AvaloniaEdit pulls Avalonia 11.4.x transitively. **Strategy (binding):** the Phase-8 build adds explicit `<PackageVersion>` entries for the transitively-pulled Avalonia sub-packages (`Avalonia.Base`, `Avalonia.Controls`, `Avalonia.Skia`, etc.) at their resolved 11.4.x version (or sets `CentralPackageTransitivePinningEnabled` deliberately), so the first build does not fail on a confusing transitive-version error.
- **`Directory.Build.targets`** — solution-wide MSBuild *targets* (runs after project evaluation). Its primary job is the **R2R-on-publish guard**: an MSBuild `<Error Condition="'$(PublishReadyToRun)' != 'true'">` on a `Publish` of a shipping RID, so the guard travels with the repo and fires on **any** `dotnet publish` (§7.2). **This is the only guard form that can fire in a CI-less v1** (PRD §12 / §7.5 — no CI pipeline) — a free-floating CI-YAML check would not exist. This file is the natural home for any future publish-time validation.

---

## 3. Threading model

The design rule: **the Avalonia UI thread (the dispatcher) owns all Avalonia primitives; every potentially-slow leg runs off-thread and marshals its result back via `Dispatcher.UIThread.Post`/`InvokeAsync`.** SPIKE-1's "layout dominates" finding is the constraint that drives every off-thread decision — the UI thread's scarce resource is the layout/render pass, so nothing that can block it (a 200k-file walk, a 33 ms folding recompute at 20k lines, a `claude -p` process, a per-record `fsync` chain) may run on it.

### 3.1 The UI thread (Avalonia dispatcher)

Runs: all Avalonia control construction and mutation, the AvaloniaEdit editor, the Candidate-A preview render-tree build + layout, the checklist `ItemsControl`, the AI-panel overlay, the confirm/reject dialogs, and the **one synchronous lockstep theme switch** (§3.7). The render loop is dispatcher-driven, so anything that yields the UI thread between two related mutations risks a partial frame — the theme switch's no-yield guarantee (§3.7) exists for exactly this reason.

### 3.2 The FR-10(a) writable-set walk — off-UI, abort-at-threshold+1

The large-root downgrade predicate (FR-10(a), MA9) counts writable regular-file descendants of the open-folder root, resolving symlinks to canonical targets (counting each once, with a cycle guard), not crossing mount boundaries, excluding special files, and **aborting at threshold+1** (only "over threshold?" matters). It runs **off the UI thread** (SPIKE-1 carry-forward; FR-10(a) "run the walk off the UI thread so folder-open never blocks") so it never costs the SC-1/SC-2 budget even on a root with a 200k-file `node_modules`. It is **re-evaluated at op-time (or on a debounced file-watch of the root)**, not cached from folder-open (FR-10(a), R3-MA1/MA10) — the re-count is also off-UI. The boolean result marshals back to the UI thread, where it arms or clears the downgrade. The walk itself is a `HoardPad.Host` concern (it touches the FS); the threshold decision is pure domain.

### 3.3 The FR-9 folding recompute — debounced + background, UI-thread commit

Realizes the SPIKE-FR9 binding perf shape exactly:

- The fold strategy reads a `TextDocument.GetText()` snapshot and builds a fresh `List<NewFolding>` on a **background thread** (it touches no UI-thread Avalonia primitive — proven thread-safe in SPIKE-FR9). It is **debounced 300 ms** after the last change (OQ-ARCH default; OQ-TD-3 pins the exact value against the Phase-8 `UpdateFoldings()` benchmark) and **cancelled on document replace**.
- `FoldingManager.UpdateFoldings()` is called **on the UI thread** (O(folds), typically sub-ms; the ~6,843-fold BST cost at 20k lines is the one unbenchmarked residual, TD §5.3).
- **Snapshot-version guard against the in-place-edit race (binding — the single most important threading-correctness rule).** The "cancelled on document replace" rule covers tab switch, but **not** an in-place edit that arrives *between* the background snapshot and the UI-thread commit — applying offsets computed against a stale `TextDocument` version would corrupt fold spans on fast typing (folds land mid-token or past the new end). The recompute therefore **captures the snapshot's `ITextSourceVersion`; on the UI-thread `UpdateFoldings()` commit, if the live `document.Version` has advanced, the result is dropped and the recompute rescheduled** rather than applied stale. (AvaloniaEdit's `UpdateFoldings(newFoldings, firstErrorOffset)` also accepts a first-error offset for this staleness class, but the version-check is the clean primitive.) A Phase-8 build that omits this ships a fold-offset corruption on typing.
- Why background, not synchronous-on-keystroke: at 20k lines the average recompute (12.36 ms) fits a 16 ms frame but the worst case (33.37 ms) does not (SPIKE-FR9), so it must stay off the critical render path SPIKE-1 identified as the bottleneck.

The `FoldingService` (in `HoardPad.App`, since it touches AvaloniaEdit) owns the debounce timer, the background recompute, the cancellation, and the UI-thread commit. **Debounce-timer kind (pinned):** the debounce uses an Avalonia `DispatcherTimer` (UI-thread tick) that kicks off the recompute via `Task.Run`, so no timer-thread callback ever touches `FoldingManager` or any AvaloniaEdit primitive off the UI thread; the timer's interval is `TimeProvider`-driven (§3, §8.1) so the debounce is deterministic in tests. **Scope-selector fallback:** the FR-9 `FoldingStrategySelector` chooses indent-vs-brace by TextMate scope, but FR-7 registers grammars **lazily on first use** (§6) — if a recompute runs before the grammar's first async tokenize pass completes, the scope query returns null/default, and the selector **falls back to indentation folding** (never mis-folds or throws). The lazy-grammar + scope-aware-folding interaction is thereby tolerated, not discovered as a bug (SPIKE-FR9 residual #2).

### 3.4 The `claude -p` process + `stream-json` parse — off-UI, cancellable

The AI subprocess runs off the UI thread with cancellation (NFR-8 — a hung backend must not freeze the editor). `IAgentBackend.StartTurnAsync` launches the process; `IAgentTurn.StreamAsync` yields parsed `stream-json` events on a background pump. Partial-message events marshal to the UI thread to render into the AI panel (FR-22). Cancellation = `IAgentTurn.KillTreeAsync` (the process-tree kill, §4). The cap-accounting and the hook callbacks run in the hook process (a separate OS process the CLI launches), not on HoardPad's UI thread. HoardPad's no-reply timer (FR-24/FR-39 default ~60 s) runs off-UI and triggers the kill — it is the **sole fail-closed enforcer** (TD §4.4), so it must fire before any hook timeout could elapse. The no-reply timer is **`TimeProvider`-driven** (§3, §8.1), so a test injects a `FakeTimeProvider` to drive the fail-closed deadline deterministically without a real ~60 s wait.

### 3.5 The named-pipe echo channel — the SC-1/SC-2 stop instant

HoardPad writes a 16-byte monotonic timestamp (`Stopwatch.GetTimestamp()` + `Stopwatch.Frequency`, little-endian) to a named pipe **the instant its editor's `TextChanged` fires for the synthetic startup keystroke** (SPIKE-1 §Environment, MA15). That write is the SC-1/SC-2 **stop** instant. The external `HoardPad.Bench` launcher owns the **start** instant and reads the timestamp. Exactly one in-process emission (MA15); first-paint / window-shown are explicitly not the stop instant. The write happens on the UI thread (it is on the `TextChanged` event path) but is a single 16-byte pipe write — negligible.

### 3.6 The persistence threads

- **The serialized per-document `.hoard` write queue (FR-31).** Each open `.hoard` document has a **single serialized async write queue** with last-write-wins coalescing (a burst of mutations collapses to one write of the final state, never out-of-order renames or partial states). **Terminal mutations** (check/uncheck, reorder, delete, mode change) are **zero-debounce** — each persists immediately via the full atomic temp+rename+`fsync` sequence before the next mutation can begin (TD §1.5 / SPIKE-1 §E confirmed the per-mutation `fsync` p90 is 1.43 ms, ~1% of the coalesce window, so zero-debounce is affordable). **Item-text keystrokes** are coalesced ≤ 150 ms (the coalesce window is `TimeProvider`-driven, §3, §8.1). The queue runs off the UI thread; the mutation is enqueued from the UI thread and the write completes in the background. Each write uses a unique temp filename (`.hoard.<ulid>.tmp`) so concurrent/retried writes never collide.
- **The per-record-`fsync` append path (activity log + undo journal).** `IAtomicFileStore.AppendRecordAsync` writes `[4B len][N B payload][4B CRC-32]`, `fsync`s, and returns the new end offset **before** the next append begins (TD §3.3, §5). This runs off the UI thread. A 25-op AI turn costs ~14–49 ms on local ext4 SSD (TD §3.3); on a slow/network/synced target it can reach seconds — a named caveat (TD §3.3), which is why it is off-UI.
- **The undo-journal before-image durability ordering (M15).** The before-image blob (`before/…bin`, whole-file create+`fsync` via the create-when-absent path) and its journal entry are written and `fsync`'d to disk **before** the destructive op is allowed to mutate the target (FR-28, PRD §9.4). The destructive op is gated on the before-image's durable completion — a background sequence the file-ops service awaits before applying the op.

### 3.7 The lockstep theme switch — single synchronous UI-thread call

`ThemeService.Switch(palette)` performs all four mutations — TextMate `SetTheme`, editor background, preview-palette swap, `Application.Current.RequestedThemeVariant` — in **one synchronous UI-thread call with no yield** (SPIKE-FR37 §Q2). The synchronous no-yield data-swap is the correct, proven primitive: for the **three surfaces SPIKE-FR37 captured pixel-identically** — TextMate `SetTheme`, editor background, preview palette — the dispatcher's next render frame sees them already in the target state (the dark→light→dark round-trip PNGs are pixel-identical in the preview pane).

**Residual (carried, not asserted away).** The **fourth** surface — `Application.Current.RequestedThemeVariant` (the Fluent chrome) — is **not** guaranteed same-frame: SPIKE-FR37 §Q2 flags that the FluentTheme variant change triggers Avalonia's internal style re-resolution on the *next layout pass*, so "on systems with slow style trees or many theme-keyed resources, there could be a one-frame gap where the FluentTheme re-resolution has not yet propagated all controls." The architecture therefore does **not** claim flat all-four-surface atomicity — chrome re-resolution can be a single frame behind the editor/preview on heavy resource trees. The documented mitigations (carried, not yet needed): keep the `RequestedThemeVariant` assignment at the same render dispatch priority as the other three; and the Win10 title-bar fallback (OQ-TD-4 — Win10 chrome may not follow `RequestedThemeVariant`, deferred to the win-x64 validation).

The `IRawTheme` per palette is cached at startup (SPIKE-FR37 §Q4 minor optimization, dropping the data-swap under 1 ms). This is the one place a UI-thread operation must explicitly **not** marshal off-thread — the synchronous data-swap depends on staying on the dispatcher.

### 3.8 Threading summary

| Work | Thread | Marshal-back | Realizes |
|---|---|---|---|
| All Avalonia primitives, editor, preview build+layout, checklist, overlay | UI | — | — |
| FR-10(a) writable-set walk + op-time re-count | Background (off-UI) | boolean → UI | FR-10(a), SPIKE-1 |
| FR-9 folding recompute | Background, debounced 300 ms, cancellable | `UpdateFoldings()` on UI | SPIKE-FR9, TD §5.3 |
| `claude -p` process + `stream-json` parse + no-reply timer + kill | Background, cancellable | partial events → UI | NFR-8, TD §4 |
| Named-pipe echo (one write) | UI (on `TextChanged`) | — | SPIKE-1, MA15 |
| `.hoard` serialized write queue (terminal=0-debounce incl. ODQ-11 indent/outdent + collapse-toggle + delete-parent both branches; item-text≤150 ms) | Background, per-document serialized | — | FR-31, FR-41, FR-42, TD §1.5 |
| Append-log per-record `fsync` (activity + undo) | Background | — | TD §3.3, PRD §9.3 |
| Undo before-image durable-before-mutation | Background, awaited before op | — | FR-28 M15, PRD §9.4 |
| Lockstep theme switch (3 surfaces same-frame; Fluent chrome may be 1 frame behind, §3.7) | UI, synchronous, no yield | — | SPIKE-FR37 |

---

## 4. AI-provider architecture

Realizes TD §4 (folding SPIKE-3 + SPIKE-4) and PRD §6.6 / C-8. The architecture is the PRD's two-archetype model behind one selector; this section makes the deployment, the cap-state store, and the append paths concrete. It does **not** weaken the safety model or re-decide any pinned cap/flag/version.

### 4.1 The two archetypes, one shared file-ops service

```
                         ┌───────────────────────────────────────┐
                         │   AI panel / view-model (HoardPad.App) │
                         └───────────────────┬───────────────────┘
                                             │ backend selector (FR-23)
                  ┌──────────────────────────┴───────────────────────────┐
                  ▼                                                        ▼
   ┌──────────────────────────────┐                       ┌──────────────────────────────┐
   │ Archetype A — claude -p host  │                       │ Archetype B — driven-completion│
   │ (default; delegated agent)    │                       │ (Anthropic API / Ollama/LiteLLM)│
   │ HoardPad launches+gates the   │                       │ HoardPad OWNS the agent loop,  │
   │ agent's own tool loop via the │                       │ defines the tools, parses tool │
   │ deny-by-default PreToolUse hook│                       │ calls, EXECUTES each op itself │
   └───────────────┬──────────────┘                       └───────────────┬──────────────┘
                   │ captured tool_input replay (FR-39)                    │
                   └───────────────────────┬───────────────────────────────┘
                                           ▼
                         ┌───────────────────────────────────────┐
                         │   Shared file-ops service (Core.Ai)    │
                         │  sandbox check (SandboxPolicy) +        │
                         │  undo journal (§9.4) + activity log     │
                         │  (§9.3). UNIFORM log/journal structure; │
                         │  enforcement strength differs (C-8).    │
                         └───────────────────┬───────────────────┘
                                             ▼
                         IAtomicFileStore / IPathResolver / IAgentBackend (HoardPad.Host)
```

Both archetypes route every file op through **one shared file-ops service** (in `HoardPad.Core.Ai`) that delivers **uniform logging + undo-journal structure** (C-8). Enforcement strength differs and is **not** uniform: Archetype B is per-op-atomic and TOCTOU-free (HoardPad is the writer); Archetype A is observe-and-veto with a residual TOCTOU window (TD §4.7, FR-26). The architecture does not describe enforcement as uniform.

### 4.2 Archetype A — the `claude -p` host

`HoardPad.Core.Ai` owns the **orchestration logic**; `HoardPad.Host`'s `IAgentBackend` + `IToolPermissionHookHost` own the **OS legs**. One turn:

1. **Resolve + launch.** `IAgentBackend.ResolveLauncher` runs the FR-24 shim order (`claude.cmd → claude.exe → configured path`, SPIKE-4 `ShimResolver`). `StartTurnAsync` launches with the FR-24 §4.1 flags (binding, verbatim from TD §4.1): `-p --output-format stream-json --verbose --include-partial-messages --max-budget-usd <ceiling> --settings <json> --tools "Write,Read,Edit" --strict-mcp-config`; **no `--cwd`** (set the OS process `WorkingDirectory` to the resolved open-folder root); **never** `bypassPermissions`/`--dangerously-skip-permissions`. Min CLI version floor **2.1.179** (TD §1.7 — a floor `≥`, not a pin). On Windows: `CREATE_SUSPENDED → AssignProcessToJobObject (while suspended) → ResumeThread`; on Linux: `setsid` (TD §2.3).
2. **Gate.** The PreToolUse hook (a separate Python process the CLI launches, §4.3) matches `*`, is allowlist-only (`Read`/`Edit`/`Write`, the latter two only when the FR-26 sandbox check passes and under cap), and emits the **nested-JSON `hookSpecificOutput` `permissionDecision`** form (TD §4.2 — the deprecated top-level form is silently ignored; exit-2 is the minimal fallback). The agency→permission mapping (TD §4.3): `auto-apply → acceptEdits` (hook still gates each write); `approve-each → hook blocks synchronously` while HoardPad shows confirm/reject, then returns `allow`/`deny`; `read-only → no write tools allowed`.
   - **Host↔hook decision transport for `approve-each` (binding — so the build does not silently reduce it to "block-until-killed").** A synchronously-blocking hook must *receive* HoardPad's confirm/reject decision so it can return `allow`/`deny` **promptly once answered**, not only be unblocked by the no-reply kill. The transport is a **HoardPad-owned IPC/file signal the blocking hook polls** (keyed by `tool_use_id`): the hook writes its captured `tool_input` to a decision-request file/channel, blocks polling for a decision file, and returns the decision the instant HoardPad writes it (SPIKE-3 C1 proved a `sleep`-then-`allow` blocking hook lets the write succeed after the block). Without naming this, a Phase-8 build could make every *confirmed* op pay the full no-reply latency (a correctness/latency gap, not a safety hole — fail-closed still holds via the kill on no-reply, §4.2 step 4). The decision-file primitive is a `HoardPad.Host`/hook concern; the decision *logic* (allow/deny) is the user's confirm plus the pure cap/sandbox check. (TD §4.3 carries `defer`/`updatedInput` as a Phase-8 *refinement* candidate, not the v1 mechanism — OQ-TD-5.)
3. **Account.** The hook reads/writes the conversation-keyed cap-state (§4.4) for its allow/deny decision against the four pinned caps (FR-11=25, FR-38=10, FR-40=20, X%=80%, TD §1.7). The cap counters accumulate across the agent's internal tool loop within one invocation and do **not** re-arm after a confirm (TD §4.5).
4. **Fail closed.** HoardPad's no-reply-deadline process-tree kill (`IAgentTurn.KillTreeAsync`) is the **sole** fail-closed enforcer (TD §4.4) — the hook runs with a no/long timeout (≥ the no-reply timer + margin, default 600 s) so the CLI's fail-open hook timeout can never win the race. On an unanswered `approve-each` confirm past the no-reply timer, HoardPad denies the op, kills the tree, and parks the captured `tool_input` in the FR-39 queue (§4.6).
5. **Self-test gate.** Before write agency is enabled, `IToolPermissionHookHost.RunFailClosedSelfTestAsync` runs the FR-24 §3 self-test (deny a Write in a scratch dir; pass only if no file appears AND `permission_denials` is populated). This is the only guard against a silently-disarmed sandbox (`-p` silently ignores an invalid hook config — SPIKE-3 D3).
6. **Liveness (MA14).** Match each `stream-json` write tool-use event against an observed per-op hook callback, keyed by `tool_use_id` (TD §4.7). A write tool-use event with no matching hook invocation is the degrade signal; FR-10(c) applies (block/confirm remaining or halt; applied ops logged).

### 4.3 PreToolUse hook deployment + injection

The hook is the production `hooks/deny_by_default.py` (the SPIKE-3/SPIKE-4-prototyped script, ported to the cross-platform `O_EXCL` SpinLock + `normcase` form — TD §4.8). Deployment + injection (binding):

- **Bundling.** The hook script is bundled as a content artifact next to the published app (§7.4). `IToolPermissionHookHost.BuildHookSettings` resolves its absolute path at runtime and references it in the generated `--settings` JSON.
- **Injection via `--settings`.** HoardPad injects the hook config **explicitly via `--settings`** (and/or `--setting-sources`), never relying on ambient user settings (TD §4.1, FR-24 §3). The generated `--settings` wires the script as the PreToolUse `*`-matcher hook with a no/long timeout (≥ the FR-39 no-reply timer + margin; default 600 s) so the hook's own timeout never wins the fail-open race (§4.2 step 4).
- **Python floor probe (TD-1).** `BuildHookSettings` probes the interpreter (`python`/`py`) and enforces the Python floor: **Python 3.12+ on Windows** (so `pathlib.resolve()` calls `GetLongPathName` and expands 8.3 short names — a sandbox-escape vector on 3.10/3.11) **or** the explicit `GetLongPathName` `ctypes` hardening. Below the floor on Windows, write agency is not armed.
- **Decision callback.** The hook process calls into `HoardPad.Core`'s pure `SandboxPolicy.Evaluate` + the cap-accounting logic for its allow/deny decision (the same pure logic the C# self-test exercises). The hook is a thin transport over the domain decision — it owns no policy.

### 4.4 The conversation-keyed cap-state store

The cap-state — the FR-40 distinct-files set + the FR-11/FR-38/FR-40 no-re-arm tripped state — is **owned by HoardPad and keyed to the user-initiated conversation / logical turn**, not the per-invocation `session_id`/`requestId` (TD §4.5, R5-BL1, live-confirmed). Mechanism (binding):

- HoardPad mints a durable **conversation / logical-turn id** when the user initiates an AI conversation.
- The cap-state is persisted under that id in a **HoardPad-owned, exclusive-locked file** — the SPIKE-4 `O_EXCL` SpinLock (atomic on POSIX `O_CREAT|O_EXCL` and Win32 `CREATE_NEW`; 5 ms spin, 10 s hard timeout, unlinked in `finally`), **never `fcntl`** (no Windows equivalent). The exclusive-lock primitive is a `HoardPad.Host` concern (the host provides the locked-file handle); the cap-state *shape* and the increment/no-re-arm/trip logic are pure `HoardPad.Core.Ai`.
- A HoardPad-initiated **continuation** (a fresh `claude -p` invocation / fresh `session_id`) **resumes the same cap-state** (SPIKE-3 H5: file #4 stayed denied across the continuation). **Only a new user-initiated conversation resets it** (H6). This is the deterministically-confirmed cross-continuation no-re-arm property — an invocation-scoped counter is explicitly rejected (it re-arms on every continuation, R5-BL1).
- The store lives under `IAppDataPaths` (the host's per-user data dir, §5.5); its on-disk path is keyed by the conversation id.

### 4.5 The shared file-ops service

A single service in `HoardPad.Core.Ai` is the one execution path for Archetype B ops **and** for replayed Archetype-A FR-39 proposals (§4.6). Per op it: (1) canonicalizes the target via `IPathResolver` and checks `SandboxPolicy.Evaluate`; (2) for a destructive op, writes + `fsync`s the before-image and journal entry **before** mutating the target (M15, §3.6); (3) applies the op via `IAtomicFileStore`; (4) appends the activity-log record (§9.3) and the undo-journal entry (§9.4). It delivers the uniform log/journal structure C-8 requires. For Archetype A the same service is **not** the per-op enforcer (the hook is) — it is the executor for replayed proposals and the writer of the uniform log/journal; the enforcement-strength difference (C-8) is preserved.

The op vocabulary is `create`/`copy`/`move`/`edit`/`delete` (a rename canonicalizes to `move`, MA11). The reachable ops differ by archetype (R3-BL1): Archetype A reaches **create / full-overwrite / partial-edit** only (`delete`/`move`/`copy`/`rename`/shell-`truncate` are Bash-foreclosed by the matcher); Archetype B reaches the full vocabulary. The service models the full vocabulary; the Archetype-A path simply never receives the foreclosed ops (a `/code` build must not wire them as live Archetype-A branches).

### 4.6 The FR-39 queue + replay

An unanswered `approve-each` confirm past the no-reply timer fails closed: deny the op, kill the `claude` process tree, and **park the captured `tool_input`** (the full PreToolUse event JSON — it fully describes the proposed create/overwrite/edit) in the FR-39 pending-proposals surface with apply/discard actions. Every queued op is appended to the activity log (outcome `queued`). **Replay runs through the shared file-ops service (the Archetype-B execution path), NOT by re-launching `claude` or re-invoking the hook** (TD §4.6) — applying a parked proposal later runs its `tool_input` through the full FR-26/FR-28/FR-29 path as an Archetype-B op. This is safe because a PreToolUse-denied/killed op left no partial on-disk state.

**Replay cap-state accounting (binding — closes the under-specified edge).** A later *user-initiated* "apply" of a parked proposal (often in a new conversation, possibly after the original conversation's cap-state was reset — §4.4) is **governed by the normal Archetype-B per-op confirm path (FR-10(b)), NOT by the per-turn injection caps (FR-11/FR-38/FR-40)**. Rationale: the per-turn caps exist to bound an *injection* chaining many ops in one unattended turn; a manual single apply is the user **explicitly authorizing that one op**, so it is not an injection vector and must not burn (or be blocked by) a per-turn cap from a turn that already ended. The replayed op still runs the full sandbox check + before-image capture + activity-log/undo-journal write (it cannot escape the sandbox or skip the audit trail); it simply is not counted against the per-turn caps. (A *batch* "apply all" of N parked proposals is still N individual user-authorized applies, each on the Archetype-B per-op path — it does not re-enter a per-turn injection cap.)

### 4.7 Carried Windows-interception deferral + TD-1…TD-5 (NOT asserted Met)

The Windows interception **runtime** legs are **Deferred to Windows hardware** (TD §4.8, SPIKE-4). The architecture carries them as constraints, never as "Met":

- The Win32 Job-Object kill + `taskkill /T /F` fallback are **win-x64 compile-confirmed only**; the runtime (no-orphan-after-kill, timed-out-hook-no-leak on Windows) is Deferred. The Linux analogues PASS.
- **TD-1** (Python 3.12+ on Windows or `GetLongPathName` hardening) is enforced by `BuildHookSettings` (§4.3).
- **TD-2** (UNC paths) handled by the normcased prefix check; **TD-3** (drive-relative `C:foo`) by `resolve()`; **TD-4** (slash direction in `tool_input.file_path`) logged, no behavior impact; **TD-5** (`CREATE_BREAKAWAY_FROM_JOB` version-dependent) → Job-Object **AND** `taskkill` regardless (already pinned in §1.3 / TD §2.3).
- A Windows-hardware session runs the SPIKE-4 turnkey checklist and re-classifies the Deferred legs; SC-5/SC-8 win-x64 "Met" remains subject to the Decision-2 renegotiation clause. This doc does **not** assert a Windows "Met."

---

## 5. Data / persistence architecture

Realizes TD §3, PRD §9.1–9.4, NFR-4. **The architecture is flat-file** — SQLite was explicitly considered and rejected in PRD §9.2 as over-engineering for a single-user single-process personal tool. **This decision is confirmed, not reopened** (the Phase-5 brief's guardrail). The flat-file design carries no concurrency burden and adds no dependency.

### 5.1 The `.hoard` store

- **Format.** UTF-8 JSON, **`schemaVersion: 2`** (ODQ-11), the PRD §9.1 v2 field contract. The model + serialization live in `HoardPad.Core.Checklist`. The model carries the v2 fields **`parentId` (string|null)** and **`collapsed` (bool)** on each item, with `order` narrowed to **per-sibling-group contiguous `0..n-1`**; the array stays **flat** (nesting is derived from `parentId`, never a nested-tree JSON).
- **v1 → v2 migration (ODQ-11 D10, binding).** On reading `schemaVersion: 1`: set `parentId = null` and `collapsed = false` for every item (all become tier-0 leaves), keep each `order` as its tier-0 sibling order; the first re-save emits `schemaVersion: 2`. Absent `parentId`/`collapsed` on any foreign/hand-authored file default the same way. Readers still reject unknown **higher** versions (>2). HoardPad has not shipped, so there is no deployed v1-reader population — the bump is effectively free; the load rule exists only for robustness on hand-authored/foreign flat files. A **dangling or cyclic `parentId` is repaired to `null` on load** (logged), and an **over-deep import is clamped to tier 2** (the depth-cap-on-import flatten, PRD §9.1) — neither rejects the file.
- **Deterministic serialization (TD §3, binding).** Encoder pinned to **`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`** (literal non-ASCII, TD §3.2(a)); **`[JsonExtensionData]` overflow bag** at both object levels (root + per-item) with the accepted one-time known/unknown-boundary relocation diff (TD §3.2(b) — the custom raw-key-order converter is **not** shipped in v1, OQ-TD-2). **Fixed property ordering** (`schemaVersion`, `title`, `settings`, `items`; per item **`id`, `text`, `checked`, `parentId`, `order`, `collapsed`** — ODQ-11, TD §3.4), overflow keys in preserved within-bag insertion order. The **MA2 invariant**: byte-identical for files already in canonical form; the first re-save of a foreign/older-form file canonicalizes it (an accepted one-time diff). `parentId`/`collapsed` are ordinary typed fields — the pinned encoder + `[JsonExtensionData]` bag + byte-stable-when-canonical invariant are **unaffected**. Known-key-promotion migration on `schemaVersion` bump (v1→v2 adds two known fields by the migration rule above; no overflow-bag promotion needed since v1 never carried them).
- **Required serialization tests (TD §3.5 + the mutation guard, in `Core.Checklist.Tests`).** Three assertions, not two: (1) **canonical round-trip** byte-identical for a file already in canonical form, over a corpus covering integers, large/64-bit integers, decimals, nulls, nested objects, and unicode at both object levels (the unicode leg asserts the pinned `UnsafeRelaxedJsonEscaping` literal-non-ASCII *as the canonical form*, and that a default-encoder-escaped foreign file canonicalizes to literal on first re-save); (2) **first-re-save canonicalization** of a non-canonical (foreign/older-form, interleaved-unknown-key) file produces the documented one-time relocation diff and is byte-identical thereafter; (3) **unknown overflow-bag keys survive a *mutation*** — after toggling a known field (e.g. an item's `checked`) the unknown keys at both object levels re-emit intact. (3) is the highest-value addition: a no-mutation round-trip can pass while a *mutation* path silently drops the bag — the exact cross-version data-loss the `[JsonExtensionData]` bag exists to prevent (PRD §9.1 line 561).
- **Hierarchy-aware display-mode mapping (PRD §9.1, ODQ-11).** The display-index → stored-`order` mapping for each of the three checked-item modes is a **pure function in `HoardPad.Core.Checklist`** (FR-33), **applied per sibling group** (the partition dimension). Within each sibling group a **completed parent** (an item with `checked == true` that has children) moves/hides **as one contiguous unit** — the parent and its entire subtree (by the pre-order walk) move or hide as a single block, never splitting the subtree from its parent; a completed leaf moves/hides within its sibling group. **Collapsed-parent visible-row rule (Gap B):** a **collapsed parent's descendants are absent from the visible-row sequence** the mapping runs over (same idiom as hidden-checked items), **regardless of the parent's `checked` state** — including a *collapsed-but-unchecked* parent (children present in the file and active, just not rendered); the collapsed parent itself stays a visible row and still participates in its group's partition; expanding restores its descendants to the pre-order walk at their stored positions (collapse mutates only `collapsed`, never `parentId`/`order`). Unit-tested per mode with the §9.1 specified outcomes (including the reverse-ordered-neighbors and partition-edge cases) **and sweeping within nested sibling groups, including a checked-parent-block move/hide and a collapsed-but-unchecked parent**. The control (§6.5) calls this pure function; it never embeds the partition arithmetic.
- **`effectiveEnabled` gating (PRD §9.1 / FR-41, ODQ-11).** `effectiveEnabled(item)` = **every strict ancestor of `item` has `checked == false`** (a tier-0 item is always enabled); the item itself stays toggleable regardless of its own `checked` state (so a completed parent can always be unchecked to reopen). A **pure ancestor-chain function in `HoardPad.Core.Checklist`** — it walks `parentId` to the root, never mutates state, and is the sole driver of the FR-41 inert/greyed lock state (the control reads it; it embeds no gating logic). Identical contract wording in PRD §9.1 and TD §5.7. **Notification model (C-1, binding):** when a parent's `checked` toggles, the control must **explicitly raise `PropertyChanged("IsEnabled")` on every descendant ViewModel** for the `effectiveEnabled` fan-out — a passive binding re-query is insufficient (Avalonia does not re-evaluate a descendant's `IsEnabled` binding because an ancestor changed). The control walks the subtree and raises the notification per descendant on every parent check/uncheck.
- **Derived structure helpers (ODQ-11).** `depth(item)` (strict-ancestor count, max 2), `isParent`/`isLeaf`, and the **sibling-group renormalize** (after any reorder/indent/outdent/delete, the affected group is renormalized to contiguous `0..n-1`) are pure functions in `HoardPad.Core.Checklist`. **Depth-cap enforcement** is pure: an interactive indent that would push an item or any descendant past tier 2 is a **no-op**; the **import clamp** flattens an over-deep loaded structure to tier 2 (PRD §9.1). **A depth-cap no-op enqueues ZERO write-queue entries (C-4, binding):** an over-cap indent (incl. held-key auto-repeat) makes no state change and must not enqueue a write or trigger an `fsync` — held-`Alt+Shift+→` at tier 2 produces no fsync burst (consistent with FR-31 + the "tier-4 indent is a no-op" rule).
- **Atomic write (FR-31, §3.6) — preserves the durability invariant under v2.** Every mutation rewrites the **whole flat `items[]` file** via `IAtomicFileStore.ReplaceWhenPresentAsync` (or `CreateWhenAbsentAsync` for a new `.hoard` via FR-34), through the serialized per-document write queue (zero-debounce terminal mutations, ≤ 150 ms item-text coalesce). **The v2 schema keeps FR-31 intact** — one flat array, one whole-file atomic temp+rename rewrite per mutation; `parentId`/`collapsed` add no second file, no nested structure, no additional write path. The new ODQ-11 terminal mutations — **indent/outdent, collapse/expand-toggle, and delete-parent (both the delete-subtree and promote-children-up-one-tier branches)** — join the **zero-debounce terminal class** on the per-document write queue (each persisted immediately via the full atomic sequence before the next mutation begins; both delete-parent branches re-persist this way and are covered by the ODQ-10 in-list undo).

### 5.2 The machine-local session/recovery store

- **Location (R4-MA1, binding).** The recovery directory is **machine-local and NOT synced** — under `IAppDataPaths.RecoveryDirectory`, separate from the maintainer's synced notes folders. Recovery snapshots never cross machines, so the cross-machine duplicate-`tabId` scenario cannot occur for snapshots, and the per-machine `seq` is a same-machine tie-break only.
- **`session.json`** carries `openFolder`, `activeTabId`, and the `tabs[]` array (PRD §9.2 field contract). It is a **rebuildable index/hint**, not a foreign key — the recovery directory scan is authoritative.
- **Self-describing snapshots.** Each recovery snapshot embeds its own `tabId`, `path`, `kind`, `cursor`, `savedAt`, the monotonic per-machine `seq`, and — for `kind: file`/`hoard` — the on-disk file's `baselineContentHash` + `baselineMtime` (B7). `tabId` is the durable correlation key (snapshot filenames are opaque, never the join key). Snapshot writes follow the full NFR-4 atomic sequence (the first snapshot of a never-saved scratch buffer uses the create-when-absent path).
- **Reconciliation (PRD §9.2, binding).** The 3-dimensional reconciliation table (snapshot present? × session-tab present? × on-disk file state) is **pure domain logic in `HoardPad.Core`** — every cell unit-tested with a fake clock + injected hashes (C-9 testability). The directory enumeration is a `HoardPad.Host` concern (`IAppDataPaths`). Content **hash** is authoritative for the present-unchanged vs present-changed-externally decision; `mtime` is only a re-hash-skip optimization (FR-28(ii)/FR-19). The open-folder dimension is reconciled separately (MA5) — a moved/renamed/deleted AI root re-prompts before write agency arms (no silent stale sandbox root). A `.hoard` baseline comparison is over **canonical-parsed content** (MA5, couples to MA2 — avoids spurious conflicts on whitespace/escaping-only reserialization). The `seq`-based same-machine tie-break and the Save-As stale-path rule (R3-MA1) are pure domain decisions.

### 5.3 The activity log (append-log)

- **Format (PRD §9.3, TD §3.3).** Per-session NDJSON-payload records, each framed as `[4B uint32 LE length][N B UTF-8 JSON payload][4B uint32 CRC-32/ISO-HDLC of payload]` (8 B overhead, polynomial `0xEDB88320`). The record fields are the §9.3 contract (`ts`, `requestId`, `backend`, `configuredMode`, `agencyMode`, `downgradeReason`, `op`, `paths` object, `outcome`, `bytesBefore`/`bytesAfter`, `undoRef`, `error`, `durationMs`, optional `tokens`/`costUsd`). **Never** API keys or file contents (NFR-7). The record model is in `HoardPad.Core`; the I/O is `IAtomicFileStore.AppendRecordAsync`.
- **Durability (TD §3.3).** Per-record `fsync` before the next append begins; on-open truncate-repair to the last validated record offset; **`StopAtFirstBad` reader default** for the v1 local-SSD target (per-record `fsync` makes interior holes impossible, so the only invalid record is the torn trailing one); **`resync-and-continue` is the named fallback** for a non-ordering FS (with the honest ~0.0004%/corrupt-record/64 KB ghost-record false-positive characterization carried, TD §3.3 B4).

### 5.4 The undo journal (append-log + before-image blobs)

- **Format (PRD §9.4).** A structure separate from the activity log (it holds the before-images the log deliberately does not). Per-entry shape per the §9.4 field contract (`undoRef`, `bufferJournalId`, `requestId`, `op`, `paths`, `priorContent`, `destPriorContent`, `preOpContentHash`/`destPriorContentHash`, `preOpMtime`, `snapshotSkipped`). The `op` enum is the create/copy/move/edit/delete subset (no `read`/`blocked` — they never enter the journal, §9.3 mi5).
- **Append durability.** When entries are appended, the same per-record-`fsync` + length-prefix + CRC + truncate-repair + `StopAtFirstBad`-default model as §5.3. The before-image blobs (`before/…bin`) are whole-file create+`fsync` via the create-when-absent path.
- **Durability ordering (M15, §3.6).** The before-image blob(s) + journal entry are `fsync`'d to disk **before** the destructive op mutates the target. The destructive op is gated on the before-image's durable completion.
- **Reversal (PRD §9.4).** Pure decision logic in `HoardPad.Core` — given an entry + the injected current hash/mtime of the target, the domain decides the reverse action, replaying in reverse activity-log order. Content hash authoritative; host re-verifies the hash immediately before the restore write to close the check→restore TOCTOU (M14). Over-cap (`snapshotSkipped: true`) entries never attempt restore.
- **Cross-session retention (MA8).** Keyed on the durable, path-independent `bufferJournalId` (minted on the buffer's first undo entry, carried in the recovery snapshot — survives Save-As / AI-rename / session end). Pruned on clean save+close, else after 7 days OR 50 ops per buffer.

### 5.5 The `IAppDataPaths` on-disk layout

`IAppDataPaths` (host-implemented, NFR-7) resolves the per-user data root, restricted to the current user where the OS supports it (chmod 600 / Windows ACL). Conceptual layout (exact paths the Phase-8 build finalizes):

```
<per-user app-data root>/                      AppDataRoot (restricted)
├── settings.json                              SettingsFilePath (FR-36, single human-readable file)
├── session.json                               the rebuildable session index (§5.2)
├── recovery/                                  RecoveryDirectory — MACHINE-LOCAL, NOT synced (R4-MA1)
│   └── <opaque>.snapshot                       self-describing recovery snapshots (tabId-keyed)
├── activity/                                  per-session activity logs (§5.3)
│   └── <sessionId>.log                         ActivityLogPath(sessionId)
├── undo/                                      UndoJournalRoot (§5.4)
│   ├── <undoRef>.entry                         append-log entries
│   └── before/<undoRef>.bin                    before-image blobs
└── capstate/                                  conversation-keyed cap-state (§4.4)
    └── <conversationId>.capstate               O_EXCL-locked cap-state file
```

The whole tree is per-user app-data — the maintainer syncs notes folders, not this directory (C-6 / R4-MA1). No content here is ever synced or transmitted; it is purely local durable state.

---

## 6. Custom-control architecture (the M18 surface)

Every custom control lives in `HoardPad.App` (the only Avalonia project) and binds to models/pure-functions in `HoardPad.Core` / `HoardPad.Core.Checklist`. The M18 effort/risk estimates (TD §5) are inherited, not re-derived. Each control's domain logic is pulled into `Core`/`Core.Checklist` so the control is a thin view over testable logic.

| FR | Control | Lives in | Domain logic in | Realizes |
|---|---|---|---|---|
| **FR-1** | Tab drag-reorder behavior over the tab-strip `ItemsControl` | App | the bound `ObservableCollection` reorder (trivial) | TD §5.4 |
| **FR-6** | Move/duplicate-line `TextDocument` commands + encoding detection | App (commands); detection via the `IEncodingDetector` seam (impl + `UTF.Unknown` in `Host`/`App`, never `Core`) | encoding detect-then-preserve *decision* is a pure function in `Core` (OQ-ARCH-4) | TD §5.5 |
| **FR-7** | Custom `IRegistryOptions` overlaying supplemental `.tmLanguage.json` grammars (TOML, Rust if it lags), registered lazily per-language | App | — (grammars are embedded resources) | TD §5.6, NFR-1 |
| **FR-9** | Folding (`IndentationFoldingStrategy` + `BraceFoldingStrategy` + `FoldingStrategySelector` by TextMate scope) + `FoldingService` | App (`FoldingService`, §3.3) | the fold strategies are pure over a `TextDocument` snapshot (SPIKE-FR9) | TD §5.3, SPIKE-FR9 |
| **FR-15 / FR-16** | The Candidate-A Markdig-AST → Avalonia control-tree walker + the FR-15 toggle (FR-16 = the per-feature rendering fidelity it must pass) | App | the Markdig parse + AST is the input; the walk is App (it builds Avalonia controls); the FR-16 fidelity fixture lives in `App.Tests` (§1.6) | TD §5.1, SPIKE-2, FR-16 |
| **FR-30/FR-32/FR-41/FR-42** | The `.hoard` checklist control (`ItemsControl` + drag-reorder within a sibling group; `Alt+Shift+←/→` indent/outdent carrying the subtree; the three hierarchy-aware checked-item display modes; the ▸/▾ collapse twisty; completion-lock greying via `effectiveEnabled`; the delete-parent prompt) — extended with **tree ARIA semantics** (`AutomationProperties` level / position-in-set / expanded / disabled, ODQ-11 D11), **not** a stock `TreeView` | App | the hierarchy-aware display-index→stored-`order` mapping, `effectiveEnabled` ancestor-chain gating, `depth`/sibling-group renormalize/depth-cap are pure functions in `Core.Checklist` (§5.1, FR-33/FR-41) | TD §5.7, PRD §9.1 |
| **FR-37** | The brand-matched TextMate theme + the lockstep `ThemeService.Switch` | App (`ThemeService`, §3.7) | the `ThemePalette` record (HoardPad brand dark/light — needs `brand.md`/Phase 7) | TD §5.2, SPIKE-FR37 |

**Composition.** The render-tree walker (FR-15) and the folding strategies (FR-9) share the theme-aware brush story — both read their colors from the `ThemePalette` the `ThemeService` swaps in one synchronous call (§3.7), so a theme switch re-themes editor + preview + chrome in lockstep. The checklist control (FR-30/FR-32) and the tab-strip (FR-1) share one drag-reorder behavior pattern (TD §5.7). None of these controls touches the FS or an OS syscall directly — every durable mutation goes through the seam services (the `.hoard` write queue, §3.6).

**Checklist-control implementation pins (ODQ-11, decision-neutral correctness — binding):**
- **Tree ARIA (C-5, D11):** the per-item row exposes an **`AutomationPeer` subclass returning `AutomationControlType.TreeItem`** with the numeric `AutomationPeer` level (`GetLevel`/the UIA `LevelProperty` analogue) — setting `AutomationProperties.Level` in XAML **alone is insufficient** for NVDA/JAWS tree navigation; the peer must report `TreeItem` + numeric level + position-in-set/set-size + expand-collapse + enabled-state. (Mirrored in design §10.2.)
- **Delete-parent prompt (C-3):** the delete-non-empty-parent prompt (D8) is a **modal `ShowDialog(owner)`**, not a modeless window — modality prevents a concurrent checklist mutation while the subtree-vs-promote choice is open (no race between the pending delete and another edit). (Mirrored in design §5.2.)

**Layout-cost grounding.** The render-tree build is cheap (~13.5 ms for ~20 KB, SPIKE-2); the cost is the Avalonia layout pass (SPIKE-1: layout dominates), governed by the NFR-9 split thresholds (~100–200 KB preview / ~500 KB editor highlight — directional, Phase-8-confirmed, TD §1.4). The checklist and tab-strip are flat small item sets — layout-dominates does not bite them.

**This document does not redraw layouts.** Visual direction (palette, type scale, spacing, the actual UX flows) is Phase 6 `docs/design.md` and Phase 7 `brand.md`. §6 specifies **where controls live and how they compose**, not how they look — the design-reference protocol applies to the Phase-6 design doc, not to this system blueprint.

---

## 7. Build / publish topology

### 7.1 Per-RID self-contained publish

`dotnet publish` self-contained, per-RID, for **win-x64** and **linux-x64** (C-3, CLAUDE.md §2). Each RID produces a self-contained binary (the .NET runtime is bundled — no machine-wide runtime dependency). The App `.csproj` declares `<RuntimeIdentifiers>win-x64;linux-x64</RuntimeIdentifiers>` so `dotnet publish -r <rid>` is first-class for both. The win-x64 publish compiles clean from the Linux box (the SPIKE-1/4/5 pattern) but is **not run** on Linux — win-x64 runtime numbers are Deferred (§7.6).

### 7.2 `PublishReadyToRun=true` is MANDATORY (binding)

> **Ship constraint (binding, NFR-1 / SPIKE-1): `PublishReadyToRun=true` is REQUIRED for both RIDs.** SC-1 **fails** without it (R2R-off = 1309 ms p90 vs the 843.5 ms p90 R2R-on PASS, SPIKE-1 §B). This upgrades NFR-1's prior "may use, measured before committed" to a required publish setting.

The publish profile (or `.csproj` `<PublishReadyToRun>true</PublishReadyToRun>`) sets it for `win-x64` and `linux-x64`. **The guard is an MSBuild `<Error>` in `Directory.Build.targets`** (§2.3) that fails any `Publish` of a shipping RID when `'$(PublishReadyToRun)' != 'true'` — a publish without R2R is a gate regression, not a tuning choice. **This MSBuild-target form is the only guard that can fire in a CI-less v1** (PRD §12 / §7.5 — no CI pipeline exists to host a YAML check), and because it travels with the repo it fires on any manual `dotnet publish`, not just one pipeline. The enabling mechanisms NFR-1 names are binding design: lazy per-language grammar registration (a grammar registered on first use, not all 20 at boot — FR-7); deferred subsystem init (the AI provider, the preview renderer, find-in-folder constructed on first use, not at launch).

### 7.3 Trim — optional, R2R-paired, grammar trim-roots pinned

Trim is **optional**: perf-neutral with R2R-on (+33 ms p90, within variance) and a size win (113 MB → 65 MB); counter-productive with R2R-off (+358 ms). Therefore **trim is a safe size win only paired with R2R-on** (SPIKE-1 §B). If trim is enabled, the **`TextMateSharp.Grammars` assembly's trim roots are pinned** (the NFR-1 known footgun — trimming the grammar resources would strip them) **and the FR-7 supplemental-grammar embedded resources are also pinned as trim roots**. SPIKE-1 confirmed trim did not break the TextMateSharp grammar resources when the roots are preserved (they pack as managed resources).

### 7.4 Hook-script bundling

The production `hooks/deny_by_default.py` is bundled as a content artifact alongside the published app (copied to the publish output, included in both RID publishes). `IToolPermissionHookHost.BuildHookSettings` resolves its absolute path at runtime and references it in the generated `--settings` JSON (§4.3). The hook requires a Python interpreter on the target (Python 3.12+ on Windows per TD-1, §4.3) — HoardPad probes for it and does not arm write agency if it is absent/below floor.

### 7.5 Out of scope (per PRD §12)

Code signing, store packaging, and auto-update are **out of scope** for v1 (PRD §12). The build topology is `dotnet publish` per-RID; distribution is manual. The architecture does not provision a signing pipeline or an update channel.

### 7.6 Carried deferrals (NOT asserted Met)

The architecture carries these forward exactly as TD §6.1 / PRD §5 do, and does **not** assert a Windows "Met":

| Deferral | Status |
|---|---|
| win-x64 perf (SC-1/SC-2/NFR-5) | Deferred — all numbers are linux-x64; win-x64 compiles clean, not run |
| Native Wayland synthetic-input + Wayland SC-1/SC-2 | Deferred — SPIKE-1 measured under X11/Xvfb; no Wayland compositor present |
| Windows interception runtime (Job-Object kill, hook-fire, deny-on-Write+Edit, no-leak) | Deferred — win-x64 compile-confirmed + Linux-analogue PASS only |
| Windows `MoveFileEx` first-ever-write power-cut | Deferred — `MoveFileEx(MOVEFILE_WRITE_THROUGH)` compile-confirmed only |
| Strict SC-2 warm-open; multi-tab idle RSS | Phase-8 harness/measurement (expected to pass; not blockers) |
| NFR-9 split thresholds; FR-37 re-tokenize flash / Win10 title-bar; FR-9 `UpdateFoldings()` at scale | Phase-8 confirmation (single-sample / sub-spike residuals) |

---

## 8. Cross-cutting

### 8.1 DI / composition

`HoardPad.App` is the composition root. At startup it builds a DI container that:

- Registers the `HoardPad.Host` concrete seams against the `HoardPad.Core` abstractions (`AtomicFileStore → IAtomicFileStore`, `PathResolver → IPathResolver`, `AgentBackend → IAgentBackend`, `ToolPermissionHookHost → IToolPermissionHookHost`, `AppDataPaths → IAppDataPaths`, `SystemTimeProvider → TimeProvider`). This is the **single place** the platform implementations meet the domain.
- Registers the domain services (the shared file-ops service, the cap-accounting service, the reconciliation service, the serialization service) and the AI archetype implementations.
- Registers the view-models and the custom-control services (`FoldingService`, `ThemeService`).

Test projects substitute fakes for the seams (a fake `IAtomicFileStore` over an in-memory FS, a fake `IEncodingDetector`, a fake `TimeProvider`) — the C-9 testability story (§1.2) depends on every seam being injectable.

**All enforcement timers are `TimeProvider`-driven (binding).** Every timeout/debounce/coalesce window — the FR-24/FR-39 no-reply fail-closed kill timer (§3.4), the `O_EXCL` SpinLock spin/hard-timeout (§4.4), the FR-9 folding debounce (§3.3), the FR-31 item-text coalesce (§3.6) — consumes the injected `System.TimeProvider` (its `CreateTimer`/`Delay`), **not** a bare `Task.Delay`/`System.Timers.Timer`. The **only** exception is the named-pipe SC-1/SC-2 stop instant, which must use `Stopwatch` (monotonic, the actual measurement clock, §3.5). This keeps every time-based gate (the no-reply fail-closed deadline, the SpinLock timeout, the coalesce/debounce boundaries) deterministic in tests via a `FakeTimeProvider` — no real wall-clock sleeps, per the project's coding standards (test discipline).

### 8.2 Settings / config

A single human-readable settings file (`settings.json`) under `IAppDataPaths.SettingsFilePath` (FR-36). It holds the FR-35 surface: active AI backend + per-backend config, agency mode, AI-panel hotkey, auto-save interval, active theme, default new-document language, and the AI-safety tunables (the FR-10(a) large-root threshold, the four caps 25/10/20/80%, the FR-24 no-reply timeout). The cap values are MEASURED and pinned (TD §1.7) but remain user-tunable within documented safe ranges; the FR-35 clamp rule (a cap cannot be set to 0/unbounded in a way that disables the injection backstop) is a pure domain validation in `HoardPad.Core.Ai`. A missing/corrupt settings file falls back to documented defaults without crashing (FR-36). Settings load/save uses the same `IAtomicFileStore` atomic path (no torn settings file on crash).

### 8.3 Logging (no telemetry — C-6)

> **No telemetry. No remote logging. No usage metrics. No crash-reporting service.** (C-6, NFR-7 — a hard privacy constraint.)

The only durable "logs" are the **local** activity log (§5.3) and the undo journal (§5.4) — both local files the user can view, neither transmitted anywhere. Diagnostic logging (if any) is local-only and never auto-uploaded. **Telemetry is deferred per the project privacy constraint** (PRD §14, TD §7) — no `telemetry-analyst` engagement for v1; success is validated locally against the PRD §5 testable criteria. (This is the design-reference/telemetry-prompt resolution: the decision is already pinned across the PRD and tech-design as a hard constraint, so it is recorded here rather than re-prompted — see §10 OQ-ARCH-6.)

### 8.4 The error model

- **Seam failures surface as typed exceptions the domain/UI handles.** `IAtomicFileStore.CreateWhenAbsentAsync`/`ReplaceWhenPresentAsync` throw `AtomicWriteUnavailable` on EXDEV/cross-device/synced-target (the caller surfaces the NFR-4 best-effort warning, §5.1). The `ProbeAtomicCapabilityAsync` returning `BestEffortWithWarning` is the proactive path; the exception is the reactive backstop.
- **AI-backend readiness failures are actionable, never silent** (FR-24): "`claude` CLI not found", "installed but not authenticated", "version too old — needs ≥ 2.1.179", "sandbox self-test failed — AI write actions disabled", "Anthropic API key not set". Write agency is enabled **only** after the FR-24 §3 self-test passes (§4.2 step 5).
- **A hung/erroring backend never freezes the UI** (NFR-8) — AI work is off-UI with cancellation (§3.4); a runaway is bounded by `--max-budget-usd` + HoardPad's wall-clock timeout (which kills the process tree) + hook-side cap counting.
- **Corrupt durable state degrades gracefully** — a torn append-log tail is truncate-repaired on open (§5.3); a corrupt settings file falls back to defaults (§8.2); an unorderable duplicate-`tabId` snapshot surfaces a conflict rather than guessing (PRD §9.2).

### 8.5 The no-network constraint (C-6)

The **only** network egress is to the user-configured AI backend: the Anthropic API (Archetype B direct), a local Ollama/LiteLLM endpoint (Archetype B local), or the `claude -p` local subprocess (Archetype A — itself a local process that talks to Anthropic). No update check, no telemetry beacon, no font/CDN fetch, no analytics. Markdown preview autolinks are link-*styled* but navigation is out of scope (NFR-7, FR-16); images load local-relative only, no network image fetch (TD §5.1). The architecture provisions no HTTP client outside the AI-backend path.

---

## 9. Traceability

Each architecture section mapped to the tech-design / PRD / spike it realizes — the basis the review round and the Phase-8 build check against.

| Architecture section | Realizes |
|---|---|
| §1 Solution / project structure | TD §2 (C-9 seam interfaces → projects/namespaces), C-9, CLAUDE.md §2 (domains separable) |
| §1.2 `IEncodingDetector` seam (keeps `UTF.Unknown` out of `Core`) | FR-6, OQ-ARCH-4, C-9 / §2.1 invariant 1 |
| §1.5 AI/checklist separate assemblies | CLAUDE.md §2, C-8, the §1.5 justification |
| §1.6 test/harness mapping (incl. `App.Tests` for SC-3/SC-4/FR-16) | PRD §5 (SC-3/SC-4), FR-16, A-6, the C-9 testability story |
| §2 Dependency graph + layering | TD §2 (`App → Host → Core`), C-9 (no `Core → Host`) |
| §2.3 mechanical C-9 enforcement (banned-symbols + arch-test) + R2R `Directory.Build.targets` guard | C-9, NFR-1 (R2R MANDATORY), PRD §12 (CI-less v1) |
| §2.2 Package manifest | SPIKE-1 §Environment, SPIKE-2 §E, SPIKE-5; TD §1.6 (zero extra Markdig) |
| §2.3 Scaffolding (`global.json`, `Directory.*.props`) | NFR-1 (.NET 8 floor), the project's coding standards (nullable/functional) |
| §3 Threading model | SPIKE-1 (layout dominates, off-UI walk, echo channel), SPIKE-FR9 (debounced+background folding), SPIKE-FR37 (synchronous lockstep), TD §1.5/§3.3/§4 |
| §4 AI-provider architecture | TD §4, PRD §6.6, C-8, SPIKE-3 (contract/hook/cap), SPIKE-4 (kill/portability/deferral), FR-22…FR-29/FR-10/FR-11/FR-38/FR-39/FR-40 |
| §4.4 conversation-keyed cap-state | TD §4.5, R5-BL1, SPIKE-3 H5/H6, SPIKE-4 (`O_EXCL` SpinLock) |
| §4.7 Windows deferral + TD-1…TD-5 | TD §4.8, SPIKE-4, PRD §5 (renegotiation clause) |
| §5 Data / persistence | TD §3, PRD §9.1–9.4, NFR-4, SPIKE-5 (durability), R4-MA1 (machine-local recovery); SQLite rejected (§9.2) — confirmed |
| §6 Custom-control architecture | TD §5, PRD §9.1 (display mapping), SPIKE-2/SPIKE-FR9/SPIKE-FR37 |
| §7 Build / publish topology | NFR-1 (R2R MANDATORY), SPIKE-1 §B (R2R/trim), C-3, PRD §12 (signing/update out of scope) |
| §8 Cross-cutting | C-6 (no telemetry/no network), NFR-7/NFR-8, FR-24/FR-35/FR-36, NFR-4 error model |

---

## 10. Open architecture questions

Each with a proposed default so none blocks Phase 6. These are architecture-shaping decisions, not scope decisions (scope is locked by the PRD; pinned HOW is locked by the tech-design).

- **OQ-ARCH-1 (AI/checklist assembly split vs namespaces, §1.5).** v1 ships `HoardPad.Core.Ai` + `HoardPad.Core.Checklist` as separate assemblies (compiler-enforced separability per CLAUDE.md §2). **Proposed default: ship the split.** Collapse to namespaces-within-`Core` only if a future maintainer finds the split is pure plumbing with no separability benefit — the documented collapse path is to merge the two extension assemblies' source into `HoardPad.Core` under `Ai`/`Checklist` namespaces, which the acyclic dependency graph (§2.1) makes mechanical.

- **OQ-ARCH-2 (seam-abstraction namespace, §1.2).** TD §2 declares seams in `HoardPad.Host.Abstractions`; to keep `App → Host → Core` acyclic, the architecture places the abstractions physically in `HoardPad.Core` under a namespace `HoardPad.Core.Host.Abstractions`. **Proposed default: `HoardPad.Core.Host.Abstractions` (abstractions in `Core`, concretes in `Host`).** The alternative — a fourth `HoardPad.Abstractions` project both `Core` and `Host` reference — is also valid and is the documented upgrade **on a concrete trigger** (a *second* independent implementer of the seams that must not take a `Core` dependency — e.g. a future headless/CLI host; none exists in the v1 Avalonia-desktop-only consumer set). For v1 the in-`Core` namespace is the lighter choice and matches the ports-and-adapters norm. **Phase-8 hazard (binding):** the `Host` token in `HoardPad.Core.Host.Abstractions` is a **naming convention, NOT a project-placement instruction** — the abstractions live physically in the `HoardPad.Core` assembly. This is the single most likely place a build agent reintroduces the forbidden `Core → Host` cycle by "moving the abstractions to where the namespace says." The Phase-8 scaffold should put a one-line comment in each seam file asserting its assembly home (`// abstraction — lives in HoardPad.Core, NOT HoardPad.Host`); the architecture-test fixture (§2.3) catches a `Core → Host` edge if it slips in anyway.

- **OQ-ARCH-3 (folding debounce interval, §3.3).** The 300 ms folding debounce is the OQ-TD-3 proposed default, pending the Phase-8 `UpdateFoldings()` BST-cost benchmark at ~6,843 folds (20k-line doc). **Proposed default: 300 ms, confirmed by the Phase-8 `UpdateFoldings()` benchmark before finalizing.** (Inherits OQ-TD-3 — this is the same decision surfaced at the architecture layer.)

- **OQ-ARCH-4 (encoding-detection placement, §2.2 / §6 / §1.2) — RESOLVED in v2.** FR-6's encoding detection splits cleanly: the **byte-sniff/BOM heuristic** (which needs the `UTF.Unknown` charset package) is an I/O-edge concern, and the **detect-then-preserve decision** (choose the buffer's effective encoding + the on-save preserve rule from the candidate + confidence) is pure. **Proposed default (binding): the byte-sniff lives behind an `IEncodingDetector` seam — interface in `HoardPad.Core.Host.Abstractions`, concrete impl + the `UTF.Unknown` package in `HoardPad.Host` (or `HoardPad.App`); only the pure decision stays in `HoardPad.Core`.** This was changed from the v1 draft (which put the detector package in `Core`) because that falsified the §2.1 invariant 1 ("`HoardPad.Core` references nothing but the BCL + `System.Text.Json`") — flagged by three reviewers. The seam keeps the invariant absolute (no `UTF.Unknown` in `Core`) **and** preserves testability: the pure decision is unit-tested with a fixture corpus of (candidate, confidence) inputs in `Core.Tests`, and the byte-sniff impl is tested against a sample-file corpus in `Host.Tests`/`App.Tests`. The whole choice is consistent with the §1.2 ports-and-adapters pattern.

- **OQ-ARCH-5 (single-instance / file-association launch, §1.3).** Opening a file via OS "Open With" (FR-3) and a single-instance editor (route a second launch's file into the running instance) are genuinely platform-specific (named in NFR-2 as permitted OS-conditional shims). They are not yet specified. **Proposed default: model both as `HoardPad.Host` seams (a single-instance/IPC seam + a file-association launch arg path), OS-conditional inside `Host`, surfaced before commit per NFR-2's "surface any deviation" rule.** v1 may ship without single-instance (each launch is its own process) if the Phase-8 budget is tight — opening a file always works via the CLI arg path (FR-3) regardless; single-instance is a UX refinement, not a gate.

- **OQ-ARCH-6 (telemetry).** Telemetry is **deferred per the project privacy constraint** (C-6/NFR-7 — a hard constraint, not a deferral of convenience; PRD §14, TD §7). No `telemetry-analyst` engagement for v1; success is validated locally against the PRD §5 testable criteria and the spike-measured gates. **Proposed default: no telemetry, ever, for v1 (the constraint is non-negotiable).** Recorded here so the decision is visible at architecture time per the spec-session telemetry protocol; this is a documented decline, not an oversight.

---

## 11. Review status — Phase-5 combined review COMPLETE (reviewed/folded)

The Phase-5 pre-publication specialist review round is **complete; verdict unanimous approve / ready to proceed, zero blockers.** v2 (this doc) folds the consolidated SHOULD-FIX items and the high-value CONSIDER items (see the §Changelog). Per-reviewer reports are on disk at `docs/review-phase5/`.

| Reviewer | Scope | Verdict | Reports |
|---|---|---|---|
| `devops-build-engineer` | §1/§2/§7 build/publish/solution topology | Sound with minor fixes — **0 blockers** (4 SHOULD-FIX folded) | `docs/review-phase5/devops-build-engineer.md` |
| `senior-csharp-multi-platform` | §1/§2 the `HoardPad.Core` no-platform-dependency boundary | Approve — **0 blockers** (2 SHOULD-FIX folded) | `docs/review-phase5/senior-csharp-multi-platform.md` |
| `senior-csharp-desktop-developer` | §1.4/§3/§6 Avalonia structure, threading, custom controls | Approve — **0 blockers** (2 SHOULD-FIX folded) | `docs/review-phase5/senior-csharp-desktop-developer.md` |
| `llm-integration-engineer` | §4 AI-provider architecture | PASS — **0 blockers** (2 SHOULD-FIX folded) | `docs/review-phase5/llm-integration-engineer.md` |
| `unit-test-engineer` | testability: test-project mapping, gates, §3.5 round-trip, A-6 fixtures | Testable as specified — **0 blockers** (3 SHOULD-FIX folded) | `docs/review-phase5/unit-test-engineer.md` |
| `sqlite-dba` | §3.6/§5.2–§5.4 append-log durability + the SQLite-rejection assessment | Sound, crash-grounded — **0 blockers**; **SQLite rejection concurred** | `docs/review-phase5/sqlite-dba.md` |
| `project-manager` | architecture ↔ PRD/tech-design consistency | Approve — **0 blockers** (summarized by the coordinator) | (summary) |

The six folded tech-design `REVIEW_REQUEST:` lines (TD §8 lines ~629–634) were also confirmed by this round and are marked **RESOLVED** in `docs/tech-design.md`. `sqlite-dba` reviewed despite not being requested in the v1 draft block (the coordinator dispatched it to independently gate the flat-file decision) — it **concurred with the SQLite rejection** on durability grounds (a flat append-log gives the same crash guarantee SQLite would, while making the M15 before-image-blob-durable-before-external-mutation ordering *easier*, not harder, and adding no native per-RID dependency). The flat-file decision (§5) is therefore independently gated and stays closed.

**No further architecture review is required before the maintainer's sign-off.** Phase 5 is ready to close → Phase 6 (`docs/design.md`).
