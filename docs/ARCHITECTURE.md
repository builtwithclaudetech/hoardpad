# HoardPad architecture

**Audience:** contributors and security reviewers who want to know why the
code is shaped the way it is, and what the AI sandbox does and does not
defend against. For install and usage, see the [README](../README.md).

This document summarizes the design. The binding detail lives in the design
record: [requirements](design-record/prd.md) (PRD),
[technical design](design-record/tech-design.md) (TD),
[architecture](design-record/architecture.md) (ARCH), and
[user-facing design](design-record/design.md). Section references below look
like "ARCH §4.3".

## 1. Why HoardPad is built this way

### The problem

HoardPad is a small, fast editor that also hosts an AI agent with write
access to your files. Those two goals pull in different directions.

- An editor must open quickly, keep working on a 50 KB file, and never lose
  your text. A checklist must survive a kill without losing order or check
  state.
- An AI agent that can create, edit, and delete files is a risk. A prompt
  injection hidden in a README, or a model mistake, could damage files
  outside the project, or wipe the project itself. The agent runs as you, so
  the operating system will not stop it.

### The constraints

- **Cross-platform.** One code base must build for Linux x64 and Windows x64.
  That rules out WPF, WinUI, and WinForms. Web-stack editors (Electron,
  Tauri, Monaco) were rejected so the product stays a native C# application
  (PRD §4.2).
- **Safe by construction.** The safety of AI file access must come from code
  that a test can break, not from instructions to the model. Every control
  must have a deterministic test that fails when the control is removed.
- **Fail closed.** When the system cannot tell whether an operation is safe,
  it must deny it.
- **No telemetry, and no network calls except the AI backend you configure**
  (ARCH §8.3, §8.5).
- **Durable.** Checklists, settings, the activity log, and the undo journal
  must survive a crash at any point.
- **Fast start.** The published build must be ReadyToRun-compiled to meet its
  cold-start target (ARCH §7.2).

### How the design answers them

1. **Separate decisions from effects.** The domain code that decides things
   (is this path inside the sandbox, has this cap tripped, how do we reverse
   this operation) is pure. It has no file, process, or clock access, so
   tests can drive every case with fakes. A single host project does all the
   operating-system work behind small interfaces.
2. **Put the enforcement point outside the model.** The AI agent can only use
   the tools a deny-by-default hook allows, and only inside the sandbox. A
   second layer (a host-side observation channel, a startup self-test, and a
   process-tree kill) covers the hook's failure modes.
3. **Design writes to be undoable and logged, and be honest about the gap.**
   A shared file-operation service is the one place that records a
   before-image, applies a change, and appends to the activity log. In 1.0.0
   only the replay path (re-applying a parked or approved operation) uses it.
   Live writes by the `claude -p` agent go through the agent's own tools, so
   they are shown in the panel but not journaled to disk (section 4.7).
4. **Make durable files atomic.** Every durable write goes through one
   atomic-write seam.

## 2. Projects and dependency direction

```
                       HoardPad.App            Avalonia UI, view models,
                (the only Avalonia reference)  composition root
                             |
      +----------------------+----------------------+
      |                      |                      |
HoardPad.Host          HoardPad.Core.Ai      HoardPad.Core.Checklist
(OS code, I/O)         (AI logic)            (.hoard model + serializer)
      |                      |                      |
      +----------------------+----------------------+
                             |
                        HoardPad.Core
        (sandbox policy, seam interfaces, record models,
         session/recovery and undo decision logic)
```

| Project | Owns |
|---|---|
| `HoardPad.Core` | `SandboxPolicy`, `NormcasedPath`, the seam interfaces (in the `HoardPad.Core.Host.Abstractions` namespace), the file-operation, activity-log, and undo-journal record models, session reconciliation, undo-reverse decisions, and the encoding detect-then-preserve decision. |
| `HoardPad.Core.Ai` | Cap accounting and clamping, the destructive-operation classifier, the shared file-operation service (`AiFileOpsService`), the union sandbox gate for per-file grants, the `claude -p` launch contract, `stream-json` parsing, the liveness match, and the tool-call loop logic. |
| `HoardPad.Core.Checklist` | The `.hoard` model, loader, deterministic serializer, hierarchy helpers, and display-mode mapping. |
| `HoardPad.Host` | `AtomicFileStore`, `PathResolver`, `AgentBackend`, `ToolPermissionHookHost`, `HookObservationChannel`, `CapStateStore`, `BeforeImageReader`, `FileGrantsWriter`, `AppDataPaths`, `EncodingDetector`, `SystemTimeProvider`. |
| `HoardPad.App` | Everything Avalonia: editor, Markdown renderer, checklist control, AI panel, settings, theming, session service, and the DI composition root. |
| `HoardPad.SelfTestStandIn` | A small executable the startup self-test launches in place of the real `claude` CLI (section 4.6). |
| `harness/HoardPad.Bench` | External launcher that times a published build. It has no project reference to the app. |
| `harness/HoardPad.AcceptanceHarness` | The AI-safety acceptance gate (section 4.8). |

