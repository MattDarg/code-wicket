using System.Threading;
using System.Threading.Tasks;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// Client-owned filesystem operations. When the agent wants to read or modify a file, it goes
    /// through here rather than touching disk directly. ACP <c>fs/read_text_file</c> and
    /// <c>fs/write_text_file</c> map onto these methods.
    /// </summary>
    /// <remarks>
    /// The VS implementation routes writes through the running document table so changes appear as
    /// diff previews, land in the undo stack, and flow through source control. Headless hosts may
    /// simply read/write disk.
    /// </remarks>
    public interface IEditApplier
    {
        /// <summary>
        /// Reads a text file. Optional <paramref name="line"/> (1-based) and <paramref name="limit"/>
        /// restrict the returned range, mirroring ACP's read semantics.
        ///
        /// <para><b>Throws <see cref="System.IO.FileNotFoundException"/> when the file does not exist —
        /// it must NOT return an empty string.</b> Every applier used to, and an absent file and an
        /// empty one are different facts the agent cannot tell apart: it asked for a file and was told
        /// the file is blank. The damaging shape is not the obvious one — a path that resolves slightly
        /// wrong reads as "", the agent concludes the file is empty, writes a complete "fixed" version
        /// to that wrong path (a write CREATES, so it succeeds), and reports the edit done while the
        /// real file is untouched and nothing anywhere says so.</para>
        ///
        /// <para>A <paramref name="line"/>/<paramref name="limit"/> range past the end of an existing
        /// file is NOT an error and still returns empty: the file was found, that range simply holds
        /// nothing. The distinction is "couldn't look" versus "looked and there is nothing there".</para>
        /// </summary>
        Task<string> ReadTextFileAsync(string path, int? line = null, int? limit = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Writes the full new contents of a file, applied through the IDE's edit pipeline. Returns what
        /// the write actually did — resolved path plus the exact before/after content — so a caller can
        /// surface an authoritative diff instead of reconstructing one from the agent's report. See
        /// <see cref="FileWriteResult"/> for why that distinction matters. Null when the host can't
        /// report (it is never required to; the write still happened).
        /// </summary>
        Task<FileWriteResult?> WriteTextFileAsync(string path, string content, CancellationToken cancellationToken = default);

        /// <summary>
        /// Surfaces a read-only diff of an edit (<paramref name="oldText"/> vs <paramref name="newText"/>)
        /// in the IDE's native diff viewer, without modifying the file. Used to preview edits that the
        /// agent applied itself (e.g. Kiro's built-in file tools) so the change is still reviewable in
        /// the IDE. Headless hosts may no-op.
        /// </summary>
        /// <param name="path">The file the edit applies to.</param>
        /// <param name="oldText">The "before" text (the changed hunk, or the whole file for a write).</param>
        /// <param name="newText">The "after" text (the changed hunk, or the whole file for a write).</param>
        /// <param name="reportedLine">
        /// The 1-based line the backend reported the edit at, when known. A surgical edit carries only
        /// its changed hunk, so the IDE may widen it to a full-file before/after by locating the hunk
        /// in the current file; this line disambiguates repeated matches and guards against a match far
        /// from where the edit was reported. Null when unreported.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task ShowDiffPreviewAsync(string path, string oldText, string newText, int? reportedLine = null, CancellationToken cancellationToken = default);
    }
}
