using System.Text.Json.Serialization;

namespace HoardPad.Core.Records;

/// <summary>
/// The §9.3 activity-log operation vocabulary.
/// <c>read</c>/<c>blocked</c> are activity-log-only — they never enter the undo journal
/// (a read mutates nothing; a blocked op never applied), so they have no before-image (§9.3 mi5,
/// §9.4 op-enum subset). Serializes to its exact lowercase wire token via
/// <see cref="System.Text.Json.JsonNamingPolicy.KebabCaseLower"/>.
/// </summary>
[JsonConverter(typeof(KebabLowerEnumConverter))]
public enum ActivityOp
{
    Read,
    Create,
    Copy,
    Move,
    Edit,
    Delete,
    Blocked,
}

/// <summary>
/// The §9.4 undo-journal operation vocabulary — the create/copy/move/edit/delete subset of
/// <see cref="ActivityOp"/> (no <c>read</c>/<c>blocked</c>; no <c>rename</c> — a rename canonicalizes
/// to <see cref="Move"/>, §9.4 MA11). This is the op set the file-ops record (§4.5) and the reverse
/// decision (M1.4) operate over.
/// </summary>
[JsonConverter(typeof(KebabLowerEnumConverter))]
public enum FileOp
{
    Create,
    Copy,
    Move,
    Edit,
    Delete,
}

/// <summary>The §9.3 <c>outcome</c> vocabulary. <c>queued</c> = failed-closed + parked (FR-39).</summary>
[JsonConverter(typeof(KebabLowerEnumConverter))]
public enum ActivityOutcome
{
    Applied,
    Blocked,
    Undone,
    Failed,
    Queued,
}

/// <summary>The §9.3 <c>backend</c> vocabulary. Kebab-lower wire tokens: <c>claude-cli</c>, <c>anthropic-api</c>, <c>local</c>.</summary>
[JsonConverter(typeof(KebabLowerEnumConverter))]
public enum AiBackend
{
    ClaudeCli,
    AnthropicApi,
    Local,
}

/// <summary>
/// The §9.3 agency-mode vocabulary — used for both <c>configuredMode</c> (user-configured, FR-27)
/// and <c>agencyMode</c> (the effective mode that gated the op, MA13).
/// </summary>
[JsonConverter(typeof(KebabLowerEnumConverter))]
public enum AgencyMode
{
    AutoApply,
    ApproveEach,
    ReadOnly,
}

/// <summary>
/// The §9.3 <c>downgradeReason</c> vocabulary (MA13) — why the op was gated/downgraded, or
/// <see cref="None"/> when <c>agencyMode == configuredMode</c> with no trigger. <c>home-root</c> and
/// <c>large-root</c> are split (R3-mi8) so a sign-off fixture can tell which FR-10(a) trigger fired.
/// </summary>
[JsonConverter(typeof(KebabLowerEnumConverter))]
public enum DowngradeReason
{
    None,
    HomeRoot,
    LargeRoot,
    OverCap,
    Clobber,
    Degraded,
    CreateCap,
    DestructiveCap,
    FilesTouchedCap,
}
