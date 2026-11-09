// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
using System.Collections.Immutable;

namespace HoardPad.Core.Host.Abstractions;

// The IAgentBackend signatures in TD §2.3 reference AgentTurnRequest and AgentStreamEvent but do
// NOT define them (TD §2.3 specifies only IAgentBackend / IAgentTurn / LaunchDescriptor verbatim).
// They are introduced here as the MINIMAL transport shapes the seam needs to compile. The full
// turn contract (the §4.1 prompt/flags carrier and the §4.2 stream-json event taxonomy) is owned
// by M4.2 (Core.Ai provider logic) and M5.2 (Host AgentBackend); those milestones flesh these out.

/// <summary>
/// One turn's launch input: the prompt, the resolved open-folder root the child runs in (WorkingDirectory per
/// §4.1 — no --cwd flag), and the FR-24 §4.1 launch arguments the Archetype-A orchestration composed (M4.2,
/// <c>HoardPad.Core.Ai.Provider.ClaudeLaunchContract</c>). The host passes <see cref="LaunchArgs"/> through to the
/// subprocess unchanged — it does NOT author the flag set (the orchestration owns the contract, incl. the
/// never-bypass / no-cwd invariants).
/// </summary>
/// <remarks>
/// Safety by construction (Gate-1): the constructor calls <see cref="LaunchArgsSafety.AssertSafe"/> on
/// <see cref="LaunchArgs"/>. A request carrying <c>--cwd</c>, <c>--dangerously-skip-permissions</c>, a
/// <c>bypassPermissions</c> token, or an empty arg list CANNOT be instantiated — the type system closes the M5.2
/// bypass hole independently of whether M5.2 remembers to check. Always construct via
/// <c>HoardPad.Core.Ai.Provider.ArchetypeAOrchestration.Begin</c>, which calls
/// <c>ClaudeLaunchContract.Compose</c> to produce a compliant arg list.
/// </remarks>
public sealed record AgentTurnRequest
{
    /// <summary>The agent's prompt / instruction text.</summary>
    public string Prompt { get; init; }

    /// <summary>The resolved open-folder root the child process runs in (set as WorkingDirectory; no --cwd flag).</summary>
    public NormcasedPath WorkingRoot { get; init; }

    /// <summary>
    /// The FR-24 §4.1 launch arguments composed by the Archetype-A orchestration. Guaranteed non-empty and free of
    /// disarm tokens (validated by <see cref="LaunchArgsSafety.AssertSafe"/> on construction).
    /// </summary>
    public ImmutableArray<string> LaunchArgs { get; init; }

    /// <summary>
    /// Constructs a turn request, asserting the <see cref="LaunchArgsSafety"/> invariants on <paramref name="launchArgs"/>
    /// before storing them. Throws <see cref="System.ArgumentException"/> if <paramref name="launchArgs"/> is
    /// empty/default or contains a forbidden token.
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="launchArgs"/> is default/empty or contains <c>--cwd</c>,
    /// <c>--dangerously-skip-permissions</c>, or any arg value containing <c>bypassPermissions</c>.
    /// </exception>
    public AgentTurnRequest(string prompt, NormcasedPath workingRoot, ImmutableArray<string> launchArgs)
    {
        LaunchArgsSafety.AssertSafe(launchArgs);
        Prompt = prompt;
        WorkingRoot = workingRoot;
        LaunchArgs = launchArgs;
    }
}

/// One parsed stream-json event surfaced from the in-flight turn. The host parses the CLI's
/// stream-json lines; the raw line is carried through for the §4.2 domain-side liveness match.
public sealed record AgentStreamEvent(string RawJsonLine);
