// HookObservationChannel.cs — the M11 per-turn hook-observation IPC channel (Step 2a).
//
// docs/m11-channel-protocol.md is the BYTE-FOR-BYTE wire contract; this implements its Host leg.
// docs/escalation-ai-live-write-path.md §4 authorizes exactly this surface (the channel) under the
// §5 invariants. This file owns the OS IPC primitive HoardPad creates and the hook connects to:
//
//   linux-x64 (RUNS — the acceptance platform):
//     a per-turn AF_UNIX / SOCK_STREAM Unix domain socket, listening at a HoardPad-owned per-turn
//     path under the system temp runtime dir, keyed to the turn's workingRoot + conversationKey.
//     NDJSON framing (protocol §1.2): one UTF-8 JSON object per '\n'-terminated line, 64 KiB bound.
//
//   win-x64 (COMPILE-CONFIRMED ONLY — runtime DEFERRED, same posture as the Windows AI-spine legs):
//     a per-turn named pipe (\\.\pipe\hoardpad-observe-<turn-token>). Guarded by
//     OperatingSystem.IsWindows() + [SupportedOSPlatform("windows")] so it compiles for win-x64 and
//     never runs on Linux. NOT asserted "Met" — a Windows-hardware milestone validates it.
//
// FAIL-CLOSED IS THE WHOLE POINT (protocol §4, spec §5 invariants 2/6):
//   - RequestDecisionAsync returns HookDecisionResponse.Deny on EVERY non-answer: a cancelled
//     deadline, a fault in the App confirm, or a torn write of the decision line. It NEVER throws a
//     fault a caller could (mis)read as an allow, and there is NO code path where it returns an
//     allow without (a) the App explicitly answering allow AND (b) the decision line being written
//     back to the still-blocked hook.
//   - DISPOSAL is the fail-closed trigger: disposing the endpoint tears down the socket/pipe, so a
//     still-blocked hook reading a closed channel gets EOF and DENIES (protocol §4 "closed").
//   - The channel can only NARROW (spec §5 invariant 1): the hook computed its autonomous deny/allow
//     FIRST; this carries observation + the App's narrowing confirm; it cannot manufacture an allow
//     for a hook-deny (the hook never even sends an `ask` for a deny — protocol §3).

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.Host;

/// <summary>
/// The M11 per-turn hook-observation channel (docs/m11-channel-protocol.md). Implements
/// <see cref="IHookObservationChannel"/>: on linux-x64 a per-turn <c>AF_UNIX</c> Unix domain socket
/// (runs); on win-x64 a named pipe (compile-confirmed, runtime Deferred). The App composes this with
/// its approve-each confirm delegate; the seam <see cref="IHookObservationChannel.OpenForTurnAsync"/>
/// is unchanged.
/// </summary>
public sealed class HookObservationChannel : IHookObservationChannel
{
    // A single message MUST be <= 64 KiB (protocol §1.2); a larger line is malformed => fail-closed.
    internal const int MaxMessageBytes = 64 * 1024;

    // The HoardPad-owned runtime dir leaf the per-turn sockets live under (system-temp based, like
    // the M5.2 self-test scratch). The dir is created 0700 on Linux so no other user can connect.
    private const string RuntimeDirLeaf = "hoardpad-observe";

    private readonly Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> _confirm;
    private readonly string _runtimeDir;

    /// <summary>
    /// Construct the channel with the App's approve-each <paramref name="confirm"/> delegate — the
    /// App shows its in-panel confirm (<b>Deny focused</b>) and returns the narrowing answer. The
    /// channel transports that answer to the blocked hook; it adds NO decision logic of its own. A
    /// confirm that throws or is cancelled resolves to <see cref="HookDecisionResponse.Deny"/>
    /// (invariant 2) — the channel never lets a confirm fault surface as an allow.
    /// </summary>
    /// <param name="confirm">
    /// The App's approve-each answer source. Invoked under the no-reply deadline; its allow/deny is
    /// ANDed (in the hook) with the hook's already-<see cref="HookDecision.Allow"/> verdict.
    /// </param>
    /// <param name="runtimeDir">
    /// The HoardPad-owned dir the per-turn sockets/pipes live under. Defaults to a
    /// <c>hoardpad-observe</c> leaf under the system temp dir (created 0700 on Linux).
    /// </param>
    public HookObservationChannel(
        Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> confirm,
        string? runtimeDir = null)
    {
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _runtimeDir = runtimeDir is { Length: > 0 }
            ? Path.GetFullPath(runtimeDir)
            : Path.Combine(Path.GetTempPath(), RuntimeDirLeaf);
        CreateRuntimeDir(_runtimeDir);
    }

