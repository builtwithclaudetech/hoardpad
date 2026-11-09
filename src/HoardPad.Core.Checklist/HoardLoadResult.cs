namespace HoardPad.Core.Checklist;

/// <summary>The kind of structural repair <see cref="HoardLoader"/> applied to a loaded document (§9.1).
/// Returned in <see cref="HoardLoadResult.Repairs"/> so the host can log it (the loader writes no log
/// file itself — it is pure).</summary>
public enum HoardRepairKind
{
    /// <summary>A <c>schemaVersion: 1</c> file was migrated to v2 (every item → <c>parentId=null</c>,
    /// <c>collapsed=false</c>; §9.1 v1→v2 rule).</summary>
    MigratedV1ToV2,

    /// <summary>An item's <c>parentId</c> referenced no existing item; repaired to <c>null</c> (§9.1).</summary>
    DanglingParentReparentedToRoot,

    /// <summary>An item's <c>parentId</c> chain formed a cycle; repaired to <c>null</c> (§9.1).</summary>
    CyclicParentReparentedToRoot,

    /// <summary>An item's derived depth exceeded the tier-2 cap; reparented to its deepest legal
    /// ancestor (§9.1 depth-cap-on-import clamp, the worked-example rule).</summary>
    OverDeepClampedToTier2,
}

/// <summary>One repair the loader applied, naming the affected item id (§9.1). Loggable by the host.</summary>
/// <param name="Kind">What was repaired.</param>
/// <param name="ItemId">The affected item's <c>id</c> (empty string for a document-level migration).</param>
public sealed record HoardRepair(HoardRepairKind Kind, string ItemId);

/// <summary>
/// The outcome of <see cref="HoardLoader.Load"/>. Either a successfully loaded (and possibly migrated /
/// repaired / clamped) <see cref="Document"/>, or a rejection (<see cref="Rejected"/> true) when the file
/// declares a <c>schemaVersion</c> HIGHER than the current — which is NEVER silently downgraded (§9.1).
/// This is the expected-failure result type (house error-handling style): rejection is a normal branch the
/// caller handles, not an exception. (Malformed JSON is a genuinely exceptional input and still throws.)
/// </summary>
public sealed record HoardLoadResult
{
    private HoardLoadResult(HoardDocument? document, bool rejected, int rejectedVersion, IReadOnlyList<HoardRepair> repairs)
    {
        Document = document;
        Rejected = rejected;
        RejectedVersion = rejectedVersion;
        Repairs = repairs;
    }

    /// <summary>The loaded document, or <c>null</c> when <see cref="Rejected"/> is true.</summary>
    public HoardDocument? Document { get; }

    /// <summary><c>true</c> when the file's <c>schemaVersion</c> was higher than
    /// <see cref="HoardSchema.CurrentVersion"/> and the load was refused (§9.1).</summary>
    public bool Rejected { get; }

    /// <summary>The offending <c>schemaVersion</c> when <see cref="Rejected"/> is true (else 0).</summary>
    public int RejectedVersion { get; }

    /// <summary>The repairs/migrations applied during load (empty when the file was already canonical v2).</summary>
    public IReadOnlyList<HoardRepair> Repairs { get; }

    internal static HoardLoadResult Loaded(HoardDocument document, IReadOnlyList<HoardRepair> repairs) =>
        new(document, rejected: false, rejectedVersion: 0, repairs);

    internal static HoardLoadResult Reject(int version) =>
        new(document: null, rejected: true, rejectedVersion: version, repairs: []);
}
