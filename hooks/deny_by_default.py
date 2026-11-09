#!/usr/bin/env python3
"""HoardPad production deny-by-default PreToolUse hook (Archetype A, M5.1).

This is the enforcement point of the AI sandbox (FR-24 / FR-26). It is the
production port of the SPIKE-3 / SPIKE-4 prototype (`spikes/spike-4/hooks/
deny_by_default.py`) to the cross-platform `O_EXCL` SpinLock + `normcase` form
(tech-design TD §4.8), with the containment decision tightened to MIRROR the
pure C# `SandboxPolicy.Evaluate` (src/HoardPad.Core/Safety/SandboxPolicy.cs)
EXACTLY — segment-boundary containment + degenerate-root fail-closed. The C#
self-test (M5.2) and the SC-5 harness (M9.3) drive THIS script and assert the
two languages agree.

PROTOCOL (claude 2.1.x PreToolUse, SPIKE-3 §"What does NOT work"):
  DENY  — exit 0 + nested JSON (preferred):
    {"hookSpecificOutput":{"hookEventName":"PreToolUse",
     "permissionDecision":"deny","permissionDecisionReason":"..."}}
  ALLOW — exit 0 + the same nested object with "permissionDecision":"allow".
  The deprecated top-level {"permissionDecision":"deny"} form is SILENTLY
  IGNORED — only the nested form (or exit code 2) denies. Exit code 2 is the
  minimal fallback (deny; stderr shown to the model) used only when the nested
  JSON cannot be produced (e.g. stdout is broken).

FAIL CLOSED, ALWAYS. Every unexpected condition — unknown tool, missing/locked/
malformed cap-state, path outside the sandbox, degenerate sandbox root, any
exception, any IO error — emits a DENY. There is no silent allow and no path
that lets an unhandled traceback be (mis)read by the host as an allow: the
top-level guard converts any escape into the exit-2 deny fallback.

CROSS-PLATFORM (TD §4.8 / SPIKE-4):
  - Exclusive lock via atomic os.open(O_CREAT|O_EXCL) (POSIX) / CREATE_NEW
    (Win32); 5 ms spin, 10 s hard timeout, unlinked in finally. NEVER fcntl
    (advisory + no Windows equivalent — the spike rejected it).
  - os.path.normcase on both sides of every path comparison (no-op on Linux;
    case-fold + separator-normalize on Windows).
  - Python 3.12+ is REQUIRED on Windows so pathlib.resolve() calls
    GetLongPathName and expands 8.3 short names (TD-1); below 3.12 the
    short-name sandbox-escape vector is open. The host's BuildHookSettings
    probes the floor (M5.2); this script also self-guards below.

CAP-STATE FILE (architecture §4.4): a HoardPad-owned, conversation-keyed,
O_EXCL-locked JSON file the HOST writes (M5.2) and this hook only READS. Shape =
the C# `CapState` record (HoardPad.Core.Ai.Caps.CapState) as serialized by
System.Text.Json (PascalCase keys). Missing / locked / malformed => fail closed.

Environment (wired by the host's --settings; absent in --self-test):
  HOARDPAD_CAP_STATE_FILE  Path to the host-owned cap-state JSON file.
  HOARDPAD_SANDBOX_ROOT    Resolved open-folder root; writes outside are denied.
  HOARDPAD_FILE_GRANTS     Path to a host-written, read-only-by-the-hook JSON
                           file (a JSON array of absolute path strings) naming
                           the per-file write grants for this turn (PANEL-A1 /
                           feature-ai-multidoc-panel §5.3 Path A). ABSENT => the
                           grant leg is entirely inert and Step 3 is byte-
                           identical to today. Present-but-faulty => empty grant
                           set (root-only fallback, FR-MD-10). Never mutated
                           during the turn (no O_EXCL needed); hook reads only.
  HOARDPAD_FR11_CAP        Per-turn create cap (default 25).
  HOARDPAD_FR38_CAP        Per-turn destructive cap (default 10).
  HOARDPAD_FR40_CAP        Per-turn distinct-files cap (default 20).
  HOARDPAD_FR38_X_PCT      FR-38 magnitude threshold % (default 80).
  HOARDPAD_TEST_MODE       "1" => debug lines to stderr.

PANEL-A1 per-file grant leg (feature-ai-multidoc-panel §5.3 Path A). PURELY
ADDITIVE and env-gated: engaged ONLY when HOARDPAD_FILE_GRANTS is present. When
absent the grant code is unreachable and decide()'s Step-3 containment is byte-
identical to before this leg existed (exactly what the SC-5 gates + the M5.2
self-test exercise). Unlike the M11 observation leg (which can only NARROW a
hook-allow to a deny), this leg WIDENS: it can turn a Step-3 root-deny into an
allow, but ONLY for a target whose resolved+normcased path is EXACTLY equal to a
resolved+normcased granted path (no prefix, no descendant — a file grant has no
children). It never widens the Step-1 allowlist, never bypasses the Step-4 caps
(a granted out-of-root write flows through FR-11/FR-38/FR-40 identically to an
in-root write, FR-MD-5), and never narrows anything. Because it widens, it is a
PROTECTED-ARTIFACT change requiring security + §8 protected review + an SC-5 re-
run. Fail-closed on every grant-file fault (FR-MD-10): missing / unreadable /
non-JSON / not-an-array / any-non-string element / any exception => EMPTY grant
set (root-only), never a wider set and never a raise that escapes decide().
"""

import json
import os
import pathlib
import socket
import sys
import time
from collections.abc import Mapping

# Allowlist principle (SPIKE-3 §B): the matcher is '*'; only these three tools
# can ever be allowed, and Write/Edit only when in-sandbox AND under cap. Every
# other tool (Bash, Agent (the Task alias), AskUserQuestion, ToolSearch, any
# unknown/mcp__* tool) is denied by default.
ALLOWED_TOOLS = frozenset({"Read", "Edit", "Write"})

# Python floor (TD-1): below 3.12 on Windows, resolve() does not expand 8.3 short
# names, opening a sandbox-escape vector. The host probes the floor, but the hook
# self-guards too — on Windows below 3.12 it fails closed unconditionally.
_PY_FLOOR = (3, 12)

# ---------------------------------------------------------------------------
# M11 hook-observation channel (docs/m11-channel-protocol.md — the byte-for-byte
# wire contract; docs/escalation-ai-live-write-path.md §4/§5 the binding spec).
#
# PURELY ADDITIVE. When HOARDPAD_OBSERVE_CHANNEL is ABSENT the hook runs its
# existing autonomous path UNCHANGED (the env-absent guard in main() is the only
# thing that engages this leg) — this is exactly what the 29 SC-5 gates exercise.
# The autonomous deny-by-default + sandbox + cap decision is the FLOOR (spec §5
# invariant 1); the channel can only NARROW a hook-allow to a deny (AND-combine),
# never widen a hook-deny to an allow. Every channel-failure mode in `approve`
# mode is a DENY (spec §5 invariant 2 / protocol §4): unreachable / unwritable /
# timeout / malformed / closed / any exception. In `report` mode a failed send
# only loses a chip and keeps the autonomous verdict (no user answer was pending).
# ---------------------------------------------------------------------------
_OBSERVE_CHANNEL_ENV = "HOARDPAD_OBSERVE_CHANNEL"  # socket path (linux) / pipe name (win); absent => leg off
_OBSERVE_MODE_ENV = "HOARDPAD_OBSERVE_MODE"        # "approve" (REPORT+ask) | "report" (REPORT only); default report
_OBSERVE_MAX_LINE = 64 * 1024                      # protocol §1.2 size bound: a line > 64 KiB is malformed => deny
_OBSERVE_CONNECT_TIMEOUT_S = 5.0                   # connect/send guard (the CLI's long PreToolUse timeout bounds the ask wait)
_OBSERVE_DECISION_TIMEOUT_S = 9.0                  # blocking read of the host `decision` (< the host's no-reply+margin window)

