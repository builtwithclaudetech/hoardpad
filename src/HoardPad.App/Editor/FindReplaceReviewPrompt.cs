// FindReplaceReviewPrompt.cs — the production IFindReplaceReviewPrompt over the modal review dialog.
//
// Resolves the owner window from the classic-desktop lifetime and shows the real FindReplaceReviewDialog
// (FR-18 review-before-write, design §3.5). Tests inject a scripted fake instead, so the in-folder replace
// orchestration is driven headless without a real modal. No owner (should not happen on the desktop path)
// → return null (NOTHING is written — the conservative outcome for a multi-file disk mutation).

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;

namespace HoardPad.App.Editor;

/// <summary>Production <see cref="IFindReplaceReviewPrompt"/>: the real modal in-folder review (FR-18).</summary>
internal sealed class FindReplaceReviewPrompt : IFindReplaceReviewPrompt
{
    public async Task<IReadOnlyList<string>?> ReviewAsync(IReadOnlyList<InFolderFilePlan> plan, CancellationToken ct)
    {
        if (OwnerWindow() is not { } owner)
            return null; // no owner → no review → no write (review-before-write fail-safe).
        return await FindReplaceReviewDialog.ShowAsync(owner, plan).ConfigureAwait(true);
    }

    private static Window? OwnerWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
