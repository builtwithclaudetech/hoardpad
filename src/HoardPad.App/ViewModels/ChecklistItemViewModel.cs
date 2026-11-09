// ChecklistItemViewModel.cs — one checklist row VM (FR-30/FR-41/FR-42, design §5.1).
//
// A thin projection over a Core HoardItem id. The parent ChecklistViewModel owns the model list and the
// pure-function results; this row VM exposes only the bound display facts a row needs. IsEnabled is the
// completion-lock (effectiveEnabled) projection and is the C-1 fan-out target: when ANY ancestor's checked
// toggles, the parent VM calls RefreshLockState on EVERY descendant row VM so its IsEnabled re-raises
// PropertyChanged (Avalonia will not re-query a descendant's IsEnabled because an ancestor changed).

using CommunityToolkit.Mvvm.ComponentModel;

namespace HoardPad.App.ViewModels;

/// <summary>One checklist row's view-model — the bound display facts (text/checked/lock/nesting) for a row.</summary>
internal sealed partial class ChecklistItemViewModel : ObservableObject
{
    /// <summary>The stable Core item id this row projects (never reused — the durable correlation key).</summary>
    public string Id { get; }

    public ChecklistItemViewModel(string id) => Id = id;

    /// <summary>The item label (FR-30). Two-way: inline edit writes back through the parent VM.</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>The item's own stored check state (FR-30). Shown honestly even when locked (D3a).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStruck))]
    [NotifyPropertyChangedFor(nameof(IsCompletedParent))]
    private bool _checked;

    /// <summary>
    /// The completion-lock state (FR-41) = <c>effectiveEnabled(item)</c>. False when ANY strict ancestor is
    /// checked → the row renders greyed/inert. The C-1 fan-out re-raises this on every descendant on a
    /// parent toggle (a passive binding re-query is insufficient).
    /// </summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>Derived nesting depth (0/1/2) for the indent guide rendering (§5.1).</summary>
    [ObservableProperty]
    private int _depth;

    /// <summary>Whether this item has ≥1 child — drives the ▸/▾ twisty's presence (FR-42, §5.1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompletedParent))]
    private bool _isParent;

    /// <summary>The durable collapse state (FR-42); ▸ collapsed / ▾ expanded. Meaningful only on a parent.</summary>
    [ObservableProperty]
    private bool _collapsed;

    /// <summary>Position within the sibling group (1-based) for the tree ARIA peer (D11). </summary>
    [ObservableProperty]
    private int _positionInSet;

    /// <summary>Sibling-group size for the tree ARIA peer (D11).</summary>
    [ObservableProperty]
    private int _sizeOfSet;

    /// <summary>Struck-through styling: a checked item under <c>strikethrough-in-place</c> (design §5.1/§5.5).</summary>
    public bool IsStruck => Checked;

    /// <summary>A checked item that is also a parent — carries the "( section complete )" text badge (§5.1).</summary>
    public bool IsCompletedParent => Checked && IsParent;
}
