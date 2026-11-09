namespace HoardPad.Core.Ai.Caps;

/// <summary>Which cap field a <see cref="CapClampError"/> concerns.</summary>
public enum CapField
{
    CreateCap,
    DestructiveCap,
    FilesTouchedCap,
    DestructiveMagnitudePercent,
}

/// <summary>Why a user-supplied cap value was rejected by the FR-35 clamp rule (§8.2).</summary>
public enum CapClampReason
{
    /// <summary>A cap count was ≤ 0 — disables the backstop by allowing no enforcement / "off" (FR-35).</summary>
    NonPositive,

    /// <summary>
    /// A cap count was set so high it never fires (the "unbounded" disabling value, FR-35) — represented by the
    /// <see cref="int.MaxValue"/> sentinel. An unbounded cap is exactly the injection backstop being disabled.
    /// </summary>
    Unbounded,

    /// <summary>The FR-38 X% magnitude was outside the meaningful 1–100 percent range (§8.2).</summary>
    MagnitudeOutOfRange,
}

/// <summary>One FR-35 clamp rejection: the offending <see cref="Field"/> and the <see cref="Reason"/>.</summary>
public sealed record CapClampError(CapField Field, CapClampReason Reason);

/// <summary>
/// The pure FR-35 clamp validation (§8.2 / FR-35 Done-when): a user-configured cap value cannot be set to 0 or
/// unbounded in a way that disables the injection backstop. This is the last-line guard that keeps the
/// FR-11/FR-38/FR-40 caps from being neutered through the settings surface (FR-35).
/// </summary>
/// <remarks>
/// Pure domain validation — no I/O, no clock. The settings layer (M-settings, Host) calls
/// <see cref="Validate"/> on any user override and rejects with a clear message (FR-35 Done-when), falling back
/// to the pinned default (<see cref="CapConfig.Pinned"/>) on rejection. The rule is deliberately narrow
/// (Rule 2): the docs pin "cannot be 0 / unbounded", not a tight numeric band, so this rejects only the
/// genuinely-disabling values and accepts every other positive, bounded configuration.
/// </remarks>
public static class CapClamp
{
    /// <summary>
    /// Validate a candidate <see cref="CapConfig"/> against the FR-35 backstop rule. Returns every rejection
    /// found (empty = the config is safe and may be used as-is).
    /// </summary>
    public static IReadOnlyList<CapClampError> Validate(CapConfig config)
    {
        var errors = new List<CapClampError>();

        CheckCount(errors, CapField.CreateCap, config.CreateCap);
        CheckCount(errors, CapField.DestructiveCap, config.DestructiveCap);
        CheckCount(errors, CapField.FilesTouchedCap, config.FilesTouchedCap);

        // The FR-38 X% magnitude must be a meaningful percent: 1..100 inclusive. 0 would count every op as a
        // full overwrite (over-trip, not a disable) and >100 / ≤0 is nonsensical; both are rejected.
        if (config.DestructiveMagnitudePercent is < 1 or > 100)
            errors.Add(new CapClampError(CapField.DestructiveMagnitudePercent, CapClampReason.MagnitudeOutOfRange));

        return errors;
    }

    /// <summary>True when <paramref name="config"/> passes the FR-35 clamp rule (no rejections).</summary>
    public static bool IsValid(CapConfig config) => Validate(config).Count == 0;

    private static void CheckCount(List<CapClampError> errors, CapField field, int value)
    {
        // ≤ 0 disables the backstop ("off" / no enforcement); int.MaxValue is the "unbounded" disabling value.
        if (value <= 0)
            errors.Add(new CapClampError(field, CapClampReason.NonPositive));
        else if (value == int.MaxValue)
            errors.Add(new CapClampError(field, CapClampReason.Unbounded));
    }
}
