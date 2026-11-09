# Changelog

All notable changes to HoardPad are documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-11-09

Initial public release.

### Added

- **Tabbed text and code editor** built on Avalonia and AvaloniaEdit.
  Includes session restore (open tabs, unsaved scratch buffers, unsaved
  edits, active tab, cursor position, open folder), auto-save, a folder tree,
  and tab drag-to-reorder.
- **Syntax highlighting** with TextMate grammars for 20 languages: Markdown,
  HTML, JavaScript, CSS, Python, C#, XML, JSON, SQL, Rust, TypeScript, YAML,
  TOML, Shell/Bash, PowerShell, Dockerfile, C, C++, Java, and Go. You can
  also set the language by hand from the Language menu.
- **Code folding** with brace-based and indentation-based strategies.
- **Find and Replace** with literal or regular-expression matching, case and
  whole-word toggles, and three scopes: the current file, all open files, and
  the open folder.
- **Encoding detection and preservation.** HoardPad detects the file
  encoding on open, keeps the original encoding and byte-order mark on save,
  and lets you reinterpret a file in another encoding.
- **Markdown source and preview.** Colorized Markdown source, and a
  read-only rendered preview (CommonMark plus tables, task lists, footnotes,
  autolinks, and strikethrough) that you toggle with Ctrl+Shift+V. The
  preview is a native Avalonia control tree. It does not use a web view.
- **`.hoard` checklists**, a native document type: nested items (three
  levels), collapse and expand, three checked-item display modes
  (strikethrough in place, move to bottom, hide when checked), and
  completion locking of a checked parent's subtree. Item order and check
  state are written to disk atomically on every change.
- **Agentic AI panel** (in-window overlay, opened from the AI menu or the
  "AI ▸" toolbar button) that can read your open documents and the open
  folder, and can create and edit files in it through the agent's `Read`,
  `Edit`, and `Write` tools. It cannot copy, move, rename, or delete files.
  Backend: Claude Code headless (`claude -p`). See "Known limitations" below
  for the status of the Anthropic API and local LLM backend options.
- **Open-folder sandbox for AI file operations.** AI writes are confined to
  the resolved open-folder root. The panel shows each observed operation as
  applied or blocked for the session. Three agency modes: auto-apply
  (default), approve-each, and read-only. Per-turn caps (fixed defaults: 25
  created files, 10 destructive operations, 20 distinct files) limit what an
  AI turn can do. An AI edit to a document open in a tab goes into the editor
  buffer and Ctrl+Z reverts it.
- **Fail-closed startup self-test.** HoardPad enables AI write access only
  after a self-test proves that the deny-by-default hook blocks an
  out-of-sandbox write. If the test fails, the panel stays read-only.
- **Prompt-injection scrubbing** through
  [DragonScrub](https://github.com/builtwithclaudetech/dragonscrub) for text
  that HoardPad re-injects into a later prompt, and for the settings-change
  block in assistant replies.
- **Dark and light themes**, configurable editor, interface, and AI-panel
  fonts, and a single human-readable `settings.json`.
- **No telemetry in the app.** HoardPad makes no network calls of its own.
  (Avalonia's build tooling collects build telemetry at build time; the
  README explains how to opt out.)
- **Self-contained builds** for Linux (x64) and Windows (x64), published with
  ReadyToRun compilation. Linux is the primary, fully tested platform.
  Windows builds have had far less runtime testing.

### Known limitations

- Only the Claude Code backend has a working transport. The Anthropic API and
  local LLM options are saved settings only.
- Live AI file operations are not journaled to disk and cannot be undone,
  except through editor undo (Ctrl+Z) for a document that is open in a tab.
  AI-created files and edits to files not open in a tab are not reversible by
  HoardPad. The on-disk undo journal and activity log are written only when
  you re-apply a parked or approved operation.
- The cap fields in Settings change only the AI panel's cap meter. The hook
  enforces the fixed defaults (a known bug).
- The `ClaudeCliPath` setting is not honored. HoardPad finds `claude` on
  `PATH`, or at `~/.local/bin/claude` on Linux.
- The `AiPanelHotkey` setting (default Ctrl+Shift+A) is stored but not bound
  to a key. Use the AI menu or the "AI ▸" button.
- The AI can propose changes to many settings, including inert ones (caps, CLI
  path, backend selection), and in auto-apply mode they apply without a
  prompt. The agent's `Read` tool is not limited to the open folder.
- On Windows, owner-only file permissions are not applied. On Linux, HoardPad
  sets mode 700 on the app-data directory it creates, but sets no mode on the
  files inside.

### Known issues

- Windows runtime behavior of the AI safety hook, process-tree kill, and
  power-cut durability has not been verified on Windows hardware. See the
  README and the design record for details.
- `dotnet build -r win-x64` fails with NETSDK1150. Use
  `dotnet publish ... --self-contained true` for Windows.

[Unreleased]: https://github.com/builtwithclaudetech/hoardpad/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/builtwithclaudetech/hoardpad/releases/tag/v1.0.0
