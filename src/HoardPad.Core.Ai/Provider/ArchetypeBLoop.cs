using System.Collections.Immutable;
using System.Text.Json;
using HoardPad.Core.Records;

namespace HoardPad.Core.Ai.Provider;

/// <summary>
/// The file-op tool HoardPad OFFERS the model in Archetype B (architecture §4.1, PRD FR-23/FR-25). Unlike
/// Archetype A (where the agent owns native Read/Write/Edit and HoardPad gates them), here HoardPad DEFINES the
/// tools, so the offered vocabulary is HoardPad's own — the full op set the shared file-ops service models
/// (§4.5): the model can request the whole <see cref="FileOp"/> vocabulary plus <c>read</c> (the on-demand
/// context tool, FR-25(b)).
/// </summary>
public enum BackendTool
{
    /// <summary>The on-demand <c>read</c> context tool (FR-25(b)) — reads a file's bytes into context.</summary>
    Read,

    /// <summary>Create a new file (FR-26 op vocabulary; FR-11 create cap on apply).</summary>
    Create,

    /// <summary>Copy a file to a destination (FR-26 op vocabulary).</summary>
    Copy,

    /// <summary>Move/rename a file (a rename canonicalizes to move, MA11).</summary>
    Move,

    /// <summary>Edit a file's contents in place (FR-26 op vocabulary).</summary>
    Edit,

    /// <summary>Delete a file (Archetype-B-only reachable op, R3-BL1).</summary>
    Delete,
}

/// <summary>
/// One parsed tool-call the model requested in an Archetype-B loop step: the tool and its raw JSON arguments. The
/// loop hands the (sandbox-checked) op to the shared file-ops service (M4.3) for execution — HoardPad is the
/// writer, so the op is per-op-atomic and TOCTOU-free (architecture §4.1, C-8). This type is the parse boundary;
/// it does not execute.
/// </summary>
/// <param name="Tool">The requested tool.</param>
/// <param name="RawArgumentsJson">The tool-call arguments as raw JSON (the service resolves paths/bytes from it).</param>
public sealed record BackendToolCall(BackendTool Tool, string RawArgumentsJson);

/// <summary>
/// The next step of an Archetype-B driven-completion loop: either the model requested tool-calls (HoardPad
/// executes each via the shared file-ops service and loops), or it returned a final assistant message (the loop
/// ends).
/// </summary>
/// <param name="ToolCalls">The parsed tool-calls to execute this step (empty when the model is done).</param>
/// <param name="IsComplete"><c>true</c> when the model returned no tool-calls — the loop terminates.</param>
public sealed record BackendLoopStep(ImmutableArray<BackendToolCall> ToolCalls, bool IsComplete)
{
    /// <summary>A completed step — the model returned no tool-calls, so the loop ends.</summary>
    public static BackendLoopStep Complete { get; } =
        new(ImmutableArray<BackendToolCall>.Empty, IsComplete: true);
}

/// <summary>
/// The Archetype-B (driven-completion) loop — platform-free (architecture §4.1, PRD FR-23). HoardPad OWNS the
/// loop: it defines the file-op tools (<see cref="BackendTool"/>), parses the model's tool-call requests
/// (<see cref="ParseStep"/>), and executes each op itself through the shared file-ops service (M4.3) — the
/// structurally stronger, TOCTOU-free path (C-8). This type owns the loop's tool-vocabulary + parse surface; the
/// per-op execution (sandbox check + undo journal + activity log) is the shared service's job.
/// </summary>
/// <remarks>
/// Pure + platform-free: tool-name mapping + JSON parse over a string, no I/O, no clock, no network. The concrete
/// model transport (Anthropic API / Ollama / LiteLLM HTTP) is a host/app concern; this is the loop's domain
/// decision surface. The offered roster mirrors the FR-26 op vocabulary plus <c>read</c> — the full set, because
/// Archetype B reaches the full vocabulary (R3-BL1), unlike Archetype A.
/// </remarks>
public static class ArchetypeBLoop
{
    /// <summary>Map a model-emitted tool name to a <see cref="BackendTool"/>; <c>null</c> when not an offered tool.</summary>
    public static BackendTool? MapTool(string toolName) => toolName switch
    {
        "read" or "Read" => BackendTool.Read,
        "create" or "Create" => BackendTool.Create,
        "copy" or "Copy" => BackendTool.Copy,
        "move" or "Move" or "rename" or "Rename" => BackendTool.Move, // rename canonicalizes to move (MA11)
        "edit" or "Edit" => BackendTool.Edit,
        "delete" or "Delete" => BackendTool.Delete,
        _ => null, // an unrecognized tool is not offered — the loop ignores it (it cannot reach the service).
    };

    /// <summary>
    /// Parse one loop step from the model's tool-call requests. Each request is a (toolName, argumentsJson) pair
    /// the host extracted from the model response. Unrecognized tools are dropped (they are not offered). A step
    /// with no recognized tool-calls is <see cref="BackendLoopStep.Complete"/> — the loop terminates.
    /// </summary>
    /// <param name="requestedToolCalls">The raw (toolName, argumentsJson) pairs from the model response.</param>
    public static BackendLoopStep ParseStep(IReadOnlyList<(string ToolName, string ArgumentsJson)> requestedToolCalls)
    {
        if (requestedToolCalls.Count == 0)
            return BackendLoopStep.Complete;

        var builder = ImmutableArray.CreateBuilder<BackendToolCall>();
        foreach (var (toolName, argsJson) in requestedToolCalls)
        {
            var tool = MapTool(toolName);
            if (tool is null)
                continue;
            if (!IsWellFormedJson(argsJson))
                continue; // a malformed tool-call argument is dropped — the service never sees an unparseable op.

            builder.Add(new BackendToolCall(tool.Value, argsJson));
        }

        var calls = builder.ToImmutable();
        return calls.Length == 0
            ? BackendLoopStep.Complete
            : new BackendLoopStep(calls, IsComplete: false);
    }

    private static bool IsWellFormedJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