    /// <summary>
    /// Create the per-turn runtime dir owner-only with NO world-accessible window. On Linux the dir is
    /// created ATOMICALLY at mode 0700 via <c>mkdir(2)</c> (EEXIST = already-present = success), so there
    /// is never a 0755 window between create and chmod for another local user to plant/connect a socket
    /// (Security W3 — the confirm round is on the safety path). On Windows the dir is created normally
    /// (named-pipe ACLs, not POSIX modes, govern access there).
    /// </summary>
    private static void CreateRuntimeDir(string runtimeDir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(runtimeDir);
            return;
        }

        // Atomic owner-only create on Linux. mkdir applies the mode under the process umask; HoardPad's
        // umask is not guaranteed 0, so we ALSO clamp the dir to 0700 after a successful create — but the
        // create itself never opens a 0755 window: mkdir's mode is at MOST 0700, the umask only narrows it.
        // The path is marshalled as a NUL-terminated UTF-8 byte array (libc's filename charset) so a
        // non-ASCII temp dir is passed through verbatim, independent of DllImport's default charset.
        byte[] pathBytes = Encoding.UTF8.GetBytes(runtimeDir);
        Array.Resize(ref pathBytes, pathBytes.Length + 1);   // NUL terminator.
        int rc = NativeMkdir(pathBytes, OwnerOnlyDirMode);
        if (rc == 0)
        {
            // Created by us at <= 0700: ensure exactly 0700 even if a non-zero umask narrowed it further
            // (a narrower mode is still safe; this just normalizes to the intended owner-rwx).
            TrySetOwnerOnly(runtimeDir, isDirectory: true);
            return;
        }

        int err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        if (err == Eexist)
        {
            // Already present (our own prior run, or the caller pre-created it). Do NOT widen it; just
            // re-clamp to owner-only so a pre-existing dir is hardened too. We never create a new window.
            TrySetOwnerOnly(runtimeDir, isDirectory: true);
            return;
        }