Rules that the build enforces:

- `HoardPad.Core` references only the .NET base class library and
  `System.Text.Json`. A banned-symbol analyzer makes `DllImport`,
  `DateTime.Now`, `DateTime.UtcNow`, `System.IO.File`, and
  `System.IO.Directory` build errors in `Core`, `Core.Ai`, and
  `Core.Checklist`. An architecture test in `HoardPad.Core.Tests` (NetArchTest)
  asserts that `Core` references no host or Avalonia assembly.
- The arrow never reverses. `Core.Ai` and `Core.Checklist` depend on `Core`
  and do not reference each other. `Host` depends on `Core`. Only `App`
  depends on all four.
- Only `HoardPad.Host` contains operating-system checks and platform-specific
  calls. One source tree compiles for both runtime identifiers.
- Time comes from an injected `TimeProvider`. Every timeout and debounce (the
  approval no-reply timer, the lock spin, folding, item-text coalescing)
  consumes it, so tests use a fake clock rather than real sleeps.

The seam interfaces live in `Core` even though their namespace contains the
word `Host`. This keeps the dependency arrow pointing toward `Core`. The
concrete classes are in `HoardPad.Host` (ARCH §1.2).

### Seams

| Concern | Interface (in `Core`) | Implementation (in `Host`) |
|---|---|---|
| Durable file writes and append logs | `IAtomicFileStore` | `AtomicFileStore` |
| Path resolution, symlinks, normalization | `IPathResolver` | `PathResolver` |
| Launching the agent and killing its process tree | `IAgentBackend` | `AgentBackend` |
| Hook settings and the startup self-test | `IToolPermissionHookHost` | `ToolPermissionHookHost` |
| Live hook observation and approval | `IHookObservationChannel` | `HookObservationChannel` |
| Per-conversation cap state | `ICapStateStore` | `CapStateStore` |
| Before-image capture for replay | `IBeforeImageReader` | `BeforeImageReader` |
| Per-file write grants for the hook | `IFileGrantsWriter` | `FileGrantsWriter` |
| App-data locations and permissions | `IAppDataPaths` | `AppDataPaths` |
| Character-set detection | `IEncodingDetector` | `EncodingDetector` |
| Wall clock | `TimeProvider` | `SystemTimeProvider` |

File I/O belongs to `Host`: every durable write goes through
`IAtomicFileStore`, including `settings.json`, `session.json`, the activity
log, and `.hoard` files. Process launch belongs to `Host` (`AgentBackend`).
Settings are modeled in `HoardPad.App` (`HoardPadSettings`, `SettingsStore`),
and `SettingsStore` is the single writer of `settings.json`. It writes
through the atomic-file seam.

`UTF.Unknown`, the charset detector, is referenced only by `Host`. `Core`
holds only the pure decision that consumes the detector's answer (ARCH §1.2).

## 3. The editor, Markdown, and `.hoard` checklists

### Editor

The editor surface is AvaloniaEdit with the TextMate integration. Grammars
load lazily on first use, so start-up does not pay for all 20. TOML is the
one language the stock grammar set lacks, so a small TOML grammar is bundled
as an embedded resource. Folding runs on a debounced background pass over an
immutable snapshot of the document, and the result is committed on the UI
thread.

Threading rule: the Avalonia UI thread owns all UI objects. Folder walks,
folding, the AI process and its `stream-json` parse, and persistence all run
off-thread and marshal results back (ARCH §3).

### Markdown pipeline

