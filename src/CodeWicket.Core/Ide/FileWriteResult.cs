namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// What a client-owned write actually did: where it landed, and the file's exact content on either
    /// side of it.
    /// </summary>
    /// <remarks>
    /// This is the <em>authoritative</em> record of an edit, and it exists because the host performed the
    /// write itself. An agent that routes edits through the client filesystem (ACP
    /// <c>fs/write_text_file</c> — Kiro's v3 engine does) also reports its own diff on the tool call, but
    /// that is the agent's <em>claim</em> about the change, and for a surgical edit it carries only the
    /// changed hunk. Reconstructing a full-file before/after from a hunk means locating it in the current
    /// file, which can be ambiguous or fail outright
    /// (<c>VsEditApplier.TryBuildFullFileSides</c>). When the write came through us, none of that guessing
    /// is necessary: <see cref="OldText"/>/<see cref="NewText"/> ARE the transformation that was applied.
    /// <para>
    /// <see cref="ResolvedPath"/> is absolute — the applier resolves a relative agent path against the
    /// workspace root — so a consumer can key on it without knowing the working directory. Callers that
    /// only need the write performed can ignore the result; a host with nothing meaningful to report
    /// (a proxy that forwards the call elsewhere) may return null.
    /// </para>
    /// </remarks>
    public sealed record FileWriteResult(string ResolvedPath, string OldText, string NewText);
}
