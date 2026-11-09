// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
namespace HoardPad.Core.Host.Abstractions;

/// <summary>
/// A path already lowered to the OS's case-fold form (the <c>os.path.normcase</c> analogue):
/// no-op on Linux; case-fold + separator-normalize on Windows (TD §2.2, SPIKE-4 rule).
/// </summary>
/// <remarks>
/// <para>
/// Constructed ONLY by the host resolver (<see cref="IPathResolver"/>) after canonicalization,
/// so domain code can never silently normcase a raw string itself. The sole producer is
/// <see cref="FromResolved"/>, which is <c>internal</c> and reachable only by <c>HoardPad.Host</c>
/// (runtime) and <c>HoardPad.Core.Tests</c> (regression), via <c>InternalsVisibleTo</c>.
/// </para>
/// <para>
/// The zero value (<c>default(NormcasedPath)</c> / <c>new NormcasedPath()</c>) is explicitly
/// distinguished as an invalid sentinel: <see cref="IsDefault"/> returns <c>true</c> and
/// <see cref="Value"/> throws <see cref="InvalidOperationException"/> rather than returning
/// <c>null</c>. This prevents a default from silently reading as a valid resolved path inside
/// <c>SandboxPolicy.Evaluate</c> (M1.2), which does a prefix check on <see cref="Value"/>.
/// </para>
/// </remarks>
public readonly record struct NormcasedPath
{
    // null iff the struct is default/zero-initialized.
    private readonly string? _value;

    private NormcasedPath(string value) => _value = value;

    /// <summary>
    /// The canonical, symlink-resolved, normcased absolute path string.
    /// Throws <see cref="InvalidOperationException"/> when called on the zero value —
    /// use <see cref="IsDefault"/> to guard at boundaries that accept untrusted input.
    /// </summary>
    public string Value =>
        _value ?? throw new InvalidOperationException(
            "default(NormcasedPath) is not a resolved path — it was never produced by IPathResolver.");

    /// <summary>
    /// <c>true</c> when this is the zero value (<c>default</c> / <c>new NormcasedPath()</c>).
    /// Domain code (SandboxPolicy, seam DTOs) uses this to guard explicitly at boundaries
    /// rather than discovering a null <see cref="Value"/> at the point of use.
    /// </summary>
    public bool IsDefault => _value is null;

    /// <summary>
    /// The sole legitimate producer. <c>HoardPad.Host</c>'s <see cref="IPathResolver"/>
    /// implementation calls this AFTER canonicalizing + normcasing a raw path.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="resolvedNormcasedValue"/> is null or empty.</exception>
    internal static NormcasedPath FromResolved(string resolvedNormcasedValue)
    {
        if (string.IsNullOrEmpty(resolvedNormcasedValue))
            throw new ArgumentException(
                "A resolved normcased path must be a non-empty string.", nameof(resolvedNormcasedValue));
        return new(resolvedNormcasedValue);
    }

    /// <summary>Returns <see cref="Value"/>, or the string <c>"(default)"</c> for the zero value.</summary>
    public override string ToString() => _value ?? "(default)";
}
