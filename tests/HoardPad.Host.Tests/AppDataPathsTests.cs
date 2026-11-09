// AppDataPathsTests.cs — §5.5 layout + NFR-7 permission restriction + R4-MA1 machine-local recovery.
//
// Three concerns:
//   1. RestrictToCurrentUser sets owner-only modes (600 file / 700 dir) — asserted via
//      File.GetUnixFileMode on Linux (the runtime leg; Windows ACL is Deferred).
//   2. RecoveryDirectory is machine-local / non-synced (R4-MA1): it resolves under the injected
//      machine-local root, NOT under AppDataRoot, so a recovery snapshot can never sit inside a
//      folder that might be synced/roamed.
//   3. The §5.5 layout members compose under the right roots.

using HoardPad.Host;
using Xunit;

namespace HoardPad.Host.Tests;

public sealed class AppDataPathsTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly string _recoveryRoot;
    private readonly AppDataPaths _paths;

    public AppDataPathsTests()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), $"hoardpad-m22-appdata-{Guid.NewGuid():N}");
        // Deliberately distinct bases — the recovery root must be provably outside the data root.
        _dataRoot = Path.Combine(baseDir, "data");
        _recoveryRoot = Path.Combine(baseDir, "state");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_recoveryRoot);
        _paths = new AppDataPaths(_dataRoot, _recoveryRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dataRoot)!, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ---- §5.5 layout ----

    [Fact]
    public void LayoutMembersComposeUnderAppDataRoot()
    {
        Assert.Equal(Path.Combine(_dataRoot, "settings.json"), _paths.SettingsFilePath);
        Assert.Equal(Path.Combine(_dataRoot, "undo"), _paths.UndoJournalRoot);
        Assert.Equal(Path.Combine(_dataRoot, "activity", "sess-1.log"), _paths.ActivityLogPath("sess-1"));
    }

    [Fact]
    public void ActivityLogPathRejectsEmptySessionId() =>
        Assert.Throws<ArgumentException>(() => _paths.ActivityLogPath(""));

    // ---- R4-MA1: recovery dir is machine-local / non-synced ----

    [Fact]
    public void RecoveryDirectoryIsUnderMachineLocalRootNotAppDataRoot()
    {
        string recovery = Path.GetFullPath(_paths.RecoveryDirectory);
        string dataRoot = Path.GetFullPath(_paths.AppDataRoot);
        string machineLocalRoot = Path.GetFullPath(_recoveryRoot);

        // It lives under the MACHINE-LOCAL root we injected...
        Assert.StartsWith(machineLocalRoot + Path.DirectorySeparatorChar, recovery);

        // ...and crucially NOT under AppDataRoot (which on a real machine can be a roamed/synced
        // location). This is the R4-MA1 invariant: recovery snapshots never cross machines.
        Assert.False(
            recovery.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"recovery dir '{recovery}' must NOT be under AppDataRoot '{dataRoot}' (R4-MA1)");
    }

    [Fact]
    public void DefaultRecoveryRootIsDistinctFromDefaultAppDataRoot()
    {
        // The parameterless ctor uses the real per-user bases. On Linux that is
        // $XDG_DATA_HOME (~/.local/share) for data vs $XDG_STATE_HOME (~/.local/state) for
        // recovery — they must be different base trees, or the machine-local guarantee is moot.
        var real = new AppDataPaths();
        string appData = Path.GetFullPath(real.AppDataRoot);
        string recovery = Path.GetFullPath(real.RecoveryDirectory);

        Assert.False(
            recovery.StartsWith(appData + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"default recovery dir '{recovery}' must not be under default AppDataRoot '{appData}'");
    }

    // ---- NFR-7: RestrictToCurrentUser owner-only modes (Linux runtime leg) ----

    [Fact]
    public void RestrictToCurrentUserSetsOwnerOnlyFileMode()
    {
        if (OperatingSystem.IsWindows())
            return;   // Windows ACL leg is Deferred — not asserted on Linux.

        string file = Path.Combine(_dataRoot, "settings.json");
        File.WriteAllText(file, "{}");

        _paths.RestrictToCurrentUser(file);

        // 600: owner read+write only, no group/other bits.
        UnixFileMode mode = File.GetUnixFileMode(file);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void RestrictToCurrentUserSetsOwnerOnlyDirectoryMode()
    {
        if (OperatingSystem.IsWindows())
            return;   // Windows ACL leg is Deferred — not asserted on Linux.

        string dir = Path.Combine(_dataRoot, "undo");
        Directory.CreateDirectory(dir);

        _paths.RestrictToCurrentUser(dir);

        // 700: owner read+write+execute (execute = traverse/list a dir), no group/other bits.
        UnixFileMode mode = File.GetUnixFileMode(dir);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
    }

    [Fact]
    public void RestrictToCurrentUserRejectsEmptyPath() =>
        Assert.Throws<ArgumentException>(() => _paths.RestrictToCurrentUser(""));

    // ---- FR-36: default app-data root — never CWD-relative (F4) ----
    //
    // On Linux/macOS, ResolveDefaultAppDataRoot never reads LocalApplicationData at all
    // (mirroring ResolveDefaultRecoveryRoot) precisely because it can resolve empty under a
    // self-contained publish, which would otherwise Path.Combine into a CWD-relative dir. These
    // tests drive the parameterless ctor's real resolution via XDG_DATA_HOME/HOME env vars,
    // saved and restored so the test process's environment is left untouched.

    [Fact]
    public void DefaultAppDataRootUsesXdgDataHomeWhenSet()
    {
        if (OperatingSystem.IsWindows())
            return;   // Windows uses %LOCALAPPDATA%, unaffected by this fix.

        string? savedXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            string xdgRoot = Path.Combine(Path.GetTempPath(), $"hoardpad-xdg-data-{Guid.NewGuid():N}");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdgRoot);

            var paths = new AppDataPaths();

            Assert.Equal(Path.Combine(xdgRoot, "HoardPad"), paths.AppDataRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", savedXdg);
        }
    }

    [Fact]
    public void DefaultAppDataRootFallsBackToHomeLocalShareWhenXdgUnset()
    {
        if (OperatingSystem.IsWindows())
            return;   // Windows uses %LOCALAPPDATA%, unaffected by this fix.

        string? savedXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);

            var paths = new AppDataPaths();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            Assert.Equal(Path.Combine(home, ".local", "share", "HoardPad"), paths.AppDataRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", savedXdg);
        }
    }

    [Fact]
    public void DefaultAppDataRootIsAlwaysAbsolute()
    {
        var paths = new AppDataPaths();

        Assert.True(Path.IsPathRooted(paths.AppDataRoot));
        Assert.Equal(Path.GetFullPath(paths.AppDataRoot), paths.AppDataRoot);
    }

    // ---- F4 fix-cycle: relative XDG_DATA_HOME must NOT be trusted (Fix 1) ----
    //
    // Empirically confirmed by review: pre-Fix-1, a RELATIVE XDG_DATA_HOME was accepted verbatim and
    // then Path.GetFullPath-anchored to the process CWD by the AppDataPaths ctor — the exact FR-36
    // violation this whole seam exists to prevent. This test drives a KNOWN CWD + a relative
    // XDG_DATA_HOME and asserts the result is the $HOME/.local/share fallback, never the CWD-joined
    // value. Confirmed: this test FAILS against the pre-Fix-1 code (asserts the fallback, but the
    // unguarded code returns the CWD-joined path) and PASSES post-fix.
    [Fact]
    public void DefaultAppDataRootIgnoresRelativeXdgDataHomeAndFallsBackToHomeLocalShare()
    {
        if (OperatingSystem.IsWindows())
            return;   // Windows uses %LOCALAPPDATA%, unaffected by this fix.

        string? savedXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string savedCwd = Directory.GetCurrentDirectory();
        string tempCwd = Path.Combine(Path.GetTempPath(), $"hoardpad-relxdg-cwd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempCwd);
        try
        {
            const string relativeXdg = "relative-xdg-data";
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", relativeXdg);
            Directory.SetCurrentDirectory(tempCwd);

            var paths = new AppDataPaths();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string expectedFallback = Path.Combine(home, ".local", "share", "HoardPad");
            string cwdJoined = Path.GetFullPath(Path.Combine(tempCwd, relativeXdg, "HoardPad"));

            Assert.Equal(expectedFallback, paths.AppDataRoot);
            Assert.NotEqual(cwdJoined, paths.AppDataRoot);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", savedXdg);
            try { Directory.Delete(tempCwd, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    // ---- F4 fix-cycle: guard the $HOME fallback's absoluteness too (Fix 2) ----
    //
    // Structural (seam-based) proof that the resolver never returns a relative path even when both
    // OS-facing lookups resolve empty — the non-Windows equivalent of the old empty-LocalApplicationData
    // gap. Drives ResolveDefaultAppDataRootNonWindows directly (internal seam, HoardPad.Host.Tests only)
    // rather than trying to force a real empty $HOME/UserProfile, which the BCL falls back to /etc/passwd
    // for and so can't be reliably forced via env vars alone.
    [Fact]
    public void ResolveDefaultAppDataRootNonWindowsIsAbsoluteEvenWhenHomeAndXdgResolveEmpty()
    {
        if (OperatingSystem.IsWindows())
            return;   // the seam mirrors the non-Windows branch only.

        string result = AppDataPaths.ResolveDefaultAppDataRootNonWindows(
            getEnvironmentVariable: _ => string.Empty,
            getUserProfile: () => string.Empty);

        Assert.True(Path.IsPathRooted(result), $"expected an absolute path, got '{result}'");
        // Never the naive Path.Combine("", ".local", "share", "HoardPad") == ".local/share/HoardPad"
        // that a Path.GetFullPath(...)-only guard (no EnsureAbsolute fallback) would silently
        // CWD-anchor.
        Assert.NotEqual(Path.GetFullPath(Path.Combine(".local", "share", "HoardPad")), result);
    }

    // ---- Fix A: EnsureAbsolute's own last-resort Path.GetTempPath() fallback was unguarded ----
    //
    // Triple-fault case: empty XDG_DATA_HOME + empty $HOME drives ResolveDefaultAppDataRootNonWindows
    // into the EnsureAbsolute fallback branch, which (pre-fix) combined Path.GetTempPath() verbatim.
    // Path.GetTempPath() reads TMPDIR/TMP/TEMP verbatim with no absoluteness check, so a RELATIVE
    // TMPDIR made that fallback itself CWD-anchored once Path.GetFullPath ran over it — the exact
    // FR-36 violation EnsureAbsolute exists to prevent, just one level deeper. Confirmed: this test
    // FAILS against the pre-Fix-A code (asserts an absolute /tmp-rooted result, but the unguarded
    // code returns the CWD-joined relative-TMPDIR path) and PASSES post-fix.
    [Fact]
    public void ResolveDefaultAppDataRootNonWindowsIsAbsoluteEvenWithRelativeTmpdirAndEmptyHomeAndXdg()
    {
        if (OperatingSystem.IsWindows())
            return;   // EnsureAbsolute's temp-fallback guard is a non-Windows-only path.

        string? savedTmpdir = Environment.GetEnvironmentVariable("TMPDIR");
        string savedCwd = Directory.GetCurrentDirectory();
        string tempCwd = Path.Combine(Path.GetTempPath(), $"hoardpad-reltmpdir-cwd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempCwd);
        try
        {
            const string relativeTmpdir = "relative-tmpdir";
            Environment.SetEnvironmentVariable("TMPDIR", relativeTmpdir);
            Directory.SetCurrentDirectory(tempCwd);

            string result = AppDataPaths.ResolveDefaultAppDataRootNonWindows(
                getEnvironmentVariable: _ => string.Empty,
                getUserProfile: () => string.Empty);

            string cwdJoined = Path.GetFullPath(
                Path.Combine(tempCwd, relativeTmpdir, "HoardPad-AppData-Fallback"));

            Assert.True(Path.IsPathRooted(result), $"expected an absolute path, got '{result}'");
            Assert.StartsWith("/", result);
            Assert.NotEqual(cwdJoined, result);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            Environment.SetEnvironmentVariable("TMPDIR", savedTmpdir);
            try { Directory.Delete(tempCwd, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    // ---- F-REC1: default recovery root — never CWD-relative, mirroring the FR-36 AppData guard ----
    //
    // ResolveDefaultRecoveryRoot had no equivalent of the two AppData guards above: it trusted
    // XDG_STATE_HOME unconditionally (no Path.IsPathRooted check) and ran the $HOME fallback
    // straight through Path.Combine with no EnsureAbsolute. These tests drive the internal seam
    // directly (HoardPad.Host.Tests only) to simulate a stripped/relative environment without
    // touching the real process environment.

    [Fact]
    public void ResolveDefaultRecoveryRootNonWindowsIgnoresRelativeXdgStateHomeAndFallsBackToAbsolute()
    {
        if (OperatingSystem.IsWindows())
            return;   // Windows uses %LOCALAPPDATA%, unaffected by this fix.

        const string relativeXdg = "relative/state";
        const string home = "/home/you";

        string result = AppDataPaths.ResolveDefaultRecoveryRootNonWindows(
            getEnvironmentVariable: name => name == "XDG_STATE_HOME" ? relativeXdg : null,
            getUserProfile: () => home);

        Assert.True(Path.IsPathRooted(result), $"expected an absolute path, got '{result}'");
        Assert.Equal(Path.Combine(home, ".local", "state"), result);
    }

    [Fact]
    public void ResolveDefaultRecoveryRootNonWindowsIsAbsoluteEvenWhenHomeAndXdgResolveEmpty()
    {
        if (OperatingSystem.IsWindows())
            return;   // the seam mirrors the non-Windows branch only.

        string result = AppDataPaths.ResolveDefaultRecoveryRootNonWindows(
            getEnvironmentVariable: _ => string.Empty,
            getUserProfile: () => string.Empty);

        Assert.True(Path.IsPathRooted(result), $"expected an absolute path, got '{result}'");
        // Never the naive Path.Combine("", ".local", "state") == ".local/state" that a missing
        // EnsureAbsolute fallback would silently CWD-anchor.
        Assert.NotEqual(Path.GetFullPath(Path.Combine(".local", "state")), result);
    }

    [Fact]
    public void ResolveDefaultRecoveryRootNonWindowsHonorsAbsoluteXdgStateHomeUnchanged()
    {
        if (OperatingSystem.IsWindows())
            return;   // Windows uses %LOCALAPPDATA%, unaffected by this fix.

        const string absoluteXdg = "/var/lib/hoardpad-state";

        string result = AppDataPaths.ResolveDefaultRecoveryRootNonWindows(
            getEnvironmentVariable: name => name == "XDG_STATE_HOME" ? absoluteXdg : null,
            getUserProfile: () => "/home/you");

        Assert.Equal(absoluteXdg, result);
    }

    // ---- F-REC1 end-to-end: real ctor + real env var, mirroring the accepted F4 AppData bar ----
    //
    // The seam tests above drive ResolveDefaultRecoveryRootNonWindows directly; this test instead
    // drives the REAL public surface — a real XDG_STATE_HOME env var, a real CWD, and the real
    // parameterless AppDataPaths() ctor — exactly like
    // DefaultAppDataRootIgnoresRelativeXdgDataHomeAndFallsBackToHomeLocalShare does for the AppData
    // side. Confirmed: this test FAILS against the pre-Fix-1 code (RecoveryDirectory resolves under
    // the CWD-anchored relative XDG_STATE_HOME) and PASSES post-fix — see the pre-fix RED capture in
    // the F-REC1 review notes.
    [Fact]
    public void DefaultRecoveryDirectoryIgnoresRelativeXdgStateHomeAndIsNotCwdAnchored()
    {
        if (OperatingSystem.IsWindows())
            return;   // Windows uses %LOCALAPPDATA%, unaffected by this fix.

        string? savedXdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        string savedCwd = Directory.GetCurrentDirectory();
        string tempCwd = Path.Combine(Path.GetTempPath(), $"hoardpad-relxdgstate-cwd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempCwd);
        try
        {
            const string relativeXdg = "relative-xdg-state";
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", relativeXdg);
            Directory.SetCurrentDirectory(tempCwd);

            var paths = new AppDataPaths();
            string recovery = Path.GetFullPath(paths.RecoveryDirectory);

            Assert.True(Path.IsPathRooted(recovery), $"expected an absolute path, got '{recovery}'");
            Assert.False(
                recovery.StartsWith(tempCwd + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                $"recovery dir '{recovery}' must NOT be CWD-anchored under '{tempCwd}' (relative XDG_STATE_HOME must be ignored)");
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", savedXdg);
            try { Directory.Delete(tempCwd, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    // ---- R4-MA1 distinctness: the double-empty-environment fallback bases must never collide ----
    //
    // ResolveDefaultAppDataRootNonWindows and ResolveDefaultRecoveryRootNonWindows both funnel into
    // EnsureAbsolute when $HOME/UserProfile and their respective XDG var resolve empty. EnsureAbsolute
    // takes a fallbackSuffix precisely so the two fallback bases stay disjoint (AppDataFallbackSuffix
    // vs RecoveryFallbackSuffix) — a regression that silently dropped the RecoveryFallbackSuffix
    // argument (defaulting back to AppDataFallbackSuffix) would nest recovery inside the AppData
    // fallback root and violate R4-MA1 without any seam-only test noticing, since both bases would
    // independently look "absolute". This test proves the two resolved fallback bases are DISJOINT —
    // neither is a path-prefix of the other — in the same double-empty scenario.
    [Fact]
    public void DoubleEmptyEnvironmentFallbackBasesAreDisjointBetweenAppDataAndRecovery()
    {
        if (OperatingSystem.IsWindows())
            return;   // the seam mirrors the non-Windows branch only.

        string appDataFallback = AppDataPaths.ResolveDefaultAppDataRootNonWindows(
            getEnvironmentVariable: _ => string.Empty,
            getUserProfile: () => string.Empty);
        string recoveryFallback = AppDataPaths.ResolveDefaultRecoveryRootNonWindows(
            getEnvironmentVariable: _ => string.Empty,
            getUserProfile: () => string.Empty);

        // Proves RecoveryFallbackSuffix (not AppDataFallbackSuffix) was actually used.
        Assert.Contains("Recovery-Fallback", recoveryFallback);
        Assert.DoesNotContain("Recovery-Fallback", appDataFallback);

        Assert.NotEqual(appDataFallback, recoveryFallback);
        Assert.False(
            recoveryFallback.StartsWith(appDataFallback + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"recovery fallback '{recoveryFallback}' must not nest under AppData fallback '{appDataFallback}'");
        Assert.False(
            appDataFallback.StartsWith(recoveryFallback + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"AppData fallback '{appDataFallback}' must not nest under recovery fallback '{recoveryFallback}'");
    }
}