# PANEL-A1 per-file grant leg (feature-ai-multidoc-panel §5.3 Path A). Absent =>
# leg entirely inert (no grant set constructed); present => a JSON array of
# absolute path strings the hook READS to extend Step-3 containment ONLY.
_FILE_GRANTS_ENV = "HOARDPAD_FILE_GRANTS"


def _test_mode() -> bool:
    return os.environ.get("HOARDPAD_TEST_MODE", "0") == "1"


def _dbg(msg: str) -> None:
    if _test_mode():
        print(f"[hook-dbg] {msg}", file=sys.stderr)


def _int_env(name: str, default: int) -> int:
    """Read an int env var; fail CLOSED (return a value that trips the cap) on a
    malformed value rather than crashing or silently widening the cap.

    A non-numeric cap override is a host-config error; the safe reading is the
    most restrictive one. Caps default to their pinned values when unset.
    """
    raw = os.environ.get(name)
    if raw is None:
        return default
    try:
        return int(raw)
    except (TypeError, ValueError):
        _dbg(f"malformed {name}={raw!r}; failing closed to 0")
        return 0  # 0 => every counted op is at/over cap => deny


# ---------------------------------------------------------------------------
# Decision emission — nested-JSON (preferred) + exit-2 fallback.
# ---------------------------------------------------------------------------

def _emit(decision: str, reason: str) -> None:
    """Emit the nested hookSpecificOutput form and exit 0. If stdout cannot be
    written (the only way the nested form can't be produced), fall back to the
    exit-2 minimal deny so a deny is NEVER downgraded to a silent allow.
    """
    payload = {
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": decision,
        }
    }
    if reason:
        payload["hookSpecificOutput"]["permissionDecisionReason"] = reason
    try:
        sys.stdout.write(json.dumps(payload))
        sys.stdout.flush()
    except OSError:
        # Cannot produce the nested JSON. For an ALLOW this is an over-deny
        # (safe); for a DENY exit-2 still denies. Either way: fail closed.
        _deny_exit2(reason or decision)
        return
    sys.exit(0)


def _deny(reason: str) -> None:
    _dbg(f"DENY: {reason}")
    _emit("deny", reason)


def _allow(reason: str = "") -> None:
    _dbg(f"ALLOW: {reason}")
    _emit("allow", reason)


def _deny_exit2(reason: str) -> None:
    """Minimal fallback deny: exit code 2 with the reason on stderr (SPIKE-3 —
    exit 2 denies and shows stderr to the model). Used only when the nested JSON
    path is unavailable. Never returns."""
    try:
        sys.stderr.write(f"deny: {reason}\n")
        sys.stderr.flush()
    except OSError:
        pass
    sys.exit(2)


# ---------------------------------------------------------------------------
# Cross-platform exclusive lock (SPIKE-4 O_EXCL SpinLock).
#
# Atomic create on POSIX (O_CREAT|O_EXCL) and Win32 (CREATE_NEW). 5 ms spin,
# 10 s hard timeout, unlinked in finally. NEVER fcntl (advisory; no Windows
# equivalent — the spike rejected it).
# ---------------------------------------------------------------------------

class SpinLock:
    _RETRY_SLEEP_S = 0.005  # 5 ms between retries
    _TIMEOUT_S = 10.0       # hard timeout floor (the CLI kills the hook long before)

    def __init__(self, lock_path: str) -> None:
        self._path = lock_path
        self._fd: int | None = None

    def acquire(self) -> None:
        deadline = time.monotonic() + self._TIMEOUT_S
        while True:
            try:
                self._fd = os.open(
                    self._path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600
                )
                return
            except FileExistsError:
                if time.monotonic() > deadline:
                    raise RuntimeError(
                        f"SpinLock timeout acquiring {self._path!r} "
                        f"after {self._TIMEOUT_S}s"
                    )
                time.sleep(self._RETRY_SLEEP_S)

    def release(self) -> None:
        if self._fd is not None:
            try:
                os.close(self._fd)
            except OSError:
                pass
            self._fd = None
        try:
            os.unlink(self._path)
        except FileNotFoundError:
            pass

    def __enter__(self) -> "SpinLock":
        self.acquire()
        return self

    def __exit__(self, *_: object) -> None:
        self.release()


# ---------------------------------------------------------------------------
# Sandbox containment — MIRRORS the pure C# SandboxPolicy.Evaluate EXACTLY.
#
# src/HoardPad.Core/Safety/SandboxPolicy.cs is the authority. The C# operates on
# ALREADY-RESOLVED, normcased paths the host resolver produced. The hook has no
# C# resolver, so it performs the resolve/normcase leg itself (IPathResolver's
# job: realpath + GetLongPathName via 3.12 resolve() + normcase), then runs the
# SAME pure containment check. The pure check below is a line-for-line port:
#   - both '/' and '\' are segment boundaries (C# Separators / IsSeparator);
#   - a '..' SEGMENT (bounded, not substring) in the candidate => DENY;
#   - trim exactly ONE trailing separator off the root, never the whole root;
#   - a root that normalizes to empty or a bare separator ('/','\\','//','\\\\')
#     => the C# THROWS ArgumentException; here that maps to DENY (fail closed);
#   - root itself is in-sandbox; a descendant must start with root AND have a
#     separator at the boundary char (defeats the /proj vs /project prefix trap);
#   - ordinal (exact-byte) comparison over the already-normcased values.
# ---------------------------------------------------------------------------

def _is_separator(c: str) -> bool:
    return c in ("/", "\\")


def _has_parent_segment(path: str) -> bool:
    """True iff any path SEGMENT is exactly '..' (C# HasParentSegment). A bare
    'contains ..' would wrongly flag names like 'my..file'; split on BOTH
    separators so a mixed-style candidate is checked correctly."""
    norm = path.replace("\\", "/")
    return ".." in norm.split("/")


def _trim_one_trailing_separator(path: str) -> str:
    """Strip exactly ONE trailing separator, but never the whole root (C#
    TrimTrailingSeparator: only when length > 1)."""
    if len(path) > 1 and _is_separator(path[-1]):
        return path[:-1]
    return path


def _contained(normcased_root: str, normcased_candidate: str) -> bool:
    """Pure containment over ALREADY-RESOLVED, normcased strings. Returns True
    iff candidate == root or is a segment-boundary descendant of root. Mirrors
    SandboxPolicy.Evaluate; every fail-closed branch returns False (the C#
    Deny / the C# ArgumentException both become a denial at the hook boundary).
    """
    # C#: candidate.IsDefault => Deny. Here: empty candidate => deny.
    if not normcased_candidate:
        return False

    # C#: a surviving '..' segment means the resolve contract was violated
    # upstream => Deny. (resolve() should have removed these; defensive.)
    if _has_parent_segment(normcased_candidate):
        return False

    norm_root = _trim_one_trailing_separator(normcased_root)

    # C# degenerate-root guard: empty or a bare separator grants the whole
    # volume. The C# THROWS; the hook fails CLOSED (deny) — a degenerate root is
    # a host contract violation and must never grant containment.
    if len(norm_root) == 0 or (len(norm_root) == 1 and _is_separator(norm_root[0])):
        _dbg(f"degenerate sandbox root {normcased_root!r} => deny")
        return False

    # The root itself is in-sandbox (ordinal/exact-byte equality).
    if normcased_candidate == norm_root:
        return True

    # Descendant: candidate starts with root AND the boundary char is a
    # separator. This separator boundary defeats the prefix trap — '/project'
    # is NOT inside '/proj' because the char after '/proj' is 'e', not a sep.
    if (
        len(normcased_candidate) > len(norm_root)
        and normcased_candidate.startswith(norm_root)
        and _is_separator(normcased_candidate[len(norm_root)])
    ):
        return True

    return False


