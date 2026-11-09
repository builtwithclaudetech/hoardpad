// Feat14AiSettingsChangeTests.cs — FEAT-14 (architecture AI file-op safety + FR boundaries).
//
// Covers, bottom-up: (1) the pure marker parser (SettingsChangeProposalParser), (2) the pure allow-list
// + value computation (SettingsChangeApplier — every field kind, the clamp, the no-op short-circuit, the
// unknown-field decline), then (3) the AiPanelViewModel integration — the capability instruction is
// prepended ONLY under the write-capable gate, an AutoApply-mode marker applies WITHOUT a confirm, an
// ApproveEach-mode marker confirms first (Deny declines / Approve applies), a ReadOnly turn never even
// offers the capability, an unwired seam declines silently, and RevertLastSettingsChangeCommand restores
// the exact pre-change snapshot. None of these tests touch the frozen spine (Core.Ai / Host / the SC-5
// harness) — FEAT-14 lives entirely in the App layer.
//
// Class names resolve the verify filter "FullyQualifiedName~Settings".

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Ai;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;
using Xunit;

namespace HoardPad.App.Tests;

// ---------------------------------------------------------------------------------------------------
//  (1) SettingsChangeProposalParser — pure marker extraction.
// ---------------------------------------------------------------------------------------------------

public sealed class SettingsChangeProposalParserTests
{
    [Fact]
    public void TryParseExtractsFieldAndValueFromAWellFormedMarker()
    {
        string text = "Sure, I'll make it bigger.\n```hoardpad-settings\n{\"field\": \"EditorFontSize\", \"value\": \"16\"}\n```\nDone.";

        bool ok = SettingsChangeProposalParser.TryParse(text, out string? field, out string? value);

        Assert.True(ok);
        Assert.Equal("EditorFontSize", field);
        Assert.Equal("16", value);
    }

    [Fact]
    public void TryParseAcceptsANumericJsonValue()
    {
        string text = "```hoardpad-settings\n{\"field\": \"EditorFontSize\", \"value\": 18}\n```";

        bool ok = SettingsChangeProposalParser.TryParse(text, out string? field, out string? value);

        Assert.True(ok);
        Assert.Equal("EditorFontSize", field);
        Assert.Equal("18", value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("just a normal reply, no marker")]
    [InlineData("```hoardpad-settings\nnot json\n```")]
    [InlineData("```hoardpad-settings\n{\"field\": \"EditorFontSize\"}\n```")] // missing "value"
    [InlineData("```hoardpad-settings\n{\"value\": \"16\"}\n```")]            // missing "field"
    [InlineData("```hoardpad-settings\n[1,2,3]\n```")]                        // not an object
    public void TryParseDeclinesAnyNonConformingText(string text)
    {
        bool ok = SettingsChangeProposalParser.TryParse(text, out string? field, out string? value);

        Assert.False(ok);
        Assert.Null(field);
        Assert.Null(value);
    }

    [Fact]
    public void TryParseNeverThrowsOnNullInput()
    {
        Assert.False(SettingsChangeProposalParser.TryParse(null, out _, out _));
    }
}

// ---------------------------------------------------------------------------------------------------
//  (2) SettingsChangeApplier — the pure allow-list + value computation.
// ---------------------------------------------------------------------------------------------------

public sealed class SettingsChangeApplierTests
{
    [Fact]
    public void EditorFontSizeChangeComputesTheUpdatedRecordAndDisplayStrings()
    {
        HoardPadSettings current = HoardPadSettings.Default with { EditorFontSize = 13 };

        SettingsChangeComputation? result = SettingsChangeApplier.TryCompute(current, "EditorFontSize", "15");

        Assert.NotNull(result);
        Assert.Equal("Editor font size", result!.FieldLabel);
        Assert.Equal("13", result.OldDisplay);
        Assert.Equal("15", result.NewDisplay);
        Assert.Equal(15, result.Updated.EditorFontSize);
    }

    [Fact]
    public void FontSizeChangeIsClampedToTheDocumentedBoundsRatherThanRejected()
    {
        HoardPadSettings current = HoardPadSettings.Default with { EditorFontSize = 13 };

        SettingsChangeComputation? result = SettingsChangeApplier.TryCompute(current, "EditorFontSize", "999");

        Assert.NotNull(result);
        Assert.Equal(HoardPadSettings.MaxEditorFontSize, result!.Updated.EditorFontSize);
    }