HoardPad parses Markdown with Markdig (core plus `UseAdvancedExtensions()`)
and walks the syntax tree to build a read-only tree of native Avalonia
controls. There is no web view, no browser runtime, and no network image
fetch. The preview is a separate visual tree, so it cannot edit the buffer.
Acceptance is feature presence and correct structure for the PRD's FR-16
feature list, not pixel parity with another editor (TD §1.6, §5.1).

### `.hoard` format and durability

A `.hoard` file is UTF-8 JSON with `schemaVersion: 2` (PRD §9.1, ARCH §5.1).
The array of items is **flat**. Nesting is derived from each item's
`parentId`, and `order` is contiguous `0..n-1` within each sibling group.

- **Atomic writes.** Every mutation rewrites the whole file through
  `IAtomicFileStore`: write a temporary file, flush it to disk, rename it over
  the target, and flush the directory. A kill leaves either the old complete
  file or the new complete file. A per-document write queue serializes writes.
  Structural changes (indent, outdent, collapse, delete) persist immediately.
  Item-text edits are coalesced for at most 150 ms.
- **Deterministic serialization.** Property order is pinned and the encoder
  is fixed, so a file already in canonical form is byte-identical after a
  load and save. Unknown keys at the root, settings, and item level are kept
  in an overflow bag and written back, so a file written by a newer version
  does not lose data when an older version saves it. The first save of a
  foreign or older file canonicalizes it once.
- **Schema versions.** A `schemaVersion: 1` file loads and migrates to 2 on
  its next save. A version higher than 2 is rejected, never silently
  downgraded. A dangling or cyclic `parentId` is repaired to top level, and
  nesting deeper than three levels is clamped on import.
- **Completion lock.** A checked parent means "completed" and locks its
  descendants. `effectiveEnabled` is a pure function: an item is interactive
  when every ancestor is unchecked. The control raises a property-change
  notification on every descendant, because Avalonia does not re-evaluate a
  child's enabled state when an ancestor changes.

### Other durable state

| State | Location | Durability |
|---|---|---|
| Settings | `settings.json` | Atomic replace. A missing or corrupt file falls back to defaults. |
| Session index | `session.json` | Atomic replace. It is a rebuildable hint. The recovery directory is the authority. |
| Recovery snapshots | machine-local state directory | Self-describing, written atomically. Never synced. A pure three-way reconciliation (snapshot, session tab, on-disk state) decides what to restore. |
| Activity log, undo journal | app-data directory | Append-only records framed as 4-byte length, JSON payload, 4-byte CRC-32. Each record is flushed before the next append. A torn tail is truncated on open (ARCH §5.3, §5.4). |
| Cap state | `capstate/` | One file per conversation, guarded by an exclusive-create lock (section 4.4). |

## 4. The AI safety spine

### 4.1 Two backend shapes, one file-operation service

The design has two backend archetypes behind one selector (ARCH §4.1):

- **Archetype A, a delegated agent.** The `claude -p` CLI runs its own tool
  loop. HoardPad launches it, gates each tool call with a hook, and watches.
  **This is the only archetype with a working transport in 1.0.0.**
- **Archetype B, a driven completion.** HoardPad owns the loop: it defines the
  tools, parses the model's tool-call requests, and performs each operation
  itself. This is the Anthropic API and the local Ollama or LiteLLM options.
  The tool-call parsing and the shared service exist and are tested. The
  network transport does not exist in 1.0.0, and the app routes every turn
  through Archetype A.

Both archetypes use one shared file-operation service in `HoardPad.Core.Ai`.
For each operation it: (1) resolves the target and checks the sandbox;
(2) for a destructive operation, writes and flushes a before-image and journal
entry **before** changing the target; (3) applies the change through the
atomic-file seam; and (4) appends the activity-log record and undo-journal
entry. For Archetype A, the service executes only replayed proposals, and
only those writes reach the on-disk journal and log. Live writes by the agent
do not go through the service. The hook is the live enforcer.

Enforcement strength differs by archetype and the design says so plainly
(ARCH §4.1). Archetype B would be per-operation atomic, because HoardPad is
the writer. Archetype A is observe-and-veto, with a narrow time-of-check to
time-of-use window.

### 4.2 The sandbox

`SandboxPolicy.Evaluate(root, candidate)` is a pure function in `Core`. It
takes a root and a candidate that the host has already resolved and
normalized, and answers allow or deny:

