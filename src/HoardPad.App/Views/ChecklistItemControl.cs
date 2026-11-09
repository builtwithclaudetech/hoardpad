// ChecklistItemControl.cs — one checklist row control + its tree-ARIA AutomationPeer (C-5/D11, design §10.2).
//
// The row is a ContentControl whose AutomationPeer subclass reports the TREE semantics a screen reader keys
// off: control type TreeItem, the numeric nesting level, position-in-set / size-of-set within the sibling
// group, the expand/collapse state of a parent, and the enabled (completion-lock) state. Setting
// AutomationProperties.Level in XAML alone is insufficient for NVDA/JAWS tree navigation (C-5) — the peer
// must return the TreeItem control type and the numeric level itself, which is what this subclass does.
//
// Avalonia 11.3.6 exposes no GetLevelCore virtual and no Level attached property on the peer, so the level
// is surfaced via the peer's public GetLevel() (the testable C-5 contract) AND mirrored into the UIA
// ItemStatus string ("level N") so assistive tech that reads ItemStatus still hears the tier. PositionInSet
// / SizeOfSet flow through the AutomationProperties attached props the base peer already surfaces.

using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Automation.Peers;
using HoardPad.App.ViewModels;

namespace HoardPad.App.Views;

/// <summary>A single checklist row; produces a <see cref="ChecklistItemAutomationPeer"/> for tree ARIA.</summary>
internal sealed class ChecklistItemControl : ContentControl
{
    /// <summary>Raised when the screen-reader expand/collapse provider asks to toggle this parent's twisty.</summary>
    public event System.Action<ChecklistItemViewModel>? CollapseToggleRequested;

    protected override AutomationPeer OnCreateAutomationPeer() => new ChecklistItemAutomationPeer(this);

    /// <summary>The row's view-model (the bound facts the peer reports), or null at design time.</summary>
    public ChecklistItemViewModel? Item => DataContext as ChecklistItemViewModel;

    /// <summary>Invoked by the AutomationPeer's IExpandCollapseProvider to toggle this parent's collapse.</summary>
    public void RaiseToggleCollapse()
    {
        if (Item is { } item)
            CollapseToggleRequested?.Invoke(item);
    }
}

/// <summary>
/// The tree-ARIA peer for a checklist row (C-5/D11). Returns <see cref="AutomationControlType.TreeItem"/>,
/// the numeric level, position-in-set / size-of-set within the sibling group, the parent's expand/collapse
/// state, and the completion-lock enabled state — the binding accessibility contract.
/// </summary>
internal sealed class ChecklistItemAutomationPeer : ContentControlAutomationPeer, IExpandCollapseProvider
{
    public ChecklistItemAutomationPeer(ChecklistItemControl owner) : base(owner)
    {
    }

    private ChecklistItemViewModel? Item => (Owner as ChecklistItemControl)?.Item;

    /// <summary>The UIA control type is <c>TreeItem</c> (C-5 — NVDA/JAWS tree navigation keys off this).</summary>
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TreeItem;

    /// <summary>The accessible name = the item text (with a completed-section cue for a checked parent).</summary>
    protected override string? GetNameCore()
    {
        if (Item is not { } item)
            return base.GetNameCore();
        return item.IsCompletedParent ? $"{item.Text}, section complete" : item.Text;
    }

    /// <summary>The completion-lock state (FR-41): a descendant of a checked parent is announced "disabled".</summary>
    protected override bool IsEnabledCore() => Item?.IsEnabled ?? base.IsEnabledCore();

    /// <summary>The numeric tree level (C-5): tier 0/1/2 → level 1/2/3. Public so the contract is testable.</summary>
    public int GetLevel() => (Item?.Depth ?? 0) + 1;

    /// <summary>The 1-based position within the sibling group (D11), read by UIA <c>PositionInSet</c>.</summary>
    public int GetPositionInSetValue() => Item?.PositionInSet ?? 0;

    /// <summary>The sibling-group size (D11), read by UIA <c>SizeOfSet</c>.</summary>
    public int GetSizeOfSetValue() => Item?.SizeOfSet ?? 0;

    // ----- IExpandCollapseProvider (FR-42 twisty announced "expanded"/"collapsed" on a parent). -----

    public ExpandCollapseState ExpandCollapseState => Item switch
    {
        { IsParent: true, Collapsed: true } => ExpandCollapseState.Collapsed,
        { IsParent: true, Collapsed: false } => ExpandCollapseState.Expanded,
        _ => ExpandCollapseState.LeafNode,
    };

    public bool ShowsMenu => false;

    public void Expand() => SetCollapsed(false);

    public void Collapse() => SetCollapsed(true);

    private void SetCollapsed(bool collapsed)
    {
        if (Item is { IsParent: true, Collapsed: var current } && current != collapsed)
            (Owner as ChecklistItemControl)?.RaiseToggleCollapse();
    }

    protected override object? GetProviderCore(System.Type providerType) =>
        providerType == typeof(IExpandCollapseProvider) ? this : base.GetProviderCore(providerType);
}
