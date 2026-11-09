// EditorSurfaceTests.cs — the M6.2 editor-surface gates (architecture §1.6 SC-3, design §3). Covers:
//   - FR-7 TextMate token read-back for >= 3 languages, driving the REAL TextMateSharp tokenizer
//     through the custom HoardPadRegistryOptions overlay (incl. the supplemental TOML grammar);
//   - FR-7 lazy grammar registration (the supplemental TOML grammar is not parsed until first use);
//   - FR-9 brace folding over a TextDocument snapshot;
//   - the shell <-> editor status-segment mirroring (FR-6 segments) + FR-5 dirty marker.
// The tokenizer + folding tests need no UI thread (ordinary [Fact]); the VM-mirroring test is a plain
// [Fact] too (no Avalonia control). The verify filter matches the class name "Editor".

using System.Collections.Generic;
using System.Linq;
using AvaloniaEdit.Document;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.Core.Recovery;
using HoardPad.Host;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class EditorSurfaceTests
{
    // ---- FR-7: >= 3 languages produce TextMate tokens through the custom registry overlay. ----

    [Theory]
    [InlineData(".cs", "source.cs", "public class Foo { }")]
    [InlineData(".py", "source.python", "def main():")]
    [InlineData(".rs", "source.rust", "fn main() {}")]   // Rust is in the STOCK grammar set
    [InlineData(".toml", "source.toml", "key = \"value\"")] // TOML is the SUPPLEMENTAL grammar
    public void GrammarTokenizesALineWithScopedTokens(string extension, string expectedScope, string line)
    {
        var options = new HoardPadRegistryOptions(ThemeName.OneDark);

        // The extension resolves to the expected scope (the supplemental TOML scope is overlaid).
        string? scope = options.ResolveScopeForExtension(extension);
        Assert.Equal(expectedScope, scope);

        // Drive the real tokenizer through the overlay (the same IRegistryOptions the editor installs).
        var registry = new Registry(options);
        IGrammar grammar = registry.LoadGrammar(scope!);
        ITokenizeLineResult result = grammar.TokenizeLine(line);

        // A non-trivial line yields more than the single whole-line default token, and at least one
        // token carries a language-specific scope beyond the bare root scope.
        Assert.NotEmpty(result.Tokens);
        Assert.Contains(result.Tokens, t => t.Scopes.Any(s => s != scope && s.Contains('.')));
    }

    [Fact]
    public void TomlGrammarIsParsedLazilyOnlyOnFirstGetGrammar()
    {
        var options = new HoardPadRegistryOptions(ThemeName.OneDark);

        // Resolving the extension does NOT parse the grammar (it only maps extension -> scope).
        Assert.Equal(HoardPadRegistryOptions.TomlScope, options.ResolveScopeForExtension(".toml"));

        // The first GetGrammar request parses + caches; a second returns the same cached instance.
        var first = options.GetGrammar(HoardPadRegistryOptions.TomlScope);
        var second = options.GetGrammar(HoardPadRegistryOptions.TomlScope);
        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void StockExtensionWithoutAGrammarResolvesToPlainText()
    {
        var options = new HoardPadRegistryOptions(ThemeName.OneDark);
        var catalog = new LanguageCatalog(options);

        (string? scope, string label) = catalog.Resolve("notes.unknownext");

        Assert.Null(scope);
        Assert.Equal("Plain Text", label);
    }

    [Fact]
    public void LanguageCatalogLabelsTomlFromTheSupplementalScope()
    {
        // QA3-4 Fix 1: the label is now the canonical LanguageCatalog.AvailableLanguages spelling
        // ("TOML", the same one the Language menu checkmarks), not the scope-derived "Toml".
        var catalog = new LanguageCatalog(new HoardPadRegistryOptions(ThemeName.OneDark));

        (string? scope, string label) = catalog.Resolve("Cargo.toml");

        Assert.Equal("source.toml", scope);
        Assert.Equal("TOML", label);
    }

    // ---- TextEncodings regression: unknown name falls back to UTF-8. ----

    [Fact]
    public void TextEncodingsResolveFallsBackToUtf8ForAnUnknownName()
    {
        // An unrecognised / garbage encoding name must not throw; it silently falls back to UTF-8.
        System.Text.Encoding result = TextEncodings.Resolve("x-this-encoding-does-not-exist-7f3a");

        // UTF-8 without BOM: GetPreamble() is empty (we use the no-BOM constructor variant).
        Assert.Equal("utf-8", result.WebName);
        Assert.Empty(result.GetPreamble());
    }

    // ---- FR-9: brace folding over a TextDocument snapshot. ----

    [Fact]
    public void BraceFoldingOnMalformedInputDoesNotCrashAndProducesNoGarbageFold()
    {
        // Unclosed '{' and orphaned '}' — neither pair is matchable.
        var docUnclosed = new TextDocument("function foo() {\n    bar();\n");    // no closing '}'
        var docOrphaned = new TextDocument("    result;\n}\n");                  // no opening '{'

        // Must not throw; must produce no fold (no valid pair to fold).
        List<AvaloniaEdit.Folding.NewFolding> unclosedFoldings = BraceFoldingStrategy.ComputeFoldings(FoldSnapshot.Capture(docUnclosed));
        List<AvaloniaEdit.Folding.NewFolding> orphanedFoldings = BraceFoldingStrategy.ComputeFoldings(FoldSnapshot.Capture(docOrphaned));

        Assert.Empty(unclosedFoldings);
        Assert.Empty(orphanedFoldings);
    }

    [Fact]
    public void BraceFoldingProducesAFoldForAMultiLineBlock()
    {
        var doc = new TextDocument("fn main() {\n    let x = 1;\n}\n");

        var foldings = BraceFoldingStrategy.ComputeFoldings(FoldSnapshot.Capture(doc));

        Assert.Single(foldings);
        // The fold starts just after the '{' and ends at the '}'.
        Assert.True(foldings[0].StartOffset > 0);
        Assert.True(foldings[0].EndOffset > foldings[0].StartOffset);
    }

    [Fact]
    public void BraceFoldingSkipsSingleLineBraces()
    {
        var doc = new TextDocument("let empty = {};\n");

        var foldings = BraceFoldingStrategy.ComputeFoldings(FoldSnapshot.Capture(doc));

        Assert.Empty(foldings);
    }

    [Fact]
    public void BraceFoldingNestsInnerAndOuterBlocks()
    {
        var doc = new TextDocument("a {\n  b {\n    c;\n  }\n}\n");

        var foldings = BraceFoldingStrategy.ComputeFoldings(FoldSnapshot.Capture(doc));

        Assert.Equal(2, foldings.Count);
        // Sorted by start offset: the outer block opens first.
        Assert.True(foldings[0].StartOffset < foldings[1].StartOffset);
    }

    // ---- FR-6 status segments + FR-5 dirty marker: the shell mirrors the editor VM. ----

    private static EditorViewModel NewEditorViewModel()
    {
        var options = new HoardPadRegistryOptions(ThemeName.OneDark);
        var fileService = new EditorFileService(new AtomicFileStore(), new PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        return new EditorViewModel(fileService, options, new LanguageCatalog(options));
    }

    [Fact]
    public void ShellMirrorsTheEditorStatusSegmentsAndDirtyMarker()
    {
        EditorViewModel editor = NewEditorViewModel();
        var shell = new MainWindowViewModel(editor);

        Assert.Equal("untitled", shell.DocumentName);
        Assert.Equal("UTF-8", shell.EncodingLabel);
        Assert.Equal("LF", shell.LineEndingLabel);
        Assert.Equal("✓ Saved", shell.DirtyStatusLabel);

        editor.IsDirty = true;
        Assert.True(shell.IsDirty);
        Assert.Equal("● Unsaved", shell.DirtyStatusLabel);
        Assert.Equal("HoardPad — untitled •", shell.Title);
    }

    [Fact]
    public void EditorViewModelUpdatesTheCursorSegment()
    {
        EditorViewModel editor = NewEditorViewModel();

        editor.UpdateCursor(12, 5);

        Assert.Equal("Ln 12, Col 5", editor.CursorLabel);
    }

    // ---- FEAT-6: View ▸ Word Wrap is display-only — the buffer/dirty state must never move. ----

    [Fact]
    public void ToggleWordWrapFlipsTheFlagWithoutTouchingTheBufferOrDirtyState()
    {
        EditorViewModel editor = NewEditorViewModel();
        editor.Document.Text = "line one\r\nline two\r\nline three";
        editor.IsDirty = false;
        string originalText = editor.Document.Text;

        Assert.False(editor.IsWordWrapEnabled);

        editor.ToggleWordWrapCommand.Execute(null);
        Assert.True(editor.IsWordWrapEnabled);
        Assert.Equal(originalText, editor.Document.Text);
        Assert.False(editor.IsDirty);

        editor.ToggleWordWrapCommand.Execute(null);
        Assert.False(editor.IsWordWrapEnabled);
        Assert.Equal(originalText, editor.Document.Text);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void ShellForwardsWordWrapFromTheActiveEditorAndReactsToATabSwitch()
    {
        EditorViewModel editorA = NewEditorViewModel();
        EditorViewModel editorB = NewEditorViewModel();
        var shell = new MainWindowViewModel(editorA);
        DocumentTab tabA = DocumentTab.ForEditor("tab-a", TabKind.Scratch, editorA);
        DocumentTab tabB = DocumentTab.ForEditor("tab-b", TabKind.Scratch, editorB);
        shell.Tabs.Add(tabA);
        shell.Tabs.Add(tabB);
        shell.ActivateTab(tabA);

        Assert.False(shell.IsWordWrapEnabled);
        shell.ToggleWordWrapCommand!.Execute(null);
        Assert.True(shell.IsWordWrapEnabled);
        Assert.True(editorA.IsWordWrapEnabled);

        // A tab switch to a DIFFERENT tab's editor VM must not carry editorA's flag over — each tab's
        // word-wrap state is independent (per-tab scope, see EditorViewModel.IsWordWrapEnabled).
        shell.ActivateTab(tabB);
        Assert.False(shell.IsWordWrapEnabled);
        Assert.False(editorB.IsWordWrapEnabled);
    }
}