def _resolve_normcased(raw_path: str) -> str | None:
    """The IPathResolver leg the hook must own in-process: realpath + (on 3.12+
    Windows) GetLongPathName via resolve(), then normcase. Returns the normcased
    absolute string, or None on any failure (=> caller fails closed).
    """
    if not raw_path:
        return None
    try:
        resolved = pathlib.Path(raw_path).resolve()
    except (OSError, ValueError, RuntimeError):
        return None
    return os.path.normcase(str(resolved))


def in_sandbox(root_raw: str, target_raw: str) -> bool:
    """Resolve + normcase both paths (the host-resolver leg), then run the pure
    SandboxPolicy.Evaluate-equivalent containment. Fails CLOSED (False) on any
    missing root, resolve failure, or containment denial.
    """
    if not root_raw:
        # No sandbox root => no containment can be proven => DENY. (The
        # prototype allowed here; production fails closed.)
        _dbg("no sandbox root set => deny")
        return False
    normcased_root = _resolve_normcased(root_raw)
    normcased_target = _resolve_normcased(target_raw)
    if normcased_root is None or normcased_target is None:
        return False
    return _contained(normcased_root, normcased_target)


# ---------------------------------------------------------------------------
# PANEL-A1 per-file grant leg (feature-ai-multidoc-panel §5.1/§5.3 Path A).
# ADDITIVE, env-gated, fail-closed. WIDENS Step-3 containment ONLY — it can turn
# a root-deny into an allow for an EXACT granted path, never widen the allowlist
# (Step 1), never bypass a cap (Step 4), never narrow anything.
# ---------------------------------------------------------------------------

def _read_file_grants(grants_file: str) -> list[str]:
    """Read the host-written grants file into a list of raw grant path strings.

    Wire contract: the file content is a JSON array of strings, each an absolute
    path to one granted file (the host writes it atomically before launching
    claude -p; it is immutable for the turn and READ-ONLY to the hook — it does
    NOT take the O_EXCL lock because it is never mutated during the turn, unlike
    the cap-state file).

    FAIL CLOSED (FR-MD-10), every branch: a missing / unreadable / non-JSON file,
    a root that is not a JSON array, or ANY element that is not a string yields an
    EMPTY list (root-only fallback) — never a wider set, never a raise. Callers
    must only reach this with the env var present (the leg is inert when absent).
    """
    if not grants_file:
        return []
    try:
        with open(grants_file, "r", encoding="utf-8") as f:
            data = json.load(f)
        if not isinstance(data, list):
            raise ValueError("grants-file root is not a JSON array")
        grants: list[str] = []
        for elem in data:
            if not isinstance(elem, str):
                # A non-string element makes the whole file suspect => fail
                # closed to NO grants rather than admitting a partial set.
                raise ValueError("grants-file contains a non-string element")
            grants.append(elem)
        return grants
    except (OSError, ValueError, TypeError, json.JSONDecodeError) as e:
        _dbg(f"grants-file read failed ({e!r}) => fail closed to no grants")
        return []


def _target_matches_grant(target_raw: str) -> bool:
    """True iff the target resolves EXACTLY to a resolved granted path.

    Reads + parses the grants file (fail-closed to no grants per
    _read_file_grants). Resolves BOTH sides through the EXISTING
    _resolve_normcased leg and returns True iff the resolved target equals some
    resolved grant by ordinal string equality (both are already normcased). Re-
    resolving the grant IN-HOOK (rather than trusting the host's canonicalization
    blindly) eliminates any host/Python resolver skew and drops any non-canonical
    entry — a grant that fails to resolve, or that resolves to something other
    than the target, simply never matches (fail closed).

    Exact equality defeats the prefix trap for free ('/a/b/c.txt' vs
    '/a/b/c.txt2' are never string-equal) and there is deliberately NO descendant
    arm: a file grant has no children, so only the equality relation can admit
    (mirrors _contained's root-equality arm, minus the descendant arm).
    """
    grants_file = os.environ.get(_FILE_GRANTS_ENV, "")
    if not grants_file:
        return False  # leg off (defensive — callers already gate on the env).
    rt = _resolve_normcased(target_raw)
    if rt is None:
        return False  # target that cannot be resolved is never granted.
    for g in _read_file_grants(grants_file):
        rg = _resolve_normcased(g)
        if rg is not None and rt == rg:
            return True
    return False


# ---------------------------------------------------------------------------
# Cap-state — READ ONLY (architecture §4.4). The host (M5.2) WRITES this file
# under the O_EXCL lock; the hook reads it under the same lock. The shape is the
# C# CapState record as System.Text.Json serializes it (PascalCase keys). Any
# missing / malformed / unreadable state => fail closed (treat every cap as
# tripped so the op is denied).
# ---------------------------------------------------------------------------

# Sentinel returned on any read failure: counts at the cap and every flag
# tripped, so the create/overwrite logic denies regardless of cap config.
class _CapState:
    __slots__ = (
        "create_count",
        "destructive_count",
        "files_touched",
        "create_tripped",
        "destructive_tripped",
        "files_tripped",
    )

    def __init__(
        self,
        create_count: int,
        destructive_count: int,
        files_touched: set[str],
        create_tripped: bool,
        destructive_tripped: bool,
        files_tripped: bool,
    ) -> None:
        self.create_count = create_count
        self.destructive_count = destructive_count
        self.files_touched = files_touched
        self.create_tripped = create_tripped
        self.destructive_tripped = destructive_tripped
        self.files_tripped = files_tripped


# Read failure => deny-everything state (all tripped).
_FAIL_CLOSED_STATE = _CapState(0, 0, set(), True, True, True)


def _read_cap_state(cap_state_file: str) -> _CapState:
    """Read the host-owned cap-state JSON. Mirrors the C# CapState record's
    PascalCase fields (CreateCount, DestructiveCount, FilesTouched,
    CreateCapTripped, DestructiveCapTripped, FilesTouchedCapTripped). A missing
    file is the START-OF-CONVERSATION empty state (no caps tripped). A present-
    but-unreadable / malformed file fails CLOSED (every cap tripped).
    """
    if not cap_state_file or not os.path.exists(cap_state_file):
        # No file yet = fresh conversation = empty state (C# CapState.Empty).
        return _CapState(0, 0, set(), False, False, False)
    try:
        with open(cap_state_file, "r", encoding="utf-8") as f:
            data = json.load(f)
        if not isinstance(data, dict):
            raise ValueError("cap-state root is not an object")
        files = data.get("FilesTouched", [])
        if not isinstance(files, list):
            raise ValueError("FilesTouched is not an array")
        return _CapState(
            create_count=int(data.get("CreateCount", 0)),
            destructive_count=int(data.get("DestructiveCount", 0)),
            files_touched={str(p) for p in files},
            create_tripped=bool(data.get("CreateCapTripped", False)),
            destructive_tripped=bool(data.get("DestructiveCapTripped", False)),
            files_tripped=bool(data.get("FilesTouchedCapTripped", False)),
        )
    except (OSError, ValueError, TypeError, json.JSONDecodeError) as e:
        _dbg(f"cap-state read failed ({e!r}) => fail closed")
        return _FAIL_CLOSED_STATE


def _lockfile_path(cap_state_file: str) -> str:
    return cap_state_file + ".lock"


# ---------------------------------------------------------------------------
# FR-38 create-vs-overwrite classification (CapClassifier port, SPIKE-3 §F).
#
# Mirrors CapClassifier.Classify + CapClassifier.MeetsMagnitudePredicate EXACTLY.
# ---------------------------------------------------------------------------

