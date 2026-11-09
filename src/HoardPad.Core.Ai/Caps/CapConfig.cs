namespace HoardPad.Core.Ai.Caps;

/// <summary>
/// The four pinned AI-safety cap values (TD §1.7 / PRD FR-11/FR-38/FR-40/FR-35; build-context "Caps pinned"):
/// FR-11 create cap = 25, FR-38 destructive cap = 10, FR-40 distinct-files cap = 20, FR-38 X% magnitude = 80%.
/// These are MEASURED + pinned by SPIKE-3 §E and remain user-tunable within documented safe ranges (FR-35).
/// </summary>
/// <remarks>
/// The cap-state shape and the increment/no-re-arm/trip logic are pure <c>HoardPad.Core.Ai</c>; only the
/// <c>O_EXCL</c>-locked persistence of the cap-state file is a <c>HoardPad.Host</c> concern (architecture §4.4).
/// User-supplied overrides must pass <see cref="CapClamp.Validate"/> — the FR-35 backstop rule (a cap cannot be
/// set to 0 or unbounded in a way that disables the injection backstop, §8.2 / FR-35 Done-when).
/// </remarks>
/// <param name="CreateCap">FR-11 per-turn create cap. Pinned default <see cref="PinnedCreateCap"/> = 25.</param>
/// <param name="DestructiveCap">FR-38 per-turn destructive-op cap. Pinned default <see cref="PinnedDestructiveCap"/> = 10.</param>
/// <param name="FilesTouchedCap">FR-40 per-turn distinct-files-touched cap. Pinned default <see cref="PinnedFilesTouchedCap"/> = 20.</param>
/// <param name="DestructiveMagnitudePercent">
/// FR-38 X% magnitude threshold (whole-numbered percent). Pinned default <see cref="PinnedDestructiveMagnitudePercent"/> = 80.
/// </param>
public sealed record CapConfig(
    int CreateCap,
    int DestructiveCap,
    int FilesTouchedCap,
    int DestructiveMagnitudePercent)
{
    /// <summary>FR-11 pinned create cap (TD §1.7) — do not change without re-measuring (SPIKE-3 §E).</summary>
    public const int PinnedCreateCap = 25;

    /// <summary>FR-38 pinned destructive-op cap (TD §1.7).</summary>
    public const int PinnedDestructiveCap = 10;

    /// <summary>FR-40 pinned distinct-files-touched cap (TD §1.7).</summary>
    public const int PinnedFilesTouchedCap = 20;

    /// <summary>FR-38 pinned X% magnitude threshold, as a whole percent (TD §1.7) — 80%.</summary>
    public const int PinnedDestructiveMagnitudePercent = 80;

    /// <summary>The pinned (measured) defaults — the values used when the user has not overridden a cap.</summary>
    public static CapConfig Pinned { get; } = new(
        CreateCap: PinnedCreateCap,
        DestructiveCap: PinnedDestructiveCap,
        FilesTouchedCap: PinnedFilesTouchedCap,
        DestructiveMagnitudePercent: PinnedDestructiveMagnitudePercent);
}
