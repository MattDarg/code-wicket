using System;
using System.Text.RegularExpressions;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// Reads source locations out of a failing test's stack trace (run_tests). Two locations matter and
    /// they are frequently different places:
    /// <list type="bullet">
    /// <item><description><see cref="Failure"/> — where it threw: the FIRST frame under the workspace
    /// root, skipping the assertion library's own frames.</description></item>
    /// <item><description><see cref="Test"/> — where the test is: the LAST frame under the workspace root,
    /// the outermost of the user's own frames (past it is only runner/reflection plumbing, which ships no
    /// source).</description></item>
    /// </list>
    /// The split is what a shared assertion helper or a BDD step definition needs: the throw site says
    /// "expected 5, actual 4" in a one-line <c>Then</c>, while the test that set that up is elsewhere.
    /// <para>
    /// Reqnroll/SpecFlow need no special-casing because of this: their generated code-behind carries
    /// <c>#line</c> pragmas back to the <c>.feature</c>, so the runtime attributes the outermost frame to
    /// the feature file itself — <see cref="Test"/> lands on the scenario, in Gherkin, which is exactly
    /// what you want to read when a Then fails. (Probed 2026-07-25 against Reqnroll 2.4 + xUnit; the frame
    /// shape is identical under VSTest and Microsoft Testing Platform.)
    /// </para>
    /// Pure text heuristics over the trace, deliberately: it needs no test-framework knowledge, so it works
    /// for every runner, and the full stack trace ships in the result either way.
    /// </summary>
    public static class TestStackFrames
    {
        // Matches a stack-frame source suffix: "... in C:\path\File.cs:line 42". The "<file>:line N" form
        // is emitted by the .NET runtime (not the test framework), so this is framework-agnostic — it just
        // needs PDBs. The runtime LOCALIZES the words though ("in …:Zeile 42." on German .NET Framework,
        // "dans …:ligne 42" on French), so the pattern must not key on "in"/"line": it anchors on the path
        // shape (drive / UNC / a deterministic-build "/_/…" root) followed by ":<word> <digits>" at the end
        // of the frame line (optionally trailed by punctuation — German frames end with a period).
        // Multiline so each frame is a separate match; the drive colon is consumed by the path prefix, so
        // it can't be mistaken for the line delimiter. Note the file extension is never matched against:
        // that's why a Reqnroll frame pointing at a ".feature" is picked up like any other.
        private static readonly Regex StackFrameLocation =
            new Regex(
                @"(?<file>(?:[A-Za-z]:[\\/]|\\\\|/)[^:*?""<>|\r\n]+?):\p{L}+\s+(?<lineNo>\d+)\p{P}?\s*$",
                RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// The best failure location: the first frame (from the top = the throw site) whose file is under
        /// the workspace root — i.e. the user's own code, which skips assertion-library frames (those
        /// usually ship no source, so they carry no <c>:line</c> anyway). Falls back to the first frame with
        /// any file:line. Null when the trace has no source info (release/no PDBs).
        /// </summary>
        public static (string File, int Line)? Failure(string? stack, string? workspaceRoot)
        {
            if (string.IsNullOrEmpty(stack))
                return null;
            (string File, int Line)? firstAny = null;
            foreach (var frame in Frames(stack!))
            {
                if (firstAny == null)
                    firstAny = (frame.File, frame.Line);
                if (IsUnderRoot(frame.File, workspaceRoot))
                    return (frame.File, frame.Line);
            }
            return firstAny;
        }

        /// <summary>
        /// Every frame in the trace that carries a source location, in trace order (top = throw site).
        /// <c>Text</c> is the whole frame line, so callers can also read the method it names.
        /// </summary>
        private static System.Collections.Generic.IEnumerable<(string Text, string File, int Line)> Frames(string stack)
        {
            // Line-at-a-time rather than one sweep of the whole trace: same matches (the pattern is
            // anchored to end-of-line either way), but it keeps each frame's method text with its file.
            foreach (var raw in stack.Split('\n'))
            {
                var m = StackFrameLocation.Match(raw);
                if (!m.Success || !int.TryParse(m.Groups["lineNo"].Value, out var line))
                    continue;
                yield return (raw, m.Groups["file"].Value.Trim(), line);
            }
        }

        /// <summary>
        /// The test's own location. Preferred rule: the last in-workspace frame that actually NAMES the
        /// test's declaring class (<paramref name="testClassName"/>, straight off the TRX's
        /// <c>TestMethod@className</c>) — a verified match rather than a guess. Falls back to the last
        /// in-workspace frame when no frame names that class, or when the caller has no class to match
        /// (the position rule alone). Never falls back outside the workspace — an outermost frame in the
        /// runner's own assembly is plumbing, not a place to send anyone.
        /// <para>
        /// The class match is what saves the awkward shapes. Position alone lands on whatever user frame
        /// happens to be outermost, which is the test method in the common case but is a shared
        /// base-class runner (<c>TestBase.Execute()</c> in the same project) when a suite has one — the
        /// class match steps past that to the real test. It also tolerates compiler-generated frames,
        /// since an async state machine, lambda or local function still carries the class in its frame
        /// text (<c>MyTests.&lt;TheTest&gt;d__3.MoveNext()</c>).
        /// </para>
        /// <para>
        /// Why fall back rather than insist on a match: a test method INHERITED from a base class reports
        /// the derived class in the TRX while the frame names the base, and that base file is genuinely
        /// where the test is — refusing to answer there would be worse than the position rule. So an
        /// unmatched class degrades to position instead of nothing, and the caller labels the destination
        /// so the user sees where a click will land before taking it.
        /// </para>
        /// Returns null when it would merely repeat <paramref name="failure"/> (the ordinary
        /// assert-inline-in-the-test case), so a non-null result always means a jump that goes somewhere
        /// new — the caller can offer it without checking.
        /// </summary>
        public static (string File, int Line)? Test(
            string? stack, string? workspaceRoot, (string File, int Line)? failure, string? testClassName = null)
        {
            if (string.IsNullOrEmpty(stack))
                return null;

            // TRX spells a nested class "Outer+Inner"; stack frames spell it "Outer.Inner".
            var className = string.IsNullOrEmpty(testClassName) ? null : testClassName!.Replace('+', '.') + ".";

            (string File, int Line)? lastInRoot = null;
            (string File, int Line)? lastOnClass = null;
            foreach (var frame in Frames(stack!))
            {
                if (!IsUnderRoot(frame.File, workspaceRoot))
                    continue;
                lastInRoot = (frame.File, frame.Line);
                if (className != null && frame.Text.IndexOf(className, StringComparison.Ordinal) >= 0)
                    lastOnClass = (frame.File, frame.Line);
            }

            var resolved = lastOnClass ?? lastInRoot;
            if (resolved == null)
                return null;
            if (failure != null
                && failure.Value.Line == resolved.Value.Line
                && string.Equals(failure.Value.File, resolved.Value.File, StringComparison.OrdinalIgnoreCase))
                return null;
            return resolved;
        }

        /// <summary>
        /// Case-insensitive "is <paramref name="path"/> under <paramref name="workspaceRoot"/>".
        /// </summary>
        /// <remarks>
        /// Delegated to <see cref="WorkspacePath.IsUnderRoot"/> rather than repeated. That is not a
        /// no-op: this copy used to hand a RELATIVE frame path straight to <c>GetFullPath</c>, which
        /// resolves against the process working directory — devenv's install folder — so such a frame was
        /// judged against the wrong origin and reported outside the workspace. The shared rule roots it
        /// against <paramref name="workspaceRoot"/> first.
        /// </remarks>
        public static bool IsUnderRoot(string? path, string? workspaceRoot)
            => WorkspacePath.IsUnderRoot(path, workspaceRoot);
    }
}
