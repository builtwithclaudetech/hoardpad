// IOpenFolderPrompt.cs — the MA5 stale-root re-prompt seam (PRD §9.2 MA5).
//
// On restore, if the prior open folder (the AI sandbox root) moved/renamed/changed/was deleted, the AI
// write agency must NOT silently arm on a stale or guessed root (MA5). HoardPad re-prompts the user to
// confirm or re-pick the root before write agency arms; until he does, the panel runs read/chat only.
// This seam is the App's "ask the user for a root" affordance; the production impl shows a folder picker
// (or a confirm), tests inject a fake that returns a scripted answer.

namespace HoardPad.App.Session;

/// <summary>
/// The MA5 re-prompt: ask the user to confirm or re-pick the AI sandbox root when the prior open folder
/// is stale (moved/renamed/deleted). Returns the confirmed root path, or null if the user declined (the
/// panel then stays read/chat only — no silent stale sandbox root).
/// </summary>
public interface IOpenFolderPrompt
{
    /// <summary>
    /// Re-prompt for the AI sandbox root (MA5). <paramref name="priorRoot"/> is the stale prior path (for
    /// context in the prompt), or null when there was none. Returns the user-confirmed root, or null if the
    /// user declined to pick one (write agency stays disabled).
    /// </summary>
    Task<string?> RePromptForRootAsync(string? priorRoot, CancellationToken ct);
}
