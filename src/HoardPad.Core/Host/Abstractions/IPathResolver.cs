// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
namespace HoardPad.Core.Host.Abstractions;

/// HOST [host]. Resolves a raw, possibly-symlinked, possibly-relative path to a canonical,
/// symlink-resolved, normcased absolute path. This is the realpath/syscall leg the domain must not own.
public interface IPathResolver
{
    /// Resolve symlinks (realpath), make absolute, normcase. On Windows, expand 8.3 short names
    /// (GetLongPathName) so a short-name cannot evade the sandbox (TD-1 hardening). Returns the
    /// canonical normcased path; the domain then calls SandboxPolicy.Evaluate.
    NormcasedPath ResolveCanonical(string rawPath, string rootForRelativeResolution);
}
