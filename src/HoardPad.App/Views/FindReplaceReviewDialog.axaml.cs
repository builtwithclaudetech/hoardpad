// FindReplaceReviewDialog.axaml.cs — the FR-18 in-folder replace REVIEW modal (design §3.5).
//
// Shown via ShowDialog(owner) BEFORE any in-folder write (review-before-write, FR-18 / OQ-2): a per-file
// list with include/exclude checkboxes + per-file match→replace counts + a summary line. [Apply] returns
// the user-confirmed paths (unchecked files excluded); closing without Apply (window-X / Cancel) returns
// null (NOTHING is written). Mirrors the RestoreConflictDialog / DeleteParentDialog modal pattern.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using HoardPad.App.Editor;

namespace HoardPad.App.Views;

/// <summary>One reviewable file row: the label + match count + the include/exclude checkbox (design §3.5).</summary>
internal sealed partial class FindReplaceReviewRow : ObservableObject
{
    /// <summary>The absolute path (the apply key) — not shown; the label is the relative path.</summary>
    public string Path { get; }

    /// <summary>The relative display label (e.g. "src/app.py").</summary>
    public string Label { get; }

    /// <summary>The match count found in this file (== the replace count when included).</summary>
    public int MatchCount { get; }

    /// <summary>"3 matches → 3 replace" / "5 matches → (excluded)" — recomputed as the checkbox toggles.</summary>
    public string CountLabel => Included
        ? $"{MatchCount} match{(MatchCount == 1 ? "" : "es")} → {MatchCount} replace"
        : $"{MatchCount} match{(MatchCount == 1 ? "" : "es")} → (excluded)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountLabel))]
    private bool _included = true; // checked by default — the user unchecks to exclude (design §3.5).

    public FindReplaceReviewRow(InFolderFilePlan plan)
    {
        Path = plan.Path;
        Label = plan.DisplayLabel;
        MatchCount = plan.MatchCount;
    }
}

/// <summary>The modal in-folder replace review (FR-18 review-before-write). Returns the confirmed paths or null.</summary>
internal sealed partial class FindReplaceReviewDialog : Window
{
    // Null until [Apply] sets it — a window closed with no Apply (window-X / Cancel) returns null, so the
    // caller writes NOTHING (the conservative outcome for a multi-file disk mutation, FR-18 / OQ-2).
    private IReadOnlyList<string>? _result;

    private readonly ObservableCollection<FindReplaceReviewRow> _rows = [];

    /// <summary>Design-time / XAML-loader ctor.</summary>
    public FindReplaceReviewDialog()
    {
        InitializeComponent();
        Closed += (_, _) => Close(_result);
    }

    private FindReplaceReviewDialog(IReadOnlyList<InFolderFilePlan> plan) : this()
    {
        foreach (InFolderFilePlan p in plan)
            _rows.Add(new FindReplaceReviewRow(p));

        var list = this.FindControl<ItemsControl>("FileList")!;
        list.ItemsSource = _rows;

        foreach (FindReplaceReviewRow row in _rows)
            row.PropertyChanged += (_, _) => UpdateSummary();
        UpdateSummary();

        // Cancel takes initial focus — an accidental Enter must not write a multi-file change (FR-18 / OQ-2).
        Opened += (_, _) => this.FindControl<Button>("CancelButton")!.Focus();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void UpdateSummary()
    {
        int totalMatches = _rows.Sum(r => r.MatchCount);
        int totalFiles = _rows.Count;
        int includedFiles = _rows.Count(r => r.Included);
        int willReplace = _rows.Where(r => r.Included).Sum(r => r.MatchCount);

        // "9 matches across 3 files · 4 will be replaced in 2 files" (design §3.5).
        var summary = this.FindControl<TextBlock>("SummaryText")!;
        summary.Text =
            $"{totalMatches} match{(totalMatches == 1 ? "" : "es")} across {totalFiles} file{(totalFiles == 1 ? "" : "s")}" +
            $" · {willReplace} will be replaced in {includedFiles} file{(includedFiles == 1 ? "" : "s")}";
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        _result = _rows.Where(r => r.Included).Select(r => r.Path).ToList();
        Close(_result);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _result = null;
        Close(_result);
    }

    /// <summary>
    /// Show the review modally over <paramref name="owner"/> and return the confirmed paths (unchecked
    /// files excluded), or null when the user cancels / closes without applying (nothing is written).
    /// </summary>
    public static async System.Threading.Tasks.Task<IReadOnlyList<string>?> ShowAsync(
        Window owner, IReadOnlyList<InFolderFilePlan> plan)
    {
        var dialog = new FindReplaceReviewDialog(plan);
        return await dialog.ShowDialog<IReadOnlyList<string>?>(owner).ConfigureAwait(true);
    }
}
