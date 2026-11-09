// PathResolver.cs — the FR-26 canonicalization seam (M2.2).
//
// This is the realpath/symlink-resolution leg the domain must NOT own (C-9 /
// architecture §1.3, TD §2.2). It produces the ONLY NormcasedPath values that
// SandboxPolicy.Evaluate (M1.2) ever sees, so its single non-negotiable invariant
// is: it must NEVER emit a degenerate or mixed-separator path to the sandbox. The
// M1.2 hostile review (Finding 1) made SandboxPolicy.Evaluate fail CLOSED on a
// degenerate root defensively; THIS resolver is the upstream closure that proves
// such a root can't be produced in the first place.
//
// Canonicalization order (each step removes a class of escape vector):
//   1. Make absolute + collapse '.'/'..'/doubled separators, OS-native separator:
//      Path.GetFullPath(raw, rootForRelativeResolution). After this there is no
//      surviving '..' segment, no doubled internal separator, and exactly one
//      separator style (the OS-native one).
//   2. Resolve symlinks (Linux: realpath(3) on the longest EXISTING ancestor, then
//      re-append the non-existent tail so create-targets still resolve; a symlinked
//      parent therefore cannot evade the sandbox). Windows (compile-only): handled
//      by GetFullPath + GetLongPathName.
//   3. normcase: identity on Linux (case-sensitive FS); on Windows (compile-only)
//      GetLongPathName 8.3 expansion (TD-1) + case-fold + separator-normalize.
//   4. Degenerate-root guard: a result that is a bare filesystem root ("/", "\",
//      "//", "\\") is rejected — a HoardPad sandbox root is always a resolved
//      open-folder directory, never the volume root. Throwing here (rather than
//      handing it on) is the upstream half of M1.2 Finding 1.
//
// The Windows leg (GetLongPathName, separator normalize, case-fold) COMPILES for
// win-x64; its runtime assertion is DEFERRED to real Windows hardware (TD §6 /
// the SPIKE-4 pattern) and never executes on this Linux box.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.Host;

/// <summary>
/// Canonicalizing path resolver per FR-26 / TD §2.2. Implements <see cref="IPathResolver"/>.
/// Linux uses <c>realpath(3)</c> on the longest existing ancestor + identity normcase;
/// Windows uses <c>GetFullPath</c> + <c>GetLongPathName</c> 8.3 expansion + case-fold
/// (compiled, runtime deferred). Guarantees a non-degenerate, single-separator-style,
/// fully-canonical <see cref="NormcasedPath"/> for <c>SandboxPolicy.Evaluate</c>.
/// </summary>
public sealed class PathResolver : IPathResolver
{
    public NormcasedPath ResolveCanonical(string rawPath, string rootForRelativeResolution)
    {
        if (string.IsNullOrEmpty(rawPath))
            throw new ArgumentException("A path to resolve must be a non-empty string.", nameof(rawPath));
        if (string.IsNullOrEmpty(rootForRelativeResolution))
            throw new ArgumentException(
                "A non-empty root is required to resolve a relative path.", nameof(rootForRelativeResolution));

        // Step 1: make absolute against the root and collapse '.'/'..'/doubled separators.
        // GetFullPath(path, basePath) requires basePath to be rooted; it emits the OS-native
        // separator and leaves no '..' segment, so the single-separator-style invariant holds
        // from here on. (On Linux '\' is NOT a separator: "foo\bar" is one literal filename,
        // which is the correct POSIX reading and keeps the result single-separator.)
        string absolute = Path.GetFullPath(rawPath, rootForRelativeResolution);

        // Step 2 + 3: resolve symlinks + normcase, OS-specific.
        string canonical = OperatingSystem.IsWindows()
            ? CanonicalizeWindows(absolute)
            : CanonicalizeLinux(absolute);

        // Step 4: degenerate-root guard — the upstream closure of M1.2 Finding 1.
        // A bare filesystem root as an open-folder sandbox root would grant containment
        // to the whole volume. The resolver must never produce one; fail loud if a raw
        // input somehow canonicalized to one.
        if (IsDegenerateRoot(canonical))
            throw new ArgumentException(
                $"Resolving '{rawPath}' yielded the degenerate root '{canonical}', which would grant the "
                + "sandbox containment over the entire filesystem volume. A HoardPad path is always a "
                + "resolved file or directory under an open-folder root, never the volume root itself.",
                nameof(rawPath));

        return NormcasedPath.FromResolved(canonical);
    }

    // ---- Linux canonicalization: realpath on the longest existing ancestor ----