- It allows the root itself or a strict descendant, comparing at path-segment
  boundaries. `/work/proj` does not contain `/work/proj-evil`.
- It throws on a degenerate root (default, empty, or a bare separator such as
  `/`), because a bad root would affect every later decision. It denies a
  candidate that contains a `..` segment or mixes separator styles.
- It does no I/O and no case folding. Resolution is the host's job:
  `PathResolver` follows symlinks, makes paths absolute, expands Windows 8.3
  short names, and normalizes case. The hook script mirrors this logic and
  the acceptance harness asserts that the two agree.

The open-folder root is resolved once. A moved, renamed, or deleted root
triggers a re-prompt before write access arms.

**Per-file grants.** The AI panel can include open documents that live
outside the open folder as context for a turn. For such a document you
included, the sandbox is widened for that **one exact path** and nothing
else. A grant matches only when the resolved path equals the granted path.
It has no prefix match and no descendants. A granted write is still subject to
the per-turn caps, and an absent or faulty grant file means no grants, so the
sandbox reverts to the root alone. The widening exists in both the
`Core.Ai` union gate and the hook, and is additive to the checks above (see
the design record, PRD §6.6).

### 4.3 The deny-by-default hook

`HoardPad.Host.ToolPermissionHookHost` builds a `--settings` JSON that
registers `hooks/deny_by_default.py` as a PreToolUse hook matching every tool,
with a long timeout. The Claude Code CLI fails open on a hook timeout, so
HoardPad does not rely on the hook's own timeout. Its process-tree kill is
the fail-closed backstop.

The agent launch contract (`ClaudeLaunchContract`) is:

```
-p --output-format stream-json --verbose --include-partial-messages
--max-budget-usd <ceiling> --settings <hook json>
--tools "Write,Read,Edit" --strict-mcp-config
```

The working directory of the process is the open-folder root. There is no
`--cwd` flag. `LaunchArgsSafety` refuses an argument list that contains
`--cwd`, `--dangerously-skip-permissions`, or a `bypassPermissions` token. It
is checked when the request is built and again at the spawn boundary.

The hook:

- Allows only `Read`, `Edit`, and `Write`. Every other tool, including shell,
  sub-agents, and MCP tools, is denied.
- Allows `Edit` and `Write` only when the resolved target is inside the
  sandbox (or exactly matches a per-file grant) and the per-turn caps allow
  it.
- Denies on any error: unknown tool, missing, locked, or malformed cap state,
  a path outside the root, a degenerate root, an exception, or a Python
  version below the floor on Windows.
- Answers with the nested `hookSpecificOutput` / `permissionDecision` JSON the
  CLI honors. Exit code 2 is the minimal fallback. The older top-level form is
  silently ignored by the CLI, so the hook never uses it.

### 4.4 Caps and cap state

Per turn, by default (`CapConfig.Pinned`):

| Cap | Default |
|---|---|
| Files created | 25 |
| Destructive operations (overwrite, delete) | 10 |
| Distinct files touched | 20 |
| Size-change threshold that makes an overwrite "destructive" | 80% |

These defaults are fixed in 1.0.0. The hook reads overrides from
`HOARDPAD_*` environment variables (for example `HOARDPAD_FR11_CAP`), but
production code never sets them, and `AiSessionBinder` passes
`CapConfig.Pinned`. The cap fields in Settings are validated (values that
would disable the backstop are rejected), but they change only the panel's
cap meter. This is a known bug. The counters are keyed to the **user-initiated
conversation**, not to a single agent invocation. A continuation turn
resumes the same counters, and only a new conversation resets them. A
per-invocation counter would re-arm on every continuation. The state lives in
one file per conversation under `capstate/`, guarded by an exclusive-create
lock (not `fcntl`, which Windows lacks). The hook reads it and the host
writes it.

### 4.5 Observation channel and approve-each

When write access is armed, each turn opens a per-turn endpoint (a Unix domain
socket on Linux, a named pipe on Windows). The hook connects to it for each
tool call and reports the operation, so the panel can show live chips and cap
meters. The channel can only **narrow** a decision. It can turn an allow into
a deny, never a deny into an allow.

- In `approve-each` mode, the hook blocks until the host returns the user's
  answer. If the channel is unreachable, malformed, closed, or times out, the
  hook denies.
