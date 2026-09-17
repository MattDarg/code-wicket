using System;
using System.Collections.Generic;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using CodeWicket.Core.Ide;

namespace CodeWicket.Ide
{
    /// <summary>
    /// Reads what the debugger is showing right now, so the user can hand it to the agent
    /// (issue #73, rung 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rung 1 lets the agent instrument a run and read back what it asked to be printed. It cannot see
    /// anything it did not think to ask for in advance, which is what this is for: the user is stopped
    /// at a breakpoint looking at the values, and the only thing standing between them and the agent is
    /// retyping.
    /// </para>
    /// <para>
    /// Everything here is UI-thread and COM, so the decidable parts — bounds, rendering, totals — live in
    /// <see cref="DebugStateFormatter"/> in Core where a test can reach them. What stays here is the part
    /// that needs a live debugger.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Public rather than internal for one reason: the VSIX's own Debug-menu command has to answer
    /// "is the debugger stopped" BEFORE the chat window exists, so it cannot go through
    /// <see cref="VsIdeServices"/> - and the alternative, a second break-mode read of its own, is
    /// exactly the drift the one-predicate rule exists to prevent.
    /// </remarks>
    public static class VsDebugState
    {
        /// <summary>
        /// The current break state, or null when the debugger is not stopped.
        /// </summary>
        /// <remarks>
        /// Null rather than an empty capture: "not stopped" is not a debug state with nothing in it, and
        /// the caller's answer to it is different (tell the user to break first, rather than show them an
        /// empty chip).
        /// </remarks>
        public static DebugStateCapture Capture(DTE2 dte, string conversationId = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (dte?.Debugger is not { } debugger)
                return null;

            if (!IsInBreakMode(dte))
                return null;

            // Saved and restored in the finally below. Reading a frame's location REQUIRES selecting it
            // — EnvDTE.StackFrame exposes no file or line — so this walk moves the user's own frame
            // selection, and leaving it moved would be a bug rather than a side effect. Same discipline
            // as ErrorListViewScope, and nothing inside may yield the UI thread, or the flicker becomes
            // visible.
            StackFrame original = null;
            try
            {
                try { original = debugger.CurrentStackFrame; }
                catch (Exception) { /* nothing to restore */ }

                return Walk(dte, debugger, conversationId);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                try
                {
                    if (original is not null)
                        debugger.CurrentStackFrame = original;
                }
                catch (Exception)
                {
                    // Best-effort: the user can click the frame they wanted. Failing the whole capture
                    // over it would trade their evidence for their cursor position.
                }
            }
        }

        /// <summary>
        /// Whether the debugger is stopped right now - the predicate BOTH surfaces that offer the
        /// gesture are gated on (the composer's menu item and the VS Debug-menu command), so the two
        /// cannot disagree about whether it is on offer.
        /// </summary>
        /// <remarks>
        /// It is not a substitute for <see cref="Capture"/> returning null, and must not be treated as
        /// one: break mode ends on its own - the program continues on another thread, the user presses
        /// F5 - so a true answer here can be false by the time the click lands. One cheap COM property,
        /// because it is read every time either menu opens.
        /// </remarks>
        public static bool IsInBreakMode(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (dte?.Debugger is not { } debugger)
                return false;

            try
            {
                return debugger.CurrentMode == dbgDebugMode.dbgBreakMode;
            }
            catch (Exception)
            {
                // No debugger service, or a shell shutting down. Not stopped, as far as we can tell -
                // and the honest answer to "can I offer this" is no.
                return false;
            }
        }