    /// <summary>
    /// Resolve symlinks via <c>realpath(3)</c>. Because the target may not exist yet (the AI
    /// proposing a file to create), we realpath the longest EXISTING ancestor and re-append the
    /// non-existent tail. This still resolves a symlinked PARENT to its canonical target — so a
    /// symlink anywhere in the existing prefix cannot evade the sandbox — while permitting
    /// create-targets whose leaf does not yet exist. normcase is identity on Linux (case-sensitive).
    /// </summary>
    private static string CanonicalizeLinux(string absolute)
    {
        // Walk up to the longest existing ancestor. The path is already absolute and '..'-free
        // (step 1), so GetDirectoryName strips one real segment per iteration with no surprises.
        string existing = absolute;
        var pendingTail = new List<string>();
        while (!PathExists(existing))
        {
            string? parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent))
            {
                // Reached the filesystem root without finding an existing ancestor. The root "/"
                // always exists, so this only happens for a pathological input; realpath of "/"
                // below resolves it and the degenerate-root guard then rejects it.
                existing = "/";
                break;
            }
            pendingTail.Add(Path.GetFileName(existing));
            existing = parent;
        }

        string resolvedExisting = Realpath(existing);

        // Re-append the non-existent tail (recorded leaf-first; replay root-first).
        for (int i = pendingTail.Count - 1; i >= 0; i--)
            resolvedExisting = Path.Combine(resolvedExisting, pendingTail[i]);

        // Path.Combine uses the OS separator and never introduces a doubled or mixed separator,
        // so the single-separator invariant is preserved. realpath already returned an absolute,
        // symlink-free, '..'-free path for the existing prefix.
        return resolvedExisting;
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// libc <c>realpath(3)</c>: canonicalize an EXISTING path (resolve every symlink, make
    /// absolute, collapse '.'/'..'). Same P/Invoke posture as M2.1's <c>AtomicFileStore</c>
    /// (LPUTF8Str marshaling for the byte-string path arg, SetLastError). Never runs on Windows.
    /// </summary>
    private static string Realpath(string path)
    {
        // resolved_path = NULL asks glibc to malloc the result; we free it via free(3).
        IntPtr resultPtr = realpath(path, IntPtr.Zero);
        if (resultPtr == IntPtr.Zero)
        {
            int errno = Marshal.GetLastWin32Error();   // BCL maps errno into GetLastWin32Error on Unix.
            throw new IOException($"realpath('{path}') failed with errno {errno}.");
        }
        try
        {
            return Marshal.PtrToStringUTF8(resultPtr)
                ?? throw new IOException($"realpath('{path}') returned an unreadable path.");
        }
        finally
        {
            free(resultPtr);
        }
    }

    // CA2101 wants Unicode (LPWStr) marshaling for P/Invoke strings, but the libc realpath(3)
    // path argument is a byte string; LPUTF8Str is the correct marshaling and Unicode would be
    // wrong. Suppressed narrowly with the right marshaling pinned (the same deliberate posture
    // as AtomicFileStore's libc open(2) import). This import only ever runs on the Linux path.
#pragma warning disable CA2101
    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolvedPath);
#pragma warning restore CA2101

    [DllImport("libc")]
    private static extern void free(IntPtr ptr);

    // ---- Windows canonicalization — COMPILE-ONLY on this box; runtime DEFERRED ----
    //
    // GetLongPathName expands 8.3 short names (TD-1) so a short name like PROGRA~1 cannot be
    // used to slip a path past the prefix check. After expansion we case-fold (Windows FS is
    // case-insensitive — SPIKE-4 normcase rule) and the separator is already '\' from
    // GetFullPath. Guarded by OperatingSystem.IsWindows() so it never runs on Linux;
    // [SupportedOSPlatform] satisfies the platform-compatibility analyzer.

    [SupportedOSPlatform("windows")]
    private static string CanonicalizeWindows(string absolute)
    {
        // GetFullPath (step 1) already resolved '..'/'.' and used '\' separators. Expand any
        // 8.3 short components to their long form (TD-1). GetLongPathName only expands existing
        // path components; a non-existent tail is returned unchanged, which is the create-target
        // case — the existing (symlink/junction-bearing) prefix is what must be hardened.
        string longForm = ExpandLongPath(absolute);

        // normcase: case-fold (invariant lower) so "C:\Work" and "c:\work" compare equal, and
        // the FR-40 distinct-files set does not double-count a case variant. Separator is already
        // '\'; no replace needed, but normalize defensively in case a forward slash survived.
        return longForm.Replace('/', '\\').ToLowerInvariant();
    }

    [SupportedOSPlatform("windows")]
    private static string ExpandLongPath(string path)
    {
        // First call with a zero-length buffer returns the required length (incl. NUL).
        // GetLongPathNameW returns DWORD and takes a DWORD cchBuffer; uint matches the Win32
        // width exactly — the same convention as MoveFileExW/ReplaceFileW DWORD flags (M2.1).
        uint needed = GetLongPathNameW(path, null, 0);
        if (needed == 0)
            // GetLongPathName fails (e.g. the path doesn't exist). The 8.3-expansion only matters
            // for existing components; a non-existent create-target is returned as-is from GetFullPath.
            return path;

        char[] buffer = new char[needed];
        uint written = GetLongPathNameW(path, buffer, needed);
        if (written == 0 || written >= needed)
            return path;   // race or unexpected growth — fall back to the GetFullPath form.
        return new string(buffer, 0, (int)written);
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, char[]? lpszLongPath, uint cchBuffer);

    // ---- Degenerate-root guard (shared) ----

    /// <summary>
    /// True when <paramref name="path"/> is a bare filesystem root that must never be handed to
    /// the sandbox as an open-folder root: "/", "\", "//", "\\". This mirrors the degenerate-root
    /// definition SandboxPolicy.Evaluate fails closed on (M1.2) — closing it here means the
    /// resolver can't produce one to begin with.
    /// </summary>
    private static bool IsDegenerateRoot(string path) =>
        path is "/" or "\\" or "//" or "\\\\";
}