- In `auto-apply` mode, a failed report costs only a chip. The hook's own
  verdict stands.
- If an approval goes unanswered past the no-reply timeout (60 seconds by
  default), HoardPad denies the operation, kills the agent's process tree, and
  parks the captured proposal in a pending list. You can apply or discard it
  later.
- **Liveness check.** HoardPad matches each write tool-use in the agent's
  `stream-json` output against an observed hook callback. A write with no
  matching hook call means the hook was bypassed or silently not running.
  HoardPad then degrades fail-closed: it stops trusting write agency for the
  rest of the turn (PRD FR-10(c)).

### 4.6 Startup self-test and fail-closed degrade

The Claude Code CLI silently ignores an invalid hook configuration, so a
broken setup would look like a working one. HoardPad therefore runs a
self-test before enabling write access. It builds the same `--settings` JSON
production uses, launches a stand-in agent (`HoardPad.SelfTestStandIn`) with
it, has the stand-in attempt an out-of-sandbox write through the real hook,
and passes only if **no file appears and a denial is recorded**. A second
self-test covers the channel-failure kill path. If either test fails, or the
Python interpreter is missing or below the Windows floor, the panel falls back
to read-only and shows a "sandbox could not be armed" state. Changing the
agency mode re-runs the gate.

The stand-in exists because the real CLI cannot take part in the
descriptor protocol the self-test uses. The self-test proves the wiring is
correct. It cannot prove a particular CLI version honors the hook; the
project verifies that in a manual live check (see section 6).

Other degrade triggers: a home-directory or very large root (more than 2,000
files by default) switches `auto-apply` to `approve-each`, unless you turn the
protection off. A clobbering operation or a tripped cap also forces
confirmation.

### 4.7 Visibility, undo, activity log, and replay

What 1.0.0 does today, and what it only has the pieces for:

- **Live AI writes.** The `claude -p` agent writes through its own `Edit` and
  `Write` tools, gated by the hook. HoardPad observes each operation over the
  hook observation channel and shows it in the AI panel's in-session activity
  view (applied or blocked). These writes are **not** written to the on-disk
  undo journal or activity log, and the UI has no file-undo action.
- **What you can undo.** When the AI edits a document that is open in a tab,
  the editor buffer is updated through the normal editor undo history, so
  Ctrl+Z reverts it. HoardPad cannot undo AI-created files, or edits to files
  that are not open in a tab.
- **What exists but is not connected to live writes.** The shared
  file-operation service records a before-image and a journal entry, and
  appends an activity-log record. The undo decision (`UndoReverseDecision`)
  is a pure function of the journal entry and the file's current hash, with
  a hash check before restoring and a skip for oversized before-images. The
  app calls the service only on the replay path. It never calls the undo
  decision, and nothing sets the "undone" badge.
- The activity-log record, when written, holds timestamp, request id,
  backend, configured and effective agency mode, downgrade reason, operation,
  paths, outcome, byte counts, and an undo reference. It never contains file
  contents or API keys.
- **Replay.** A parked proposal is applied through the shared file-operation
  service as a single user-approved operation. It runs the full sandbox check
  and writes the before-image and log. This is the only path that writes them. It does not count against the per-turn
  caps, because you authorized that one operation.

### 4.8 Acceptance gate

The harness in `harness/HoardPad.AcceptanceHarness` runs the production hook
as a subprocess with crafted tool inputs (create, edit, out-of-sandbox write,
destructive overwrite). It asserts the hook's decision and the resulting
on-disk state, activity-log entry, and undo-journal entry. It includes a
cross-continuation step that proves caps do not reset. A separate gate drives
the shared service with the operations Archetype A cannot reach (delete,
clobbering move). The build mutation-tests this gate: deliberately breaking a
safety property must make a gate fail (see
[how-it-was-built](how-it-was-built.md)).

## 5. Prompt-injection scrubbing

