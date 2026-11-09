// ChecklistFileService.cs — `.hoard` load + atomic save (FR-30/FR-31/FR-34, architecture §5.1).
//
// LOAD: read raw UTF-8 bytes off the UI thread, run the PURE Core HoardLoader (version gate + v1→v2
// migration + dangling/cyclic repair + over-deep clamp + sibling renormalize). Bind to Core — never
// reimplement the load pipeline.
// SAVE: serialize via the PURE Core HoardSerializer (deterministic, byte-stable when canonical), then
// write through IAtomicFileStore — CreateWhenAbsentAsync for the very first save of a new document
// (FR-34), ReplaceWhenPresentAsync for every subsequent write (the atomic temp→fsync→rename sequence,
// NFR-4). The caller (ChecklistViewModel.WriteCurrentStateAsync) tracks a _hasPersisted flag and passes
// it here — no File.Exists check-then-act race (TOCTOU). Never System.IO.File for the WRITE path.

using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.Core.Checklist;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.App.Checklist;

/// <summary>Loads and atomically saves <c>.hoard</c> documents, binding to the pure Core load/serialize.</summary>
internal sealed class ChecklistFileService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IAtomicFileStore _store;

    public ChecklistFileService(IAtomicFileStore store) => _store = store;

    /// <summary>
    /// Load a <c>.hoard</c> file: read the bytes off the UI thread, then run the pure Core loader
    /// (migration/repair/clamp/renormalize). Returns the <see cref="HoardLoadResult"/> so the caller can
    /// surface a rejection or the applied repairs (the loader is pure — it logs nothing itself).
    /// </summary>
    public static async Task<HoardLoadResult> LoadAsync(string path, CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        string json = Utf8NoBom.GetString(StripBom(bytes));
        return HoardLoader.Load(json);
    }

    /// <summary>
    /// Atomically persist a document to <paramref name="path"/> (FR-31 / NFR-4). Serializes via the pure
    /// Core serializer, then writes through the atomic store. <paramref name="fileAlreadyExists"/> must be
    /// <c>true</c> if the file has been written at least once (use the Create path otherwise) — the caller
    /// tracks this flag to avoid a File.Exists check-then-act TOCTOU race. Returns the bytes written so a
    /// caller/test can assert the round-trip. Runs off the dispatcher.
    /// </summary>
    public async Task<ReadOnlyMemory<byte>> SaveAsync(
        string path,
        HoardDocument document,
        bool fileAlreadyExists,
        CancellationToken ct)
    {
        string json = HoardSerializer.Serialize(document);
        ReadOnlyMemory<byte> content = Utf8NoBom.GetBytes(json);

        if (fileAlreadyExists)
            await _store.ReplaceWhenPresentAsync(path, content, ct).ConfigureAwait(false);
        else
            await _store.CreateWhenAbsentAsync(path, content, ct).ConfigureAwait(false);

        return content;
    }

    private static ReadOnlySpan<byte> StripBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes.AsSpan(3)
            : bytes;
}