        // Any other errno (a missing parent, a permission failure): fall back to the managed create so the
        // failure surfaces with a normal .NET exception, then best-effort clamp. This path is the only one
        // that could theoretically open a brief window, but it is reached only when atomic mkdir failed for
        // a reason other than EEXIST (e.g. ENOENT on a missing parent) — i.e. the dir did not yet exist and
        // mkdir would not have raced anyway.
        Directory.CreateDirectory(runtimeDir);
        TrySetOwnerOnly(runtimeDir, isDirectory: true);
    }

    private const int OwnerOnlyDirMode = 0x1C0;   // 0700 (S_IRWXU) — owner rwx, no group/other.
    private const int Eexist = 17;                // POSIX EEXIST.

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int NativeMkdir(byte[] path, int mode);

    public Task<IHookObservationEndpoint> OpenForTurnAsync(
        NormcasedPath workingRoot, string conversationKey, CancellationToken ct)
    {
        if (workingRoot.IsDefault)
            throw new ArgumentException(
                "The turn's working root must be a resolved NormcasedPath (never the default sentinel).",
                nameof(workingRoot));
        if (string.IsNullOrEmpty(conversationKey))
            throw new ArgumentException("A conversation key is required.", nameof(conversationKey));
        ct.ThrowIfCancellationRequested();

        // The per-turn token binds the endpoint to the working root + conversation key (protocol
        // §1.1). It is the HOARDPAD_OBSERVE_CHANNEL address: a short, stable, collision-resistant
        // leaf that also fits the AF_UNIX sun_path 108-byte limit.
        string turnToken = EndpointToken.Derive(workingRoot.Value, conversationKey);

        IHookObservationEndpoint endpoint = OperatingSystem.IsWindows()
            ? WindowsObservationEndpoint.Open(turnToken, _confirm)
            : UnixObservationEndpoint.Open(Path.Combine(_runtimeDir, turnToken + ".sock"), _confirm);

        return Task.FromResult(endpoint);
    }

    /// <summary>
    /// The address (socket path on Linux / pipe name on Windows) the host injects into the hook via
    /// <c>HOARDPAD_OBSERVE_CHANNEL</c>. Used by <see cref="ToolPermissionHookHost"/>'s env injection
    /// (so the env value and the bound primitive are derived from the SAME inputs and cannot drift).
    /// </summary>
    public string ResolveChannelAddress(NormcasedPath workingRoot, string conversationKey)
    {
        if (workingRoot.IsDefault)
            throw new ArgumentException(
                "The turn's working root must be a resolved NormcasedPath.", nameof(workingRoot));
        if (string.IsNullOrEmpty(conversationKey))
            throw new ArgumentException("A conversation key is required.", nameof(conversationKey));

        string turnToken = EndpointToken.Derive(workingRoot.Value, conversationKey);
        return OperatingSystem.IsWindows()
            ? WindowsObservationEndpoint.PipeName(turnToken)
            : Path.Combine(_runtimeDir, turnToken + ".sock");
    }

    // ---- per-turn endpoint token (stable across processes; AF_UNIX-length-safe) ----

    private static class EndpointToken
    {
        /// <summary>
        /// A stable, filesystem-safe, length-bounded per-turn token derived from the working root +
        /// conversation key. SHA-256 (not <c>GetHashCode</c>, which is per-process randomized) so a
        /// continuation invocation maps the same turn to the same endpoint, exactly like
        /// <see cref="CapStateStore.StableFileName"/>. 16 hex chars (64 bits) keeps the full
        /// <c>sun_path</c> under the 108-byte AF_UNIX limit even under a long temp dir.
        /// </summary>
        internal static string Derive(string workingRoot, string conversationKey)
        {
            byte[] material = Encoding.UTF8.GetBytes(workingRoot + " " + conversationKey);
            byte[] hash = System.Security.Cryptography.SHA256.HashData(material);
            return "hp-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }
    }

    internal static void TrySetOwnerOnly(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            UnixFileMode mode = isDirectory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute   // 0700
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;                              // 0600
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
            // Best-effort hardening; the socket still functions if chmod is unavailable.
        }
    }
}

/// <summary>
/// Shared NDJSON framing + the fail-closed approve-each round, transport-agnostic (protocol §1.2,
/// §2, §3, §4). Both the Linux socket endpoint and the Windows pipe endpoint compose this over their
/// own <see cref="System.IO.Stream"/>; all the safety-critical decision-line logic lives HERE once.
/// </summary>
internal sealed class HookObservationSession
{
    private readonly Stream _stream;
    private readonly Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> _confirm;
    private readonly NdjsonLineReader _reader;
    // True once the owning endpoint has begun teardown: a decision write is then SKIPPED (the hook
    // reads a closed channel and denies on its side anyway — protocol §4). This keeps the fail-closed
    // write-back off the disposal path, so DisposeAsync can never deadlock on a stream it is tearing down.
    private readonly Func<bool> _isTorndown;

    internal HookObservationSession(
        Stream stream,
        Func<HookDecisionRequest, CancellationToken, Task<HookDecisionResponse>> confirm,
        Func<bool> isTorndown)
    {
        _stream = stream;
        _confirm = confirm;
        _isTorndown = isTorndown;
        _reader = new NdjsonLineReader(stream, HookObservationChannel.MaxMessageBytes);
    }

