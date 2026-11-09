# Security Policy

## Supported versions

Security fixes are applied to the `main` branch. Only the latest release is
actively maintained.

## Reporting a vulnerability

If you discover a security vulnerability in HoardPad, please report it
through **GitHub Security Advisories** rather than a public issue:

1. Go to the [Security tab](https://github.com/builtwithclaudetech/hoardpad/security)
   of this repository.
2. Click **Report a vulnerability**.
3. Fill in the details. A minimal reproduction helps most: the agency mode,
   the open-folder root, the file operation that misbehaved, and the
   operating system.

**Do not disclose the vulnerability publicly until it has been addressed.**

If you are not sure whether an issue is a security vulnerability, report it
privately anyway. It is easier to downgrade a report than to un-disclose one.

## In scope

HoardPad lets an AI agent act on your files, so the security boundary is the
AI file-operation sandbox. These reports are in scope:

- **AI sandbox escape.** Any write or file creation performed by the AI
  (the agent's `Write` and `Edit` tools) that falls outside the resolved open-folder root, including
  through `..` segments, absolute paths, symlinks, Windows 8.3 short names,
  or case differences. The one intended exception is a per-file grant: an
  open document outside the folder that you included in the AI panel can be
  written, at that exact path only. A grant that matches more than its exact
  path is in scope.
- **Hook bypass.** Any way to get the AI agent to run a tool other than the
  allowed `Read`, `Edit`, and `Write`, or to make `hooks/deny_by_default.py`
  allow an operation it should deny, or to fail open instead of closed.
- **Unshown or misreported file operation.** An AI file operation that the
  hook allowed but that the AI panel did not show, or that the panel showed
  as blocked although it was applied. Also in scope: an AI edit to a document
  that is open in a tab that Ctrl+Z cannot revert. (Live AI operations are not
  otherwise logged to disk or undoable in 1.0.0. See "Known limitations".)
- **Cap bypass.** A way to exceed the per-turn create, destructive, or
  distinct-files caps, or to reset them within one conversation.
- **Self-test bypass.** A way to enable AI write access when the startup
  self-test did not pass.
- **Scrub bypass at a documented sink.** Prompt-injection text that survives
  [DragonScrub](https://github.com/builtwithclaudetech/dragonscrub) at
  `STANDARD` level at one of the two sinks HoardPad scrubs (see
  "Security model" below), where the text is within DragonScrub's documented
  detection scope.
- **Crash-induced `.hoard` data loss.** A crash, kill, or power loss that
  leaves a `.hoard` file torn, truncated, or with lost item order or check
  state, when the last mutation had completed.
- **Credential exposure.** A path that writes your Anthropic API key to a
  log, a transcript, or anywhere outside `settings.json`.

## Out of scope

- **Things you explicitly allowed.** In `auto-apply` mode, the AI can change
  files inside the open folder without asking, up to the caps. In
  `approve-each` mode, an operation you approve is applied. These are
  settings, not vulnerabilities. Read-only mode semantics are likewise
  as documented: the AI can read but not write.
- **Reads outside the open folder.** The sandbox governs writes. The hook
  always allows the agent's `Read` tool, and does not restrict it to the open
  folder. Reports of this are welcome as design feedback.
- **Choosing a very broad open folder.** HoardPad downgrades a home-directory
  or very large root to `approve-each` by default, but you can turn that
  protection off in settings. The sandbox is only as narrow as the folder
  you open.
- **The behavior of third-party AI backends.** What the Claude Code CLI, the
  Anthropic API, or a local model decides to do or say is outside HoardPad's
  control. HoardPad's boundary is what those tools are allowed to do to your
  files.
- **Prompt-injection classes DragonScrub does not claim to catch.** See the
  DragonScrub README: acrostics, multi-turn escalation, and purely semantic
  manipulation. Reports of these are welcome as documentation issues.
- **Attacks that need local code execution as your user.** For example,
  editing `settings.json` or the hook script directly.

## Response timeline

- **Acknowledgment**: within 7 days of the report.
- **Initial assessment**: within 14 days.
- **Fix**: depends on severity. Confirmed sandbox escapes, hook bypasses,
  and data-loss defects are prioritized.

## Security model

For the full design, see [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

- **No telemetry and no network calls of its own.** HoardPad contains no
  HTTP client. The only outbound traffic is whatever the AI backend you
  configure makes. The default backend, the Claude Code CLI, is a local
  process that talks to Anthropic on its own.
- **Sandbox by construction.** AI file operations are checked against the
  open-folder root by a pure containment function after the path is resolved
  (symlinks followed, made absolute, Windows short names expanded, case
  folded). A degenerate root such as `/` is rejected, and a `..` segment in a
  candidate path is denied.
- **Deny by default.** The AI agent is launched with only the `Read`, `Edit`,
  and `Write` tools offered, MCP configuration stripped, and a PreToolUse hook
  (`hooks/deny_by_default.py`) that allows only those three tools and only
  in-sandbox, under-cap writes. Every other tool is denied. The launch code
  refuses to start an agent whose arguments contain `--cwd`,
  `--dangerously-skip-permissions`, or a `bypassPermissions` token.
- **Fail closed.** Missing, locked, or malformed cap state, an unknown tool,
  an unresolvable path, a hook exception, or a broken approval channel all
  produce a deny. HoardPad enables AI write access only after a startup
  self-test shows that the hook blocks an out-of-sandbox write. HoardPad
  kills the agent's whole process tree on cancel or when an approval request
  goes unanswered.
- **Observe-and-veto, not per-operation execution.** For the default Claude
  Code backend, the agent performs the write and the hook can only allow or
  deny it. This leaves a narrow time-of-check to time-of-use window that a
  backend where HoardPad performs the write itself would not have. The design
  record discusses this trade-off (PRD §6.6, technical design §4).
- **Known verification gap.** The self-test proves that HoardPad wires the hook
  correctly. It cannot prove that a given version of the Claude Code CLI
  honors the hook at run time. That check is a manual step in the project's
  acceptance process. HoardPad's design targets Claude Code CLI 2.1.179 or
  later.
- **Scrubbing is narrow by design.** HoardPad does not place document text in
  a prompt itself. It uses DragonScrub at two sinks: prior conversation lines
  that it re-injects into a later prompt (`STANDARD` level), and the
  `hoardpad-settings` block in an assistant reply, which is removed from the
  stored reply when the turn ends so it is never re-injected. Text the agent reads from your files through its own tools is not
  scrubbed. The deny-by-default hook, the sandbox, and the caps apply no
  matter what the model was told.
- **Settings the AI can propose.** The AI can propose changes to a fixed
  allow-list of settings. The allow-list is broad. It includes fonts, font
  sizes, colors, theme, default language, and auto-save interval, and also
  `ActiveBackend`, `ClaudeCliPath`, `AnthropicModel`, `LocalEndpoint`,
  `LocalModel`, the four cap values (`CreateCap`, `DestructiveCap`,
  `FilesTouchedCap`, `DestructiveMagnitudePercent`), `NoReplySeconds`, and
  `AiPanelHotkey`. The change goes through the same agency gate as a file
  write, so **in `auto-apply` mode it is applied without a prompt**. The panel
  offers a "revert last settings change" command. Cap values that would
  disable the safety backstop are rejected. The AI can never propose a change
  to the API key, `ModelId`, `EffortLevel`, the agency mode, the large-folder
  protection, or the large-folder threshold. Several allow-listed fields are
  currently inert, which limits their impact: the cap fields change only the
  panel's cap meter, `ClaudeCliPath` is not honored, and backend selection
  does not change the transport (every turn uses the Claude Code CLI). Treat
  this as a gap in the design, not as a protection.
- **Local data and permissions.** Settings, session data, the undo journal,
  the activity log, and recovery snapshots are stored under your per-user
  data directory. On Linux, HoardPad creates the app-data root and the
  recovery directory with mode 700 (owner only) when it creates them. If the
  directory already exists, HoardPad leaves its mode alone. HoardPad sets no
  file mode on the files inside: the atomic file writer does not call chmod,
  so files get your default umask and are protected by the directory's
  mode. **On Windows, the owner-only restriction is not implemented in
  1.0.0**; the files rely on the default permissions of your user profile.
- **API key storage.** If you configure an Anthropic API key, HoardPad stores
  it in plain text in `settings.json`, which is the only place it is
  written. Treat that file as a secret. Do not commit it or share it.

## Known limitations

These are known gaps in 1.0.0. They are documented, not hidden.

- **Live AI file operations are not journaled to disk and cannot be undone**,
  except through editor undo (Ctrl+Z) for a document that is open in a tab.
  The agent writes through its own `Edit` and `Write` tools, and HoardPad
  observes each operation over the hook channel and shows it in the panel for
  the session. Only the replay path (re-applying a parked or approved
  operation) writes to the on-disk undo journal and activity log. Files the AI
  creates, and edits to files not open in a tab, are not reversible by
  HoardPad.
- **Per-turn caps are fixed.** The hook enforces 25 created files, 10
  destructive operations, 20 distinct files, and an 80 percent magnitude
  threshold. The Settings cap fields change only the panel's cap meter. This
  is a known bug.
- **`ClaudeCliPath` is ignored.** HoardPad locates `claude` through `PATH`
  (and `~/.local/bin/claude` on Linux).
- **The AI-panel hotkey is not bound.** The `AiPanelHotkey` setting is stored
  but not wired to a key. Open the panel from the AI menu or the **AI ▸**
  button.
- **Reads are not sandboxed**, and **the AI can change many settings without
  a prompt in `auto-apply` mode** (see "Settings the AI can propose").
- **The agent cannot copy, move, rename, or delete files**, because it has
  only `Read`, `Edit`, and `Write`. The shared file-operation service in the
  code contains those operations for the unconnected API and local backends.

## Dependencies

- **.NET 8** (self-contained in published builds).
- **Avalonia 11.3.6, AvaloniaEdit 11.4.1, TextMateSharp.Grammars 2.0.4,
  Markdig 0.45.0**: UI, editor, syntax highlighting, and Markdown parsing.
- **CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection**: view
  models and dependency injection.
- **UTF.Unknown, System.Text.Encoding.CodePages, System.IO.Hashing**: encoding
  detection and record checksums.
- **DragonScrub 0.2.0**: prompt-injection scrubbing. It is vendored in
  `packages/` and resolved through `NuGet.config`.
- **Python 3** is required at run time to execute the bundled hook script. The
  script uses only the Python standard library. On Windows, HoardPad requires
  Python 3.12 or later and does not enable AI write access below that.

See [`NOTICE`](NOTICE) for licenses.