        /// <summary>
        /// The breakpoint the debugger is stopped ON, or null — <b>not simply the last one hit</b>.
        /// </summary>
        /// <remarks>
        /// <c>BreakpointLastHit</c> means what it says: it survives stepping, so after ten steps it
        /// still names the breakpoint that started them. Reporting it unguarded would produce a
        /// confident wrong answer about why the debugger is stopped, which is the failure this whole
        /// issue keeps meeting. So it is only accepted when its file AND line match the frame the
        /// debugger is actually on — cheap, and it can never name a location other than the one the
        /// capture already reports.
        /// </remarks>
        /// <summary>
        /// Why the debugger is stopped, in one sentence, or null when it has nothing to say.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Public because there are now TWO surfaces asking</b> — the user's pushed
        /// <c>&lt;debug-state&gt;</c> chip and the agent's own <c>read_expression</c> pull — and the
        /// same rule applies here as to <see cref="IsInBreakMode"/>: one implementation, so the two
        /// cannot disagree. A push and a pull describing the same stop in different words would be a new
        /// way to mislead, built while fixing an old one.
        /// </para>
        /// <para>
        /// <paramref name="file"/> and <paramref name="line"/> are the location execution is actually
        /// stopped at, and they are what makes the breakpoint half trustworthy — see
        /// <see cref="TryBreakpointReason"/>. The caller supplies them because the two surfaces get them
        /// from different places: rung 2 by selecting frames, the tool from AD7's document context.
        /// </para>
        /// </remarks>
        public static string DescribeStop(DTE2 dte, string file, int line, string conversationId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dte?.Debugger is not { } debugger)
                return null;

            // An exception WINS over a breakpoint: a breakpoint can sit on the line an exception was
            // thrown from, and only one of the two explains why execution stopped here.
            return TryStopReason(debugger) ?? TryBreakpointReason(debugger, file, line, conversationId);
        }

        private static string TryBreakpointReason(Debugger debugger, string topFile, int topLine, string conversationId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(topFile) || topLine <= 0)
                return null;

