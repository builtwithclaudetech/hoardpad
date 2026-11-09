using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HoardPad.App.Ai;
using HoardPad.App.Hosting;
using HoardPad.App.Session;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HoardPad.App;

/// <summary>
/// The Avalonia application + composition root (architecture §1.4 / §8.1). On framework init it
/// builds the DI container (wiring every <see cref="HoardPad.Host"/> seam to its
/// <see cref="HoardPad.Core"/> abstraction), then resolves the shell view-model into the main
/// <see cref="MainWindow"/>. The provider is exposed so later milestones resolve feature surfaces.
/// </summary>
public sealed class App : Application
{
    /// <summary>
    /// The composition-root service provider, available once init completes. Internal:
    /// feature view-models are constructor-injected at composition time — callers must not
    /// perform service-locator lookups against this (architecture §8.1 / R2 review fix).
    /// </summary>
    internal IServiceProvider? Services { get; private set; }

    /// <summary>
    /// F-FS1c: the bound on how long shutdown waits for <see cref="WorkspaceService.StopAutoSaveTimerAsync"/>
    /// to drain an in-flight auto-save batch. A drain that is still running past this bound (e.g. a very
    /// large dirty document on slow disk) must not hang the OS close indefinitely — proceed to the final
    /// session-index persist regardless, mirroring the existing 2-second bound already placed on that
    /// persist itself just below.
    /// </summary>
    private static readonly TimeSpan ShutdownAutoSaveDrainTimeout = TimeSpan.FromSeconds(5);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        ServiceProvider provider = CompositionRoot.Build();
        Services = provider;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = shell };
            desktop.MainWindow = window;

            // Theming (M7.1, FR-37, architecture §3.7): publish the singleton so editor surfaces (created by
            // XAML, not DI) self-register against it, then apply the persisted theme — the one synchronous
            // lockstep switch (chrome + preview now, plus any editor already registered). Editors registered
            // later (a tab opened after startup) are brought to the active palette on registration.
            var themes = provider.GetRequiredService<HoardPad.App.Theme.ThemeService>();
            HoardPad.App.Theme.ThemeService.Current = themes;
            ApplyStartupThemeAndFonts(provider, this);

            // QA3-1 defensive hardening (hostile-review escalation): re-assert the chrome font as LOCAL
            // values on the Window once it is fully up, in ADDITION to the DynamicResource binding declared
            // in MainWindow.axaml. A live process-boundary hostile review reported the DynamicResource losing
            // to later theme machinery in a real windowed process (5/6 cycles); an exhaustive live re-trace
            // (19 relaunch cycles across pre-seeded and real Settings-UI-driven flows, both Dark and Light
            // startup themes, both fresh-default and repeated-edit histories) could not reproduce it here —
            // see the QA3-1 debugging notes — but this reassert costs nothing and closes the gap regardless of
            // whether the theorized mechanism is live in some environment this session didn't hit.
            window.Opened += (_, _) => SettingsFontApplier.ReassertWindowFont(window);

            // Persist the session index on shutdown (the rebuildable hint, §9.2), then dispose the DI
            // container (the seams hold no native handles at rest, but this is the correct lifetime hook).
            //
            // Avalonia's ShutdownRequested pattern for async work (BLOCK fix):
            //   1. First invocation: cancel the default shutdown, kick off the async persist, then call
            //      Shutdown() once done. Second invocation (from that Shutdown() call): allow it through.
            //   2. We bound-timeout the persist (2 seconds) so a slow flush does not hang the OS close.
            //   3. Never .GetAwaiter().GetResult() from a UI-thread handler — that deadlocks the dispatcher.
            var workspace = provider.GetRequiredService<WorkspaceService>();

            // F-FS1c: start the periodic auto-save timer — snapshots every DIRTY tab (not just the active
            // one) on an interval, closing the gap where in-progress edits were only ever snapshotted on
            // the crash-recovery path. Reuses the existing FR-20 "Auto-save interval (s)" setting
            // (HoardPadSettings.AutoSaveSeconds, already clamped 1..30 and surfaced in the Settings tab) —
            // that setting previously persisted but drove no actual timer. Stopped in ShutdownRequested
            // below so the DispatcherTimer is never leaked.
            workspace.StartAutoSaveTimer(
                TimeSpan.FromSeconds(provider.GetRequiredService<HoardPadSettings>().AutoSaveSeconds));

            // Wire Settings ▸ Preferences… (M6.8, ODQ-12): the WorkspaceService owns tab creation, so the
            // shell command delegates to it (the shell VM stays decoupled from the workspace coordinator).
            shell.OpenSettingsHandler = () => workspace.OpenSettingsAsync(CancellationToken.None);

            // Wire File ▸ New / Open / Save toolbar commands (BUG-7): the WorkspaceService owns file
            // operations + the tab set, so the shell commands delegate to it (same pattern as OpenSettingsHandler).
            shell.NewDocumentHandler = () => { workspace.NewDocument(); return Task.CompletedTask; };
            shell.NewChecklistHandler = () => { workspace.NewChecklist(); return Task.CompletedTask; };
            shell.OpenDocumentHandler = () => workspace.OpenDocumentAsync(CancellationToken.None);
            shell.SaveDocumentHandler = () => workspace.SaveDocumentAsync(CancellationToken.None);

            // QA3-7 (Option B): wire _Checklist ▸ Save As… for the active checklist tab.
            shell.SaveAsChecklistHandler = () => workspace.SaveAsActiveChecklistAsync(CancellationToken.None);

            // FEAT-2: wire File ▸ Save As… for the active document tab of any kind — routes to the QA3-7
            // checklist relocate for a `.hoard` tab or the new editor relocate for a text tab.
            shell.SaveAsHandler = () => workspace.SaveAsActiveDocumentAsync(CancellationToken.None);

            // Wire File ▸ Open Folder… (STF-5): opens the OS folder picker and, on a confirmed pick, sets
            // the AI sandbox root + binds/arms the panel (WorkspaceService.OpenFolderPickerAsync → OpenFolderAsync).
            shell.OpenFolderHandler = () => workspace.OpenFolderPickerAsync(CancellationToken.None);

            // QA3-2: wire the folder-tree click-to-open handler — the WorkspaceService owns file/tab
            // creation, so the shell command delegates to it (same pattern as OpenDocumentHandler).
            shell.OpenFolderTreeFileHandler = path => workspace.OpenFolderTreeFileAsync(path, CancellationToken.None);

            // QA2-18 LAZY picker: wire the AI panel's first-write folder request to the workspace. The panel
            // fires this ONLY on the first write-mode AI action with no folder bound yet (never at startup);
            // the workspace shows the approved first-write explainer, opens the OS picker, and binds + arms
            // a confirmed root. This replaces the removed startup re-prompt that fired the picker unprompted.
            AiPanelViewModel aiPanel = provider.GetRequiredService<AiPanelViewModel>();
            aiPanel.RequestWriteFolderAsync =
                () => workspace.RequestWriteFolderAsync(CancellationToken.None);

            // PANEL-2 (feature-ai-multidoc-panel FR-MD-3/6): wire the AI panel's open-document provider over
            // the shell's open tabs + active tab. Called at each turn-arm to snapshot the currently-open
            // documents (path + focused/dirty/read-only flags, NO contents) — the checked-set assembly + the
            // FR-MD-3 descriptor read from this. Settings tabs are excluded (not documents). Mapping is pure.
            aiPanel.OpenDocumentsProvider = () => SnapshotOpenDocuments(shell);

            // PANEL-3 (feature-ai-multidoc-panel FR-MD-6(d), OQ-MD-3): wire the Save-As-on-check seam. When the
            // user checks a never-saved scratch buffer, the panel forces a Save-As (via the workspace dialog)
            // so it resolves to a real path before the turn; on cancel the panel reverts the check. Targets the
            // exact scratch tab by the row's tab id.
            aiPanel.RequestSaveAsForScratchAsync = row =>
                row.TabId is { Length: > 0 } tabId
                    ? workspace.SaveAsScratchAsync(tabId, CancellationToken.None)
                    : Task.FromResult<string?>(null);

            // QA3-8: wire the up-front unsaved-focused-document save-prompt seam over the SAME Save-As dialog
            // the OQ-MD-3 scratch-check flow above uses — SendAsync fires this IMMEDIATELY on a write-capable
            // Send when the focused document has never been saved, before any other write-mode setup.
            aiPanel.RequestSaveForUnsavedFocusedDocAsync = doc =>
                doc.TabId is { Length: > 0 } tabId
                    ? workspace.SaveAsScratchAsync(tabId, CancellationToken.None)
                    : Task.FromResult<string?>(null);

            // PANEL-4 (feature-ai-multidoc-panel FR-MD-7/8): wire the pre-turn flush seam. At turn-arm the panel
            // auto-saves each checked dirty document to disk (sealing an editor undo point) BEFORE the
            // before-image capture and the first backend call — so the agent reads current content and
            // undo/replay stay correct. A failed flush aborts the turn-arm (fail-safe, never launch on stale bytes).
            aiPanel.FlushDocumentAsync = path => workspace.FlushDocumentByPathAsync(path, CancellationToken.None);

            // QA3-9: wire the post-write refresh seam. Whenever the panel applies a write (a live auto-apply
            // chip or an FR-39 replay), refresh a matching OPEN editor tab's buffer from disk so a later Save
            // can never silently clobber the AI's edit with stale in-memory bytes.
            aiPanel.RefreshOpenDocumentAsync = path => workspace.RefreshOpenDocumentByPathAsync(path, CancellationToken.None);

            // FEAT-14: wire the AI-requested-settings-change seams. CurrentSettingsProvider reads the
            // persisted snapshot (LoadAsync — off the composition-time-only sync path, never blocking the
            // UI thread); ApplySettingsChangeAsync persists via SettingsStore.UpdateAsync (QA4-17 —
            // NOT SaveAsync: the field-scoped patch runs INSIDE UpdateAsync's own read-modify-write critical
            // section, against the base loaded fresh at that moment, which is what makes the clobber fix
            // work) AND live-applies the SAME QA3-1/FEAT-15 font/style resource writers the startup +
            // settings-tab live-edit paths use, so an AI-applied (or reverted) change is visible immediately.
            SettingsStore settingsStore = provider.GetRequiredService<SettingsStore>();

            // QA4-7: seed the shell's default new-document language from the composition-time settings
            // snapshot (the same singleton ApplyStartupThemeAndFonts/AI-panel seeding above already load —
            // SettingsStore.Load's own doc comment calls out "the editor default" as one of its intended
            // seeds) and keep it live via SettingsChanged (same BUG-5 posture as the AI panel's model/effort
            // push in CompositionRoot.cs) so an in-session Settings ▸ Save takes effect on the very next
            // File ▸ New without a relaunch. Dispatched to the UI thread because SaveAsync fires
            // SettingsChanged off the UI thread (the awaiting task's continuation).
            shell.DefaultLanguage = provider.GetRequiredService<HoardPadSettings>().DefaultLanguage;
            settingsStore.SettingsChanged += updatedSettings =>
                Dispatcher.UIThread.Post(() => shell.DefaultLanguage = updatedSettings.DefaultLanguage);

            // F-FS1c fix 5: live-apply an auto-save-interval settings change to the ALREADY-RUNNING periodic
            // timer too (same SettingsChanged live-push pattern as DefaultLanguage above) — before this fix,
            // AutoSaveSeconds persisted but never re-wired the running DispatcherTimer, so a change only took
            // effect on the next relaunch. Extracted to ApplyAutoSaveIntervalChange (not a bare lambda) so
            // the wiring itself is unit-testable, mirroring ApplyAiSettingsChangeAsync/ApplyStartupThemeAndFonts.
            settingsStore.SettingsChanged += updatedSettings =>
                Dispatcher.UIThread.Post(() => ApplyAutoSaveIntervalChange(workspace, updatedSettings));

            aiPanel.CurrentSettingsProvider = ct => settingsStore.LoadAsync(ct);
            aiPanel.ApplySettingsChangeAsync = (patch, ct) => ApplyAiSettingsChangeAsync(provider, this, patch, ct);

            // PANEL-3 (FR-MD-6): populate + refresh the multi-doc checklist for DISPLAY when the panel is shown
            // and whenever the open-tab set / active tab changes, so the checklist is interactive before a turn
            // is sent. This is a display refresh only — it never composes or mutates a turn's grant set.
            shell.Tabs.CollectionChanged += (_, _) => aiPanel.RefreshMultiDocRows();
            shell.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.ActiveTab)
                    || e.PropertyName == nameof(MainWindowViewModel.IsAiPanelVisible))
                {
                    aiPanel.RefreshMultiDocRows();
                }
            };
            aiPanel.RefreshMultiDocRows(); // initial population (covers any tabs restored before this wiring).

            // Wire File ▸ Exit (BUG-8): shut down the desktop application, which triggers the ShutdownRequested
            // handler (persists session index, disposes DI) then exits.
            shell.ExitHandler = () => { desktop.Shutdown(); return Task.CompletedTask; };

            // Attach the M6.7 find/replace panel (FR-17/FR-18): built over the shell tab set + the open-folder
            // root + the atomic store, attached post-construction (the panel's workspace seam needs the shell
            // + workspace, which the shell VM does not itself depend on — same wiring as OpenSettingsHandler).
            shell.AttachFindReplace(provider.GetRequiredService<FindReplaceViewModel>());

            // M9.1 perf bench (architecture §3.5): start the named-pipe echo channel + open the bench file
            // + arm the SC-1/SC-2 stop-instant emitter — but ONLY when the bench launcher set
            // HOARDPAD_BENCH_PIPE. In every normal/test run TryStart returns null and nothing below runs
            // (inert by default; never touches the AI spine).
            HoardPad.App.Bench.BenchEchoChannel? benchChannel =
                HoardPad.App.Bench.BenchHarness.TryStart(provider, window);

            bool _shutdownPending = false;
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (_shutdownPending)
                {
                    // Second call (we triggered it below) — let it proceed, dispose the provider.
                    benchChannel?.Dispose();
                    provider.Dispose();
                    return;
                }
                _shutdownPending = true;
                e.Cancel = true; // postpone the platform close while we flush.

                // F-FS1c fix 4: stop the periodic auto-save timer AND DRAIN any in-flight snapshot batch
                // before the final persist — StopAutoSaveTimer alone only stops FUTURE ticks, so a batch
                // already mid-flight could otherwise still be writing when PersistAsync below runs. Awaiting
                // StopAutoSaveTimerAsync guarantees no OnAutoSaveTick continuation is still running by the
                // time this line completes.
                //
                // F-FS1c bound: StopAutoSaveTimerCore() (which unconditionally stops future ticks) already
                // ran synchronously as the first line of StopAutoSaveTimerAsync before this awaits anything,
                // so a timeout here only ever affects whether we WAIT for an already-in-flight batch — never
                // whether the timer itself stops. A timed-out drain must not hang the OS close forever.
                try
                {
                    await workspace.StopAutoSaveTimerAsync().WaitAsync(ShutdownAutoSaveDrainTimeout).ConfigureAwait(true);
                }
                catch (TimeoutException)
                {
                    // best-effort: proceed to the final persist below even if a batch is still draining.
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await workspace.PersistAsync(cts.Token).ConfigureAwait(true); }
                catch { /* best-effort: a timed-out or failed persist must not block the OS close */ }

                desktop.Shutdown();
            };

            // FS-1 tier (i): file(s) passed on the command line (double-click / "Open With"). Filtered to
            // args that actually exist on disk — a defensive guard so an unrelated future CLI flag can
            // never be mistaken for a file to open. desktop.Args is the raw argv HoardPad was launched with.
            IReadOnlyList<string> passedFiles =
                (desktop.Args ?? Array.Empty<string>()).Where(System.IO.File.Exists).ToArray();

            // Startup restore (FR-19) runs after the window is shown so the FR-19 restore-conflict modal +
            // the MA5 re-pick picker have an owner window. Posted to the dispatcher (it touches Avalonia
            // VMs) so it does not block the framework-init completion.
            window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                // BLOCK fix: wrap fire-and-forget to surface exceptions via a status log/banner rather
                // than silently swallowing them (a corrupt snapshot must not leave the app blank with no
                // indication — the workspace surfaces an error status the shell can show).
                _ = workspace.RestoreAsync(passedFiles, CancellationToken.None).ContinueWith(
                    t =>
                    {
                        if (t.IsFaulted)
                            Avalonia.Logging.Logger.TryGet(Avalonia.Logging.LogEventLevel.Error, "WorkspaceService")
                                ?.Log(this, "Startup restore failed: {0}", t.Exception?.GetBaseException().Message ?? "unknown");
                    },
                    TaskContinuationOptions.OnlyOnFaulted);
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// F-FS1c fix 5: live-apply an auto-save-interval settings change to the running periodic timer.
    /// <see cref="WorkspaceService.StartAutoSaveTimer"/> already stop-then-starts idempotently (never
    /// leaks the prior <see cref="Avalonia.Threading.DispatcherTimer"/>) and clamps out-of-range values
    /// (fix 1), so re-wiring here on every settings change is always safe. Extracted so the wiring itself
    /// is unit-testable without spinning up the full desktop app lifetime, mirroring
    /// <see cref="ApplyAiSettingsChangeAsync"/>/<see cref="ApplyStartupThemeAndFonts"/>.
    /// </summary>
    internal static void ApplyAutoSaveIntervalChange(WorkspaceService workspace, HoardPadSettings updated) =>
        workspace.StartAutoSaveTimer(TimeSpan.FromSeconds(updated.AutoSaveSeconds));

    /// <summary>
    /// QA3-1: apply the persisted theme, THEN re-apply the persisted font settings — one atomic startup
    /// step so the two calls can never drift apart or be reordered by a future edit (before this
    /// extraction, ApplyStartupTheme + SettingsFontApplier.Apply were two independent call-site lines with
    /// no test proving either ran, or ran in this order — the exact dead-wiring shape this project has
    /// shipped before). ApplyLiveFonts (SettingsView, the live-edit path) was the ONLY writer of the
    /// Brand.Font.* resources, so without this a relaunch silently discarded a saved font choice and fell
    /// back to the BrandTheme.axaml compiled defaults (QA3-1 root cause). SettingsFontApplier is the single
    /// writer shared with that live-edit path and clamps the persisted sizes to the documented bounds — a
    /// hand-edited settings.json is not otherwise validated. Exercised directly (against the real
    /// CompositionRoot.ConfigureServices graph) by SettingsFontApplierAppStartupWiringTests.
    /// </summary>
    internal static void ApplyStartupThemeAndFonts(IServiceProvider provider, Application app)
    {
        provider.GetRequiredService<HoardPad.App.Theme.ThemeController>().ApplyStartupTheme();
        HoardPadSettings settings = provider.GetRequiredService<HoardPadSettings>();
        SettingsFontApplier.Apply(app, settings);
        // FEAT-15: AI panel style — same single-shared-writer posture as the font re-apply above, so a
        // relaunch can never silently drop a saved AI font/color choice.
        AiPanelStyleApplier.Apply(app, settings);
    }

    /// <summary>
    /// QA4-17 (reopens FEAT-14): persist + live-apply an AI-requested (or reverted) settings change,
    /// extracted from the composition-root wiring so it is independently testable against the REAL
    /// <see cref="CompositionRoot.ConfigureServices"/> graph (the same pattern as
    /// <see cref="ApplyStartupThemeAndFonts"/>).
    ///
    /// QA4-17 hostile-round CRITICAL fix (concurrency): <paramref name="patch"/> is a FIELD-SCOPED delegate
    /// (<c>SettingsChangeApplier.SettingsChangeComputation.Patch</c>/<c>RevertPatch</c>) — NOT a precomputed
    /// full record. It runs entirely INSIDE <see cref="SettingsStore.UpdateAsync"/>'s own critical section,
    /// against the base loaded FRESH at that exact moment, and mutates ONLY the one field it targets. A
    /// prior version of this method re-asserted ~24 allow-listed fields from a point-in-time snapshot the
    /// AI turn had computed earlier (e.g. before an approve-each confirm await) — a concurrent field-scoped
    /// writer (<c>ThemeController.PersistAsync</c>'s theme toggle, a Settings-tab Save, another AI-applied
    /// change) landing in that window would be SILENTLY REVERTED by the stale re-assertion. Because
    /// <paramref name="patch"/> only ever touches the single field <c>SettingsChangeApplier.TryCompute</c>
    /// actually computed as changed, every other field — including one a concurrent writer just changed —
    /// passes through <paramref name="patch"/> completely untouched, by construction, regardless of
    /// timing. Model/Effort/ConfirmLargeHomeFolder/AgencyMode/LargeRootThreshold/AnthropicApiKey
    /// (QA4-17 constraint, docs/decisions.md DR-1 + the AnthropicApiKey hostile-round exclusion) can still
    /// never reach disk through this method — <c>SettingsChangeApplier</c> has no switch case that builds a
    /// patch touching any of those six fields in the first place (defense in depth: even a hypothetical
    /// bypass of that allow-list would still need a DIFFERENT patch delegate targeting one of those fields,
    /// which nothing in this codebase constructs).
    ///
    /// Live-applies ONLY the mechanisms whose backing field(s) actually changed, determined by diffing the
    /// FRESH before/after pair from the SAME critical-section invocation (never from a stale snapshot):
    /// <see cref="SettingsFontApplier"/>/<see cref="AiPanelStyleApplier"/> (the twelve font/color fields,
    /// QA3-1/FEAT-15) run only if one of them differs; <see cref="HoardPad.App.Theme.ThemeController.SetTheme"/>
    /// (M7.1) runs only if Theme differs. <c>DefaultLanguage</c>'s live push (the subscription near
    /// <c>shell.DefaultLanguage</c> above) already fires off the SAME <c>SettingsChanged</c> event
    /// <see cref="SettingsStore.UpdateAsync"/> raises below, so it needs no separate call here. Every other
    /// newly-allowed field (<c>ActiveBackend</c>, <c>ClaudeCliPath</c>, <c>AnthropicModel</c>,
    /// <c>LocalEndpoint</c>, <c>LocalModel</c>, <c>AiPanelHotkey</c>, <c>AutoSaveSeconds</c>, <c>Caps</c>,
    /// <c>NoReplySeconds</c>) has no existing in-memory runtime cache to push into — the SAME posture the
    /// Settings-tab Save button already has for these same fields today (persist-only, takes effect on the
    /// next read/relaunch); this task does not invent new live-wiring for them.
    /// </summary>
    internal static async Task ApplyAiSettingsChangeAsync(
        IServiceProvider provider, Application app, Func<HoardPadSettings, HoardPadSettings> patch, CancellationToken ct)
    {
        SettingsStore settingsStore = provider.GetRequiredService<SettingsStore>();

        HoardPadSettings? before = null;
        HoardPadSettings? after = null;
        await settingsStore.UpdateAsync(s =>
        {
            before = s;
            after = patch(s);
            return after;
        }, ct).ConfigureAwait(true);

        if (before is null || after is null || before == after)
            return; // UpdateAsync's own no-op guard already skipped the save — nothing changed to live-apply.

        bool fontOrStyleChanged =
            before.EditorFontFamily != after.EditorFontFamily || before.EditorFontSize != after.EditorFontSize
            || before.UiFontFamily != after.UiFontFamily || before.UiFontSize != after.UiFontSize
            || before.AiInputFontFamily != after.AiInputFontFamily || before.AiInputFontSize != after.AiInputFontSize
            || before.AiResponseFontFamily != after.AiResponseFontFamily || before.AiResponseFontSize != after.AiResponseFontSize
            || before.AiResponseColor != after.AiResponseColor
            || before.AiUserFontFamily != after.AiUserFontFamily || before.AiUserFontSize != after.AiUserFontSize
            || before.AiUserColor != after.AiUserColor;
        if (fontOrStyleChanged)
        {
            SettingsFontApplier.Apply(app, after);
            AiPanelStyleApplier.Apply(app, after);
        }

        if (before.Theme != after.Theme)
            provider.GetRequiredService<HoardPad.App.Theme.ThemeController>().SetTheme(after.Theme);
    }

    // PANEL-2/PANEL-3 (feature-ai-multidoc-panel FR-MD-3/6): map the shell's open tabs to the AI multi-doc
    // panel's open-document snapshot. One OpenDocumentInfo per DOCUMENT tab (editor + .hoard checklist); the
    // settings tab is excluded (it is not a document). Carries paths + flags ONLY — never buffer contents
    // (NG-4). The focused document is the active tab; dirty comes from the editor VM; read-only is now the
    // document's REAL on-disk read-only state (PANEL-3 FR-MD-6(b)) — a scratch buffer (no path) is never
    // read-only (nothing on disk to be read-only yet).
    internal static System.Collections.Generic.List<OpenDocumentInfo> SnapshotOpenDocuments(MainWindowViewModel shell)
    {
        var docs = new System.Collections.Generic.List<OpenDocumentInfo>(shell.Tabs.Count);
        foreach (DocumentTab tab in shell.Tabs)
        {
            if (tab.IsSettings)
                continue; // the settings tab is not a document (no path, not a write target).

            bool isFocused = ReferenceEquals(tab, shell.ActiveTab);

            if (tab.Editor is { } editor)
            {
                docs.Add(new OpenDocumentInfo(
                    Path: editor.Document.Path,
                    DisplayName: editor.Document.DisplayName,
                    IsFocused: isFocused,
                    IsDirty: editor.IsDirty,
                    IsReadOnly: IsFileReadOnly(editor.Document.Path),
                    TabId: tab.TabId));
            }
            else if (tab.Checklist is { } checklist)
            {
                // A .hoard checklist auto-saves (never "dirty" in the FR-5 sense); its path is its on-disk file.
                docs.Add(new OpenDocumentInfo(
                    Path: checklist.FilePath,
                    DisplayName: checklist.DisplayTitle,
                    IsFocused: isFocused,
                    IsDirty: false,
                    IsReadOnly: IsFileReadOnly(checklist.FilePath),
                    TabId: tab.TabId));
            }
        }

        return docs;
    }

    // PANEL-3 (FR-MD-6(b)): read a document's REAL on-disk read-only state so the panel's writable-vs-read-only
    // affordance is truthful. A scratch buffer (null/empty path) or a not-yet-existing file is not read-only.
    // Any I/O fault (deleted mid-read, permission) is treated as NOT read-only — the enforcement boundary is
    // the sandbox gate, not this advisory affordance, so a benign read fault must never fabricate a read-only
    // badge. This is a fast attribute read (no content), consistent with the NO-CONTENTS snapshot contract.
    internal static bool IsFileReadOnly(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        try
        {
            var attrs = System.IO.File.GetAttributes(path);
            return attrs.HasFlag(System.IO.FileAttributes.ReadOnly);
        }
        catch (System.IO.FileNotFoundException) { return false; }
        catch (System.IO.DirectoryNotFoundException) { return false; }
        catch (System.UnauthorizedAccessException) { return false; }
        catch (System.IO.IOException) { return false; }
    }
}
