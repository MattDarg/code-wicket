namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// What we tell the agent when the editor refused a write (issue #189).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A hedged reason is worse than no reason, because the reader replaces it.</b> The first
    /// version of this said the edit was rejected and that it "usually means the file is read-only or
    /// needs checking out". Measured on the wire 2026-09-03: the message reached Kiro intact, and the
    /// agent then told the user *"likely because `temp.txt` is open in the editor — could you close or
    /// save it?"* — a cause we never suggested, advice that does not work, and it offered to write the
    /// file through a shell command instead. Given a sentence that declines to commit, a model supplies
    /// its own hypothesis and states it with more confidence than ours had.
    /// </para>
    /// <para>
    /// So the message says what was CHECKED. The read-only attribute is one cheap, definite file-system
    /// fact, and it separates the common case from everything else; where it does not hold we describe
    /// the refusal without naming a cause at all, rather than offering a guess for the agent to firm up.
    /// </para>
    /// <para>
    /// <b>And it must close the workaround it invited.</b> Routing around the refusal is exactly the harm
    /// — the user was asked and said no — so the read-only case says so outright, with the reason, since
    /// an agent told only "this failed" reaches for the next tool that might not.
    /// </para>
    /// <para>
    /// Pure and in Core so it is testable: the applier that calls it is net472 and VS-bound, and the
    /// wording is the whole of the behaviour here.
    /// </para>
    /// </remarks>
    public static class FileWriteRefusal
    {
        /// <param name="path">The resolved path the write targeted.</param>
        /// <param name="readOnly">
        /// Whether the file carries the read-only attribute. Nullable because the check itself can fail
        /// (the file may have gone), and "we could not tell" must not be reported as "it is writable".
        /// </param>
        public static string Describe(string path, bool? readOnly) => readOnly == true
            ? $"{path} is marked read-only and Visual Studio was not permitted to make it writable, so "
              + "nothing was written and the file is unchanged. Ask the user to clear the read-only "
              + "attribute (or to allow the prompt) and try again. Do not write the file another way — "
              + "a shell command would bypass a refusal the user made deliberately."
            : $"Visual Studio cancelled the edit to {path}, so nothing was written and the file is "
              + "unchanged. The editor refused the change without saying why; a pending source-control "
              + "checkout and the file's permissions are both worth the user checking.";
    }
}
