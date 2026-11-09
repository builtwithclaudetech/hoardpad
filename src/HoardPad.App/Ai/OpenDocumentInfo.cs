// OpenDocumentInfo.cs — the open-document snapshot the AI multi-doc panel enumerates (PANEL-2,
// feature-ai-multidoc-panel §2.1 Terms, FR-MD-3/FR-MD-6). One immutable record per currently-open document
// tab, carrying ONLY the facts the checked-set assembly + the FR-MD-3 context descriptor need: the on-disk
// path (null for a never-saved scratch buffer), whether it is the FOCUSED document, and its dirty /
// read-only status. It carries NO file CONTENTS (FR-MD-3 / NG-4: the descriptor is paths + flags only —
// read tiers stay owned by FR-25).
//
// The panel obtains a fresh snapshot at turn-arm via an IReadOnlyList<OpenDocumentInfo> provider (a Func the
// composition root wires over MainWindowViewModel.Tabs + ActiveTab). A snapshot decouples the panel from the
// live tab model and makes the arm-time assembly deterministically testable.

namespace HoardPad.App.Ai;

/// <summary>
/// An immutable snapshot of one currently-open document, as seen by the AI multi-doc panel at turn-arm
/// (feature-ai-multidoc-panel §2.1). Carries the path + flags the checked-set grant assembly (FR-MD-1/2)
/// and the FR-MD-3 context descriptor need — never the buffer contents (NG-4).
/// </summary>
/// <param name="Path">
/// The document's on-disk path, or <c>null</c> for a never-saved scratch buffer (NG-5). A null-path
/// document can be checked but resolves to NO grant (it has no resolvable path) — the assembly drops it
/// without crashing.
/// </param>
/// <param name="DisplayName">The tab's display name (file name / "untitled") — for the descriptor + PANEL-3 rows.</param>
/// <param name="IsFocused"><c>true</c> for the focused (active-tab) document — pre-checked by default on arm (FR-MD-6(a)).</param>
/// <param name="IsDirty"><c>true</c> when the buffer has unsaved edits (FR-MD-3 descriptor flag; FR-MD-7 flush is PANEL-4).</param>
/// <param name="IsReadOnly"><c>true</c> when the document is read-only on disk (FR-MD-3 descriptor flag).</param>
/// <param name="TabId">
/// PANEL-3 (FR-MD-6(d), OQ-MD-3): the durable tab id, so the Save-As-on-check flow can target the EXACT
/// scratch buffer the user checked (a scratch buffer's path is null, so a path cannot identify it). Null in
/// tests / paths that do not exercise the Save-As flow — the flow then cannot target a specific tab and the
/// scratch check simply reverts (correct fail-closed: a path-less buffer grants nothing).
/// </param>
public sealed record OpenDocumentInfo(
    string? Path,
    string DisplayName,
    bool IsFocused,
    bool IsDirty,
    bool IsReadOnly,
    string? TabId = null)
{
    /// <summary><c>true</c> when this document has no on-disk path (a scratch buffer) — non-grantable in v1 (NG-5).</summary>
    public bool IsScratch => string.IsNullOrEmpty(Path);
}