def _meets_magnitude_predicate(prior_bytes: int, proposed_bytes: int, percent: int) -> bool:
    """Port of C# CapClassifier.MeetsMagnitudePredicate (integer delta, no division).
    prior_bytes <= 0 => False (a prior-empty Edit is sub-threshold; a destructive
    empty-file *overwrite* is already caught by the Write branch in Classify).
    delta * 100 >= percent * prior mirrors the C# integer arithmetic exactly.
    """
    if prior_bytes <= 0:
        return False
    delta = abs(prior_bytes - proposed_bytes)
    return delta * 100 >= percent * prior_bytes


def _op_is_destructive(tool_name: str, target: str, proposed: str, x_pct: int) -> bool:
    """Port of CapClassifier.Classify for an EXISTING target. Returns True iff the
    op routes to DestructiveOverwrite (FR-38); False => SubThresholdEdit.
    Precondition: caller confirmed the target EXISTS.
    Fails CLOSED (True) when the prior size cannot be read.
    """
    if tool_name == "Write":
        # Whole-file Write onto existing => always DestructiveOverwrite.
        return True
    if tool_name == "Edit":
        try:
            prior = os.path.getsize(target)
        except OSError:
            return True  # cannot size => treat as destructive (safe, fail closed)
        proposed_bytes = len(proposed.encode("utf-8")) if proposed else 0
        _dbg(f"FR-38 magnitude prior={prior}B proposed={proposed_bytes}B x_pct={x_pct}")
        return _meets_magnitude_predicate(prior, proposed_bytes, x_pct)
    return False


# ---------------------------------------------------------------------------
# tool_input extraction.
# ---------------------------------------------------------------------------

def _target_path(tool_input: Mapping[str, object]) -> str:
    val = tool_input.get("file_path", "")
    return val if isinstance(val, str) else ""


def _proposed_content(tool_name: str, tool_input: Mapping[str, object]) -> str:
    key = "content" if tool_name == "Write" else "new_string"
    val = tool_input.get(key, "")
    return val if isinstance(val, str) else ""


# ---------------------------------------------------------------------------
# Core decision. Returns ("allow"|"deny", reason). NEVER raises (the caller's
# top-level guard would turn a raise into an exit-2 deny, but the under-cap
# accounting wraps its own IO so a lock/read failure is a clean deny here).
# ---------------------------------------------------------------------------

def decide(event: Mapping[str, object]) -> tuple[str, str]:
    tool_name = event.get("tool_name", "")
    tool_input = event.get("tool_input", {})
    if not isinstance(tool_input, dict):
        return "deny", "malformed tool_input (not an object)"

    # Step 1 — deny-by-default allowlist (Agent (the Task alias) and every
    # unknown/mcp__* tool fall through to deny here).
    if tool_name not in ALLOWED_TOOLS:
        return "deny", f"tool not in allowlist: {tool_name!r}"

    # Step 2 — Read is always allowed.
    if tool_name == "Read":
        return "allow", "Read always allowed"

    target = _target_path(tool_input)
    if not target:
        return "deny", f"{tool_name} with no file_path"

    sandbox_root = os.environ.get("HOARDPAD_SANDBOX_ROOT", "")

    # Step 3 — sandbox containment (mirrors SandboxPolicy.Evaluate). Out of
    # sandbox / degenerate root / resolve failure all => deny — UNLESS the
    # PANEL-A1 per-file grant leg admits the exact target. The grant leg is
    # engaged ONLY when HOARDPAD_FILE_GRANTS is present: when it is ABSENT the
    # short-circuit below is never evaluated and this branch is byte-identical to
    # before the leg existed. A grant match widens containment for THIS one path
    # only; it then flows into Step 4 caps UNCHANGED (FR-MD-5). The in-root deny
    # reason wording is preserved exactly when no grant is involved.
    if not in_sandbox(sandbox_root, target):
        if not (os.environ.get(_FILE_GRANTS_ENV) and _target_matches_grant(target)):
            return "deny", f"out of sandbox: {target!r}"

    # Step 4 — under-cap accounting under the exclusive lock.
    cap_state_file = os.environ.get("HOARDPAD_CAP_STATE_FILE", "")
    fr11_cap = _int_env("HOARDPAD_FR11_CAP", 25)
    fr38_cap = _int_env("HOARDPAD_FR38_CAP", 10)
    fr40_cap = _int_env("HOARDPAD_FR40_CAP", 20)
    fr38_x_pct = _int_env("HOARDPAD_FR38_X_PCT", 80)

    if not cap_state_file:
        # No cap-state path means the host did not wire the conversation state.
        # We cannot prove under-cap => fail closed.
        return "deny", "no cap-state file configured"

    canonical = _resolve_normcased(target)
    if canonical is None:
        return "deny", f"could not canonicalize target: {target!r}"

    proposed = _proposed_content(tool_name, tool_input)

    try:
        with SpinLock(_lockfile_path(cap_state_file)):
            state = _read_cap_state(cap_state_file)
            target_exists = os.path.exists(target)
            files_after_count = len(state.files_touched | {canonical})

            if not target_exists:
                # CREATE path (CapClassifier.Classify: !TargetExists => Create).
                # FR-11: post-increment then > cap (C# createCount++ then > CreateCap).
                # Equivalent pre-check: create_count >= cap (before increment).
                if state.create_tripped or state.create_count >= fr11_cap:
                    return "deny", f"FR-11 create cap ({fr11_cap}) reached"
                # FR-40: >= cap (C# filesTouchedCount >= FilesTouchedCap).
                if state.files_tripped or files_after_count >= fr40_cap:
                    return "deny", f"FR-40 distinct-files cap ({fr40_cap}) reached"
                return "allow", (
                    f"create {state.create_count + 1}/{fr11_cap}, "
                    f"files {files_after_count}/{fr40_cap}"
                )

            # OVERWRITE/EDIT path (CapClassifier.Classify: TargetExists).
            # FR-40 checked first on every overwrite kind (C# computes filesTouchedTripped
            # before branching on Create/Destructive/SubThreshold).
            if state.files_tripped or files_after_count >= fr40_cap:
                return "deny", f"FR-40 distinct-files cap ({fr40_cap}) reached"
            if _op_is_destructive(tool_name, target, proposed, fr38_x_pct):
                # FR-38: post-increment then > cap.
                if state.destructive_tripped or state.destructive_count >= fr38_cap:
                    return "deny", f"FR-38 destructive cap ({fr38_cap}) reached"
                return "allow", (
                    f"destructive overwrite {state.destructive_count + 1}/{fr38_cap}, "
                    f"files {files_after_count}/{fr40_cap}"
                )
            # SubThresholdEdit: no per-op counter; FR-40 already checked above.
            return "allow", f"sub-threshold edit, files {files_after_count}/{fr40_cap}"
    except (OSError, RuntimeError) as e:
        # Lock timeout / IO failure during accounting => fail closed.
        return "deny", f"cap-state accounting failed: {e!r}"


# ---------------------------------------------------------------------------
# M11 observation/ask leg (docs/m11-channel-protocol.md). ADDITIVE and OPTIONAL:
# engaged ONLY when HOARDPAD_OBSERVE_CHANNEL is present (the guard lives in main()).
# It NEVER re-runs or alters decide() — the autonomous (decision, reason) is the
# FLOOR (spec §5 invariant 1). The leg can only AND-narrow a hook-allow to a deny.
# Every channel-failure mode in `approve` mode is a DENY (protocol §4); a failed
# `report` send keeps the autonomous verdict (no user answer was pending).
# ---------------------------------------------------------------------------

