namespace CodeWicket.Core
{
    /// <summary>
    /// Whether a conversation may be resumed into the working directory a session is about to run in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It exists because the backend answers a bad resume with SUCCESS.</b> Kiro scopes its sessions
    /// by working directory. Asked to load a real session id from a directory that session does not
    /// belong to, it does not fail - it returns a brand new EMPTY session wearing the requested id.
    /// Measured on a five-week-old conversation: title "New Session", <c>createdAt</c> the moment of
    /// the load, <c>lastModifiedAt</c> 14 ms later, <c>workspacePaths</c> naming the NEW root.
    /// </para>
    /// <para>
    /// Because the load succeeds, nothing populates a failure reason and nothing is reported: the user
    /// reads their whole transcript beside an agent that has never heard of it. Every other signal is
    /// healthy - the conversation is intact in our store, the id is correct, and the backend still
    /// holds the original. Only comparing the directory the conversation BELONGS to against the one
    /// the session will RUN in separates the two.
    /// </para>
    /// <para>
    /// A pure rule rather than an <c>if</c> inside the engine, so it can be driven by a test without a
    /// backend - which matters here more than usual, because the backend is the component whose answer
    /// cannot be trusted.
    /// </para>
    /// </remarks>
    public static class ResumeRootGuard
    {
        /// <summary>The reason a pinned root carries (issue #185), worded for the working-directory
        /// notice: it has to say the root was CHOSEN for this conversation, or the transcript silently
        /// contradicts the project settings file that would have put the agent elsewhere.</summary>
        public const string PinnedReason = "chosen for this conversation, whose history lives there";

        /// <summary>
        /// Why <paramref name="conversationRoot"/> cannot be resumed into
        /// <paramref name="sessionRoot"/>, or null when it can.
        /// </summary>
        /// <param name="conversationRoot">Where the conversation was last run. <b>Null or empty means
        /// UNKNOWN, and unknown never refuses</b> - a conversation recorded before this was persisted
        /// has no answer to give, and refusing every one of those on the strength of a field they never
        /// carried would be a worse bug than the one this prevents.</param>
        /// <param name="sessionRoot">Where the session being started will run.</param>
        /// <param name="sessionRootReason">Why it runs there - an override and the file it came
        /// from, a workspace marker, or the scope setting. Named in the message INSTEAD of a
        /// remedy, because the remedy is directional and this is not.</param>
        public static string? Refuse(
            string? conversationRoot, string? sessionRoot, string? sessionRootReason = null)
        {
            if (string.IsNullOrEmpty(conversationRoot) || string.IsNullOrEmpty(sessionRoot))
                return null;

            // Spelling must not decide this: the conversation's root is read from a persisted file and
            // the session's is freshly resolved, so the two arrive by different routes.
            if (WorkspaceRootLocator.SameRoot(conversationRoot, sessionRoot))
                return null;

            // States the two directories and WHY the session runs where it does - and prescribes
            // nothing, because the remedy is directional and we do not always know which way.
            //
            // "Remove agentWorkspaceRoot" is right when the conversation predates an override that
            // is now in force, and exactly backwards when the conversation was created UNDER one
            // that has since been removed - the same mismatch, reached from the other side. Naming
            // what is in effect covers both, and the reason string already says it: an override and
            // the file it came from, a workspace marker, or the scope setting.
            var why = string.IsNullOrWhiteSpace(sessionRootReason)
                ? string.Empty
                : $" ({sessionRootReason})";

            // "Cannot be reloaded here" rather than the earlier "conversations are stored per working
            // directory": the latter was measured false for Kiro on 2026-09-12 (both engines carry
            // a conversation across roots), and a sentence the reader can disprove costs the rest
            // of the notice its credibility. What stays true on every backend is that the history
            // was written against the other directory.
            return $"it belongs to a different working directory ('{conversationRoot}'), and this "
                   + $"session runs in '{sessionRoot}'{why}. Its history was written against that "
                   + "directory, so it was not reloaded here.";
        }

        /// <summary>
        /// Why <paramref name="pinnedRoot"/> cannot be the directory a conversation is kept in
        /// (issue #185), or null when it can. A root the conversation previously ran in was validated
        /// when it was resolved, but a pin is a NEW way to set one — read back from a persisted file
        /// the user may have edited, or pointing at a directory since deleted — so it takes the same
        /// two checks the locator's own walk applies: it must exist, and it must not be a directory the
        /// locator refuses outright (the profile, a drive root).
        /// </summary>
        public static string? RefusePin(string? pinnedRoot)
        {
            if (pinnedRoot is null || pinnedRoot.Length == 0)
                return "no directory was given";
            if (!System.IO.Directory.Exists(pinnedRoot))
                return $"'{pinnedRoot}' no longer exists";
            if (WorkspaceRootLocator.IsRefusedAsWorkspace(pinnedRoot))
                return $"'{pinnedRoot}' is never a workspace (a user profile or drive root)";
            return null;
        }
    }
}
