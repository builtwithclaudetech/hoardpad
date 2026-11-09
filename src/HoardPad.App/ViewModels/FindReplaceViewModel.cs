// FindReplaceViewModel.cs — the M6.7 docked find/replace panel view-model (FR-17 / FR-18, design §3.5).
//
// Drives the custom in-editor Find/Replace panel (NOT the stock AvaloniaEdit SearchPanel — design §3.5
// forbids it). Owns the FR-18 query state (find/replace text + the three toggles), the live "N of M"
// match count, next/prev navigation (with wraparound), and the Replace / Replace-All commands across the
// three FR-17 scopes (current file / all open / in folder). Toggles + find-text changes recompute the
// count LIVE. The in-folder replace is gated by a review modal (review-before-write, FR-18 / OQ-2).
//
// The pure FindReplaceEngine does the regex work; this VM is the orchestration + state. The scope access
// (buffers + the open-folder root + the atomic write path) is the IFindReplaceWorkspace seam, so the
// orchestration unit-tests without the shell / live editor. Navigation moving the editor caret/selection
// is a view concern wired via NavigateRequested (the panel raises it; the EditorView highlights).

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HoardPad.App.Editor;

namespace HoardPad.App.ViewModels;

/// <summary>The FR-17 replace scope: the active buffer, every open buffer, or the open folder (recursive).</summary>
internal enum FindReplaceScope
{
    /// <summary>The active editor buffer only.</summary>
    CurrentFile,

    /// <summary>Every open text tab's buffer.</summary>
    AllOpen,

    /// <summary>The open folder, recursive (review-before-write).</summary>
    InFolder,
}

/// <summary>The review-modal seam (FR-18 in-folder review). Production shows the modal; tests script the answer.</summary>
internal interface IFindReplaceReviewPrompt
{
    /// <summary>
    /// Review the per-file plan BEFORE any write. Returns the user-confirmed selection (the paths to
    /// actually replace in), or null when the user cancels (no write at all). The modal lets the user
    /// uncheck files to exclude them (design §3.5 review surface).
    /// </summary>
    Task<IReadOnlyList<string>?> ReviewAsync(IReadOnlyList<InFolderFilePlan> plan, CancellationToken ct);
}

/// <summary>The result of an in-folder apply: files written + total replacements (the summary count).</summary>
internal readonly record struct InFolderApplyResult(int FilesWritten, int TotalReplacements);

/// <summary>The docked find/replace panel VM (FR-17 / FR-18). Backs <c>FindReplaceView</c>.</summary>
internal sealed partial class FindReplaceViewModel : ObservableObject
{
    private readonly IFindReplaceWorkspace _workspace;
    private readonly IFindReplaceReviewPrompt _reviewPrompt;

    // The match cursor over the CURRENT buffer (for next/prev + the "N of M" count). Recomputed live.
    private IReadOnlyList<FindMatch> _matches = System.Array.Empty<FindMatch>();
    private int _currentMatchIndex = -1; // -1 = no current match (empty list or not yet navigated).

    public FindReplaceViewModel(IFindReplaceWorkspace workspace, IFindReplaceReviewPrompt reviewPrompt)
    {
        _workspace = workspace;
        _reviewPrompt = reviewPrompt;
        RefreshInFolderAvailability();
    }

    // ---- FR-18 query state. Each setter recomputes the live match count (OnXChanged partial hooks). ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchCountLabel))]
    [NotifyPropertyChangedFor(nameof(HasBadPattern))]
    private string _findText = string.Empty;