[DragonScrub](https://github.com/builtwithclaudetech/dragonscrub) is a
deterministic scrubber with no model calls. HoardPad uses it at two sinks:

1. **Re-injected conversation text.** HoardPad rebuilds context for each turn
   by prepending the panel's earlier messages, bounded by a turn count and a
   size cap. Each kept line is scrubbed at `STANDARD` level, and the prompt
   block is built from the cleaned text. The stored transcript is not
   changed.
2. **The settings-change block in an assistant reply.** The AI may propose a
   setting change in a fenced `hoardpad-settings` block. HoardPad parses the
   proposal and routes it through the approval gate. When the turn ends, on
   every path including cancel and error, it removes the block from the stored
   reply with DragonScrub's block redaction. The block is then not shown after
   the turn and is never re-injected into a later prompt. The allow-list of
   settings the AI may propose is broad (it includes the backend selection,
   the four cap values, and the CLI path, and in `auto-apply` mode they apply
   without a prompt; see [`SECURITY.md`](../SECURITY.md)). The AI cannot
   propose changes to the API key, model, effort, agency mode, or large-folder
   protection.

When a scrub redacts something, HoardPad shows a neutral chip and appends the
rule categories (not the text) to a local log. HoardPad does not embed
document text in prompts itself. The agent reads files through its own
tools, and that text is **not** scrubbed. See the threat model below.

## 6. Threat model summary

### What the sandbox defends against

- A prompt-injected or mistaken agent writing, deleting, or creating files
  outside the open folder, by any path trick (`..`, absolute path, symlink,
  Windows short name, case difference).
- An agent using any tool other than `Read`, `Edit`, and `Write` (no shell,
  no sub-agents, no MCP tools), or launching with permission-bypass flags.
- A runaway agent: per-turn caps on creates, destructive changes, and
  distinct files, a spending ceiling (5.00 US dollars per turn by default),
  and a process-tree kill.
- A silently disarmed sandbox: the startup self-test, the liveness check, and
  the fail-closed degrade to read-only.
- Invisible AI changes: the panel shows each observed operation, and the
  liveness check flags a write the hook never reported. (This is visibility,
  not recovery. See "What it does not defend against".)

### What it does not defend against

- **Damage inside the open folder.** The AI can edit, overwrite, and delete
  files inside the root, up to the caps. In 1.0.0, live AI writes are not
  journaled to disk and HoardPad cannot undo them, except through Ctrl+Z for
  a document open in a tab. Opening a narrow folder, and keeping your own
  backups or version control, is the main protection. HoardPad warns on very
  large roots, but you can turn that off.
- **A time-of-check to time-of-use window for Archetype A.** The hook decides
  before the agent writes. A path swapped between the decision and the write
  (for example by another local process) is not prevented by the hook alone.
- **Whether the CLI honors the hook.** The self-test checks HoardPad's
  wiring. A CLI version that silently stopped honoring PreToolUse hooks is
  detectable only through the liveness check and the manual live verification.
- **Reading outside the folder.** The sandbox governs writes. The
  agent's `Read` tool is allowed by the hook; the working directory is the
  root, but the hook does not restrict reads to it.
- **Text the agent reads from your files.** That text is not scrubbed. A
  poisoned file can still influence the model. The defense is that the model's
  influence is limited to what the hook, sandbox, and caps allow.
- **The AI backend itself.** What Anthropic, or a local model, does with the
  content you send it is outside HoardPad's control.
- **Local attackers running as you.** Anything that can edit
  `settings.json` or the hook script already has your privileges.
- **Windows runtime behavior.** The Windows hook, Job Object kill, durable
  first-write path, and file-permission restriction have not been verified on
  Windows hardware (TD §4.8, ARCH §4.7, §7.6).

## 7. Build and publish

- Central package management pins every version in `Directory.Packages.props`.
  Avalonia is pinned at 11.3.6 while AvaloniaEdit 11.4.1 pulls Avalonia 11.4
  packages transitively. The design record documents that this mixed pairing
  was tested at run time (ARCH §2.2).
- `dotnet publish -r <rid> --self-contained true -p:PublishReadyToRun=true`
  produces a self-contained build. A target in `Directory.Build.targets`
  fails any RID publish that omits `PublishReadyToRun=true`. Trimming is off,
  because Avalonia and TextMateSharp resolve types and grammars by name and a
  trimmer could drop one silently.
- The hook script is copied into the publish output under `hooks/`. The app
  resolves it relative to the application directory, and the stand-in
  executable sits next to it.
- `dotnet build -r win-x64` fails with NETSDK1150, because the app references
  the non-self-contained stand-in. Use `dotnet publish` for Windows.
- Code signing, store packaging, and auto-update are out of scope for 1.0
  (ARCH §7.5).