    [Fact]
    public void AiResponseFontSizeUsesTheSharedUiFontSizeBoundsNotTheEditorBounds()
    {
        HoardPadSettings current = HoardPadSettings.Default with { AiResponseFontSize = 12 };

        // 30 is within the editor bound (8..36) but ABOVE the UI/AI-panel bound (8..24) — proves the
        // AI-panel fields clamp against MinUiFontSize/MaxUiFontSize, not the editor's wider range.
        SettingsChangeComputation? result = SettingsChangeApplier.TryCompute(current, "AiResponseFontSize", "30");

        Assert.NotNull(result);
        Assert.Equal(HoardPadSettings.MaxUiFontSize, result!.Updated.AiResponseFontSize);
    }

    [Fact]
    public void FontSizeChangeDeclinesAnUnparseableValue()
    {
        HoardPadSettings current = HoardPadSettings.Default;

        Assert.Null(SettingsChangeApplier.TryCompute(current, "EditorFontSize", "huge"));
    }

    [Fact]
    public void FontSizeChangeIsANoOpWhenTheClampedValueEqualsTheCurrentValue()
    {
        HoardPadSettings current = HoardPadSettings.Default with { EditorFontSize = HoardPadSettings.MaxEditorFontSize };

        // Requests something above the max — clamps back down to the SAME current value ⇒ no-op.
        Assert.Null(SettingsChangeApplier.TryCompute(current, "EditorFontSize", "500"));
    }

    [Fact]
    public void FontFamilyChangeComputesTheUpdatedRecord()
    {
        HoardPadSettings current = HoardPadSettings.Default with { UiFontFamily = null };

        SettingsChangeComputation? result = SettingsChangeApplier.TryCompute(current, "UiFontFamily", "  Fira Code  ");

        Assert.NotNull(result);
        Assert.Equal("UI font family", result!.FieldLabel);
        Assert.Equal("(default)", result.OldDisplay);
        Assert.Equal("Fira Code", result.NewDisplay);
        Assert.Equal("Fira Code", result.Updated.UiFontFamily);
    }

    [Fact]
    public void FontFamilyChangeDeclinesAnEmptyValue()
    {
        Assert.Null(SettingsChangeApplier.TryCompute(HoardPadSettings.Default, "UiFontFamily", "   "));
    }

    [Fact]
    public void ColorChangeAcceptsAValidHexAndComputesTheUpdatedRecord()
    {
        HoardPadSettings current = HoardPadSettings.Default with { AiUserColor = null };

        SettingsChangeComputation? result = SettingsChangeApplier.TryCompute(current, "AiUserColor", "#E5B93D");

        Assert.NotNull(result);
        Assert.Equal("#E5B93D", result!.Updated.AiUserColor);
        Assert.Equal("(theme default)", result.OldDisplay);
    }

    [Fact]
    public void ColorChangeDeclinesAnUnparseableHex()
    {
        Assert.Null(SettingsChangeApplier.TryCompute(HoardPadSettings.Default, "AiUserColor", "not-a-color"));
    }

    [Fact]
    public void ColorChangeToDefaultClearsAnExistingOverride()
    {
        HoardPadSettings current = HoardPadSettings.Default with { AiResponseColor = "#123456" };

        SettingsChangeComputation? result = SettingsChangeApplier.TryCompute(current, "AiResponseColor", "default");

        Assert.NotNull(result);
        Assert.Null(result!.Updated.AiResponseColor);
    }

    [Theory]
    [InlineData("AgencyMode")]
    [InlineData("ModelId")]
    [InlineData("EffortLevel")]
    [InlineData("ConfirmLargeHomeFolder")]
    [InlineData("LargeRootThreshold")]
    [InlineData("NotARealField")]
    public void EverySafetyRelevantOrUnknownFieldIsDeclinedByConstruction(string field)
    {
        // QA4-17: the exclusion set is SIX fields total — the five below (plus any unrecognized name) —
        // AND AnthropicApiKey (hostile-round CRITICAL fix; still excluded, NOT allowed — its dedicated
        // refusal test lives in Qa417AiSettingsChangeExcludedFieldRefusalTests.AnthropicApiKeyIsNeverAiWritable,
        // not here, purely to keep this Theory's InlineData list unchanged). ActiveBackend/Caps/
        // NoReplySeconds/AiPanelHotkey/Theme (unlike AnthropicApiKey) DID move to the ALLOWED side — see
        // Qa417AiSettingsChangeTests. No switch case exists for the five below — TryCompute must return
        // null, never touch them.
        Assert.Null(SettingsChangeApplier.TryCompute(HoardPadSettings.Default, field, "anything"));
    }
}

// ---------------------------------------------------------------------------------------------------
//  (3) AiPanelViewModel integration — the capability gate, the agency-routed apply, and revert.
// ---------------------------------------------------------------------------------------------------

/// <summary>A turn runner that records the received TurnRequest.Prompt and yields a SCRIPTED response.</summary>
internal sealed class RecordingTurnRunner : IAiTurnRunner
{
    private readonly IReadOnlyList<TurnEvent> _script;
    public List<TurnRequest> Requests { get; } = [];

