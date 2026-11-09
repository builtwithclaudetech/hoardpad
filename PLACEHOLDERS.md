# Placeholders and user-supplied configuration

HoardPad ships with no secrets, no API keys, no endpoints, and no
account-specific values. Everything below is something **you** supply on your
own machine. Nothing here is committed to this repository.

Placeholders in this file use the form `CHANGE_ME` or a generic default
such as `http://localhost:11434`. Replace them with your own values.

## AI backend configuration

You configure the AI backend in **Settings** inside HoardPad. Settings are
saved to `settings.json` (see [Where settings and data live](#where-settings-and-data-live)).
The JSON keys below are the exact property names in that file.

| What | Settings key | Default | Notes |
|---|---|---|---|
| Active AI backend | `ActiveBackend` | `claude-cli` | One of `claude-cli`, `anthropic-api`, `local`. In 1.0.0, only `claude-cli` has a working transport. See "Known limitations" in the README. |
| Path to the `claude` CLI | `ClaudeCliPath` | none | **Not honored in 1.0.0.** The Settings field is saved, but the AI turn runner does not pass it to the launcher, so it has no effect. HoardPad looks for `claude` as follows. On Linux: `claude` on `PATH`, then `~/.local/bin/claude` (it must have an execute bit). On Windows: `claude.cmd` on `PATH`, then `claude.exe` on `PATH`. If none is found, the AI turn fails with a "could not resolve the claude CLI" error. Put `claude` on `PATH` for the GUI session. |
| Anthropic API key | `AnthropicApiKey` | none | Use `CHANGE_ME` in examples. Stored in plain text in `settings.json`. HoardPad does not read it from an environment variable. |
| Anthropic model name | `AnthropicModel` | none (backend default) | For example, a model ID from your Anthropic account. |
| Local LLM endpoint | `LocalEndpoint` | none | The Settings field shows the hint `http://localhost:11434` (the usual Ollama address). For LiteLLM, use the address of your own proxy. |
| Local LLM model name | `LocalModel` | none (backend default) | The model name your Ollama or LiteLLM server serves. |
| Model override for the Claude Code backend | `ModelId` | none (CLI default) | Passed to the `claude` CLI as `--model`. |
| Reasoning effort for the Claude Code backend | `EffortLevel` | none (CLI default) | Passed to the `claude` CLI as `--effort`. |

### The Claude Code CLI (default backend)

The default backend runs the `claude` command-line tool in headless mode
(`claude -p`).

1. Install the Claude Code CLI by following Anthropic's own instructions.
2. Make sure `claude` is on your `PATH` (or, on Linux, at
   `~/.local/bin/claude`). Setting `ClaudeCliPath` does not help in 1.0.0.
3. Run `claude` once in a terminal and sign in. HoardPad does not store or
   manage Claude Code credentials. The CLI handles its own authentication.

The design targets Claude Code CLI 2.1.179 or later.

### Python for the safety hook

HoardPad runs `hooks/deny_by_default.py` through a Python interpreter. It
looks on `PATH` for `python3` then `python` on Linux, and for `python`, `py`,
then `python3` on Windows. On Windows, Python 3.12 or later is required.
Without a suitable interpreter, AI write access stays off.

## Per-turn AI limits

The cap values below appear in Settings and in `settings.json`, but **they
do not change what the safety hook enforces in 1.0.0**. The hook always
applies its fixed defaults (25 created files, 10 destructive operations, 20
distinct files, 80 percent magnitude). The hook can read overrides from
`HOARDPAD_*` environment variables, but HoardPad does not set them, and the
session passes the pinned defaults. Changing a cap in Settings changes only
the cap meter in the AI panel. This is a known bug. HoardPad rejects cap
values that would turn off the safety backstop.

| What | Settings key | Default | Effect in 1.0.0 |
|---|---|---|---|
| Agency mode | `AgencyMode` | `auto-apply` (also `approve-each`, `read-only`) | Enforced. |
| Max files created per turn | `Caps.CreateCap` | 25 | Panel cap meter only. |
| Max destructive operations per turn | `Caps.DestructiveCap` | 10 | Panel cap meter only. |
| Max distinct files touched per turn | `Caps.FilesTouchedCap` | 20 | Panel cap meter only. |
| Destructive size-change threshold (percent) | `Caps.DestructiveMagnitudePercent` | 80 | Panel cap meter only. |
| Seconds to wait for an approve-each answer | `NoReplySeconds` | 60 |
| File-count threshold for the large-folder check | `LargeRootThreshold` | 2000 |
| Downgrade large or home folders to approve-each | `ConfirmLargeHomeFolder` | `true` |
| AI panel hotkey | `AiPanelHotkey` | `Ctrl+Shift+A` (stored and editable, but **not bound to any key in 1.0.0**. Use the AI menu or the **AI ▸** button.) |

Each AI turn also runs with a spending ceiling of 5.00 US dollars, passed to
the `claude` CLI as `--max-budget-usd`. This value is set in code and is not a
user setting in 1.0.0.

## Where settings and data live

HoardPad stores everything in a per-user directory named `HoardPad`.

| What | Linux | Windows |
|---|---|---|
| App data root (settings, session, activity logs, undo journal, cap state, scrub log) | `$XDG_DATA_HOME/HoardPad`, or `~/.local/share/HoardPad` if `XDG_DATA_HOME` is unset or not absolute | `%LOCALAPPDATA%\HoardPad` |
| Recovery snapshots (machine-local, never synced) | `$XDG_STATE_HOME/HoardPad/recovery`, or `~/.local/state/HoardPad/recovery` | `%LOCALAPPDATA%\HoardPad\recovery` |

Inside the app data root:

```
settings.json                 your settings (may contain your API key)
session.json                  open tabs and folder, for session restore
activity/<sessionId>.log      activity log (written only when you re-apply a
                              parked or approved AI operation; live AI
                              writes are not recorded here)
undo/                         undo journal and before-image copies (same
                              limit as the activity log)
capstate/                     per-conversation AI cap counters
scrub-findings.ndjson         record of text DragonScrub redacted
```

Two settings-file notes:

- If `settings.json` is missing or corrupt, HoardPad falls back to the
  defaults. It does not crash.
- `settings.json` can hold your Anthropic API key in plain text. Do not
  commit it, paste it into an issue, or share it.

## Environment variables HoardPad reads

| Variable | Used for |
|---|---|
| `PATH` | Finding `claude` and the Python interpreter. |
| `XDG_DATA_HOME`, `XDG_STATE_HOME` | Linux data and recovery locations. Ignored if not absolute paths. |
| `HOME` (Linux) | Fallback for the locations above. |
| `LOCALAPPDATA` (Windows) | Windows data location. |

HoardPad also sets `HOARDPAD_*` variables (for example `HOARDPAD_SANDBOX_ROOT`)
on the AI agent process it launches. These carry the sandbox root and cap
state to the safety hook. You do not set them yourself.

## What is not committed

- No API keys, tokens, or passwords.
- No `settings.json` or any other per-user data.
- No signing certificates. Release builds are not code-signed.
