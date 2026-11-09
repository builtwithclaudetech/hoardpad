// HarnessSupport.cs — shared composition + the production-hook subprocess driver (M9.3).
//
// This file owns the two reusable pieces every SC-5 gate needs:
//
//   1. HookDriver — drives the REAL production hook (hooks/deny_by_default.py) as a
//      `python3` subprocess: it pipes a crafted PreToolUse event JSON to stdin and parses
//      the hook's nested-JSON `hookSpecificOutput.permissionDecision`. This is the whole
//      point of SC-5(i): the SHIPPED python artifact is the thing being gated, not a
//      C# reimplementation of it. The hook reads its sandbox root + cap-state file from the
//      env vars production wires (HOARDPAD_SANDBOX_ROOT / HOARDPAD_CAP_STATE_FILE / caps).
//
//   2. RealStack — composes the production Core.Ai + Host artifacts into a live
//      AiFileOpsService over a temp open-folder + temp AppData tree: the real PathResolver,
//      the real PathResolverSandboxGate (canonicalize→evaluate), the real AtomicFileStore,
//      and the real O_EXCL CapStateStore. No fakes — a SC-5 gate that faked any of these
//      would not prove the safety property it claims to.
//
// linux-x64 only for assertions (C-6 / the M9.3 hard constraints): win-x64 runtime legs are
// Deferred. These gates run python3 + libc realpath/fsync, which are the Linux production legs.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;
using HoardPad.Host;

namespace HoardPad.AcceptanceHarness;

/// <summary>The hook's binary verdict, parsed from its nested-JSON <c>permissionDecision</c>.</summary>
internal enum HookDecision
{
    Allow,
    Deny,
}

/// <summary>The result of driving the production hook over one crafted event.</summary>
/// <param name="Decision">The parsed <c>allow</c>/<c>deny</c> verdict.</param>
/// <param name="Reason">The hook's <c>permissionDecisionReason</c> (empty when none).</param>
/// <param name="ExitCode">The hook process exit code (0 for the nested-JSON path; 2 is the deny fallback).</param>
/// <param name="RawStdout">The raw stdout (for diagnostics on a parse failure).</param>
internal readonly record struct HookOutcome(HookDecision Decision, string Reason, int ExitCode, string RawStdout);

/// <summary>
/// Drives the REAL production deny-by-default hook (<c>hooks/deny_by_default.py</c>) as a
/// <c>python3</c> subprocess — the SC-5(i) authority. The crafted PreToolUse event is piped to
/// stdin; the nested-JSON <c>hookSpecificOutput.permissionDecision</c> is parsed back. Exit code 2
/// (the no-stdout fallback) is honored as a deny. The hook reads its sandbox root / cap-state file /
/// caps from the environment, exactly as the host wires them in production.
/// </summary>
internal static class HookDriver
{
    /// <summary>The crafted PreToolUse event for a tool call (the shape <c>decide()</c> reads).</summary>
    internal static string BuildEvent(string toolName, IReadOnlyDictionary<string, string> toolInput)
    {
        // Match the exact key the hook reads: tool_name + tool_input{file_path, content|new_string,...}.
        var input = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in toolInput)
            input[kv.Key] = kv.Value;
        var ev = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tool_name"] = toolName,
            ["tool_input"] = input,
        };
        return JsonSerializer.Serialize(ev);
    }

    /// <summary>
    /// Run the production hook over <paramref name="eventJson"/> with the given env (sandbox root,
    /// cap-state file, caps). Returns the parsed decision. Never throws on a deny — a deny is the
    /// expected, asserted outcome.
    /// </summary>
    internal static async Task<HookOutcome> RunAsync(
        string eventJson, IReadOnlyDictionary<string, string?> env, CancellationToken ct)
    {
        string hookPath = ProductionHookPath();

        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(hookPath);

        // The host wires these env vars; the hook reads them. A null value means "unset" so a gate
        // can exercise the no-sandbox-root / no-cap-state fail-closed branches.
        foreach (var kv in env)
        {
            if (kv.Value is null)
                psi.Environment.Remove(kv.Key);
            else
                psi.Environment[kv.Key] = kv.Value;
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Drain stdout/stderr concurrently while writing stdin to avoid a pipe-buffer deadlock.
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.StandardInput.WriteAsync(eventJson.AsMemory(), ct).ConfigureAwait(false);
        proc.StandardInput.Close();

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);

        return Parse(stdout, proc.ExitCode);
    }

    /// <summary>
    /// Parse the hook's verdict. The nested-JSON form (exit 0 + <c>hookSpecificOutput</c>) is the
    /// primary path; exit code 2 with no parseable allow is the deny fallback. Anything that is not
    /// a positive nested-allow reads as a deny (fail closed — the same posture the host takes).
    /// </summary>
    internal static HookOutcome Parse(string stdout, int exitCode)
    {
        // Exit 2 = the minimal deny fallback (no nested JSON produced). Honor it.
        if (exitCode == 2)
            return new HookOutcome(HookDecision.Deny, "exit-2 deny fallback", exitCode, stdout);

        if (string.IsNullOrWhiteSpace(stdout))
            // No stdout and not exit-2: the host treats only a positive nested-allow as allow.
            return new HookOutcome(HookDecision.Deny, "no stdout (no positive allow)", exitCode, stdout);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("hookSpecificOutput", out JsonElement hso)
                && hso.TryGetProperty("permissionDecision", out JsonElement decisionEl))
            {
                string decision = decisionEl.GetString() ?? "deny";
                string reason = hso.TryGetProperty("permissionDecisionReason", out JsonElement r)
                    ? r.GetString() ?? string.Empty
                    : string.Empty;
                HookDecision d = string.Equals(decision, "allow", StringComparison.Ordinal)
                    ? HookDecision.Allow
                    : HookDecision.Deny;
                return new HookOutcome(d, reason, exitCode, stdout);
            }
        }
        catch (JsonException)
        {
            // Unparseable stdout => no positive allow => deny (fail closed).
        }

        return new HookOutcome(HookDecision.Deny, "no nested permissionDecision (no positive allow)", exitCode, stdout);
    }

    /// <summary>
    /// Resolve the bundled production hook (<c>hooks/deny_by_default.py</c>) by walking up from the
    /// test output dir to the repo root — the SAME resolver pattern the M5.2 HookHostTests use, so the
    /// harness drives the REAL shipped script (not a copy).
    /// </summary>
    internal static string ProductionHookPath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "hooks", "deny_by_default.py");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate hooks/deny_by_default.py walking up from the test output dir.");
    }
}