    public RecordingTurnRunner(IEnumerable<TurnEvent> script) => _script = script.ToList();

    public async IAsyncEnumerable<TurnEvent> RunAsync(
        TurnRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        await Task.CompletedTask;
        foreach (TurnEvent ev in _script)
        {
            ct.ThrowIfCancellationRequested();
            yield return ev;
        }
    }

    public Task StopAsync() => Task.CompletedTask;
}

public sealed class Feat14AiSettingsChangeTests
{
    private const string MarkerResponse =
        "Sure!\n```hoardpad-settings\n{\"field\": \"EditorFontSize\", \"value\": \"16\"}\n```\nDone.";

    private static async Task<AiPanelViewModel> ArmedAsync(
        RecordingTurnRunner runner, AgencyMode configuredMode, IAiConfirmService? confirm = null)
    {
        var vm = new AiPanelViewModel(
            runner,
            confirm ?? new ScriptedConfirmService(),
            new ScriptedHookHost(selfTestPasses: true),
            new FakeAgentBackend(),
            TimeProvider.System);

        vm.BindSession(
            "/proj",
            new AiFileOpsService(
                new ChecklistTestSupportSandboxGate(),
                new CountingAtomicFileStore(),
                new NullCapStateStore(),
                new FakeAppDataPaths(),
                sessionId: "s1",
                HoardPad.Core.Ai.Caps.CapConfig.Pinned),
            conversationId: "conv-feat14");

        vm.ConfiguredMode = configuredMode;
        await vm.ArmAgencyAsync();
        return vm;
    }