def _tool_use_id(event: Mapping[str, object]) -> str:
    """The CLI tool_use_id the hook saw on stdin — the MA14 correlation key
    (protocol §2). The documented PreToolUse schema does not currently guarantee
    this top-level field (see the report's flagged ambiguity), so it is read
    defensively: present + str => that value; absent/non-str => "" (which the
    Core.Ai projection treats as an unkeyable observation — no MA14 callback).
    Never affects the autonomous floor.
    """
    val = event.get("tool_use_id", "")
    return val if isinstance(val, str) else ""


def _report_op_class(tool_name: str, target: str, proposed: str) -> str:
    """Derive the protocol §2 op_class string (create|overwrite|edit) for the
    REPORT, mirroring the SAME classification decide() used. This is a pure
    re-derivation for the chip label — it makes NO decision and does not touch
    the cap-state. Fails toward "overwrite" only when a prior size cannot be read
    (consistent with _op_is_destructive's fail-closed posture); the report is
    cosmetic, never on the safety path.
    """
    if not target or not os.path.exists(target):
        return "create"
    if _op_is_destructive(tool_name, target, proposed, _int_env("HOARDPAD_FR38_X_PCT", 80)):
        return "overwrite"
    return "edit"


def _report_magnitude(proposed: str) -> int:
    """The proposed post-op byte size the hook saw (protocol §2 magnitude; the
    FR-38 magnitude input). 0 when unknown. Pure; never on the safety path."""
    return len(proposed.encode("utf-8")) if proposed else 0


def _connect_channel(address: str) -> socket.socket:
    """Connect to the per-turn host endpoint: AF_UNIX stream socket on POSIX, a
    named pipe opened as a byte stream on Windows. Raises on any failure (the
    caller converts a raise into a fail-closed deny in `approve` mode, or a
    dropped chip in `report` mode). The Windows named-pipe leg is compile-parity
    only (runtime Deferred, spec §6) — it uses the stdlib so it imports + runs the
    same fail-closed path; the linux-x64 AF_UNIX leg is the acceptance platform.
    """
    if sys.platform.startswith("win"):
        # win-x64: a named pipe (\\.\pipe\hoardpad-observe-<token>). Deferred
        # runtime (compile-confirmed only) — opened in r+b binary mode and wrapped
        # so the same _send_line/_recv_line byte protocol applies. (The host
        # creates the pipe before launching claude -p, protocol §1.1.)
        raise OSError(
            "win-x64 named-pipe observe channel is runtime-Deferred (compile-parity only); "
            "fail closed"
        )
    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    sock.settimeout(_OBSERVE_CONNECT_TIMEOUT_S)
    sock.connect(address)
    return sock


def _send_line(sock: socket.socket, obj: Mapping[str, object]) -> None:
    """Serialize obj to one NDJSON line (UTF-8, '\\n'-terminated, no embedded raw
    newlines — json.dumps escapes them) and send it whole (protocol §1.2). Raises
    on a too-large frame or any socket error; the caller fails closed in `approve`
    mode. compact separators keep the frame minimal and well under 64 KiB.
    """
    line = json.dumps(obj, separators=(",", ":")) + "\n"
    data = line.encode("utf-8")
    if len(data) > _OBSERVE_MAX_LINE:
        raise ValueError(f"outgoing message exceeds {_OBSERVE_MAX_LINE} bytes (malformed)")
    sock.settimeout(_OBSERVE_CONNECT_TIMEOUT_S)
    sock.sendall(data)


def _recv_line(sock: socket.socket, timeout_s: float) -> str:
    """Block reading ONE '\\n'-terminated NDJSON line, up to timeout_s and the
    64 KiB bound (protocol §1.2/§4). Raises on timeout, a closed connection before
    a newline, or a frame that exceeds the bound — every one is a fail-closed deny
    for the caller. Returns the line WITHOUT the trailing newline.

    timeout_s is enforced as a MONOTONIC WALL-CLOCK deadline across the WHOLE read
    (security W1): socket.settimeout() is a per-recv() timeout, so a byte-drip
    (one byte just under timeout_s apart) could otherwise keep this read alive far
    past timeout_s — a slow-loris hang bounded only by the host's PreToolUse
    timeout. We recompute the remaining budget before each recv(); when it is
    exhausted we raise, which the caller turns into a fail-closed DENY.
    """
    deadline = time.monotonic() + timeout_s
    chunks: list[bytes] = []
    total = 0
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            # Wall-clock deadline reached (e.g. a byte-drip slow-loris) — the
            # whole read can never exceed timeout_s. protocol §4: DENY.
            raise TimeoutError("decision read exceeded the wall-clock deadline")
        sock.settimeout(remaining)
        b = sock.recv(4096)
        if not b:
            # Connection closed by the host before a decision line arrived
            # (endpoint disposed) — protocol §4: DENY.
            raise ConnectionError("channel closed before a complete decision line")
        nl = b.find(b"\n")
        if nl != -1:
            chunks.append(b[: nl + 1])
            total += nl + 1
            if total > _OBSERVE_MAX_LINE:
                raise ValueError(f"incoming message exceeds {_OBSERVE_MAX_LINE} bytes (malformed)")
            return b"".join(chunks).decode("utf-8").rstrip("\n")
        chunks.append(b)
        total += len(b)
        if total > _OBSERVE_MAX_LINE:
            # A line that grows past the bound without a newline is malformed.
            raise ValueError(f"incoming message exceeds {_OBSERVE_MAX_LINE} bytes (malformed)")


def _report_message(
    event: Mapping[str, object], tool_name: str, target: str, proposed: str, decision: str
) -> dict[str, object]:
    """Build the hook->host REPORT object (protocol §2) for one gated op (allow OR
    deny). target_path is "" when the op was denied before a path could be
    resolved (protocol §2). hook_decision carries the AUTONOMOUS verdict (the floor).
    """
    canonical = _resolve_normcased(target) if target else None
    return {
        "type": "report",
        "tool_use_id": _tool_use_id(event),
        "tool_name": tool_name,
        "target_path": canonical if canonical is not None else "",
        "op_class": _report_op_class(tool_name, target, proposed),
        "magnitude": _report_magnitude(proposed),
        "hook_decision": decision,
    }


def _ask_message(
    event: Mapping[str, object], tool_name: str, target: str, proposed: str
) -> dict[str, object]:
    """Build the hook->host ASK object (protocol §3) — sent ONLY for a hook-ALLOW
    op in `approve` mode. The host raises it to the App's Deny-focused confirm and
    writes back exactly one `decision` line keyed by the same tool_use_id.
    """
    canonical = _resolve_normcased(target) if target else None
    return {
        "type": "ask",
        "tool_use_id": _tool_use_id(event),
        "tool_name": tool_name,
        "target_path": canonical if canonical is not None else "",
        "op_class": _report_op_class(tool_name, target, proposed),
    }


def _user_allowed(sock: socket.socket, expected_tool_use_id: str) -> bool:
    """Block reading ONE host->hook `decision` line and validate it (protocol §3/§4).
    Returns True ONLY for a well-formed decision whose tool_use_id matches AND whose
    `allow` is exactly the JSON boolean true. EVERY other outcome — timeout, closed
    connection, non-JSON, missing fields, wrong type, > 64 KiB, OR a tool_use_id
    mismatch — raises or returns False, which the caller turns into a DENY. There is
    NO branch where a channel problem yields True (fail-closed by construction).
    """
    raw = _recv_line(sock, _OBSERVE_DECISION_TIMEOUT_S)  # raises on timeout/closed/oversize => deny
    obj = json.loads(raw)  # raises json.JSONDecodeError on non-JSON => deny
    if not isinstance(obj, dict):
        raise ValueError("decision line is not a JSON object")
    if obj.get("type") != "decision":
        raise ValueError(f"unexpected message type on the decision channel: {obj.get('type')!r}")
    got_id = obj.get("tool_use_id")
    if not isinstance(got_id, str) or got_id != expected_tool_use_id:
        # tool_use_id mismatch => treat as no-answer => DENY (protocol §3 row).
        raise ValueError(
            f"decision tool_use_id mismatch (got {got_id!r}, want {expected_tool_use_id!r})"
        )
    allow = obj.get("allow")
    if not isinstance(allow, bool):
        raise ValueError("decision.allow is missing or not a boolean")
    return allow