    /// <summary>
    /// Yield each hook <c>report</c> as a <see cref="HookOpEvent"/> (protocol §2). An <c>ask</c> line
    /// (protocol §3, approve mode) is handled INLINE on this same connection: the read loop drives
    /// <see cref="RequestDecisionAsync"/> (the App confirm + the <c>decision</c> write-back) and keeps
    /// reading — the ask never escapes as a report. A malformed/oversized/other-type line is SKIPPED
    /// (observation is never on the safety path — protocol §2). The sequence completes on EOF (the
    /// hook closed) or on cancellation (turn end / Stop / kill).
    /// </summary>
    internal async IAsyncEnumerable<HookOpEvent> ObserveAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;   // turn ended / Stop / no-reply kill — end the observe loop.
            }
            catch (IOException)
            {
                yield break;   // the hook connection broke — end the observe loop (no report lost on the safety path).
            }

            if (line is null)
                yield break;   // EOF — the hook closed its end of the channel.

            if (WireMessage.TryParseReport(line, out HookOpEvent? ev) && ev is not null)
            {
                yield return ev;
            }
            else if (WireMessage.TryParseAsk(line, out HookDecisionRequest? ask) && ask is not null)
            {
                // The approve-each safety path: confirm + write the decision back to the blocked hook.
                // RequestDecisionAsync is fail-closed and never throws, so the read loop never breaks
                // on an ask — it answers (deny on any fault) and continues observing.
                _ = await RequestDecisionAsync(ask, ct).ConfigureAwait(false);
            }
            // else: a decision / malformed / oversized line — skip it (not on the observe safety path).
        }
    }

    /// <summary>
    /// The approve-each round (protocol §3) — the SAFETY PATH. Invoke the App's confirm for the
    /// hook-allowed op carried by <paramref name="request"/>, then write the <c>decision</c> line
    /// back to the blocked hook. FAIL-CLOSED by construction (protocol §4, invariant 2): the ONLY
    /// way an allow is returned is when (a) the App's confirm resolves to allow, AND (b) the decision
    /// line is written back without fault. A cancelled deadline, a confirm that throws, or a torn
    /// write ALL resolve to <see cref="HookDecisionResponse.Deny"/>. This method never throws.
    /// </summary>
    internal async Task<HookDecisionResponse> RequestDecisionAsync(
        HookDecisionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The fail-closed answer the blocked hook will read for ANY non-allow outcome. We bind the
        // tool_use_id up front so even an early fault writes back a correctly-keyed deny (the hook
        // validates the id; a mismatch is itself a deny — protocol §3).
        HookDecisionResponse answer = HookDecisionResponse.Deny(request.ToolUseId);

        try
        {
            // Ask the App. A cancelled deadline (no-reply timer / Stop / channel fault) surfaces as
            // OperationCanceledException; ANY other exception from the confirm is caught below. In
            // every such case `answer` stays Deny — there is no branch that turns a fault into allow.
            HookDecisionResponse appAnswer = await _confirm(request, ct).ConfigureAwait(false);

            // Honor the App's answer ONLY when it explicitly allowed AND it is for THIS op. A
            // mismatched id (a stale/forged answer) is treated as a non-answer => deny (protocol §3).
            if (appAnswer.Allow &&
                string.Equals(appAnswer.ToolUseId, request.ToolUseId, StringComparison.Ordinal))
            {
                answer = HookDecisionResponse.Allowed(request.ToolUseId);
            }
            // else: the App denied, gave no answer, or answered for a different op => stays Deny.
        }
        catch (OperationCanceledException)
        {
            // The no-reply deadline tripped (or a Stop / kill cancelled). Fail-closed: stays Deny.
            // We DO NOT rethrow — a thrown OperationCanceledException could be (mis)read by a caller
            // as anything other than a deny; the contract is a Deny RESPONSE, never a thrown fault.
        }
#pragma warning disable CA1031 // Fail-closed: ANY confirm fault must become a deny, never propagate as a non-deny.
        catch
        {
            // ANY confirm fault (a bug in the App's confirm, a disposed dependency, …) => deny.
        }
#pragma warning restore CA1031

        // Write the decision line back to the still-blocked hook. If the write itself faults (the
        // hook already closed, the socket is gone), we DOWNGRADE to deny and report deny: a hook
        // that cannot read our decision is, on its side, in the "closed before a decision" branch
        // (protocol §4) and denies anyway. We never upgrade a write-fault into an allow. The SAME
        // token (linked from the endpoint's _lifetime by the caller) bounds the write, so a stuck
        // write cannot block the read loop past teardown — a cancelled write resolves to a deny.
        if (!await TryWriteDecisionAsync(answer, ct).ConfigureAwait(false) && answer.Allow)
            answer = HookDecisionResponse.Deny(request.ToolUseId);

        return answer;
    }

    private async Task<bool> TryWriteDecisionAsync(HookDecisionResponse answer, CancellationToken ct)
    {
        // If the endpoint has begun teardown, DO NOT write to the stream it is disposing — the hook
        // reads a closed channel and denies on its side (protocol §4), and writing here would race the
        // disposal. Returning false makes RequestDecisionAsync downgrade any (impossible) allow to deny.
        if (_isTorndown())
            return false;

        // The decision write is the ONLY host→hook write and is issued serially from the single read
        // loop's ask handling, so no write gate is needed (one writer, one record per ask). The write
        // AND flush are both bounded by `ct` (linked from _lifetime): a stuck hook that never drains
        // our decision cannot wedge the inline read loop — cancellation surfaces OperationCanceledException,
        // which the catch below turns into `false` => a non-allow (the hook reads a closed channel => deny).
        try
        {
            byte[] frame = WireMessage.EncodeDecision(answer);
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // A failed/cancelled decision-write must fail closed (the hook reads a closed channel => deny).
        catch
        {
            // Includes OperationCanceledException from a cancelled write/flush: a cancelled write is a
            // non-answer, never an allow — RequestDecisionAsync downgrades any would-be allow to deny.
            return false;
        }
#pragma warning restore CA1031
    }
}