/// <summary>
/// A live composition of the production Core.Ai + Host artifacts over a temp open-folder sandbox and a
/// temp AppData tree: the real <see cref="PathResolver"/>, the real <see cref="PathResolverSandboxGate"/>,
/// the real <see cref="AtomicFileStore"/>, the real O_EXCL <see cref="CapStateStore"/>, and a real
/// <see cref="AiFileOpsService"/>. No fakes — this is the apparatus SC-5(i)/(ia) apply allowed ops
/// through and assert on-disk + activity-log + undo-journal over.
/// </summary>
internal sealed class RealStack : IDisposable
{
    /// <summary>The temp test root (deleted on dispose) holding the sandbox + AppData subtrees.</summary>
    internal string Base { get; }

    /// <summary>The resolved open-folder sandbox root (the AI's writable tree). All in-root writes go here.</summary>
    internal string SandboxRoot { get; }

    /// <summary>
    /// A placeholder cap-state file path the hook reads (HOARDPAD_CAP_STATE_FILE) for gates that drive the
    /// hook directly without the store having persisted a conversation. The real CapStateStore derives its
    /// OWN SHA-256-named files under <see cref="CapStateDirectory"/>; this path is only the env default.
    /// </summary>
    internal string CapStateFile { get; }

    /// <summary>The directory the CapStateStore persists conversation cap-state files into.</summary>
    internal string CapStateDirectory { get; }

    internal AtomicFileStore Store { get; }
    internal AppDataPaths Paths { get; }
    internal CapStateStore CapStore { get; }
    internal PathResolver Resolver { get; }
    internal PathResolverSandboxGate Gate { get; }
    internal AiFileOpsService Service { get; }

    internal string SessionId { get; } = "sess-" + Guid.NewGuid().ToString("N");

