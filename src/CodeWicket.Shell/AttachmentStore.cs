using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CodeWicket.Core;

namespace CodeWicket.Shell
{
    /// <summary>
    /// Where a pasted image goes to live once the message carrying it has been sent (issue #118).
    ///
    /// <para><b>Written at SEND, not at paste.</b> A paste the user thinks better of and removes from
    /// the composer leaves nothing behind — the bytes sit in memory until the message is committed,
    /// which is also the moment the transcript first has somewhere to point.</para>
    ///
    /// <para><b>And a path is what the transcript stores, never the bytes.</b> The session log is an
    /// append-only JSON file that is re-read in full whenever the history picker enumerates, so a
    /// couple of megabytes of base64 per prompt would be paid for on every open, forever, to render a
    /// thumbnail.</para>
    ///
    /// <para><b>LocalAppData, never temp</b> — the standing rule for anything agent-facing (corporate
    /// EDR flags temp activity), and here the files are the user's own screen captures, which is not
    /// content to scatter.</para>
    ///
    /// <para><b>Flat, deliberately.</b> Bucketing per conversation would read better in Explorer, but
    /// <see cref="RetentionSweep"/> is non-recursive by design, so a nested layout would be swept only
    /// at its top level — i.e. never. The conversation id is in the file NAME instead, which keeps one
    /// directory the sweep can actually see.</para>
    /// </summary>
    public static class AttachmentStore
    {
        /// <summary>
        /// <b>There is deliberately NO age rule here</b>, unlike the log and diff-scratch sweeps this
        /// otherwise mirrors. Those hold scratch: a log or a before/after copy is dead once its moment
        /// passes, so a timer is exactly right. This holds CONTENT — an image the user attached is part
        /// of the conversation, and conversations are kept indefinitely, so attachments were the only
        /// piece of conversation content deleted on a clock. The original 7 days was copied from the
        /// scratch policy without that being noticed; the only real reason to reclaim these is SIZE, and
        /// the budget below already says so. A size bound also degrades gracefully where a timer simply
        /// deletes: a conversation loses its pictures when the disk fills, not on a birthday.
        /// </summary>
        internal static readonly TimeSpan? MaxAge = null;

        /// <summary>
        /// Default total size the directory is kept under, when config.json says nothing. Roomier than
        /// the diff scratch's budget because these are load-bearing for a RESTORED transcript — an
        /// evicted attachment leaves a message whose picture is gone — and a screenshot is a few
        /// hundred KB, so this is a few hundred of them.
        /// </summary>
        public const int DefaultLimitMb = 100;

        /// <summary>
        /// Floor for the configured limit. A hand-edited 0 would otherwise mean "keep nothing" and
        /// empty the directory on the next window open, which is a lot of damage for a typo — and a
        /// limit below one paste is not a policy anyone wants. Clamping up is the safe direction.
        /// </summary>
        public const int MinimumLimitMb = 10;

        /// <summary>The configured budget in bytes, clamped. Read at sweep time, so a changed setting
        /// applies at the next window open without anything having to be told about it.</summary>
        internal static long BudgetBytes =>
            Math.Max(MinimumLimitMb, ExtensionConfig.Load().AttachmentStorageLimitMb) * 1024L * 1024L;

        /// <summary>
        /// Writes one attachment and returns its full path, or null if it could not be written.
        /// Best-effort by contract: the bytes have already gone to the agent by the time this is
        /// called, so a failure here costs the transcript its thumbnail and nothing else. It must
        /// never take the send down with it.
        /// </summary>
        public static string? Save(string conversationId, string mimeType, byte[] bytes)
        {
            if (bytes is null || bytes.Length == 0)
                return null;

            var root = Root();
            if (root is null)
                return null;

            try
            {
                Directory.CreateDirectory(root);

                // Content-addressed: the same image pasted twice writes one file, and a re-sent
                // message reuses the file the first send made rather than accumulating copies.
                var path = Path.Combine(root, FileName(conversationId, mimeType, bytes));
                if (!File.Exists(path))
                    File.WriteAllBytes(path, bytes);
                return path;
            }
            catch (Exception)
            {
                // Disk full, redirected folder offline, AV holding the handle — see the summary.
                return null;
            }
        }

        /// <summary>
        /// Schedules one retention pass over the attachment directory. Off-thread and fire-and-forget
        /// for the reason every sweep here is (issue #100/#88): bulk enumerate-and-delete must never
        /// run on a UI thread, and the ceiling belongs to the machine — AV hooks, folder redirection
        /// onto a share, VDI. Nothing downstream waits on the result.
        /// <para>
        /// The catch is not redundant with <see cref="RetentionSweep.Run"/>'s own. An escaped
        /// exception here would fault an unobserved task, which since .NET 4.5 fails at finalization
        /// rather than crashing — so the failure mode would be silence, and retention would simply
        /// stop with the folder growing again.
        /// </para>
        /// </summary>
        public static void SweepInBackground()
        {
            var root = Root();
            if (root is null)
                return;

            Task.Run(() =>
            {
                try
                {
                    // Same line shape as every other scratch directory we reap, so one grep of
                    // engine.log covers them all.
                    DiagnosticLog.ReportRetention("attachments", RetentionSweep.Run(
                        root, MaxAge, BudgetBytes));
                }
                catch (Exception)
                {
                    // Retention is best-effort; a failed pass must not fault an unobserved task.
                }
            });
        }

