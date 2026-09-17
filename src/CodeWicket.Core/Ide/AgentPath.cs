using System;
using System.IO;
using System.Security;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// One definition of "the same file" for every path an agent reports or asks us to touch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An agent names one file in several spellings, and both the transcript and the client filesystem
    /// key on the string: the edit dedup key is <c>toolCallId|path</c>, the write capture and the
    /// read-content cache are dictionaries. So a spelling that escapes canonicalization does not
    /// degrade — it SPLITS, silently. Three have bitten:
    /// </para>
    /// <list type="bullet">
    /// <item>drive-letter CASE — a hunk's <c>rawInput</c> path (uppercase) against the finalized
    /// <c>file:///c%3A/…</c> URI, which decodes lowercase;</item>
    /// <item>relative vs ABSOLUTE — Kiro v3 puts whatever the model typed in <c>rawInput.path</c>, so a
    /// workspace-relative POSIX path meets the absolute URI of the same file. The model's spelling is
    /// its own choice and can change mid-conversation;</item>
    /// <item>SEPARATOR style — <c>Path.Combine</c> leaves the agent's separators alone, so rooting
    /// <c>proj/File.cs</c> yields the mixed <c>C:\ws\proj/File.cs</c>, matching neither.</item>
    /// </list>
    /// <para>
    /// It lives in Core, and is shared by the ACP mapper and the VS edit applier, because those two
    /// deciding differently is the same bug wearing a different hat: the mapper's answer is what the
    /// transcript shows and what the write capture is looked up by, the applier's is where the bytes
    /// actually land. It is also the only way the rule is testable at all — the applier is net472 + VS
    /// SDK and no test project can reference it.
    /// </para>
    /// </remarks>
    public static class AgentPath
    {
        /// <summary>
        /// The canonical local path for <paramref name="path"/>: a <c>file://</c> URI is decoded, a
        /// relative path is rooted against <paramref name="root"/>, and separators and <c>.</c>/<c>..</c>
        /// collapse to one spelling with an uppercase drive letter.
        /// </summary>
        /// <param name="path">The path as the agent spelled it: local, relative, or a <c>file://</c> URI.</param>
        /// <param name="root">
        /// The directory a relative path is measured from — for an agent's path that is the ACP session's
        /// cwd, which is NOT necessarily the solution root (issue #54). Null or empty leaves a relative
        /// path relative: rooting it against anything else names a different file, and
        /// <see cref="Path.GetFullPath(string)"/> would silently resolve it against the PROCESS working
        /// directory, which in the VS host is devenv's install folder.
        /// </param>
        public static string Canonical(string path, string? root = null)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            var local = DecodeFileUri(path);

            if (!string.IsNullOrEmpty(root) && !IsRooted(local))
            {
                try { local = Path.Combine(root!, local); }
                catch (ArgumentException) { /* invalid chars — keep what we have */ }
            }

            return UppercaseDrive(Collapse(local));
        }

        /// <summary>
        /// Which root an agent's relative path is measured from, in precedence order: the directory the
        /// AGENT is running in, then the solution root, then <paramref name="fallback"/>.
        /// </summary>
        /// <remarks>
        /// The agent's cwd wins because it is the origin the agent actually used —
        /// <c>WorkspaceRootLocator</c> walks up for the backend's workspace marker, so a <c>.kiro</c>
        /// above the solution folder puts the two a level or more apart (issue #54). Getting this wrong
        /// does not fail: it resolves to a DIFFERENT real file, a write creates it, and the edit is
        /// reported clean while the file the agent meant is untouched. The solution root remains the
        /// fallback because it is right whenever the two coincide, which is most installs — which is also
        /// why the bug can sit unnoticed. Lives in Core, with the canonicalizer, because the applier that
        /// consumes it is net472 + VS SDK: a rule left in there is a rule no test can reach.
        /// </remarks>
        public static string? PreferredRoot(string? agentRoot, string? solutionRoot, string? fallback = null)
        {
            if (!string.IsNullOrEmpty(agentRoot))
                return agentRoot;
            if (!string.IsNullOrEmpty(solutionRoot))
                return solutionRoot;
            return fallback;
        }

        /// <summary>
        /// True when <paramref name="path"/> is usable as an absolute location. Anything unparseable is
        /// reported relative, which is the conservative answer — it just doesn't get rooted.
        /// </summary>
        public static bool IsRooted(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            try { return Path.IsPathRooted(path); }
            catch (ArgumentException) { return false; }
        }

        // v3 sends percent-encoded file:// URIs ("file:///c%3A/ws/…"); a plain path passes through.
        private static string DecodeFileUri(string path)
        {
            if (!path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return path;
            try
            {
                var local = new Uri(path).LocalPath;
                // A percent-encoded drive colon defeats Uri's Windows-path detection, leaving "/c:/ws/…"
                // — strip the leading slash and use Windows separators.
                if (local.Length >= 3 && local[0] == '/' && char.IsLetter(local[1]) && local[2] == ':')
                    local = local.Substring(1).Replace('/', '\\');
                return local;
            }
            catch (UriFormatException) { return path; }
        }

        // Collapses separators and "."/".." to one spelling. ONLY for a rooted path — see the `root`
        // parameter's note on GetFullPath. An unrootable relative path still gets its separators
        // unified, so two spellings of one relative path meet even with no root to hand.
        private static string Collapse(string path)
        {
            if (!IsRooted(path))
                return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            try { return Path.GetFullPath(path); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
            {
                return path;
            }
        }

        private static string UppercaseDrive(string path)
            => path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':'
                ? char.ToUpperInvariant(path[0]) + path.Substring(1)
                : path;
    }
}