    internal RealStack(CapConfig? capConfig = null)
    {
        Base = Path.Combine(Path.GetTempPath(), $"hoardpad-sc5-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Base);

        SandboxRoot = Path.Combine(Base, "open-folder");
        Directory.CreateDirectory(SandboxRoot);

        string appData = Path.Combine(Base, "appdata");
        string recovery = Path.Combine(Base, "recovery");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(recovery);

        // The activity-log + undo-journal directories the AppDataPaths layout points at must exist
        // before the AtomicFileStore appends to them (it does not mkdir -p).
        Directory.CreateDirectory(Path.Combine(appData, "activity"));
        Directory.CreateDirectory(Path.Combine(appData, "undo"));

        CapStateDirectory = Path.Combine(Base, "capstate");
        Directory.CreateDirectory(CapStateDirectory);
        CapStateFile = Path.Combine(CapStateDirectory, "conv.capstate");

        Store = new AtomicFileStore();
        Paths = new AppDataPaths(appData, recovery);
        CapStore = new CapStateStore(CapStateDirectory, Store);
        Resolver = new PathResolver();

        // The production gate over the REAL resolved root — the canonicalize→evaluate chokepoint.
        NormcasedPath resolvedRoot = Resolver.ResolveCanonical(SandboxRoot, SandboxRoot);
        Gate = new PathResolverSandboxGate(Resolver, resolvedRoot, SandboxRoot);

        Service = new AiFileOpsService(
            Gate, Store, CapStore, Paths, SessionId, capConfig ?? CapConfig.Pinned);
    }

    /// <summary>An in-sandbox absolute path for <paramref name="relative"/> under the open-folder root.</summary>
    internal string InRoot(string relative) => Path.Combine(SandboxRoot, relative);

    /// <summary>An out-of-sandbox absolute path: a sibling of the open-folder root, under <see cref="Base"/>.</summary>
    internal string OutsideRoot(string name) => Path.Combine(Base, name);

    /// <summary>The env block the production hook reads (sandbox root, cap-state file, pinned caps).</summary>
    internal Dictionary<string, string?> HookEnv(CapConfig? capConfig = null)
    {
        CapConfig cfg = capConfig ?? CapConfig.Pinned;
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOARDPAD_SANDBOX_ROOT"] = SandboxRoot,
            ["HOARDPAD_CAP_STATE_FILE"] = CapStateFile,
            ["HOARDPAD_FR11_CAP"] = cfg.CreateCap.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["HOARDPAD_FR38_CAP"] = cfg.DestructiveCap.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["HOARDPAD_FR40_CAP"] = cfg.FilesTouchedCap.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["HOARDPAD_FR38_X_PCT"] = cfg.DestructiveMagnitudePercent.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// The env block for the PANEL-A1 per-file grant leg (feature-ai-multidoc-panel §5.3 Path A):
    /// the base <see cref="HookEnv"/> plus <c>HOARDPAD_FILE_GRANTS</c> pointing at
    /// <paramref name="grantsFile"/>. Passing <c>null</c> leaves the grant env unset (leg inert) —
    /// identical to <see cref="HookEnv"/>. Additive helper for the Sc5FileGrants gates only.
    /// </summary>
    internal Dictionary<string, string?> HookEnvWithGrants(string? grantsFile, CapConfig? capConfig = null)
    {
        Dictionary<string, string?> env = HookEnv(capConfig);
        env["HOARDPAD_FILE_GRANTS"] = grantsFile;
        return env;
    }

    /// <summary>
    /// Write a host-shaped grants file (a JSON array of absolute path strings) under <see cref="Base"/>
    /// and return its path — the exact wire contract <c>_read_file_grants</c> in the production hook
    /// reads (feature-ai-multidoc-panel §5.3 Path A). Each call gets a fresh unique file name.
    /// </summary>
    internal string WriteGrantsFile(params string[] grantedPaths)
    {
        string path = Path.Combine(Base, $"grants-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(grantedPaths));
        return path;
    }

    /// <summary>
    /// Write a RAW (possibly malformed) grants-file body under <see cref="Base"/> and return its path —
    /// for the AC-MD-6 fail-closed gates that feed non-JSON / non-array / non-string-element bodies.
    /// </summary>
    internal string WriteRawGrantsFile(string rawBody)
    {
        string path = Path.Combine(Base, $"grants-raw-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, rawBody);
        return path;
    }

    /// <summary>
    /// A real out-of-sandbox directory (sibling of the open-folder root, under <see cref="Base"/>),
    /// created on disk so the hook's <c>_resolve_normcased</c> (realpath) resolves it — mirrors the
    /// PANEL-A1 self-test's <c>granted_dir</c> fixture. Returns its absolute path.
    /// </summary>
    internal string OutsideDir(string name)
    {
        string dir = Path.Combine(Base, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Read the per-session activity log back as decoded NDJSON-equivalent record payloads (one per op).</summary>
    internal async Task<IReadOnlyList<string>> ReadActivityLogAsync(CancellationToken ct)
    {
        string logPath = Paths.ActivityLogPath(SessionId);
        if (!File.Exists(logPath))
            return Array.Empty<string>();
        AppendLogRepairResult repair = await Store
            .OpenAndRepairAsync(logPath, AppendReadMode.StopAtFirstBad, ct).ConfigureAwait(false);
        return repair.Records.Select(r => Encoding.UTF8.GetString(r.Span)).ToList();
    }

    /// <summary>
    /// The single conversation cap-state payload file the store persisted (this fresh stack drives exactly
    /// one conversation, so its SHA-256-named <c>.capstate</c> payload is unambiguous). Excludes the
    /// <c>.ver</c>/<c>.lock</c> sidecars and the unused placeholder <see cref="CapStateFile"/>.
    /// </summary>
    internal string SoleConversationCapStateFile()
    {
        string[] files = Directory.GetFiles(CapStateDirectory, "*.capstate")
            .Where(f => !f.EndsWith(".ver", StringComparison.Ordinal)
                     && !f.EndsWith(".lock", StringComparison.Ordinal)
                     && !string.Equals(f, CapStateFile, StringComparison.Ordinal))
            .ToArray();
        if (files.Length != 1)
            throw new InvalidOperationException(
                $"Expected exactly one persisted conversation cap-state file under '{CapStateDirectory}', found {files.Length}.");
        return files[0];
    }

    /// <summary>True iff at least one undo-journal entry file exists under the undo root (an applied destructive/create op).</summary>
    internal bool AnyUndoJournalEntries() =>
        Directory.Exists(Paths.UndoJournalRoot)
        && Directory.GetFiles(Paths.UndoJournalRoot, "*.entry").Length > 0;

    public void Dispose()
    {
        try { Directory.Delete(Base, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}

/// <summary>
/// Builders for the <see cref="FileOpRequest"/>/<see cref="FileOpContext"/> the shared service applies.
/// Mirrors the host's job: the host has already <c>stat</c>'d the target (existence + pre-op bytes) and
/// staged the before-image; here we synthesize that metadata for a deterministic gate.
/// </summary>
internal static class OpFactory
{
    internal static FileOpContext Context(DowngradeReason downgrade = DowngradeReason.None, bool counts = true) =>
        new(
            Ts: DateTimeOffset.UtcNow,
            Backend: AiBackend.ClaudeCli,
            ConfiguredMode: AgencyMode.AutoApply,
            AgencyMode: AgencyMode.AutoApply,
            DowngradeReason: downgrade,
            CountsAgainstPerTurnCaps: counts);

    /// <summary>A create op (target absent → FR-11). No before-image.</summary>
    internal static FileOpRequest Create(string rawTarget, string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return new FileOpRequest(
            Operation: new FileOperation(RequestId: "req-" + Guid.NewGuid().ToString("N")[..8], Op: FileOp.Create,
                Paths: new OpPaths(Target: rawTarget)),
            Content: bytes,
            Classification: new OpClassificationInput(WriteTool.Write, TargetExists: false, TargetPreOpBytes: 0, ProposedBytes: bytes.Length),
            BufferJournalId: "buf-" + Guid.NewGuid().ToString("N")[..8],
            UndoRef: "undo-" + Guid.NewGuid().ToString("N")[..8],
            PriorContent: null,
            PreOpContentHash: null,
            PreOpMtime: null,
            BytesBefore: 0,
            BytesAfter: bytes.Length);
    }

    /// <summary>An edit/overwrite op (target exists → FR-38/sub-threshold). Carries a before-image so it is undoable.</summary>
    internal static FileOpRequest Edit(string rawTarget, string newContent, long priorBytes, WriteTool tool = WriteTool.Edit)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(newContent);
        return new FileOpRequest(
            Operation: new FileOperation(RequestId: "req-" + Guid.NewGuid().ToString("N")[..8], Op: FileOp.Edit,
                Paths: new OpPaths(Target: rawTarget)),
            Content: bytes,
            Classification: new OpClassificationInput(tool, TargetExists: true, TargetPreOpBytes: priorBytes, ProposedBytes: bytes.Length),
            BufferJournalId: "buf-" + Guid.NewGuid().ToString("N")[..8],
            UndoRef: "undo-" + Guid.NewGuid().ToString("N")[..8],
            PriorContent: new BeforeImage(Kind: "inline"),
            PreOpContentHash: "sha256-stub",
            PreOpMtime: DateTimeOffset.UtcNow,
            BytesBefore: priorBytes,
            BytesAfter: bytes.Length);
    }
}
