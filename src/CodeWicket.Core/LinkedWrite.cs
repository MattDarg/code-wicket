using CodeWicket.Core.Ide;

namespace CodeWicket.Core
{
    /// <summary>
    /// A write whose path reaches its file through a symbolic link or junction below the agent's
    /// workspace root — the one shape that makes the other two built-in rules' spelling tests say
    /// nothing at all. A hit is never auto-approved: not by a permission mode, not by an allow rule,
    /// not by a remembered "always". It is put to the user, flagged, with "Allow always" withheld.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> <see cref="ProtectedPaths"/> and <see cref="EscalationFiles"/> both judge
    /// a write on the SPELLING of its path: one asks whether the path lies under the host's own state
    /// roots, the other whether it ends in one of the backends' permission files. Both spellings are
    /// resolved by <c>Path.GetFullPath</c>, which never touches the filesystem — so a junction inside
    /// the working tree pointing at <c>%APPDATA%\code-wicket</c> gives an agent a path that passes
    /// both tests and lands on a file neither rule would have allowed. The link itself is cheap to
    /// make: <c>mklink /J</c> needs no privilege, and it is a COMMAND, so under Allow all — or on a
    /// backend that approves its own commands — nothing asks about it first.
    /// </para>
    /// <para>
    /// <b>The claim is the one that can be kept: a link below the root, not "this reaches a protected
    /// file".</b> Following the link to see where it lands is the operation
    /// <see cref="ReparsePoints"/> exists to avoid — on a link naming a UNC share that is an NTLM
    /// handshake and an SMB timeout, which is the whole point of the inline-image containment one layer down. So
    /// the question asked is the one that can be answered without opening anything: does any
    /// component below the root redirect? Where it does, the path's spelling has no authority and the
    /// user decides. It errs only towards the banner, and the cost of erring is a prompt on a
    /// repository that genuinely carries links inside itself.
    /// </para>
    /// <para>
    /// <b>Only BELOW the root, and only where a root is known.</b> A link above the root — a
    /// developer's <c>C:\src</c> junction onto another volume — carries the whole tree and moves
    /// nothing out of reach, the same distinction <see cref="ReparsePoints.AnyBelow"/> draws for the
    /// inline-image containment. A path that is not under the root lexically is not this rule's
    /// business either, and with no root known nothing is asked and nothing is read from disk.
    /// </para>
    /// <para>
    /// <b>Reads are exempt</b>, as they are for the other two rules: what escalates is the state
    /// being REWRITTEN, and a read grants nothing.
    /// </para>
    /// <para>
    /// <b>A tripwire, not a wall</b> — the standing limit of everything on this tier. It sees only
    /// the writes a backend ASKS about; one the backend performs on its own reaches no rule here.
    /// And it is a check at the moment of asking: a link created after the answer is a different
    /// file by the time the bytes land, which no host-side rule can close while the backend owns the
    /// write.
    /// </para>
    /// <para>
    /// The sentences the user reads are here rather than in the UI for
    /// <see cref="ProtectedPaths"/>' reason: the wording is the behaviour, and a banner that said
    /// "matches your always-prompt rule" over a rule the user never wrote would send them to their
    /// settings to find it.
    /// </para>
    /// </remarks>
    public static class LinkedWrite
    {
        /// <summary>
        /// Whether <paramref name="path"/> lies under <paramref name="root"/> lexically AND reaches
        /// its file through a symbolic link or junction below it. False when either is missing, when
        /// the path is outside the root, and when nothing on the way down redirects — so a host with
        /// no root, or a path it cannot judge, reads no disk at all.
        /// </summary>
        public static bool Reaches(string? root, string? path)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path))
                return false;
            if (!AgentPath.IsRooted(path!) || !WorkspacePath.IsUnderRoot(path!, root!))
                return false;

            return ReparsePoints.AnyBelow(root!, path!);
        }

        /// <summary>
        /// The banner's call-out, in place of "Matches your always-prompt rule". Says what was found
        /// and not where it goes, because finding out where it goes is the thing this refuses to do.
        /// </summary>
        public static string BannerReason => "Reaches its file through a link inside the workspace";

        /// <summary>
        /// The transcript row's account, completing "You were asked because …". It names the
        /// consequence rather than the mechanism: a reader who has an allow rule covering the folder
        /// needs to know why it did not apply.
        /// </summary>
        public static string RowReason =>
            "the path reaches its file through a link inside the workspace, so where it actually lands "
            + "is not what the path says, and no permission mode or allow rule covers that.";
    }
}
