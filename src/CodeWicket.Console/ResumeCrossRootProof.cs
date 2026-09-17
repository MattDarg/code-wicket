using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Providers.ClaudeCode;
using CodeWicket.Providers.Kiro;
using CodeWicket.Shell;

namespace CodeWicket.ConsoleHost
{
    /// <summary>
    /// What does a backend do when asked to resume a conversation from a DIFFERENT working directory
    /// than the one it was created in? (issue #59)
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a characterisation proof, not a gate.</b> There is no right answer to assert: both
    /// plausible behaviours are legitimate for a backend to have, and the host handles either. What is
    /// not acceptable is not KNOWING, because the two differ enormously in how a failure presents.
    /// </para>
    /// <para>
    /// Measured on Kiro in Visual Studio, on a five-week-old conversation: it does <b>not</b> fail. It returns a
    /// brand new EMPTY session wearing the requested id - <c>createdAt</c> the moment of the load,
    /// <c>lastModifiedAt</c> 14 ms later, title "New Session", <c>workspacePaths</c> naming the new
    /// root. The load reports success, so nothing populates <c>ResumeFailureReason</c> and nothing is
    /// shown; the user reads their whole transcript beside an agent that has never heard of it.
    /// </para>
    /// <para>
    /// Claude Code was never measured. #108 records only that it keys its store by a hash of the
    /// working directory, which suggests a cross-root load should simply miss and fail cleanly - the
    /// opposite and much better behaviour - but that is inference from one sentence, and inference is
    /// exactly what this proof exists to replace. This is the leg that turns a one-off observation in
    /// a session log into something anyone can re-run.
    /// </para>
    /// <para>
    /// The proof drives the PROVIDER directly rather than the engine, deliberately: <c>ResumeRootGuard</c>
    /// would refuse this load before it was ever issued, which is correct for the product and useless
    /// for measuring the backend. What is under test here is the thing the guard exists because of.
    /// </para>
    /// </remarks>
    internal static class ResumeCrossRootProof
    {
        private const string Magic = "PLUM";

        /// <summary>
        /// The two questions this proof can put to a backend. They share every leg but the second's
        /// inputs, and they are kept together because the answer to one was once mistaken for the
        /// answer to the other: the "brand new EMPTY session wearing the requested id" measured in Visual Studio
        /// on a five-week-old conversation was read as Kiro's CROSS-ROOT behaviour, and #183's
        /// pre-check was built on it. Re-measured 2026-09-12 (kiro-cli 2.21.4 / KAS 0.63.3), both Kiro
        /// engines CARRY a conversation across roots - so whatever produced that empty session, it was
        /// not the root. An id the backend no longer holds is the remaining candidate, and it is the
        /// trigger nobody opts into (issue #185).
        /// </summary>
        internal enum Leg
        {
            /// <summary>Resume a real id from a DIFFERENT working directory.</summary>
            CrossRoot,

            /// <summary>Resume an id of the right SHAPE that the backend has never seen, from the
            /// same working directory - what an expired or pruned session looks like from here.</summary>
            UnknownId,
        }

