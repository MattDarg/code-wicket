using System.Text;
using System.Text.RegularExpressions;

namespace CodeWicket.Providers.Acp
{
    /// <summary>
    /// Which lines of an agent CLI's stderr are worth telling the user about, and how to tell two
    /// reports of the same problem apart from two different problems (issue #208).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The capture already existed; only the trigger was missing.</b> Every backend's stderr is
    /// drained into <see cref="AgentDiagnostics"/> and has been since #82 - but nothing read it until
    /// a <c>SessionError</c> attached it to the error panel. In the incident that produced this issue
    /// the session never failed: kiro-cli was refused eight times with HTTP 429, retried internally,
    /// and the turn stayed alive - so the buffer held the whole explanation and nobody ever looked in
    /// it, for twenty minutes, while the user watched a stalled pane.
    /// </para>
    /// <para>
    /// <b>The filter is not optional.</b> Measured across every captured <c>engine.log</c>: 585
    /// <c>[INFO]</c> lines against 8 <c>[ERROR]</c>. Relaying the stream unfiltered would be 98.6%
    /// noise and would bury the thing it exists to surface.
    /// </para>
    /// <para>
    /// <b>And it is Kiro-shaped, which is worth stating rather than implying.</b> This keys on the
    /// level marker kiro-cli writes; claude-agent-acp does not level-tag its stderr, and we hold no
    /// captured sample of it writing any. So this carrier degrades to silence there - the hook is
    /// universal, the predicate is not, and when another CLI's shape is known this is a predicate
    /// change rather than new plumbing.
    /// </para>
    /// <para>
    /// <b>The marker answers two questions and is only reliable on one of them.</b> "Who wrote this
    /// line" and "how bad did they think it was" are different questions, and a CLI that forwards a
    /// child process's stderr stamps its own level on both its own words and the child's. Measured
    /// 2026-09-07 across every captured <c>engine.log</c> - 732 relayed lines, 638 <c>[INFO]</c>, 9
    /// <c>[ERROR]</c>, 85 untagged - the nine errors are the eight HTTP 429 refusals above plus
    /// <c>[ERROR] (node:66940) ExperimentalWarning: SQLite is an experimental feature…</c>. The
    /// second is node telling the user about node, through <c>process.emitWarning</c>, on the
    /// bundled agent's first load of <c>node:sqlite</c>; it is informational BY CONSTRUCTION in the
    /// runtime that emitted it, and kiro-cli called it an error only because it arrived on stderr.
    /// So the level marker on a relayed line describes the pipe, not the content - see
    /// <see cref="IsRelayedRuntimeWarning"/>.
    /// </para>
    /// </remarks>
    internal static class AgentStderrNotice
    {
        /// <summary>The level marker kiro-cli writes at the head of a line it considers an error.</summary>
        private const string ErrorMarker = "[ERROR]";

        /// <summary>How much of one line is shown. Long enough for a JSON body, short enough for a notice.</summary>
        internal const int MaxLength = 600;

        /// <summary>
        /// A diagnostic the language runtime wrote about itself, which the CLI merely forwarded:
        /// node's <c>process.emitWarning</c> shape, with or without its bracketed code
        /// (<c>(node:123) ExperimentalWarning: …</c>, <c>(node:123) [DEP0040] DeprecationWarning: …</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is a provenance test, not a keyword blocklist.</b> It does not decide that
        /// "ExperimentalWarning" is unimportant; it observes that the line was written by node rather
        /// than by the CLI whose level marker it is carrying, and that node's own word for it is
        /// <i>warning</i>. Nothing here interprets the message - the same refusal to build a taxonomy
        /// over a vendor's payload that keeps <c>retryAfterMilliseconds</c> from becoming a deadline.
        /// </para>
        /// <para>
        /// <b>Not CLI-specific, deliberately.</b> node is the runtime under both shipping backends -
        /// kiro-cli launches a bundled node agent, and claude-agent-acp IS a node program - so this
        /// shape is shared while the level markers are per-CLI. That is the seam: if a per-agent
        /// severity vocabulary is ever needed it belongs in <see cref="AcpAgentConfig"/> as data,
        /// and this stays here.
        /// </para>
        /// <para>
        /// <b>What suppression costs, which is nothing.</b> The line is still in <c>engine.log</c>
        /// unconditionally and still in <see cref="AgentDiagnostics"/>, so if the runtime ever dies
        /// for real the <c>SessionError</c> panel shows the whole buffer including every line this
        /// declined (#82). This carrier exists for the case where nothing fails, which is the only
        /// case it has to be right about.
        /// </para>
        /// </remarks>
        internal static bool IsRelayedRuntimeWarning(string line) =>
            NodeWarning.IsMatch(line);

