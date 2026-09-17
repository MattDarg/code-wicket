using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CodeWicket.Core.Ide
{
    /// <summary>One local or argument as the debugger reported it.</summary>
    public sealed record DebugLocal(string Name)
    {
        public string? Type { get; init; }
        public string? Value { get; init; }
    }

    /// <summary>
    /// One stack frame the user's own code owns, with the values in scope there.
    /// </summary>
    /// <remarks>
    /// Only frames with SOURCE reach this type. Everything past the user's own code is collapsed into
    /// <see cref="DebugStateCapture.ExternalFrames"/>, the way VS's own Call Stack collapses
    /// <c>[External Code]</c> — and that is not only tidiness: the first external frame reports the
    /// PREVIOUS frame's location rather than failing (issue #73, measured twice), so a walk that stops
    /// before it excludes the lie by construction instead of trying to detect it.
    /// </remarks>
    public sealed record DebugFrame(string Method)
    {
        public string? Module { get; init; }

        /// <summary>Absolute path, or null when the frame's location could not be resolved.</summary>
        public string? File { get; init; }

        public int Line { get; init; }

        public IReadOnlyList<DebugLocal> Locals { get; init; } = Array.Empty<DebugLocal>();

        /// <summary>Locals the capture stopped short of reading. Stated, never silent.</summary>
        public int OmittedLocals { get; init; }

        public bool HasLocation => !string.IsNullOrEmpty(File) && Line > 0;
    }

    /// <summary>
    /// What the debugger was showing when the user asked to hand it over (issue #73, rung 2).
    /// </summary>
    public sealed record DebugStateCapture
    {
        /// <summary>Innermost first, matching the Call Stack window.</summary>
        public IReadOnlyList<DebugFrame> Frames { get; init; } = Array.Empty<DebugFrame>();

        /// <summary>
        /// Frames past the user's own code. A count rather than a list: they carry no source to open and
        /// no locals worth reading, and the first of them cannot be located truthfully anyway.
        /// </summary>
        public int ExternalFrames { get; init; }

        /// <summary>Source frames the walk stopped short of, past <see cref="DebugStateLimits.MaxFrames"/>.</summary>
        public int OmittedFrames { get; init; }

        public string? ThreadName { get; init; }

        /// <summary>Why the debugger stopped, when it says — a breakpoint, an exception.</summary>
        public string? StopReason { get; init; }

        public bool IsEmpty => Frames.Count == 0 && ExternalFrames == 0;
    }

    /// <summary>
    /// How much of a break-mode capture is worth carrying, in one place so the reader and the writer
    /// cannot disagree about it.
    /// </summary>
    /// <remarks>
    /// The caps on FRAMES and LOCALS are consulted while capturing, because each one costs a COM call
    /// and reading a thousand locals to throw away nine hundred is work nobody asked for. The cap on a
    /// VALUE is applied while formatting, because by then the string has already been read and clamping
    /// it twice would be two rules to keep in step. Whichever end applies a cap, the count it dropped is
    /// stated — see <see cref="DebugStateFormatter"/>.
    /// </remarks>
    public static class DebugStateLimits
    {
        /// <summary>
        /// User frames carried. Deep enough for a recursion to be recognisable as one — the case that
        /// motivated a real number rather than three or four.
        /// </summary>
        /// <remarks>
        /// <b>Raised from 12 on the first real capture</b> (in Visual Studio, 2026-08-26), which is the only evidence
        /// that could have settled it: an ordinary 10-deep recursion produced exactly 12 user frames
        /// and filled the budget to the last one. Nothing was lost — the totals were honest and a
        /// deeper stack would have said "… N more frames of your code not shown" — but a number chosen
        /// so a recursion stays recognisable has no headroom if a modest one consumes all of it.
        /// <para>The raise is close to free now that a frame no longer carries a redundant
        /// <c>this</c> line: at roughly two lines per frame, 20 frames costs about what 12 did before
        /// that fix.</para>
        /// </remarks>
        public const int MaxFrames = 20;

        /// <summary>Locals and arguments per frame.</summary>
        public const int MaxLocalsPerFrame = 24;

        /// <summary>
        /// Characters of one value. A collection or a long string renders as a wall otherwise, and the
        /// capture rides a prompt.
        /// </summary>
        public const int MaxValueChars = 200;
    }

    /// <summary>
    /// Renders a capture into the <c>&lt;debug-state&gt;</c> block that goes on the wire, and into the
    /// text the user sees on the chip. One rendering, so the two cannot describe different things.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every reduction is stated. That is the <see cref="WorkspaceDiagnostics"/> rule and it matters more
    /// here, because the reader is a model reasoning about why a value is what it is: a frame list
    /// silently cut at twelve reads as the whole stack, and "no locals shown" reads as "this frame had
    /// none" rather than "we stopped looking".
    /// </para>
    /// <para>
    /// Locations are presented as IDE-derived rather than authoritative. They are resolved by selecting
    /// each frame and reading where the editor landed, which is exact for frames with source and is the
    /// only route available — <c>EnvDTE.StackFrame</c> exposes no file or line at all.
    /// </para>
    /// </remarks>
    public static class DebugStateFormatter
    {
        /// <summary>The wire block's contents, without the surrounding tag.</summary>
        public static string Format(DebugStateCapture capture, string? workspaceRoot)
        {
            if (capture is null || capture.IsEmpty)
                return "The debugger was stopped, but no frames could be read.";

            var sb = new StringBuilder();
            AppendHeader(sb, capture, workspaceRoot);

            // Whether the header already carries the exception decides whether the frames repeat it.
            var exceptionInHeader = !string.IsNullOrEmpty(capture.StopReason);
            for (var i = 0; i < capture.Frames.Count; i++)
                AppendFrame(sb, capture.Frames[i], i + 1, workspaceRoot, exceptionInHeader);

            AppendTotals(sb, capture);
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// The chip's label: short, and specific enough to tell two captures apart in one conversation.
        /// </summary>
        public static string Label(DebugStateCapture capture)
        {
            if (capture is null || capture.IsEmpty)
                return "Debug state";

            var frames = capture.Frames.Count + capture.OmittedFrames;
            var where = capture.Frames.Count > 0 ? ShortMethod(capture.Frames[0].Method) : null;
            return where is null
                ? $"Debug state · {Plural(frames, "frame")}"
                : $"Debug state · {where} · {Plural(frames, "frame")}";
        }

        private static void AppendHeader(StringBuilder sb, DebugStateCapture capture, string? workspaceRoot)
        {
            var top = capture.Frames.Count > 0 ? capture.Frames[0] : null;
            sb.Append("Stopped");
            if (top is not null)
            {
                sb.Append(" in ").Append(top.Method);
                if (top.HasLocation)
                    sb.Append(" at ").Append(Where(top, workspaceRoot));
            }

            sb.AppendLine(".");

            // Its OWN line, parallel to the thread, rather than in brackets on the end of the first
            // one. Measured on the first exception capture (in Visual Studio, 2026-08-26): parenthesised it read
            // "at HelloWorldService.cs:40 (exception: {"Test"})", which buries the single most
            // important fact about the stop inside an aside on a line that is already long - and an
            // exception message is unbounded, so the aside can be longer than the sentence containing
            // it. The reason a debugger stopped is not a parenthetical.
            if (!string.IsNullOrEmpty(capture.StopReason))
                sb.AppendLine(capture.StopReason);

            if (!string.IsNullOrEmpty(capture.ThreadName))
                sb.Append("Thread: ").AppendLine(capture.ThreadName);

            sb.AppendLine();
        }

        private static void AppendFrame(
            StringBuilder sb, DebugFrame frame, int position, string? workspaceRoot, bool exceptionInHeader)
        {
            sb.Append('#').Append(position).Append(' ').Append(frame.Method);
            if (frame.HasLocation)
                sb.Append("  ").Append(Where(frame, workspaceRoot));
            sb.AppendLine();

            var shown = 0;
            foreach (var local in frame.Locals)
            {
                if (SaysNothing(local, exceptionInHeader))
                    continue;

                shown++;
                sb.Append("    ").Append(local.Name);
                if (!string.IsNullOrEmpty(local.Type))
                    sb.Append(" (").Append(local.Type).Append(')');
                sb.Append(" = ").AppendLine(ClampValue(local.Value));
            }

            // "None shown" and "none there" are different facts, and only one of them is about the code.
            if (frame.OmittedLocals > 0)
                sb.Append("    … ").Append(frame.OmittedLocals).AppendLine(" more not shown");
            else if (shown == 0)
                sb.AppendLine("    (no locals in scope)");
        }

        private static void AppendTotals(StringBuilder sb, DebugStateCapture capture)
        {
            sb.AppendLine();
            if (capture.OmittedFrames > 0)
            {
                sb.Append("… ").Append(capture.OmittedFrames)
                  .Append(" more frame").Append(capture.OmittedFrames == 1 ? string.Empty : "s")
                  .AppendLine(" of your code not shown");
            }

            // Always stated, including none: an absent line would read as "the stack ends here", which is
            // a claim about the program rather than about what was carried.
            sb.Append("External code: ")
              .AppendLine(capture.ExternalFrames == 0
                  ? "none"
                  : Plural(capture.ExternalFrames, "frame") + " (no source, not listed)");
        }

        /// <summary>
        /// Whether a local is worth a line — which today means one thing only: <c>this</c>, whose value
        /// is the debugger's default rendering of its own type.
        /// </summary>
        /// <remarks>
        /// <para>Measured on the first real capture (in Visual Studio, 2026-08-26): a 12-frame recursion produced 26
        /// local lines, and <b>12 of them were <c>this (ConsoleApp1.HelloWorldService) =
        /// {ConsoleApp1.HelloWorldService}</c></b> — the same line, once per frame, saying nothing the
        /// frame's own method name had not already said. Nearly half the payload, in a block that is
        /// bounded precisely because it rides a prompt.</para>
        /// <para><b>This is not a "reduction" that the stated-totals rule requires announcing</b>, and
        /// the distinction is the whole justification: a reduction drops something the reader would
        /// otherwise learn, while <c>this = {ThatSameType}</c> is a restatement of the line above it.
        /// Announcing it would replace one dead line with another.</para>
        /// <para>Deliberately narrow in two ways. Only <c>this</c>, because for any OTHER local the
        /// NAME is informative even when the value is not — <c>service (Foo) = {Foo}</c> still tells
        /// you <c>service</c> is in scope, whereas <c>this</c> is implied by the frame existing. And
        /// only when the value is exactly the braced type: a type with a real <c>ToString</c>
        /// (<c>{Order Id=42 Status=Paid}</c>) is the most useful line on the frame and must survive.</para>
        /// </remarks>
        private static bool SaysNothing(DebugLocal local, bool exceptionInHeader)
        {
            // The debugger's exception slot, not a local. It resolves the same on EVERY frame - the
            // second exception capture carried twelve identical
            // `$exception (System.Exception) = {"Test"}` lines - and the header states it once, in
            // better form. Dropped only when the header actually has it: if the reason could not be
            // read, these lines are the only record there is and losing them would be a reduction
            // rather than a de-duplication.
            if (string.Equals(local.Name, "$exception", StringComparison.Ordinal))
                return exceptionInHeader;

            if (!string.Equals(local.Name, "this", StringComparison.Ordinal))
                return false;
            if (string.IsNullOrEmpty(local.Type) || string.IsNullOrEmpty(local.Value))
                return false;

            return string.Equals(local.Value, "{" + local.Type + "}", StringComparison.Ordinal);
        }

        /// <summary>
        /// The one line describing why the debugger stopped, built from the debugger's own
        /// <c>$exception</c> — <b>label included</b>, so the layer that knows WHAT kind of reason this
        /// is also names it. The formatter prints <see cref="DebugStateCapture.StopReason"/> verbatim
        /// and does not need to know; a second kind of stop reason can therefore be added without a
        /// label here and a matching branch there having to be kept in step.
        /// </summary>
        /// <remarks>
        /// <para>The debugger's value form is UNWRAPPED, and that is the point of doing this in Core
        /// where it can be tested. <c>Expression.Value</c> for an exception comes back as
        /// <c>{"Test"}</c> — braces because it is an object, quotes because the message is a string —
        /// so the untouched form read <c>(exception: {"Test"})</c>: two layers of debugger punctuation
        /// around a one-word message. With the type beside it the result is the shape .NET prints
        /// exceptions in, which is the form a reader already knows how to scan.</para>
        /// <para>Conservative: unwrapping only strips a matched pair, so a value that is not wrapped
        /// survives untouched, and a message that legitimately contains braces or quotes inside it
        /// keeps them.</para>
        /// </remarks>
        public static string? DescribeException(string? type, string? value)
        {
            var message = Unwrap(Unwrap(value?.Trim(), '{', '}'), '"', '"');
            var hasType = !string.IsNullOrEmpty(type);
            var hasMessage = !string.IsNullOrEmpty(message);

            if (!hasType && !hasMessage)
                return null;
            if (!hasMessage)
                return "Exception: " + type;
            if (!hasType)
                return "Exception: " + message;

            return "Exception: " + type + ": " + message;
        }

        /// <summary>
        /// The one line describing a stop at a BREAKPOINT — the other half of
        /// <see cref="DescribeException"/>, and labelled here for the same reason.
        /// </summary>
        /// <remarks>
        /// <para><b>It says a breakpoint is HERE, not that it is why you stopped</b>, and the
        /// difference is the whole guard. The debugger offers <c>BreakpointLastHit</c>, which is
        /// exactly what it says — LAST hit, and still set after you have stepped ten lines away — so a
        /// causal claim built on it would name a stale breakpoint with total confidence. The caller
        /// therefore only asks when that breakpoint's location matches the frame the debugger is
        /// actually stopped on, and even then the wording stays a statement about the line. Stepping
        /// onto a line that has a breakpoint makes it read slightly oddly and never makes it false.</para>
        /// <para><b>Whose breakpoint it is, is the half worth having.</b> Rung 1 tags every breakpoint
        /// the agent sets (<see cref="Breakpoints.Tag"/>), so a capture can tell the agent it is
        /// looking at the stop its OWN instrumentation asked for — which is the two rungs closing into
        /// a loop rather than two features that happen to share an issue. An untagged breakpoint is
        /// the user's and says nothing extra; that is the common case and it should stay quiet.</para>
        /// <para>The location is deliberately absent: the header's first line already gives it, and
        /// repeating it here would be the <c>this</c> problem in a new place.</para>
        /// </remarks>
        /// <summary>One breakpoint sitting on the line execution stopped at.</summary>
        public sealed record BreakpointAtStop(string? Tag, string? Condition)
        {
            public bool FromThisConversation { get; init; }
        }

        /// <summary>
        /// What to say when MORE THAN ONE breakpoint sits on the stopped line: that it cannot be told
        /// which one fired, and what the candidates are.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The danger is not misattribution, it is a false claim about the PROGRAM.</b> The debugger
        /// offers <c>BreakpointLastHit</c>, one breakpoint, and nothing distinguishes two that share a
        /// file and a line — so naming one is a coin toss. Lose the toss with a conditional breakpoint
        /// and the block reads "set by this conversation (condition: i == 47)", from which the only
        /// reasonable inference is that the condition HELD — while the stop may in fact have come from
        /// the user's unconditional breakpoint on the same line with that condition false. A correctly
        /// formatted sentence producing a confident wrong conclusion about live data.
        /// </para>
        /// <para>
        /// So the ambiguity is stated, the same answer <see cref="DebugEvaluation.MayNeedCode"/> gives to
        /// a different unanswerable question. Conditions are listed WITHOUT any suggestion that they were
        /// met, because that is exactly what is not known.
        /// </para>
        /// </remarks>
        public static string? DescribeAmbiguousBreakpoints(IReadOnlyList<BreakpointAtStop> candidates)
        {
            if (candidates is null || candidates.Count == 0)
                return null;
            if (candidates.Count == 1)
                return DescribeBreakpoint(candidates[0].Tag, candidates[0].Condition, candidates[0].FromThisConversation);

            var sb = new StringBuilder();
            sb.Append("Breakpoint — ").Append(candidates.Count)
              .Append(" breakpoints are set on this line, so which one stopped execution cannot be determined");
            if (candidates.Any(c => !string.IsNullOrWhiteSpace(c.Condition)))
                sb.Append(" (a listed condition may or may not have been met)");
            sb.AppendLine(":");

            foreach (var candidate in candidates)
            {
                sb.Append("  - ");
                if (candidate.FromThisConversation)
                    sb.Append("set by this conversation");
                else if (Breakpoints.IsAgentTag(candidate.Tag))
                    sb.Append("set by the agent in an earlier conversation");
                else
                    sb.Append("set by the user");

                if (!string.IsNullOrWhiteSpace(candidate.Condition))
                    sb.Append(" (condition: ").Append(candidate.Condition!.Trim()).Append(')');
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        public static string? DescribeBreakpoint(string? tag, string? condition, bool fromThisConversation)
        {
            var sb = new StringBuilder("Breakpoint");
            if (fromThisConversation)
                sb.Append(" — set by this conversation");
            else if (Breakpoints.IsAgentTag(tag))
                sb.Append(" — set by the agent in an earlier conversation");

            if (!string.IsNullOrWhiteSpace(condition))
                sb.Append(" (condition: ").Append(condition!.Trim()).Append(')');

            return sb.ToString();
        }

        /// <summary>Strips ONE matched pair of delimiters, or returns the input unchanged.</summary>
        private static string? Unwrap(string? text, char open, char close)
        {
            if (text is null || text.Length < 2 || text[0] != open || text[text.Length - 1] != close)
                return text;

            return text.Substring(1, text.Length - 2).Trim();
        }

        private static string Where(DebugFrame frame, string? workspaceRoot) =>
            WorkspacePath.Relative(frame.File!, workspaceRoot) + ":" + frame.Line;

        /// <summary>
        /// A value, bounded. A null value is reported as unavailable rather than as an empty string —
        /// the debugger declining to evaluate something and the value being empty are different answers.
        /// </summary>
        private static string ClampValue(string? value)
        {
            if (value is null)
                return "(unavailable)";
            if (value.Length <= DebugStateLimits.MaxValueChars)
                return value;

            var dropped = value.Length - DebugStateLimits.MaxValueChars;
            return value.Substring(0, DebugStateLimits.MaxValueChars) + "… (+" + dropped + " chars)";
        }

        /// <summary>
        /// The method's own name, without its type or namespace and without its parameter list.
        /// </summary>
        /// <remarks>
        /// It kept the last TWO dotted segments until a chip was rendered and looked at
        /// (<c>--screenshot-debug-context</c>, 2026-08-26): <c>HelloWorldService.SayHelloWorld</c> is
        /// 31 characters before the frame count, and the strip cut the label at
        /// <c>"Debug state · HelloWorldService.SayHelloWorld · 4 f…"</c> — losing the count, which is
        /// the half that says how much was attached. The type name is also the half least likely to
        /// tell two captures in one conversation apart, since a recursion or a call chain inside one
        /// class shares it. So: one segment, and the count survives.
        /// </remarks>
        private static string? ShortMethod(string? method)
        {
            if (string.IsNullOrEmpty(method))
                return null;

            var name = method!;
            var paren = name.IndexOf('(');
            if (paren > 0)
                name = name.Substring(0, paren);

            var lastDot = name.LastIndexOf('.');
            return lastDot <= 0 || lastDot == name.Length - 1 ? name : name.Substring(lastDot + 1);
        }

        private static string Plural(int count, string noun) =>
            count == 1 ? "1 " + noun : count + " " + noun + "s";
    }
}