def _apply_observation_leg(
    event: Mapping[str, object], decision: str, reason: str
) -> tuple[str, str]:
    """The optional observation/ask leg over the autonomous (decision, reason).

    Engaged only when HOARDPAD_OBSERVE_CHANNEL is present (main() guards this).
    The autonomous decision is already computed and is the FLOOR — this function
    can only AND-narrow a hook-allow to a deny; it NEVER widens a hook-deny.

    `report` mode: send the REPORT for every gated op; a send failure only loses a
    chip, so the autonomous (decision, reason) is returned unchanged.

    `approve` mode: send the REPORT; then for a hook-ALLOW op ONLY, send the ASK,
    BLOCK for the host `decision`, validate the tool_use_id, and AND-combine
    (final = hook_allow AND user_allow). EVERY channel-failure mode is a DENY.
    A hook-DENY is never asked (already final — the user cannot widen it).
    """
    address = os.environ.get(_OBSERVE_CHANNEL_ENV, "")
    if not address:
        return decision, reason  # leg off (defensive — main() already checked).
    mode = os.environ.get(_OBSERVE_MODE_ENV, "report")
    approve = mode == "approve"

    tool_name = event.get("tool_name", "")
    tool_name = tool_name if isinstance(tool_name, str) else ""
    tool_input = event.get("tool_input", {})
    target = _target_path(tool_input) if isinstance(tool_input, dict) else ""
    proposed = (
        _proposed_content(tool_name, tool_input) if isinstance(tool_input, dict) else ""
    )

    sock: socket.socket | None = None
    try:
        sock = _connect_channel(address)
    except (OSError, ValueError) as e:
        # Channel unreachable (connect failed). protocol §4:
        #   approve mode => DENY (cannot obtain the required ask answer);
        #   report mode  => keep the autonomous verdict (only a chip is lost).
        _dbg(f"observe connect failed ({e!r})")
        if approve and decision == "allow":
            return "deny", f"observe channel unreachable (approve mode, fail closed): {e!r}"
        return decision, reason

    try:
        # REPORT (both modes, every gated op — allow OR deny). protocol §2.
        try:
            _send_line(sock, _report_message(event, tool_name, target, proposed, decision))
        except (OSError, ValueError) as e:
            # protocol §4 (unwritable mid-turn): approve+allow => DENY; else keep.
            _dbg(f"observe report send failed ({e!r})")
            if approve and decision == "allow":
                return "deny", f"observe report unwritable (approve mode, fail closed): {e!r}"
            return decision, reason

        # ASK round — approve mode AND only for a hook-ALLOW op. A hook-DENY is
        # already final; the hook NEVER asks for one (it cannot be widened).
        if approve and decision == "allow":
            try:
                _send_line(sock, _ask_message(event, tool_name, target, proposed))
                user_allow = _user_allowed(sock, _tool_use_id(event))
            except (OSError, ValueError, json.JSONDecodeError) as e:
                # Unwritable ask / timeout / closed / malformed / wrong id =>
                # DENY (protocol §4). No path here yields allow.
                _dbg(f"observe ask round failed ({e!r})")
                return "deny", f"observe ask failed (approve mode, fail closed): {e!r}"
            if not user_allow:
                # AND-narrowing: hook_allow AND user_deny => deny.
                return "deny", "user denied the op (approve-each narrowing)"
            # hook_allow AND user_allow => the autonomous allow stands.
            return decision, f"{reason} [user-approved]"

        # report mode (or a hook-deny in approve mode): the autonomous verdict
        # is final; the report was best-effort.
        return decision, reason
    finally:
        try:
            sock.close()
        except OSError:
            pass


# ---------------------------------------------------------------------------
# Entry point. Reads the PreToolUse event JSON from stdin, decides, emits.
# The try/except is the LAST fail-closed backstop: any unexpected exception
# becomes an exit-2 deny so the host can never (mis)read an unhandled traceback
# as an allow.
# ---------------------------------------------------------------------------

def _windows_below_floor() -> bool:
    return sys.platform.startswith("win") and sys.version_info < _PY_FLOOR


def main() -> None:
    if _windows_below_floor():
        # TD-1: below 3.12 on Windows, resolve() does not expand 8.3 short
        # names => the sandbox can be evaded. Refuse to run = deny everything.
        _deny(
            f"Python {sys.version_info.major}.{sys.version_info.minor} below "
            f"the {_PY_FLOOR[0]}.{_PY_FLOOR[1]} Windows floor (TD-1 short-name escape)"
        )
        return
    try:
        raw = sys.stdin.read()
    except OSError as e:
        _deny(f"could not read hook input: {e!r}")
        return
    try:
        event = json.loads(raw)
        if not isinstance(event, dict):
            raise ValueError("hook input root is not an object")
    except (ValueError, json.JSONDecodeError) as e:
        _deny(f"malformed hook input: {e!r}")
        return

    # Outer fail-closed backstop (the docstring's "any escape => exit-2 deny"
    # promise made real): an unhandled exception escaping decide(), the
    # observation leg, or the emit becomes an exit-2 DENY rather than Python's
    # default exit 1 (unspecified under the hook protocol). BaseException is
    # deliberate — even an unexpected non-Exception escape must fail CLOSED. This
    # only ADDS a catch; it does not touch the decision logic or the autonomous
    # path below. (SystemExit raised by _deny_exit2 itself re-propagates cleanly.)
    try:
        # The autonomous deny-by-default + sandbox + cap decision is computed FIRST
        # and independently — it is the FLOOR (M11 spec §5 invariant 1).
        decision, reason = decide(event)

        # PURELY ADDITIVE M11 observation/ask leg: engaged ONLY when the channel env
        # is present. When HOARDPAD_OBSERVE_CHANNEL is ABSENT the autonomous decision
        # above is the final decision and behavior is byte-identical to before this
        # leg existed (what the 29 SC-5 gates exercise). The leg can only AND-narrow a
        # hook-allow to a deny (and fails closed on every channel failure); it can
        # never widen a hook-deny to an allow.
        if os.environ.get(_OBSERVE_CHANNEL_ENV):
            decision, reason = _apply_observation_leg(event, decision, reason)

        if decision == "allow":
            _allow(reason)
        else:
            _deny(reason)
    except SystemExit:
        # _deny_exit2 (and any deliberate exit) already chose the code — honor it.
        raise
    except BaseException as e:  # noqa: BLE001 — last-resort fail-closed backstop
        _deny_exit2(f"unhandled hook escape: {e!r}")


# ---------------------------------------------------------------------------
# Self-test (--self-test). The FR-24 §3 fail-closed behavioral self-test, run
# in-process (no claude -p): attempt to DENY a Write to a path OUTSIDE a scratch
# sandbox. PASS iff (a) a deny decision is recorded AND (b) no file appears at
# the target. Exit 0 on success, non-zero on any failure.
# ---------------------------------------------------------------------------