        /// <summary>
        /// node's warning header. Linear - no nested quantifier to backtrack on, which matters here
        /// because this runs on the process-pool callback draining the agent's stderr (#177).
        /// </summary>
        private static readonly Regex NodeWarning = new Regex(
            @"\(node:\d+\)\s+(\[[A-Z0-9]+\]\s+)?\w*Warning:",
            RegexOptions.CultureInvariant);

        /// <summary>Whether this stderr line is worth surfacing. See the remarks on the filter.</summary>
        /// <remarks>
        /// <b>An exclusion, never an allowlist, and the direction is the decision.</b> The tempting
        /// shape is to require the CLI's own error shape (<c>[ERROR] [KRS] …</c>) and drop the rest -
        /// but that makes an unrecognised error silent, and a silent stall with the answer already on
        /// disk is the exact incident this class was written for. So an unknown shape still surfaces,
        /// and only a positively identified foreign diagnostic is subtracted. It fails towards saying
        /// too much, which is the survivable direction.
        /// </remarks>
        internal static bool IsError(string? line) =>
            line is not null
            && line.IndexOf(ErrorMarker, System.StringComparison.Ordinal) >= 0
            && !IsRelayedRuntimeWarning(line);

        /// <summary>
        /// A key that treats two reports of the same problem as one, so a backend refusing the same
        /// request repeatedly says so once.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Digits and hex runs are removed, and that is the whole rule.</b> The eight captured
        /// refusals were the same refusal: identical text apart from a per-request id and a retry
        /// window. Keying on the raw line would have reported all eight; keying on something parsed
        /// out of the body would mean interpreting a vendor payload we have one day's evidence for.
        /// Blanking the parts that vary is neither, and it fails in the safe direction - two genuinely
        /// different errors that differ ONLY in their numbers collapse into one notice, which
        /// under-reports rather than floods.
        /// </para>
        /// <para>
        /// Deliberately NOT the message shown. The user sees the line as the backend wrote it,
        /// numbers and all - this exists to decide whether to show it, not what to show.
        /// </para>
        /// </remarks>
        internal static string DedupeKey(string line)
        {
            var sb = new StringBuilder(line.Length);
            var blanked = false;
            foreach (var c in line)
            {
                var volatileChar = char.IsDigit(c)
                    || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F');
                if (volatileChar)
                {
                    // One placeholder per run, so "429" and "3600000" collapse the same way.
                    if (!blanked)
                        sb.Append('#');
                    blanked = true;
                    continue;
                }

                blanked = false;
                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>The notice text for a line: the backend's own words, trimmed and bounded.</summary>
        /// <remarks>
        /// Truncation ANNOUNCES itself and names where the rest is, per the house rule - a silently
        /// cut line reads as the whole story, and this is evidence someone may be pasting into a bug
        /// report.
        /// </remarks>
        internal static string Format(string line, string logFileName)
        {
            var trimmed = line.Trim();
            return trimmed.Length <= MaxLength
                ? trimmed
                : trimmed.Substring(0, MaxLength) + "… (truncated — the whole line is in " + logFileName + ")";
        }
    }
}
