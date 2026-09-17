using System;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// A tool result that is DATA rather than text: our own JSON payload, self-identifying by a marker,
    /// which the host renders as a card instead of as tool-row output (<c>run_tests</c>, and
    /// <c>set_breakpoint</c>/<c>list_breakpoints</c> since issue #73).
    /// <para>
    /// The marker is matched as an exact PREFIX, which is why the producer serializes
    /// <c>resultKind</c> as its first property: recognising the payload costs no parse, and ordinary
    /// tool output that merely quotes the marker text (a file-read of this repo's own source) can't
    /// false-positive into a card.
    /// </para>
    /// <para>
    /// It lives in Core because three layers have to agree on it and cannot see each other: the ACP
    /// mapper (so a display clamp never cuts a structured payload mid-string — issue #83), the shell's
    /// tool side-channel (which forwards the full-fidelity copy to the chat), and the view-model (which
    /// parses it into the card). It was three copies of the same literal with "keep in sync" comments.
    /// </para>
    /// </summary>
    public static class StructuredToolResult
    {
        /// <summary>The <c>run_tests</c> payload marker — the first thing the serialized result says.</summary>
        public const string TestRunPrefix = "{\"resultKind\":\"testRun\"";

        /// <summary>True when <paramref name="text"/> is our run_tests payload (prefix match, no parse).</summary>
        public static bool IsTestRun(string? text) =>
            text != null && text.StartsWith(TestRunPrefix, StringComparison.Ordinal);

        /// <summary>The breakpoint payload marker (issue #73) — set_breakpoint and list_breakpoints.</summary>
        public const string BreakpointsPrefix = "{\"resultKind\":\"breakpoints\"";

        /// <summary>True when <paramref name="text"/> is our breakpoint payload (prefix match, no parse).</summary>
        public static bool IsBreakpoints(string? text) =>
            text != null && text.StartsWith(BreakpointsPrefix, StringComparison.Ordinal);

        /// <summary>
        /// True for ANY payload of ours that must survive intact — what the transport-side readers ask,
        /// as opposed to the view-model, which asks which card to build.
        /// </summary>
        /// <remarks>
        /// The two questions are separated deliberately. A reader that only ever needs "is this ours"
        /// — the mapper's clamp exemption, the shell's side-channel — must not carry a list of kinds it
        /// would then have to be remembered to extend: a new payload kind that reached the clamp as
        /// ordinary text would be cut mid-string at 2000 chars and throw where the card is parsed, which
        /// is issue #83 exactly, and it would do so only on payloads large enough to notice in the field.
        /// </remarks>
        public static bool IsStructured(string? text) => IsTestRun(text) || IsBreakpoints(text);

        /// <summary>
        /// The tool-call id the shell's side-channel delivery wears, or null when the text is not one of
        /// ours. Synthetic: no such tool call exists, because this delivery is the HOST forwarding its own
        /// full-fidelity copy rather than a frame the agent sent.
        /// </summary>
        /// <remarks>
        /// Per-kind, not one shared id. The view-model keys its card dedupe on the payload's own unique
        /// field and falls back to this id, so a single shared value would make two different kinds of
        /// payload look like the same delivery — and the one that arrived second would be silently
        /// dropped. The strings live here beside the markers they belong to, so the producer and any
        /// reader cannot disagree about them.
        /// </remarks>
        public static string? SyntheticToolCallId(string? text) =>
            IsTestRun(text) ? "cwkt-testrun"
            : IsBreakpoints(text) ? "cwkt-breakpoints"
            : null;
    }
}
