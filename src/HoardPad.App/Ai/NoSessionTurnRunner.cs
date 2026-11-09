// NoSessionTurnRunner.cs — the no-folder-open turn runner (M6.6, design §6.8).
//
// The default IAiTurnRunner the composition root installs BEFORE a folder is open (the M6.3
// folder-open flow swaps in the session-scoped AiTurnRunner). With no folder open, AI file-write
// agency is disabled entirely (FR-26 / design §6.8) — there is no resolved sandbox root and no
// claude -p working directory to gate. This runner therefore performs NO file ops and streams a
// single read-only note explaining the no-folder state. Stop is a no-op (no process to kill).
//
// This keeps the AI panel fully functional for the no-folder case (the transcript renders, Stop is
// wired, the caps stay at zero) without building the M6.3 folder mechanism. When M6.3 opens a folder
// it replaces this with the real AiTurnRunner (session root + hook --settings + budget).

namespace HoardPad.App.Ai;

/// <summary>
/// The no-folder-open <see cref="IAiTurnRunner"/> (design §6.8). Streams a read-only note that file
/// actions need an open folder; performs no file ops and no process launch. Replaced by the
/// session-scoped <see cref="AiTurnRunner"/> when M6.3 opens a folder.
/// </summary>
public sealed class NoSessionTurnRunner : IAiTurnRunner
{
    /// <inheritdoc />
    public async IAsyncEnumerable<TurnEvent> RunAsync(
        TurnRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        // A single read-only assistant note — no folder open, so no file actions and no process.
        yield return TurnEvent.Assistant(
            "No folder is open, so AI file actions are off. Open a folder to enable them. "
            + "You can still ask questions about open documents.");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync() => Task.CompletedTask;
}
