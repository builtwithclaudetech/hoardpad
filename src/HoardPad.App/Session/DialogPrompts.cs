// DialogPrompts.cs — production prompt adapters for the FR-19 restore conflict + MA5 stale-root re-prompt.
//
// These resolve the owner window from the classic-desktop lifetime and show the real modal dialogs:
//   - RestoreConflictPrompt → RestoreConflictDialog.ShowAsync(owner, fileName) (FR-19, design §9.6).
//   - StorageOpenFolderPrompt → the Avalonia StorageProvider folder picker (MA5 re-pick), with a
//     no-owner / no-pick fallback to null (write agency stays disabled — no silent stale root).
//
// Tests inject scripted fakes for both seams instead of these (the conflict/MA5 reconcile is driven
// headless without a real modal). Production wires these in the composition root.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using HoardPad.App.Views;

namespace HoardPad.App.Session;

/// <summary>Production <see cref="IRestoreConflictPrompt"/>: the real modal restore-conflict dialog (FR-19).</summary>
internal sealed class RestoreConflictPrompt : IRestoreConflictPrompt
{
    public async Task<RestoreConflictChoice> AskAsync(string fileName, CancellationToken ct)
    {
        // No owner window (should not happen on the desktop path) → conservative keep-disk (never silently
        // arm a clobbering Save with the stale recovered buffer).
        if (OwnerWindow() is not { } owner)
            return RestoreConflictChoice.KeepDisk;
        return await RestoreConflictDialog.ShowAsync(owner, fileName).ConfigureAwait(true);
    }

    private static Window? OwnerWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}

/// <summary>Production <see cref="IOpenFolderPrompt"/>: the MA5 re-pick folder picker (StorageProvider).</summary>
internal sealed class StorageOpenFolderPrompt : IOpenFolderPrompt
{
    public async Task<string?> RePromptForRootAsync(string? priorRoot, CancellationToken ct)
    {
        if (OwnerWindow() is not { } owner)
            return null; // no owner → no re-pick → write agency stays disabled (MA5 fail-safe).

        var options = new FolderPickerOpenOptions
        {
            Title = priorRoot is null
                ? "Choose the AI's document folder"
                : $"The AI folder moved — choose it again (was {priorRoot})",
            AllowMultiple = false,
        };

        IReadOnlyList<IStorageFolder> picked = await owner.StorageProvider
            .OpenFolderPickerAsync(options).ConfigureAwait(true);
        if (picked.Count == 0)
            return null; // user cancelled → no silent stale root (MA5).

        return picked[0].TryGetLocalPath();
    }

    private static Window? OwnerWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