def _self_test() -> int:
    import tempfile

    failures: list[str] = []

    with tempfile.TemporaryDirectory(prefix="hoardpad_selftest_") as base:
        sandbox = os.path.join(base, "sandbox")
        os.makedirs(sandbox, exist_ok=True)
        outside_target = os.path.join(base, "ESCAPED.txt")  # sibling of sandbox

        # The crafted out-of-sandbox Write event.
        event = {
            "tool_name": "Write",
            "tool_input": {"file_path": outside_target, "content": "escape attempt"},
        }

        prev_root = os.environ.get("HOARDPAD_SANDBOX_ROOT")
        os.environ["HOARDPAD_SANDBOX_ROOT"] = sandbox
        try:
            decision, reason = decide(event)
        finally:
            if prev_root is None:
                os.environ.pop("HOARDPAD_SANDBOX_ROOT", None)
            else:
                os.environ["HOARDPAD_SANDBOX_ROOT"] = prev_root

        # (a) a denial must be recorded.
        if decision != "deny":
            failures.append(
                f"out-of-sandbox Write was NOT denied (got {decision!r}: {reason})"
            )
        else:
            print(f"[self-test] out-of-sandbox Write denied: {reason}")

        # (b) no file may appear at the out-of-sandbox target. (decide() never
        # writes; this asserts the hook is purely a gate, per FR-24 §3.)
        if os.path.exists(outside_target):
            failures.append(f"FAIL: a file appeared at the escape target {outside_target!r}")
        else:
            print("[self-test] no file written to the out-of-sandbox target")

        # Sanity counter-check: an in-sandbox Write under cap must ALLOW, so the
        # deny above is a real decision, not a deny-everything bug.
        in_target = os.path.join(sandbox, "ok.txt")
        cap_file = os.path.join(base, "conv.capstate")
        in_event = {
            "tool_name": "Write",
            "tool_input": {"file_path": in_target, "content": "ok"},
        }
        prev_root = os.environ.get("HOARDPAD_SANDBOX_ROOT")
        prev_cap = os.environ.get("HOARDPAD_CAP_STATE_FILE")
        os.environ["HOARDPAD_SANDBOX_ROOT"] = sandbox
        os.environ["HOARDPAD_CAP_STATE_FILE"] = cap_file
        try:
            in_decision, in_reason = decide(in_event)
        finally:
            for k, v in (
                ("HOARDPAD_SANDBOX_ROOT", prev_root),
                ("HOARDPAD_CAP_STATE_FILE", prev_cap),
            ):
                if v is None:
                    os.environ.pop(k, None)
                else:
                    os.environ[k] = v

        if in_decision != "allow":
            failures.append(
                f"in-sandbox under-cap Write was NOT allowed (got {in_decision!r}: {in_reason})"
            )
        else:
            print(f"[self-test] in-sandbox under-cap Write allowed: {in_reason}")

        # -----------------------------------------------------------------
        # Regression A — FR-38 near-truncate Edit must DENY (escape fixed).
        # An Edit shrinking a 100-byte file to 15 bytes: delta=85, 85*100=8500
        # >= 80*100=8000 => DestructiveOverwrite. With destructive_count already
        # at cap (10), the op must be denied.
        # -----------------------------------------------------------------
        reg_a_target = os.path.join(sandbox, "large.txt")
        prior_content = "x" * 100  # 100 bytes prior
        with open(reg_a_target, "w", encoding="utf-8") as fh:
            fh.write(prior_content)
        small_proposed = "y" * 15  # 15 bytes proposed; delta=85 >= 80% of 100
        # State: destructive_count at cap (10), flag already tripped.
        cap_file_a = os.path.join(base, "conv_a.capstate")
        with open(cap_file_a, "w", encoding="utf-8") as fh:
            json.dump({
                "CreateCount": 0,
                "DestructiveCount": 10,
                "FilesTouched": [],
                "CreateCapTripped": False,
                "DestructiveCapTripped": True,
                "FilesTouchedCapTripped": False,
            }, fh)
        ev_a = {
            "tool_name": "Edit",
            "tool_input": {
                "file_path": reg_a_target,
                "old_string": prior_content,
                "new_string": small_proposed,
            },
        }
        prev_root_a = os.environ.get("HOARDPAD_SANDBOX_ROOT")
        prev_cap_a = os.environ.get("HOARDPAD_CAP_STATE_FILE")
        os.environ["HOARDPAD_SANDBOX_ROOT"] = sandbox
        os.environ["HOARDPAD_CAP_STATE_FILE"] = cap_file_a
        try:
            dec_a, reason_a = decide(ev_a)
        finally:
            for k, v in (
                ("HOARDPAD_SANDBOX_ROOT", prev_root_a),
                ("HOARDPAD_CAP_STATE_FILE", prev_cap_a),
            ):
                if v is None:
                    os.environ.pop(k, None)
                else:
                    os.environ[k] = v
        if dec_a != "deny":
            failures.append(
                f"Regression A FAIL: near-truncate Edit (100→15B) at FR-38 cap "
                f"was NOT denied (got {dec_a!r}: {reason_a})"
            )
        else:
            print(f"[self-test] Regression A PASS: near-truncate Edit denied: {reason_a}")

        # -----------------------------------------------------------------
        # Regression B — FR-40 at exactly 20 files must DENY (off-by-one fixed).
        # With files_touched already containing 19 distinct canonical paths, the
        # 20th op (files_after_count == 20 == cap) must deny, not allow.
        # -----------------------------------------------------------------
        # Build 19-file set of normcased canonical paths already in state.
        existing_files = [
            os.path.normcase(os.path.join(sandbox, f"f{i}.txt")) for i in range(19)
        ]
        cap_file_b = os.path.join(base, "conv_b.capstate")
        with open(cap_file_b, "w", encoding="utf-8") as fh:
            json.dump({
                "CreateCount": 0,
                "DestructiveCount": 0,
                "FilesTouched": existing_files,
                "CreateCapTripped": False,
                "DestructiveCapTripped": False,
                "FilesTouchedCapTripped": False,
            }, fh)
        # Target is a 20th distinct file (not yet in FilesTouched).
        new_20th = os.path.join(sandbox, "f_new.txt")
        ev_b = {
            "tool_name": "Write",
            "tool_input": {"file_path": new_20th, "content": "20th"},
        }
        prev_root_b = os.environ.get("HOARDPAD_SANDBOX_ROOT")
        prev_cap_b = os.environ.get("HOARDPAD_CAP_STATE_FILE")
        os.environ["HOARDPAD_SANDBOX_ROOT"] = sandbox
        os.environ["HOARDPAD_CAP_STATE_FILE"] = cap_file_b
        try:
            dec_b, reason_b = decide(ev_b)
        finally:
            for k, v in (
                ("HOARDPAD_SANDBOX_ROOT", prev_root_b),
                ("HOARDPAD_CAP_STATE_FILE", prev_cap_b),
            ):
                if v is None:
                    os.environ.pop(k, None)
                else:
                    os.environ[k] = v
        if dec_b != "deny":
            failures.append(
                f"Regression B FAIL: 20th distinct file (== FR-40 cap) was NOT denied "
                f"(got {dec_b!r}: {reason_b})"
            )
        else:
            print(f"[self-test] Regression B PASS: 20th file denied: {reason_b}")

        # -----------------------------------------------------------------
        # M11 — observe-channel fail-closed (spec §5 invariant 2, protocol §4).
        # In `approve` mode with an UNREACHABLE channel, an in-sandbox under-cap
        # Write that the AUTONOMOUS path ALLOWS must be DENIED by the combined
        # flow, and NO file may appear at the target. This drives the SAME
        # autonomous-decision-then-leg combine that main() runs.
        # -----------------------------------------------------------------
        m11_target = os.path.join(sandbox, "m11_should_not_exist.txt")
        cap_file_m11 = os.path.join(base, "conv_m11.capstate")  # missing => fresh, under cap
        m11_event = {
            "tool_name": "Write",
            "tool_input": {"file_path": m11_target, "content": "approve-mode write"},
        }
        # A guaranteed-unreachable AF_UNIX address (no host listening here).
        dead_channel = os.path.join(base, "no_such_observe.sock")

        # Sanity: with NO channel env, the autonomous path ALLOWS this op, proving
        # the deny below is the LEG's doing, not a deny-everything bug.
        saved = {
            k: os.environ.get(k)
            for k in (
                "HOARDPAD_SANDBOX_ROOT",
                "HOARDPAD_CAP_STATE_FILE",
                _OBSERVE_CHANNEL_ENV,
                _OBSERVE_MODE_ENV,
            )
        }
        os.environ["HOARDPAD_SANDBOX_ROOT"] = sandbox
        os.environ["HOARDPAD_CAP_STATE_FILE"] = cap_file_m11
        os.environ.pop(_OBSERVE_CHANNEL_ENV, None)
        os.environ.pop(_OBSERVE_MODE_ENV, None)
        try:
            base_dec, base_reason = decide(m11_event)

            # Now arm `approve` mode against the dead channel and run the combine.
            os.environ[_OBSERVE_CHANNEL_ENV] = dead_channel
            os.environ[_OBSERVE_MODE_ENV] = "approve"
            leg_dec, leg_reason = decide(m11_event)
            if os.environ.get(_OBSERVE_CHANNEL_ENV):
                leg_dec, leg_reason = _apply_observation_leg(m11_event, leg_dec, leg_reason)
        finally:
            for k, v in saved.items():
                if v is None:
                    os.environ.pop(k, None)
                else:
                    os.environ[k] = v

        if base_dec != "allow":
            failures.append(
                f"M11 setup FAIL: the autonomous path did NOT allow the in-sandbox under-cap "
                f"Write (got {base_dec!r}: {base_reason}) — cannot prove the leg's deny is real"
            )
        else:
            print(f"[self-test] M11 setup: autonomous path allows the op: {base_reason}")

        if leg_dec != "deny":
            failures.append(
                f"M11 fail-closed FAIL: approve mode + unreachable channel did NOT deny "
                f"the (autonomously-allowed) Write (got {leg_dec!r}: {leg_reason})"
            )
        else:
            print(f"[self-test] M11 fail-closed PASS: approve + dead channel denied: {leg_reason}")

        if os.path.exists(m11_target):
            failures.append(
                f"M11 fail-closed FAIL: a file appeared at {m11_target!r} despite the deny"
            )
        else:
            print("[self-test] M11 fail-closed PASS: no file written under the dead channel")

        # -----------------------------------------------------------------
        # PANEL-A1 — per-file grant leg (feature-ai-multidoc-panel §5.3 Path A).
        # Proves the leg is NON-VACUOUS and fail-closed:
        #   (1) Grant PASS: an OUT-OF-SANDBOX target that IS listed in a
        #       HOARDPAD_FILE_GRANTS file (+ a valid cap-state) => ALLOW
        #       (it was DENIED before the leg — asserted below by the env-absent
        #       baseline). This is the widen the leg exists to do.
        #   (2) Grant sibling REFUSE: with the SAME grants file (granting c.txt),
        #       a write to a sibling other.txt in the SAME out-of-sandbox dir =>
        #       DENY (per-file granularity, AC-MD-5 — no prefix/dir widening).
        #   (3) Fail-closed: with HOARDPAD_FILE_GRANTS pointing at a malformed /
        #       nonexistent file, the granted-path write => DENY (root-only
        #       fallback, AC-MD-6 / FR-MD-10).
        # -----------------------------------------------------------------
        # An out-of-sandbox directory (sibling of the sandbox) with a granted
        # file c.txt and a NON-granted sibling other.txt.
        grant_dir = os.path.join(base, "granted_dir")
        os.makedirs(grant_dir, exist_ok=True)
        grant_target = os.path.join(grant_dir, "c.txt")      # the granted path
        grant_sibling = os.path.join(grant_dir, "other.txt")  # NOT granted
        cap_file_pa1 = os.path.join(base, "conv_pa1.capstate")  # missing => under cap
        grants_ok = os.path.join(base, "grants_ok.json")
        with open(grants_ok, "w", encoding="utf-8") as fh:
            json.dump([grant_target], fh)  # JSON array of one absolute path

        grant_event = {
            "tool_name": "Write",
            "tool_input": {"file_path": grant_target, "content": "granted write"},
        }
        sibling_event = {
            "tool_name": "Write",
            "tool_input": {"file_path": grant_sibling, "content": "sibling write"},
        }

        saved_pa1 = {
            k: os.environ.get(k)
            for k in ("HOARDPAD_SANDBOX_ROOT", "HOARDPAD_CAP_STATE_FILE", _FILE_GRANTS_ENV)
        }
        os.environ["HOARDPAD_SANDBOX_ROOT"] = sandbox
        os.environ["HOARDPAD_CAP_STATE_FILE"] = cap_file_pa1
        try:
            # Baseline: with NO grants env, the granted path is out-of-sandbox and
            # MUST deny — proving the ALLOW below is the leg's doing, not a bug.
            os.environ.pop(_FILE_GRANTS_ENV, None)
            base_dec_pa1, base_reason_pa1 = decide(grant_event)

            # (1) Grant PASS — grants file lists the target => ALLOW.
            os.environ[_FILE_GRANTS_ENV] = grants_ok
            pass_dec, pass_reason = decide(grant_event)

            # (2) Grant sibling REFUSE — SAME grants file, sibling target => DENY.
            sib_dec, sib_reason = decide(sibling_event)

            # (3) Fail-closed — grants env points at a nonexistent file => DENY.
            os.environ[_FILE_GRANTS_ENV] = os.path.join(base, "no_such_grants.json")
            fc_dec, fc_reason = decide(grant_event)
        finally:
            for k, v in saved_pa1.items():
                if v is None:
                    os.environ.pop(k, None)
                else:
                    os.environ[k] = v

        if base_dec_pa1 != "deny":
            failures.append(
                f"PANEL-A1 setup FAIL: the granted path was NOT denied without the grants env "
                f"(got {base_dec_pa1!r}: {base_reason_pa1}) — cannot prove the leg's allow is real"
            )
        else:
            print(f"[self-test] PANEL-A1 setup: env-absent baseline denies the out-of-root path: {base_reason_pa1}")

        if pass_dec != "allow":
            failures.append(
                f"PANEL-A1 grant PASS FAIL: an out-of-sandbox target listed in the grants file "
                f"was NOT allowed (got {pass_dec!r}: {pass_reason})"
            )
        else:
            print(f"[self-test] PANEL-A1 grant PASS: granted out-of-root Write allowed: {pass_reason}")

        if sib_dec != "deny":
            failures.append(
                f"PANEL-A1 sibling REFUSE FAIL: a non-granted sibling in the granted dir was NOT "
                f"denied (got {sib_dec!r}: {sib_reason}) — grant must be per-file, not per-dir (AC-MD-5)"
            )
        else:
            print(f"[self-test] PANEL-A1 sibling REFUSE PASS: non-granted sibling denied: {sib_reason}")

        if fc_dec != "deny":
            failures.append(
                f"PANEL-A1 fail-closed FAIL: with a nonexistent grants file the granted path was "
                f"NOT denied (got {fc_dec!r}: {fc_reason}) — must fall back to root-only (FR-MD-10)"
            )
        else:
            print(f"[self-test] PANEL-A1 fail-closed PASS: malformed/missing grants file denied: {fc_reason}")

    if failures:
        for f in failures:
            print(f"[self-test] {f}", file=sys.stderr)
        print("[self-test] RESULT: FAIL", file=sys.stderr)
        return 1
    print("[self-test] RESULT: PASS")
    return 0


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        raise SystemExit(_self_test())
    main()
