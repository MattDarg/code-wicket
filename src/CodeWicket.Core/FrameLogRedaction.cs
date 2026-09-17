using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeWicket.Core
{
    /// <summary>
    /// What a <see cref="FrameLogSink"/> writes. Frames go to disk verbatim except for two values that
    /// must not, each for its own reason.
    ///
    /// <para><b>Neither reason is about ACP</b>, which is why this sits in Core and applies to the MCP
    /// tee too. One is about what a user attaches to a bug report; the other is about the log
    /// directory's size budget. Both hold for any frame stream we tee, and the MCP one carries our own
    /// tool results — an image a tool returned, or a token in an argument — so exempting it would have
    /// been an accident of where the code happened to live.</para>
    ///
    /// <list type="bullet">
    /// <item><b>A bearer token must not be written at all.</b> Kiro v3's
    /// <c>_kiro/auth/getAccessToken</c> host callback answers client→agent, so the user's token would
    /// otherwise sit in a diagnostic file they are likely to attach to a bug report.</item>
    /// <item><b>An image's base64 must not be written WHOLE</b> (issue #118). It is not a secret — it
    /// is a matter of size. The log directory runs to a 50 MB total budget and was measured sitting at
    /// its ceiling with 705 files, so one screenshot per prompt (a megabyte or two, twice over once the
    /// agent replays it on a later <c>session/load</c>) would evict the real diagnostics this file
    /// exists to preserve. The elision keeps the frame's SHAPE — the block, its mime type, and how
    /// much was dropped — which is everything a reader of this log actually needs; nobody debugs a
    /// protocol problem by reading a megabyte of base64.</item>
    /// </list>
    ///
    /// <para>Both are applied on whole frames only. The callers buffer to the newline boundary before
    /// calling in, because StreamJsonRpc does not guarantee frame-aligned writes and a value split
    /// across two of them would otherwise slip through unmatched.</para>
    /// </summary>
    public static class FrameLogRedaction
    {
        // Matches a JSON accessToken string value; the token itself (a JWT/opaque bearer) contains no
        // quotes, so a non-quote scan captures it whole.
        private static readonly Regex AccessTokenValue = new Regex(
            "(\"accessToken\"\\s*:\\s*\")[^\"]+(\")", RegexOptions.CultureInvariant);

        /// <summary>
        /// A <c>data</c> value that is a long run of base64 — an ACP <c>image</c> block's payload, or
        /// an MCP image content block's.
        /// Deliberately keyed on the VALUE's shape rather than on the key name alone: "data" is a
        /// generic enough name to appear on frames that have nothing to do with images, and a short
        /// value is worth keeping whatever its key. Nothing legitimate in this protocol puts a
        /// kilobyte of unbroken base64 anywhere else.
        /// </summary>
        private static readonly Regex ImageDataValue = new Regex(
            "(\"data\"\\s*:\\s*\")([A-Za-z0-9+/]{" + MinElidedChars + ",}={0,2})(\")",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// How long a base64 run has to be before it is elided. Comfortably above anything the protocol
        /// carries in a <c>data</c> field for other purposes, and below the smallest image worth
        /// attaching (1 KB of base64 is a ~750-byte picture).
        /// </summary>
        private const int MinElidedChars = 1024;

        /// <summary>
        /// Applies both substitutions, returning the SAME instance when nothing matched so the caller
        /// can tell "unchanged" from "rewritten" without comparing strings.
        /// </summary>
        public static string Scrub(string text)
        {
            var result = text;

            if (result.IndexOf("accessToken", StringComparison.Ordinal) >= 0)
                result = AccessTokenValue.Replace(result, "$1<REDACTED>$2");

            // The cheap gate first: no "data" key means no image block, and that is almost every frame.
            if (result.IndexOf("\"data\"", StringComparison.Ordinal) >= 0)
                result = ImageDataValue.Replace(result, Elide);

            return result;
        }

        /// <summary>
        /// Replaces the payload with a note SAYING HOW MUCH IT DROPPED. A bare marker would leave a
        /// reader unable to tell a thumbnail from a full-screen capture, which is exactly the question
        /// this log gets read for when a prompt is unexpectedly large.
        /// </summary>
        private static string Elide(Match match) =>
            match.Groups[1].Value
            + "<"
            + match.Groups[2].Value.Length.ToString(CultureInfo.InvariantCulture)
            + " base64 chars elided>"
            + match.Groups[3].Value;
    }
}