        internal static async Task<int> RunAsync(string? backend, string? engine = null, Leg leg = Leg.CrossRoot)
        {
            var useClaude = string.Equals(backend, "claude", StringComparison.OrdinalIgnoreCase);
            var label = useClaude
                ? "claude-code"
                : "kiro" + (string.IsNullOrEmpty(engine) ? " (default engine)" : $" --agent-engine {engine}");
            Console.WriteLine(leg == Leg.CrossRoot
                ? $"== code-wicket console: a cross-root resume, as the backend answers it ({label}, issue #59) =="
                : $"== code-wicket console: a resume of an id the backend never held, as it answers it ({label}, issue #185) ==");
            Console.WriteLine();

            var root = HostScratch.ResolveDir("console/resume-cross-root-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, ".git"));
                var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
                var elsewhere = Directory.CreateDirectory(Path.Combine(root, "elsewhere")).FullName;

                // The Kiro ENGINE is selectable, and it turns out to matter here: the default
                // engine and v3 do not answer this question the same way. Passed through so the
                // proof can be run against either rather than silently characterising one.
                IAgentProvider Provider() => useClaude
                    ? new ClaudeCodeAgentProvider()
                    : new KiroAgentProvider(new KiroProviderOptions { AgentEngine = engine });

                // --- leg 1: create a conversation in 'home', and put something in it to look for ----
                string conversationId;
                {
                    var ide = new ConsoleIdeServices(home);
                    await using var session = await Provider()
                        .StartSessionAsync(new SessionOptions { WorkspaceRootPath = home }, ide)
                        .ConfigureAwait(false);

                    await DrainAsync(session, $"Remember this word: {Magic}. Reply with just OK.")
                        .ConfigureAwait(false);
                    conversationId = session.ConversationId;
                }

                Console.WriteLine($"created in        = {home}");
                Console.WriteLine($"conversation id   = {conversationId}");
                Console.WriteLine();

                // --- leg 2: resume that id from a DIFFERENT root - or, on the other leg, an id of the
                // same shape the backend has never held, from the SAME root ------------------------
                var resumeRoot = leg == Leg.CrossRoot ? elsewhere : home;
                var resumeId = leg == Leg.CrossRoot ? conversationId : UnknownIdShapedLike(conversationId);
                var ide2 = new ConsoleIdeServices(resumeRoot);
                string? startFailure = null;
                string? resumeFailureReason = null;
                int? replayed = null;
                string answer = string.Empty;

                if (leg == Leg.UnknownId)
                {
                    Console.WriteLine($"asking for        = {resumeId}  (never issued by this backend)");
                    Console.WriteLine();
                }

                try
                {
                    await using var session = await Provider()
                        .StartSessionAsync(
                            new SessionOptions
                            {
                                WorkspaceRootPath = resumeRoot,
                                ResumeConversationId = resumeId,
                            },
                            ide2)
                        .ConfigureAwait(false);

                    resumeFailureReason = (session as IResumeFallbackReport)?.ResumeFailureReason;
                    replayed = (session as IResumeFallbackReport)?.ReplayedHistoryCount;
                    answer = await DrainAsync(
                        session,
                        "What word did I ask you to remember? Reply with only that word, or NONE if you "
                        + "have no record of it. Use no tools.").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    startFailure = ex.Message;
                }

                var remembered = answer.Contains(Magic, StringComparison.OrdinalIgnoreCase);

                Console.WriteLine($"resumed from      = {resumeRoot}");
                Console.WriteLine($"start threw       = {startFailure ?? "(no)"}");
                Console.WriteLine($"resume failure    = {resumeFailureReason ?? "(none reported)"}");
                // The instrument issue #185's post-hoc check reads: on a load that carried the
                // conversation this must be positive, or the check would call every such resume empty.
                Console.WriteLine($"frames replayed   = {(replayed is { } n ? n.ToString() : "(not reported)")}");
                Console.WriteLine($"agent replied     = \"{answer.Trim()}\"");
                Console.WriteLine();

                // Three outcomes, and naming which one happened IS the result.
                var what = leg == Leg.CrossRoot ? "a cross-root resume" : "a resume of an id it never held";
                if (startFailure is not null || !string.IsNullOrEmpty(resumeFailureReason))
                {
                    Console.WriteLine(
                        $"FINDING ({label}): REFUSES {what}. The failure is reported, so the "
                        + "host can tell the user the earlier context was not loaded. This is the safe "
                        + "behaviour.");
                }
                else if (remembered)
                {
                    Console.WriteLine(leg == Leg.CrossRoot
                        ? $"FINDING ({label}): CARRIES the conversation across roots - the store is not "
                          + "scoped by working directory after all. Re-read the assumption behind "
                          + "ResumeRootGuard before changing anything on the strength of it."
                        : $"FINDING ({label}): answered with the word for an id it was NEVER GIVEN - the "
                          + "backend matched something other than the id. Read the wire before believing "
                          + "any of this run.");
                }
                else
                {
                    Console.WriteLine(
                        $"FINDING ({label}): SILENTLY SUCCEEDS with no history. The load reports success, "
                        + "so nothing populates ResumeFailureReason and nothing is shown - the user reads "
                        + "their whole transcript beside an agent that has never heard of it. This is the "
                        + "behaviour a post-hoc check (issue #185) exists for, and it is the one that "
                        + "cannot be detected from the response alone.");
                }

                Console.WriteLine();
                Console.WriteLine(
                    "(Characterisation, not a gate: the host refuses this load before it is issued, so "
                    + "either backend behaviour is handled. The value here is knowing which it is.)");
                return 0;
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); }
                catch { /* scratch cleanup is best effort */ }
            }
        }

        /// <summary>
        /// An id the backend cannot hold, in the shape of one it just issued: every hex digit after
        /// any <c>prefix_</c> is rotated, so a <c>sess_</c> prefix or a bare GUID keeps its form and
        /// any shape-based validation passes. The point is to reach the store lookup, not a syntax
        /// check - rotating the prefix too (<c>sess_</c> → <c>sfss_</c>) makes the id malformed, and a
        /// malformed id being waved through says nothing about an unknown one.
        /// </summary>
        internal static string UnknownIdShapedLike(string issued)
        {
            var chars = issued.ToCharArray();
            for (var i = issued.LastIndexOf('_') + 1; i < chars.Length; i++)
            {
                var c = chars[i];
                if (c >= '0' && c <= '8') chars[i] = (char)(c + 1);
                else if (c == '9') chars[i] = 'a';
                else if (c >= 'a' && c <= 'e') chars[i] = (char)(c + 1);
                else if (c == 'f') chars[i] = '0';
            }

            return new string(chars);
        }

        private static async Task<string> DrainAsync(IAgentSession session, string prompt)
        {
            var reply = new StringBuilder();
            await foreach (var ev in session.SendAsync(new PromptInput(prompt)))
                if (ev is AgentEvent.AssistantTextDelta delta)
                    reply.Append(delta.Text);
            return reply.ToString();
        }
    }
}