        /// <summary>
        /// The directory's path, or null when LocalAppData is unavailable. Never creates it, and
        /// never falls back to temp: a sweep may only ever be pointed at a directory we exclusively
        /// own, and an age-and-budget delete pass over <c>%TEMP%</c> would reach the whole machine's.
        /// </summary>
        internal static string? Root()
        {
            if (_rootOverride is { Length: > 0 })
                return _rootOverride;

            return StoragePaths.LocalOrNull("attachments");
        }

        private static string? _rootOverride;

        /// <summary>
        /// Points the store at a scratch directory instead of the real one, for automated runs.
        /// <para>
        /// The same rule as <see cref="ExtensionConfig.RedirectTo"/>, for the same reason: an
        /// automated check that drives a real gesture writes what the real gesture writes, and doing
        /// that to the developer's own machine is a bug the harness inflicts on the product's user.
        /// Here it would be their own screen captures accumulating in LocalAppData from a test run —
        /// swept eventually, but never theirs in the first place.
        /// </para>
        /// <para>
        /// Interactive runs deliberately do NOT redirect; sharing the real directory is what makes the
        /// Desktop host a faithful preview.
        /// </para>
        /// </summary>
        public static void RedirectTo(string path) => _rootOverride = path;

        /// <summary>
        /// Turns whatever a transcript recorded for an attachment into a path that exists now, or null.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A saved transcript stores the FILE NAME, not a full path, so the attachment directory can
        /// move without stranding every image ever pasted. That is safe only because this directory is
        /// deliberately FLAT (see the type summary — the sweep is non-recursive, so nothing may nest),
        /// which makes a bare name unambiguous.
        /// </para>
        /// <para>
        /// Older logs recorded an ABSOLUTE path, so both shapes have to be read. The current root is
        /// tried FIRST even for those, because that is the location that keeps working: a user who
        /// moved the directory has the file under the new root, and one who did not is found by the
        /// fallback. Preferring the stored path would have made a move look like a loss.
        /// </para>
        /// <para>
        /// Null is a real answer and always was — retention sweeps this directory on its own schedule,
        /// so a restored attachment already degrades to a plain chip when the file is gone.
        /// </para>
        /// </remarks>
        public static string? ResolveStored(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return null;

            try
            {
                var name = Path.GetFileName(stored!);
                var root = Root();
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(root))
                {
                    var here = Path.Combine(root!, name);
                    if (File.Exists(here))
                        return here;
                }

                // An older log's absolute path, on a machine where nothing has moved.
                return Path.IsPathRooted(stored) && File.Exists(stored) ? stored : null;
            }
            catch (Exception)
            {
                // A stored value with invalid path characters must not take down a transcript load.
                return null;
            }
        }

        /// <summary>What a transcript should record for a file this store saved: its name alone.</summary>
        public static string StoredForm(string? savedPath) =>
            string.IsNullOrWhiteSpace(savedPath) ? string.Empty : Path.GetFileName(savedPath!);

        /// <summary>
        /// <c>&lt;conversation&gt;-&lt;content hash&gt;.&lt;ext&gt;</c>. The conversation id leads so a
        /// directory listing groups by conversation without needing subdirectories the sweep cannot
        /// see; the hash is what makes the name content-addressed. Both are sanitized because a
        /// conversation id comes from the backend and a mime type comes off the clipboard — neither is
        /// ours, and both end up in a path.
        /// </summary>
        internal static string FileName(string conversationId, string mimeType, byte[] bytes)
        {
            string hash;
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(bytes);
                var chars = new char[16];
                for (var i = 0; i < 8; i++)
                {
                    chars[i * 2] = HexDigit(digest[i] >> 4);
                    chars[i * 2 + 1] = HexDigit(digest[i] & 0xF);
                }
                hash = new string(chars);
            }

            var id = Sanitize(conversationId);
            if (id.Length > 24)
                id = id.Substring(0, 24);
            if (id.Length == 0)
                id = "session";

            return string.Format(CultureInfo.InvariantCulture, "{0}-{1}{2}", id, hash, Extension(mimeType));
        }

        private static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'a' + (value - 10));

        /// <summary>
        /// The file extension for a media type. Only the formats a prompt can actually carry, and an
        /// unknown one becomes <c>.bin</c> rather than anything derived from the string — a mime type
        /// off the clipboard is untrusted input on its way into a filename.
        /// </summary>
        internal static string Extension(string? mimeType)
        {
            if (mimeType is null)
                return ".bin";

            switch (mimeType.Trim().ToLowerInvariant())
            {
                case "image/png": return ".png";
                case "image/jpeg": return ".jpg";
                case "image/gif": return ".gif";
                case "image/webp": return ".webp";
                default: return ".bin";
            }
        }

        private static string Sanitize(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var chars = value!.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                var safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';
                if (!safe)
                    chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