    [ObservableProperty]
    private string _replaceText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchCountLabel))]
    [NotifyPropertyChangedFor(nameof(HasBadPattern))]
    private bool _useRegex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchCountLabel))]
    [NotifyPropertyChangedFor(nameof(HasBadPattern))]
    private bool _caseSensitive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchCountLabel))]
    [NotifyPropertyChangedFor(nameof(HasBadPattern))]
    private bool _wholeWord;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewInFolderCommand))]
    private FindReplaceScope _scope = FindReplaceScope.CurrentFile;

    /// <summary>Whether the panel is shown (the shell toggles this; Esc closes it). The view binds visibility.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>Close the panel by hiding it via the × button or Esc (BUG-2).</summary>
    [RelayCommand]
    private void Hide() => IsVisible = false;

    /// <summary>Whether the in-folder scope is selectable (a folder is open). False disables the In-folder radio.</summary>
    [ObservableProperty]
    private bool _isInFolderAvailable;

    /// <summary>The open-folder root label for the In-folder radio ("In folder: &lt;root&gt;"), or empty when none.</summary>
    [ObservableProperty]
    private string _openFolderLabel = string.Empty;

    /// <summary>The last apply/replace summary surfaced to the user (the FR-18 replace-all summary count).</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>The live "N of M" / "M matches" count over the current buffer (FR-18 — updates live).</summary>
    public string MatchCountLabel
    {
        get
        {
            if (HasBadPattern)
                return "bad pattern";
            int total = _matches.Count;
            if (total == 0)
                return string.IsNullOrEmpty(FindText) ? string.Empty : "0 matches";
            int ordinal = _currentMatchIndex >= 0 ? _currentMatchIndex + 1 : 1;
            return $"{ordinal} of {total}";
        }
    }

    /// <summary>True when the current find text is a non-empty but malformed regex (FR-18 — surfaced, never crashes).</summary>
    public bool HasBadPattern => UseRegex && !string.IsNullOrEmpty(FindText) && !BuildEngine().IsValid;

    /// <summary>
    /// Raised after the current-buffer match set changes (a navigation or a replace) so the bound editor
    /// view can highlight the current match. Carries the active match span, or null to clear the highlight.
    /// </summary>
    public event System.Action<FindMatch?>? NavigateRequested;

    /// <summary>Build the engine for the current query (the pure regex worker). Cheap — rebuilt per use.</summary>
    private FindReplaceEngine BuildEngine() =>
        FindReplaceEngine.Compile(new FindReplaceQuery(FindText, ReplaceText, UseRegex, CaseSensitive, WholeWord));

    // Recompute the live count whenever a query field changes (the [ObservableProperty] partial hooks).
    partial void OnFindTextChanged(string value) => RecomputeMatches();

    partial void OnUseRegexChanged(bool value) => RecomputeMatches();

    partial void OnCaseSensitiveChanged(bool value) => RecomputeMatches();

    partial void OnWholeWordChanged(bool value) => RecomputeMatches();

    /// <summary>
    /// Recompute the current-buffer match set + reset the navigation cursor (FR-18 live count). Called on
    /// every query-field change and after the panel re-binds to a new active buffer. Clears the highlight
    /// when there is no match.
    /// </summary>
    public void RecomputeMatches()
    {
        string? text = _workspace.CurrentBuffer?.GetText();
        _matches = text is null ? System.Array.Empty<FindMatch>() : BuildEngine().FindAll(text);
        _currentMatchIndex = _matches.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(MatchCountLabel));
        NavigateRequested?.Invoke(_currentMatchIndex >= 0 ? _matches[_currentMatchIndex] : null);
    }

    /// <summary>Refresh the in-folder availability + label from the workspace (a folder opened/closed).</summary>
    public void RefreshInFolderAvailability()
    {
        string? root = _workspace.OpenFolderRoot;
        IsInFolderAvailable = root is not null;
        OpenFolderLabel = root is not null ? $"In folder: {root}" : "In folder (open a folder first)";
        if (root is null && Scope == FindReplaceScope.InFolder)
            Scope = FindReplaceScope.CurrentFile; // never leave the scope on a disabled option.
        ReviewInFolderCommand.NotifyCanExecuteChanged();
    }

    // ---- FR-18 navigation: next / previous with wraparound. ----

    [RelayCommand]
    private void FindNext() => MoveMatch(+1);

    [RelayCommand]
    private void FindPrevious() => MoveMatch(-1);

    private void MoveMatch(int delta)
    {
        if (_matches.Count == 0)
            return;
        // Wraparound: (i + delta) modulo count, normalised positive (FR-18 next/prev cycle through matches).
        _currentMatchIndex = ((_currentMatchIndex + delta) % _matches.Count + _matches.Count) % _matches.Count;
        OnPropertyChanged(nameof(MatchCountLabel));
        NavigateRequested?.Invoke(_matches[_currentMatchIndex]);
    }

    // ---- FR-18 replace / replace-all across the FR-17 scopes. ----

    /// <summary>
    /// Replace the single current match in the current buffer (FR-18 Replace). After the splice the
    /// match set is recomputed from scratch and the cursor resets to the first remaining match (index 0),
    /// so navigation re-anchors at the top of the updated buffer rather than advancing to the old next.
    /// </summary>
    [RelayCommand]
    private void Replace()
    {
        if (_workspace.CurrentBuffer is not { } buffer || _currentMatchIndex < 0 || _currentMatchIndex >= _matches.Count)
            return;

        FindReplaceEngine engine = BuildEngine();
        if (!engine.IsValid)
            return;

        // Replace just the active match span: take the text before/after the match and splice the rendered
        // replacement (with $-group substitution) for that single occurrence between them.
        FindMatch match = _matches[_currentMatchIndex];
        string text = buffer.GetText();
        string segment = text.Substring(match.Index, match.Length);
        ReplaceResult rendered = engine.ReplaceAll(segment); // one occurrence in the isolated segment.
        string newText = text[..match.Index] + rendered.NewText + text[(match.Index + match.Length)..];

        buffer.SetText(newText);
        StatusMessage = "Replaced 1";
        RecomputeMatches();
    }

    /// <summary>Replace ALL matches in the selected scope (current file / all open). FR-18 replace-all summary.</summary>
    [RelayCommand]
    private async Task ReplaceAllAsync(CancellationToken ct)
    {
        switch (Scope)
        {
            case FindReplaceScope.CurrentFile:
                ReplaceAllInCurrent();
                break;
            case FindReplaceScope.AllOpen:
                ReplaceAllInOpenBuffers();
                break;
            case FindReplaceScope.InFolder:
                await ReviewInFolderAsync(ct).ConfigureAwait(true);
                break;
        }
    }

    private void ReplaceAllInCurrent()
    {
        if (_workspace.CurrentBuffer is not { } buffer)
            return;
        FindReplaceEngine engine = BuildEngine();
        ReplaceResult result = engine.ReplaceAll(buffer.GetText());
        if (result.ReplacedCount > 0)
            buffer.SetText(result.NewText);
        StatusMessage = $"Replaced {result.ReplacedCount} in current file";
        RecomputeMatches();
    }

    private void ReplaceAllInOpenBuffers()
    {
        FindReplaceEngine engine = BuildEngine();
        int totalReplacements = 0;
        int filesChanged = 0;
        foreach (IFindReplaceBuffer buffer in _workspace.OpenBuffers)
        {
            ReplaceResult result = engine.ReplaceAll(buffer.GetText());
            if (result.ReplacedCount > 0)
            {
                buffer.SetText(result.NewText);
                totalReplacements += result.ReplacedCount;
                filesChanged++;
            }
        }
        StatusMessage = $"Replaced {totalReplacements} across {filesChanged} open file{(filesChanged == 1 ? "" : "s")}";
        RecomputeMatches();
    }

    // ---- FR-18 in-folder review-before-write (OQ-2 default). ----

    private bool CanReviewInFolder() => IsInFolderAvailable;

    /// <summary>
    /// Build the in-folder per-file plan (match counts), show the review modal (review-before-write), then
    /// atomically write ONLY the user-confirmed files through the store. Reports per-file counts (FR-17
    /// Done-when) and the summary. The [Review…] button + the in-folder Replace All both route here.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReviewInFolder))]
    private async Task ReviewInFolderAsync(CancellationToken ct)
    {
        FindReplaceEngine engine = BuildEngine();
        if (!engine.IsValid)
        {
            StatusMessage = "Enter a valid find pattern first";
            return;
        }

        // Plan: enumerate the folder tree off the UI thread (W2: EnumerateFolderFiles may do a full recursive
        // Directory.EnumerateFiles scan — a sync tree walk must never run on the dispatcher thread, NFR-1).
        IReadOnlyList<string> candidates;
        try
        {
            candidates = await Task.Run(() => _workspace.EnumerateFolderFiles(), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "In-folder scan cancelled";
            return;
        }

        // Read each candidate file and count matches; keep only matched files (the review surface lists them).
        // The read is already off the UI thread via ReadFileAsync; non-text files are skipped (read null).
        var plan = new List<InFolderFilePlan>();
        var contents = new Dictionary<string, string>(System.StringComparer.Ordinal);
        string root = _workspace.OpenFolderRoot ?? string.Empty;
        try
        {
            foreach (string path in candidates)
            {
                ct.ThrowIfCancellationRequested();
                string? text = await _workspace.ReadFileAsync(path, ct).ConfigureAwait(true);
                if (text is null)
                    continue;
                int count = engine.CountMatches(text);
                if (count == 0)
                    continue;
                contents[path] = text;
                plan.Add(new InFolderFilePlan(path, RelativeLabel(root, path), count));
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "In-folder scan cancelled";
            return;
        }

        if (plan.Count == 0)
        {
            StatusMessage = "No matches in folder";
            return;
        }

        // Review BEFORE write (FR-18 / OQ-2). The modal returns the confirmed paths (unchecked = excluded),
        // or null when the user cancels — in which case NOTHING is written.
        IReadOnlyList<string>? confirmed = await _reviewPrompt.ReviewAsync(plan, ct).ConfigureAwait(true);
        if (confirmed is null)
        {
            StatusMessage = "In-folder replace cancelled";
            return;
        }

        // C1: wrap the apply in its own error surface so a disk fault on file N names the failing file and
        // how many files were already written (the brief: "must surface failures, not silently drop a file").
        // This is the legitimate try boundary around a disk mutation — not a blanket catch.
        try
        {
            InFolderApplyResult applied = await ApplyInFolderAsync(engine, contents, confirmed, ct).ConfigureAwait(true);
            StatusMessage =
                $"Replaced {applied.TotalReplacements} in {applied.FilesWritten} file{(applied.FilesWritten == 1 ? "" : "s")}";
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == ct)
        {
            // Surface the cancellation status so the user knows partial writes may have occurred.
            // The fault count is surfaced by the exception message from ApplyInFolderAsync.
            StatusMessage = ex.Message; // set by ApplyInFolderAsync before re-throwing.
        }
        catch (IOException ex)
        {
            StatusMessage = ex.Message; // set by ApplyInFolderAsync before re-throwing.
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = ex.Message; // set by ApplyInFolderAsync before re-throwing.
        }

        RecomputeMatches();
    }

    /// <summary>
    /// Write each confirmed file atomically via the workspace store. On a disk fault (IOException /
    /// UnauthorizedAccessException) or mid-apply cancellation, the exception message is pre-populated with a
    /// "failed at {label}: {reason} — N file(s) already written" summary and then re-thrown so the caller's
    /// try/catch (C1) sets <see cref="StatusMessage"/> without needing to know the inner accounting.
    /// </summary>
    private async Task<InFolderApplyResult> ApplyInFolderAsync(
        FindReplaceEngine engine,
        IReadOnlyDictionary<string, string> contents,
        IReadOnlyList<string> confirmedPaths,
        CancellationToken ct)
    {
        int filesWritten = 0;
        int totalReplacements = 0;
        var confirmedSet = new HashSet<string>(confirmedPaths, System.StringComparer.Ordinal);

        foreach ((string path, string original) in contents)
        {
            if (!confirmedSet.Contains(path))
                continue; // unchecked file — excluded, NOT written.

            if (ct.IsCancellationRequested)
            {
                // Wrap the cancellation so the caller's catch gets the partial-write count in the message.
                throw new OperationCanceledException(
                    $"In-folder replace cancelled after {filesWritten} file{(filesWritten == 1 ? "" : "s")} written",
                    ct);
            }

            ReplaceResult result = engine.ReplaceAll(original);
            if (result.ReplacedCount == 0)
                continue;

            // Legitimate disk-mutation try boundary (C1). On ENOSPC/EIO/IOException the message names the
            // failing file and how many files were already written before the fault — surfaced to the user.
            try
            {
                await _workspace.WriteFileAsync(path, result.NewText, ct).ConfigureAwait(true);
            }
            catch (IOException ex)
            {
                // Preserve the file label in the message so the caller can set StatusMessage directly.
                string label = RelativeLabel(_workspace.OpenFolderRoot ?? string.Empty, path);
                throw new IOException(
                    $"In-folder replace failed at '{label}': {ex.Message} — {filesWritten} file{(filesWritten == 1 ? "" : "s")} already written",
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                string label = RelativeLabel(_workspace.OpenFolderRoot ?? string.Empty, path);
                throw new UnauthorizedAccessException(
                    $"In-folder replace failed at '{label}': {ex.Message} — {filesWritten} file{(filesWritten == 1 ? "" : "s")} already written",
                    ex);
            }

            filesWritten++;
            totalReplacements += result.ReplacedCount;
        }

        return new InFolderApplyResult(filesWritten, totalReplacements);
    }

    private static string RelativeLabel(string root, string path)
    {
        if (string.IsNullOrEmpty(root))
            return path;
        string relative = System.IO.Path.GetRelativePath(root, path);
        return relative;
    }
}