            try
            {
                if (debugger.BreakpointLastHit is not { } hit)
                    return null;

                string file = null;
                var line = 0;
                try { file = hit.File; } catch (Exception) { }
                try { line = hit.FileLine; } catch (Exception) { }

                if (line != topLine || string.IsNullOrEmpty(file))
                    return null;

                // Through AgentPath rather than a raw compare: a breakpoint's File and a frame's
                // location are two spellings of the same thing, and drive-letter case and separator
                // style are exactly what that canonicaliser exists to reconcile (see AGENTS.md).
                if (!string.Equals(
                        AgentPath.Canonical(file), AgentPath.Canonical(topFile),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                string tag = null;
                string condition = null;
                try { tag = (hit as Breakpoint2)?.Tag; } catch (Exception) { }
                try { condition = hit.Condition; } catch (Exception) { }

                // BreakpointLastHit names ONE breakpoint and nothing separates two that share a file and
                // a line - so rather than describe it as though it were alone, collect every enabled
                // breakpoint sitting here and let the formatter state the ambiguity when there is one.
                // Only reached in break mode, and only once BreakpointLastHit has already matched the
                // stopped location, so a session that is merely running pays nothing for it.
                var candidates = BreakpointsAt(debugger, topFile, topLine, conversationId);
                if (candidates.Count > 1)
                    return DebugStateFormatter.DescribeAmbiguousBreakpoints(candidates);

                return DebugStateFormatter.DescribeBreakpoint(
                    tag, condition, Core.Ide.Breakpoints.IsFromConversation(tag, conversationId));
            }
            catch (Exception)
            {
                // No breakpoint service, or an engine that does not track the last hit. Saying nothing
                // is the honest answer and is what a plain stop produced before this existed.
                return null;
            }
        }

        /// <summary>
        /// Every ENABLED breakpoint on the stopped line. Disabled ones are excluded because they cannot
        /// have fired, so counting them would manufacture an ambiguity that does not exist.
        /// </summary>
        private static System.Collections.Generic.List<DebugStateFormatter.BreakpointAtStop> BreakpointsAt(
            Debugger debugger, string file, int line, string conversationId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var found = new System.Collections.Generic.List<DebugStateFormatter.BreakpointAtStop>();
            var wanted = AgentPath.Canonical(file);

            try
            {
                foreach (Breakpoint breakpoint in debugger.Breakpoints)
                {
                    try
                    {
                        if (!breakpoint.Enabled || breakpoint.FileLine != line)
                            continue;
                        if (!string.Equals(AgentPath.Canonical(breakpoint.File), wanted, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string tag = null;
                        try { tag = (breakpoint as Breakpoint2)?.Tag; } catch (Exception) { }

                        found.Add(new DebugStateFormatter.BreakpointAtStop(tag, Blank(breakpoint.Condition))
                        {
                            FromThisConversation = Core.Ide.Breakpoints.IsFromConversation(tag, conversationId),
                        });
                    }
                    catch (Exception)
                    {
                        // A breakpoint whose members throw cannot be compared. Skipping it can only
                        // UNDERSTATE the ambiguity, which fails toward the existing behaviour.
                    }
                }
            }
            catch (Exception)
            {
                // No breakpoint collection: fall back to describing the single hit, as before.
            }

            return found;
        }

        private static DebugStateCapture Walk(DTE2 dte, Debugger debugger, string conversationId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var frames = new List<DebugFrame>();
            var external = 0;
            var omitted = 0;
            var reachedExternal = false;

            foreach (StackFrame frame in debugger.CurrentThread.StackFrames)
            {
                // THE rule, and the reason the walk stops rather than filters (issue #73, measured
                // twice): the FIRST frame past the user's code selects without error and does NOT move
                // the editor, so reading its location returns the previous frame's — no exception, no
                // empty value, a confident wrong answer. Everything past it throws instead. Stopping at
                // the boundary excludes the liar by construction, and matches what VS's own Call Stack
                // shows as [External Code].
                if (reachedExternal || !HasSource(frame))
                {
                    reachedExternal = true;
                    external++;
                    continue;
                }

                if (frames.Count >= DebugStateLimits.MaxFrames)
                {
                    omitted++;
                    continue;
                }

                frames.Add(ReadFrame(dte, debugger, frame));
            }

            // An exception WINS over a breakpoint, because a breakpoint can be sitting on the line an
            // exception was thrown from and only one of the two explains why execution stopped here.
            var top = frames.Count > 0 ? frames[0] : null;
            var reason = TryStopReason(debugger)
                         ?? TryBreakpointReason(
                                debugger,
                                top is { HasLocation: true } ? top.File : null,
                                top?.Line ?? 0,
                                conversationId);

            return new DebugStateCapture
            {
                Frames = frames,
                ExternalFrames = external,
                OmittedFrames = omitted,
                ThreadName = TryThreadName(debugger),
                StopReason = reason,
            };
        }

        /// <summary>
        /// Whether this frame is the user's own code, decided by LANGUAGE.
        /// </summary>
        /// <remarks>
        /// Not by module path, which was the obvious answer and is wrong: one of the two frames measured
        /// lying had its module inside the workspace's own output folder — a copied NuGet dll — so
        /// "under the workspace root" says yes to a frame with no source at all. Every frame that
        /// reported its location correctly was <c>C#</c>; every one that lied or threw was
        /// <c>Unknown</c>. Biased safely: a real C# frame reported as Unknown costs a location we could
        /// have had, while trusting an Unknown one invents one.
        /// </remarks>
        private static bool HasSource(StackFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var language = frame.Language;
                return !string.IsNullOrEmpty(language)
                       && !string.Equals(language, "Unknown", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static DebugFrame ReadFrame(DTE2 dte, Debugger debugger, StackFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string method = null;
            string module = null;
            try { method = frame.FunctionName; } catch (Exception) { }
            try { module = frame.Module; } catch (Exception) { }

            string file = null;
            var line = 0;
            var locals = new List<DebugLocal>();
            var omittedLocals = 0;

            try
            {
                // Selecting the frame is what makes both readable: the editor navigates to it (the only
                // route to a file and line) and Locals/Arguments resolve against it.
                debugger.CurrentStackFrame = frame;

                try
                {
                    var doc = dte.ActiveDocument;
                    // Qualified: Core.Ide has a TextSelection of its own (the workspace context's).
                    if (doc?.Selection is EnvDTE.TextSelection selection)
                    {
                        file = doc.FullName;
                        line = selection.CurrentLine;
                    }
                }
                catch (Exception)
                {
                    // No location for this frame; it still contributes its name and its locals.
                }

                ReadValues(frame, locals, ref omittedLocals);
            }
            catch (Exception)
            {
                // A frame the debugger will not select contributes what was read before it refused.
            }

            return new DebugFrame(method ?? "(unknown)")
            {
                Module = module,
                File = file,
                Line = line,
                Locals = locals,
                OmittedLocals = omittedLocals,
            };
        }

        /// <summary>
        /// Arguments first, then locals — the order the reader thinks in: what came in, then what the
        /// method made of it.
        /// </summary>
        /// <remarks>
        /// Top level only. Expanding <c>Expression.DataMembers</c> is where a capture explodes — one
        /// object graph can be unbounded — and the bound has to be a decision rather than a discovery.
        /// </remarks>
        private static void ReadValues(StackFrame frame, List<DebugLocal> into, ref int omitted)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Read directly rather than through a guarded lambda: the thread analyzer cannot see that a
            // closure runs synchronously inside a method which has already asserted the UI thread, and
            // every access inside one costs a VSTHRD010.
            Expressions arguments = null;
            Expressions locals = null;
            try { arguments = frame.Arguments; } catch (Exception) { }
            try { locals = frame.Locals; } catch (Exception) { }

            foreach (var source in new[] { arguments, locals })
            {
                if (source is null)
                    continue;

                foreach (Expression expression in source)
                {
                    string name = null;
                    string type = null;
                    string value = null;
                    try
                    {
                        name = expression.Name;
                        type = expression.Type;
                        value = expression.Value;
                    }
                    catch (Exception)
                    {
                        // A value the debugger cannot evaluate here — a name with no value is still
                        // worth reporting, since "in scope but unavailable" is a fact about the frame.
                    }

                    if (string.IsNullOrEmpty(name) || !seen.Add(name))
                        continue;

                    if (into.Count >= DebugStateLimits.MaxLocalsPerFrame)
                    {
                        omitted++;
                        continue;
                    }

                    into.Add(new DebugLocal(name) { Type = Blank(type), Value = value });
                }
            }
        }

        /// <summary>The stopped thread's name, shared with the pull for the same reason as
        /// <see cref="DescribeStop"/>.</summary>
        public static string ThreadName(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return dte?.Debugger is { } debugger ? TryThreadName(debugger) : null;
        }

        private static string TryThreadName(Debugger debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var thread = debugger.CurrentThread;
                var name = thread?.Name;
                return string.IsNullOrEmpty(name) ? "Thread " + thread?.ID : name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Why the debugger stopped, when it can say. Only an exception is reported: a plain breakpoint
        /// stop needs no explanation, and inventing one would put words in the debugger's mouth.
        /// </summary>
        private static string TryStopReason(Debugger debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var exception = debugger.GetExpression("$exception", false, 50);
                if (exception is { IsValidValue: true } && !string.IsNullOrEmpty(exception.Value))
                {
                    // Composed in Core, where the unwrapping of the debugger's own value form is
                    // testable: Value arrives as {"Test"} and the type beside it is what turns that
                    // into the shape .NET prints exceptions in.
                    string type = null;
                    try { type = exception.Type; } catch (Exception) { }
                    return DebugStateFormatter.DescribeException(type, exception.Value);
                }
            }
            catch (Exception)
            {
                // No exception in scope, or an engine that does not offer the pseudo-variable.
            }

            return null;
        }

        private static string Blank(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