/// <summary>
/// NDJSON line reader (protocol §1.2): reads UTF-8 bytes up to a <c>'\n'</c>, enforcing the 64 KiB
/// per-message bound. A line that exceeds the bound is returned to the caller for malformed handling
/// (the parser rejects it) rather than buffered unboundedly — a hostile/oversized frame can never
/// exhaust memory or be silently accepted.
/// </summary>
internal sealed class NdjsonLineReader
{
    private const byte Newline = (byte)'\n';

    private readonly Stream _stream;
    private readonly int _maxBytes;
    private readonly byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    internal NdjsonLineReader(Stream stream, int maxBytes)
    {
        _stream = stream;
        _maxBytes = maxBytes;
    }

    /// <summary>
    /// Read one UTF-8 line (without the terminating <c>'\n'</c>). Returns <c>null</c> at EOF. A line
    /// whose byte length would exceed the 64 KiB bound returns the over-length bytes decoded as a
    /// (deliberately) un-parseable string so the caller's parser rejects it (protocol §4 malformed).
    /// </summary>
    internal async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var accumulated = new System.IO.MemoryStream();
        while (true)
        {
            // Serve any buffered bytes first.
            int nl = IndexOfNewline();
            if (nl >= 0)
            {
                accumulated.Write(_buffer, _start, nl - _start);
                _start = nl + 1;   // consume the newline.
                return Decode(accumulated);
            }

            // No newline in the buffer — drain what we have into the accumulator and refill.
            if (_end > _start)
            {
                accumulated.Write(_buffer, _start, _end - _start);
                _start = _end = 0;
            }

            // Enforce the size bound on the accumulating (still-unterminated) line.
            if (accumulated.Length > _maxBytes)
            {
                // Drain the rest of this oversized line up to the next newline (or EOF) so the next
                // read starts on a clean message boundary, then return a marker the parser rejects.
                await DrainToNewlineOrEofAsync(ct).ConfigureAwait(false);
                return OversizedMarker;
            }

            int read = await _stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                // EOF. A trailing partial line without a terminating '\n' is treated as EOF (no message).
                return null;
            }
            _start = 0;
            _end = read;
        }
    }

    private async Task DrainToNewlineOrEofAsync(CancellationToken ct)
    {
        while (true)
        {
            int nl = IndexOfNewline();
            if (nl >= 0)
            {
                _start = nl + 1;
                return;
            }
            _start = _end = 0;
            int read = await _stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length), ct).ConfigureAwait(false);
            if (read == 0)
                return;   // EOF.
            _start = 0;
            _end = read;
        }
    }

    private int IndexOfNewline()
    {
        for (int i = _start; i < _end; i++)
        {
            if (_buffer[i] == Newline)
                return i;
        }
        return -1;
    }

    // A sentinel the JSON parser cannot parse as a report/decision => the caller fails closed on it.
    internal const string OversizedMarker = " OVERSIZED ";

    private static string Decode(System.IO.MemoryStream ms)
    {
        // Trim a trailing '\r' so CRLF-terminated lines parse identically to LF (defensive — the
        // protocol is LF, but a tolerant trim costs nothing and removes a malformed-by-CR footgun).
        ReadOnlySpan<byte> bytes = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
            bytes = bytes[..^1];
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>
/// The byte-for-byte wire (de)serialization (protocol §2, §3). Pure, allocation-light, and the SINGLE
/// place the JSON shapes are encoded/decoded — so the Linux and Windows endpoints agree exactly and a
/// protocol change is one edit. All parsing is fail-closed: any malformed/missing/oversized field
/// yields "not a report" / a deny, never a silent allow.
/// </summary>
internal static class WireMessage
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Parse a hook→host <c>report</c> line (protocol §2) into a <see cref="HookOpEvent"/>. Returns
    /// <c>false</c> for any line that is not a well-formed report (wrong/missing <c>type</c>, missing
    /// required fields, bad JSON, or the oversized marker) — the caller then skips it (observation is
    /// not on the safety path).
    /// </summary>
    internal static bool TryParseReport(string line, out HookOpEvent? ev)
    {
        ev = null;
        if (string.IsNullOrEmpty(line) || ReferenceEquals(line, NdjsonLineReader.OversizedMarker))
            return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            System.Text.Json.JsonElement root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return false;
            if (!TryGetString(root, "type", out string? type) || type != "report")
                return false;
            if (!TryGetString(root, "tool_use_id", out string? toolUseId) || toolUseId is null)
                return false;
            if (!TryGetString(root, "tool_name", out string? toolName) || toolName is null)
                return false;
            // target_path may be "" (a denied-before-resolve op, protocol §2) — present-but-empty is valid.
            string targetPath = TryGetString(root, "target_path", out string? tp) && tp is not null ? tp : string.Empty;
            if (!TryGetString(root, "op_class", out string? opClassRaw) || !TryParseOpClass(opClassRaw, out HookOpClass opClass))
                return false;
            long magnitude = root.TryGetProperty("magnitude", out System.Text.Json.JsonElement mag) &&
                             mag.ValueKind == System.Text.Json.JsonValueKind.Number &&
                             mag.TryGetInt64(out long m)
                ? m : 0L;
            if (!TryGetString(root, "hook_decision", out string? decRaw) || !TryParseDecision(decRaw, out HookDecision decision))
                return false;

            ev = new HookOpEvent(toolUseId, toolName, targetPath, opClass, magnitude, decision);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;   // malformed JSON => not a report => skipped (protocol §4 malformed).
        }
    }

    /// <summary>
    /// Parse a hook→host <c>ask</c> line (protocol §3) into a <see cref="HookDecisionRequest"/>.
    /// Returns <c>false</c> for any malformed/non-ask line.
    /// </summary>
    internal static bool TryParseAsk(string line, out HookDecisionRequest? request)
    {
        request = null;
        if (string.IsNullOrEmpty(line) || ReferenceEquals(line, NdjsonLineReader.OversizedMarker))
            return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            System.Text.Json.JsonElement root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return false;
            if (!TryGetString(root, "type", out string? type) || type != "ask")
                return false;
            if (!TryGetString(root, "tool_use_id", out string? toolUseId) || toolUseId is null)
                return false;
            if (!TryGetString(root, "tool_name", out string? toolName) || toolName is null)
                return false;
            string targetPath = TryGetString(root, "target_path", out string? tp) && tp is not null ? tp : string.Empty;
            if (!TryGetString(root, "op_class", out string? opClassRaw) || !TryParseOpClass(opClassRaw, out HookOpClass opClass))
                return false;

            request = new HookDecisionRequest(toolUseId, toolName, targetPath, opClass);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Encode a host→hook <c>decision</c> line (protocol §3): <c>{"type":"decision","tool_use_id":"…","allow":bool}\n</c>.
    /// </summary>
    internal static byte[] EncodeDecision(HookDecisionResponse answer)
    {
        // Hand-build the object so the field order + names match the protocol exactly and the line is
        // a single '\n'-terminated record. tool_use_id is JSON-escaped via the serializer.
        string json = System.Text.Json.JsonSerializer.Serialize(
            new DecisionWire(type: "decision", tool_use_id: answer.ToolUseId, allow: answer.Allow),
            DecisionJsonOptions);
        return Utf8NoBom.GetBytes(json + "\n");
    }

    /// <summary>
    /// Encode a hook→host <c>report</c> line (protocol §2). Used by the test stand-in hook driver so
    /// the round-trip is proved against the SAME encoder the production hook's protocol mirrors.
    /// </summary>
    internal static byte[] EncodeReport(HookOpEvent ev)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(
            new ReportWire(
                type: "report",
                tool_use_id: ev.ToolUseId,
                tool_name: ev.ToolName,
                target_path: ev.TargetPath,
                op_class: OpClassToWire(ev.OpClass),
                magnitude: ev.Magnitude,
                hook_decision: ev.HookDecision == HookDecision.Allow ? "allow" : "deny"),
            DecisionJsonOptions);
        return Utf8NoBom.GetBytes(json + "\n");
    }

    /// <summary>Encode a hook→host <c>ask</c> line (protocol §3) — used by the test hook driver.</summary>
    internal static byte[] EncodeAsk(HookDecisionRequest req)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(
            new AskWire(
                type: "ask",
                tool_use_id: req.ToolUseId,
                tool_name: req.ToolName,
                target_path: req.TargetPath,
                op_class: OpClassToWire(req.OpClass)),
            DecisionJsonOptions);
        return Utf8NoBom.GetBytes(json + "\n");
    }

    private static readonly System.Text.Json.JsonSerializerOptions DecisionJsonOptions = new()
    {
        WriteIndented = false,
    };

    private static bool TryGetString(System.Text.Json.JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out System.Text.Json.JsonElement el) ||
            el.ValueKind != System.Text.Json.JsonValueKind.String)
            return false;
        value = el.GetString();
        return value is not null;
    }

    private static bool TryParseOpClass(string? raw, out HookOpClass opClass)
    {
        switch (raw)
        {
            case "create": opClass = HookOpClass.Create; return true;
            case "overwrite": opClass = HookOpClass.Overwrite; return true;
            case "edit": opClass = HookOpClass.Edit; return true;
            default: opClass = HookOpClass.Edit; return false;
        }
    }

    private static string OpClassToWire(HookOpClass opClass) => opClass switch
    {
        HookOpClass.Create => "create",
        HookOpClass.Overwrite => "overwrite",
        _ => "edit",
    };

    private static bool TryParseDecision(string? raw, out HookDecision decision)
    {
        switch (raw)
        {
            case "allow": decision = HookDecision.Allow; return true;
            case "deny": decision = HookDecision.Deny; return true;
            default: decision = HookDecision.Deny; return false;   // unknown verdict => the safe reading.
        }
    }

    // System.Text.Json source-friendly wire records (snake_case names match the protocol verbatim).
    private sealed record DecisionWire(string type, string tool_use_id, bool allow);
    private sealed record ReportWire(
        string type, string tool_use_id, string tool_name, string target_path,
        string op_class, long magnitude, string hook_decision);
    private sealed record AskWire(string type, string tool_use_id, string tool_name, string target_path, string op_class);
}
