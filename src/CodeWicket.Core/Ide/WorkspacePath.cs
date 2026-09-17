using System;
using System.IO;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// How a path is SPELLED for a human or for an agent, once <see cref="AgentPath"/> has settled which
    /// file it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two consumers, one rule. The transcript shows a file's path on an edit card, a tool row and a test
    /// failure; the composer transcribes a dropped file's path into the prompt. Those two disagreeing is
    /// the same class of bug <see cref="AgentPath"/> exists to stop — the user reads one spelling and the
    /// agent is handed another, each measured from a different origin.
    /// </para>
    /// <para>
    /// It lives in Core, beside <see cref="AgentPath"/>, because <see cref="ForPrompt"/> has to COMPOSE
    /// with the canonicalizer (see its remarks) and because Core is the only place with no WPF, so the
    /// rule is reachable from the test project and from a net472 host alike.
    /// </para>
    /// <para>
    /// <see cref="IsUnderRoot"/> is extracted rather than repeated. The same prefix test had been written
    /// out three times — here, in <c>TestStackFrames</c> and in <c>FileReferenceResolver</c> — and each
    /// copy got to decide for itself whether to root a relative input first. That is not a tidiness
    /// concern: the copies disagreed, and the one that called <c>GetFullPath</c> on a relative path
    /// resolved it against the PROCESS working directory, which in the VS host is devenv's install
    /// folder.
    /// </para>
    /// </remarks>
    public static class WorkspacePath
    {
        /// <summary>
        /// Case-insensitive "is <paramref name="path"/> under <paramref name="root"/>" (net472-safe, no
        /// <c>Path.GetRelativePath</c>).
        /// </summary>
        /// <remarks>
        /// The root is compared WITH a trailing separator, which is what stops a sibling <c>ws2</c>
        /// matching root <c>ws</c> — a bare prefix test reports the sibling as inside and every path
        /// under it then displays with its first segment eaten. A relative input is rooted against
        /// <paramref name="root"/> first (see <see cref="Absolute"/>); anything unresolvable is reported
        /// false, which is the conservative answer since the caller then keeps the absolute form.
        /// </remarks>
        public static bool IsUnderRoot(string? path, string? root)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path))
                return false;
            try
            {
                return Prefixed(Path.GetFullPath(Absolute(path!, root)), root!);
            }
            catch (Exception e) when (IsPathFailure(e))
            {
                return false;
            }
        }

        /// <summary>
        /// Path relative to <paramref name="workspaceRoot"/> (forward slashes) when the file is under it,
        /// else the path unchanged. Anything outside the root (or unresolvable) stays as it came in.
        /// <para>
        /// An input that is ALREADY relative is rooted against <paramref name="workspaceRoot"/> first.
        /// Handing it straight to <c>GetFullPath</c> resolves it against the PROCESS working directory —
        /// devenv's install dir — so it never matched the root and was displayed verbatim: two cards for
        /// one edit then showed a different number of leading segments, each measured from a different
        /// origin. Silent, because the fallback is the unchanged string, which looks like a path.
        /// </para>
        /// </summary>
        public static string Relative(string path, string? workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot) || string.IsNullOrEmpty(path))
                return path;
            try
            {
                var full = Path.GetFullPath(Absolute(path, workspaceRoot));
                return Prefixed(full, workspaceRoot!)
                    ? full.Substring(RootPrefix(workspaceRoot!).Length).Replace('\\', '/')
                    : path;
            }
            catch (Exception e) when (IsPathFailure(e))
            {
                return path;
            }
        }

        /// <summary>
        /// The inverse of <see cref="Relative"/>: an agent-written path rooted against the workspace.
        /// An already-rooted path (or one that can't be combined) is returned unchanged.
        /// </summary>
        public static string Absolute(string path, string? workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot) || string.IsNullOrEmpty(path))
                return path;
            try
            {
                return Path.IsPathRooted(path)
                    ? path
                    : Path.GetFullPath(Path.Combine(workspaceRoot!, path));
            }
            catch (Exception e) when (IsPathFailure(e))
            {
                return path;
            }
        }

        /// <summary>
        /// The spelling a path takes when it is handed to the AGENT rather than shown to the user:
        /// workspace-relative under <paramref name="root"/>, absolute otherwise, and double-quoted when it
        /// contains whitespace so it survives as one token.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not simply <c>Quote(Relative(path, root))</c>.</b> It canonicalizes first, so a
        /// <c>file://</c> URI and a drive letter in either case reach the agent as the one spelling the
        /// transcript is already showing.
        /// </para>
        /// <para>
        /// <b>The contract, and it binds the CALLER:</b> hand this an ALREADY-ROOTED path, or a
        /// <paramref name="root"/> to measure a relative one from. Given neither, it returns the input
        /// unchanged — because the only way to root a relative path here is
        /// <see cref="Path.GetFullPath(string)"/>, which resolves against the PROCESS working directory,
        /// and in the VS host that is devenv's install folder. Emitting such a path is issue #54's
        /// failure mode: the agent re-measures it from its own cwd, which is not an error but a DIFFERENT
        /// REAL FILE — a write creates it, so the edit reports clean while the file the user meant is
        /// untouched. Returning the caller's own string is the one answer that adds no false precision.
        /// The drop path holds up its end by refusing tokens that are not rooted
        /// (<c>FileDropPaths.Filter</c>), which is where that fact is actually knowable.
        /// </para>
        /// <para>
        /// Quoting is plain double quotes with NO escaping, because a Windows path cannot contain a
        /// <c>"</c> — do not add escaping here on the theory that it is missing.
        /// </para>
        /// </remarks>
        public static string ForPrompt(string path, string? root)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            var spelling = Relative(AgentPath.Canonical(path, root), root);
            return NeedsQuoting(spelling) ? "\"" + spelling + "\"" : spelling;
        }

        private static bool NeedsQuoting(string text)
        {
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                    return true;
            }

            return false;
        }

        // The root with exactly one trailing separator — the form both the prefix test and the substring
        // must agree on, so they are computed the same way rather than each spelling it out.
        private static string RootPrefix(string root)
            => Path.GetFullPath(root)
                   .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               + Path.DirectorySeparatorChar;

        private static bool Prefixed(string fullPath, string root)
            => fullPath.StartsWith(RootPrefix(root), StringComparison.OrdinalIgnoreCase);

        // The set Path's own members throw for a malformed path. Caught rather than propagated because
        // every member here has a truthful fallback (report "outside the root", keep the input spelling),
        // and a path the user typed or an agent invented must not take down a render or a drop.
        private static bool IsPathFailure(Exception e)
            => e is ArgumentException
               || e is NotSupportedException
               || e is PathTooLongException
               || e is System.Security.SecurityException;
    }
}