    /// <summary>
    /// QA4-17: a settings-change apply seam matching the real field-scoped-patch delegate shape
    /// (<c>Func&lt;HoardPadSettings, HoardPadSettings&gt;</c>). Applies each received patch against a
    /// running <see cref="Baseline"/> (seed it to match whatever <c>CurrentSettingsProvider</c> returns, so
    /// the FIRST recorded result equals what the old full-record <c>computation.Updated</c> would have
    /// been) and records the resulting settings snapshot after each call — the same shape prior tests
    /// asserted against, but now proving the change is genuinely FIELD-SCOPED (every other field survives
    /// untouched across calls, exactly like the real <c>App.ApplyAiSettingsChangeAsync</c>).
    /// </summary>
    private sealed class RecordingApply
    {
        public HoardPadSettings Baseline { get; set; } = HoardPadSettings.Default;
        public List<HoardPadSettings> Applied { get; } = [];
        public Task Handler(Func<HoardPadSettings, HoardPadSettings> patch, CancellationToken ct)
        {
            Baseline = patch(Baseline);
            Applied.Add(Baseline);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task WriteCapableTurnPrependsTheCapabilityInstructionToTheRunnerPromptButNotTheTranscript()
    {
        var runner = new RecordingTurnRunner([TurnEvent.Assistant("ok")]);
        AiPanelViewModel vm = await ArmedAsync(runner, AgencyMode.AutoApply);

        vm.PromptInput = "make the font bigger";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Single(runner.Requests);
        Assert.Contains(SettingsChangeProposalParser.CapabilityInstruction, runner.Requests[0].Prompt);
        Assert.EndsWith("make the font bigger", runner.Requests[0].Prompt);
        // The TRANSCRIPT shows the bare prompt (PANEL-2 posture) — never the capability instruction text.
        Assert.Contains(vm.Transcript, t => t.Kind == TranscriptKind.UserMessage && t.Text == "make the font bigger");
        Assert.DoesNotContain(vm.Transcript, t => t.Text.Contains("HoardPad capability"));
    }

    [Fact]
    public async Task ReadOnlyTurnNeverOffersTheCapabilityAndNeverAppliesAMarkerEvenIfPresent()
    {
        // A read-only turn's runner would never legitimately emit the marker (the backend has no write
        // tools offered), but this proves the APP-SIDE gate independently: even if text containing the
        // marker somehow arrived, a ReadOnly turn never calls HandleSettingsChangeProposalAsync at all.
        var runner = new RecordingTurnRunner([TurnEvent.Assistant(MarkerResponse)]);
        var apply = new RecordingApply();
        AiPanelViewModel vm = await ArmedAsync(runner, AgencyMode.ReadOnly);
        vm.CurrentSettingsProvider = _ => Task.FromResult(HoardPadSettings.Default);
        vm.ApplySettingsChangeAsync = apply.Handler;

        vm.PromptInput = "make the font bigger";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.DoesNotContain(SettingsChangeProposalParser.CapabilityInstruction, runner.Requests[0].Prompt);
        Assert.Empty(apply.Applied);
        Assert.False(vm.CanRevertSettingsChange);
    }

    [Fact]
    public async Task AutoApplyModeAppliesTheProposedChangeWithoutAConfirmAndRendersAnAuditableChip()
    {
        var runner = new RecordingTurnRunner([TurnEvent.Assistant(MarkerResponse)]);
        var confirm = new ScriptedConfirmService();
        var apply = new RecordingApply();
        AiPanelViewModel vm = await ArmedAsync(runner, AgencyMode.AutoApply, confirm);
        vm.CurrentSettingsProvider = _ => Task.FromResult(HoardPadSettings.Default with { EditorFontSize = 13 });
        apply.Baseline = HoardPadSettings.Default with { EditorFontSize = 13 };
        vm.ApplySettingsChangeAsync = apply.Handler;

        vm.PromptInput = "make the font bigger";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(confirm.SettingsChangeRequests); // AutoApply never confirms — same posture as a file write.
        Assert.Single(apply.Applied);
        Assert.Equal(16, apply.Applied[0].EditorFontSize);
        Assert.Contains(vm.Transcript, t => t.Kind == TranscriptKind.SafetyEventChip
            && t.Text.Contains("Editor font size") && t.Text.Contains("13") && t.Text.Contains("16"));
        Assert.True(vm.CanRevertSettingsChange);
        Assert.Contains("Editor font size", vm.RevertSettingsChangeLabel);

        // QA4-17 hostile-round fix: the fenced marker JSON is stripped from the assistant's rendered
        // message once parsed — it must never linger in the chat bubble or be re-injected into a later
        // turn's prompt by AiConversationContextBuilder. The surrounding prose survives untouched.
        TranscriptLine assistantMessage = Assert.Single(vm.Transcript, t => t.Kind == TranscriptKind.AssistantMessage);
        Assert.DoesNotContain("hoardpad-settings", assistantMessage.Text);
        Assert.DoesNotContain("EditorFontSize", assistantMessage.Text);
        Assert.Contains("Sure!", assistantMessage.Text);
        Assert.Contains("Done.", assistantMessage.Text);
    }

    [Fact]
    public async Task ApproveEachModeConfirmsFirstAndAppliesOnlyWhenApproved()
    {
        var runner = new RecordingTurnRunner([TurnEvent.Assistant(MarkerResponse)]);
        var confirm = new ScriptedConfirmService(settingsAnswers: [true]);
        var apply = new RecordingApply();
        AiPanelViewModel vm = await ArmedAsync(runner, AgencyMode.ApproveEach, confirm);
        vm.CurrentSettingsProvider = _ => Task.FromResult(HoardPadSettings.Default with { EditorFontSize = 13 });
        apply.Baseline = HoardPadSettings.Default with { EditorFontSize = 13 };
        vm.ApplySettingsChangeAsync = apply.Handler;

        vm.PromptInput = "make the font bigger";
        await vm.SendCommand.ExecuteAsync(null);

        SettingsChangeConfirmRequest req = Assert.Single(confirm.SettingsChangeRequests);
        Assert.Equal("Editor font size", req.FieldLabel);
        Assert.Equal("13", req.OldValue);
        Assert.Equal("16", req.NewValue);
        Assert.Single(apply.Applied);
        Assert.Equal(16, apply.Applied[0].EditorFontSize);
    }

    [Fact]
    public async Task ApproveEachModeDeclinesTheChangeWhenTheUserDenies()
    {
        var runner = new RecordingTurnRunner([TurnEvent.Assistant(MarkerResponse)]);
        var confirm = new ScriptedConfirmService(settingsAnswers: [false]);
        var apply = new RecordingApply();
        AiPanelViewModel vm = await ArmedAsync(runner, AgencyMode.ApproveEach, confirm);
        vm.CurrentSettingsProvider = _ => Task.FromResult(HoardPadSettings.Default with { EditorFontSize = 13 });
        vm.ApplySettingsChangeAsync = apply.Handler;

        vm.PromptInput = "make the font bigger";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Single(confirm.SettingsChangeRequests);
        Assert.Empty(apply.Applied); // Denied — never applied.
        Assert.False(vm.CanRevertSettingsChange);
        Assert.Contains(vm.Transcript, t => t.Kind == TranscriptKind.SystemNote && t.Text.Contains("declined"));
    }

    [Fact]
    public async Task AnUnwiredApplySeamDeclinesTheProposalWithoutThrowing()
    {
        var runner = new RecordingTurnRunner([TurnEvent.Assistant(MarkerResponse)]);
        AiPanelViewModel vm = await ArmedAsync(runner, AgencyMode.AutoApply);
        // CurrentSettingsProvider / ApplySettingsChangeAsync are deliberately left null (no wiring).

        vm.PromptInput = "make the font bigger";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.False(vm.CanRevertSettingsChange); // nothing was applied — fail-closed on an unwired seam.
        Assert.False(vm.IsTurnInFlight);
    }

    [Fact]
    public async Task AnUnrecognizedOrInvalidFieldIsDeclinedWithAVisibleNote()
    {
        var runner = new RecordingTurnRunner(
            [TurnEvent.Assistant("```hoardpad-settings\n{\"field\": \"AgencyMode\", \"value\": \"read-only\"}\n```")]);
        var apply = new RecordingApply();
        AiPanelViewModel vm = await ArmedAsync(runner, AgencyMode.AutoApply);
        vm.CurrentSettingsProvider = _ => Task.FromResult(HoardPadSettings.Default);
        vm.ApplySettingsChangeAsync = apply.Handler;

        vm.PromptInput = "turn off write access";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(apply.Applied); // AgencyMode has no switch case in SettingsChangeApplier — declined.
        Assert.Contains(vm.Transcript, t => t.Kind == TranscriptKind.SystemNote && t.Text.Contains("could not be applied"));
    }

    [Fact]
    public async Task RevertLastSettingsChangeRestoresTheExactPreChangeSnapshotAndClearsCanRevert()
    {
        var runner = new RecordingTurnRunner([TurnEvent.Assistant(MarkerResponse)]);
        var apply = new RecordingApply();
        AiPanelViewModel vm = await ArmedAsync(runner, AgencyMode.AutoApply);
        HoardPadSettings before = HoardPadSettings.Default with { EditorFontSize = 13, UiFontSize = 11 };
        vm.CurrentSettingsProvider = _ => Task.FromResult(before);
        apply.Baseline = before;
        vm.ApplySettingsChangeAsync = apply.Handler;

        vm.PromptInput = "make the font bigger";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.CanRevertSettingsChange);
        Assert.Single(apply.Applied);

        Assert.True(vm.RevertLastSettingsChangeCommand.CanExecute(null));
        await vm.RevertLastSettingsChangeCommand.ExecuteAsync(null);

        Assert.Equal(2, apply.Applied.Count);
        // QA4-17: the revert is FIELD-SCOPED (only EditorFontSize, the one field the marker actually
        // changed) — UiFontSize=11 survives not because it was in some captured snapshot, but because the
        // revert patch never touches it (proves the concurrency-safety fix: an untouched field is
        // completely immune to this seam, exactly like a real concurrent writer's edit would be).
        Assert.Equal(13, apply.Applied[1].EditorFontSize);
        Assert.Equal(11, apply.Applied[1].UiFontSize);
        Assert.False(vm.CanRevertSettingsChange);
        Assert.Equal(string.Empty, vm.RevertSettingsChangeLabel);
        Assert.Contains(vm.Transcript, t => t.Text == "Reverted the last AI-applied settings change.");
    }
}
