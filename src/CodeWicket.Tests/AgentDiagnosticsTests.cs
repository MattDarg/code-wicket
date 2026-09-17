using System;
using System.Linq;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Pins the failure panel from issue #82 — a user got "dispatch error" and nothing else, while the
    /// backend's version, its exit code and its own stderr were all either on disk or one field away.
    ///
    /// <para>The invariants worth pinning are mostly about <b>what this must NOT claim</b>. The panel
    /// deliberately makes no causal link between the agent's output and the error beside it (the
    /// transport cannot support one — separate pipes, separate buffering), so the label carrying that
    /// disclaimer is as load-bearing as the content, and a silent truncation would turn a partial tail
    /// into an apparently complete story.</para>
    /// </summary>
    public sealed class AgentDiagnosticsTests
    {
        private static AgentDiagnostics Kiro() => new AgentDiagnostics("kiro", @"C:\tools\kiro-cli.exe");

        [Fact]
        public void Describe_LeadsWithTheVersionAndTheErrorCode()
        {
            var diagnostics = Kiro();
            diagnostics.Version = "kiro-cli-chat 2.13.0";

            var text = diagnostics.Describe(errorCode: -32603, stack: null)!;

            // The version is the fact that resolved #82 (2.0.1 -> 2.16.0) and appeared nowhere.
            Assert.Contains("kiro-cli: kiro-cli-chat 2.13.0", text);
            Assert.Contains("JSON-RPC error code: -32603", text);
            Assert.Contains(@"C:\tools\kiro-cli.exe", text);
        }

        /// <summary>
        /// "We couldn't tell" has to be said out loud. Rendering nothing where the version goes is
        /// indistinguishable from a panel that simply doesn't report versions, which is the state that
        /// let #82 happen.
        /// </summary>
        [Fact]
        public void Describe_SaysSoWhenTheVersionIsUnknown()
        {
            var text = Kiro().Describe(errorCode: null, stack: null)!;

            Assert.Contains("version unknown", text);
            Assert.DoesNotContain("JSON-RPC error code", text);
        }

        /// <summary>
        /// The heading is the entire claim being made about this text: whose it is, how much of it,
        /// and that it is NOT being offered as the cause. Losing any of those turns session context
        /// into an accusation.
        /// </summary>
        [Fact]
        public void Describe_LabelsTheAgentOutputAsSessionWideAndNotAsTheCause()
        {
            var diagnostics = Kiro();
            diagnostics.Add("[INFO] [KiroAgent] MCP subsystem initialized");
            diagnostics.Add("[INFO] Auth: --auth=acp-callback");

            var text = diagnostics.Describe(errorCode: null, stack: null)!;

            Assert.Contains("kiro-cli output this session (2 lines)", text);
            Assert.Contains("Not necessarily related to the error above", text);
            Assert.Contains("engine.log", text);
            Assert.Contains("[INFO] [KiroAgent] MCP subsystem initialized", text);
            Assert.Contains("[INFO] Auth: --auth=acp-callback", text);
        }

        /// <summary>
        /// A trimmed tail that doesn't say it was trimmed reads as the whole session. Measured, the
        /// healthy case is ~16 lines, so this only ever bites on the pathological one — a panicking or
        /// retry-looping CLI — which is exactly when a reader must not assume they have all of it.
        /// </summary>
        [Fact]
        public void Describe_SaysHowMuchOutputItDropped()
        {
            var diagnostics = Kiro();
            for (var i = 0; i < AgentDiagnostics.MaxLines + 25; i++)
                diagnostics.Add("line " + i);

            var text = diagnostics.Describe(errorCode: null, stack: null)!;

            Assert.Contains($"({AgentDiagnostics.MaxLines} lines, 25 earlier dropped)", text);
            Assert.DoesNotContain("line 0" + Environment.NewLine, text);  // oldest evicted
            Assert.Contains("line " + (AgentDiagnostics.MaxLines + 24), text); // newest kept
        }

        /// <summary>Line count alone would let a CLI that writes few enormous lines (one serialized
        /// payload per line) fill the panel regardless.</summary>
        [Fact]
        public void Describe_BoundsHugeLinesByCharactersToo()
        {
            var diagnostics = Kiro();
            var fat = new string('x', 8 * 1024);
            for (var i = 0; i < 40; i++)
                diagnostics.Add(fat);

            var text = diagnostics.Describe(errorCode: null, stack: null)!;

            Assert.True(text.Length < AgentDiagnostics.MaxChars * 2, $"panel grew to {text.Length} chars");
            Assert.Contains("earlier dropped", text);
        }

        /// <summary>
        /// An exit is reported as a fact about A PROCESS, never as the agent having died. The
        /// distinction matters: <c>kiro-cli.exe</c> is a launcher for a bundled node
        /// agent, and node survives it holding the inherited stdio handles — measured 2026-08-08,
        /// where a session answered a brand-new prompt eight seconds after this exit was recorded.
        /// Hence "launcher process exited", which is true either way.
        /// <para>It must stay absent while the process lives — absent is not "exited cleanly", and
        /// rendering it as 0 would invent a death that has not happened.</para>
        /// </summary>
        [Fact]
        public void Describe_ReportsAnExitAsTheLauncherAndOnlyOnceThereHasBeenOne()
        {
            var diagnostics = Kiro();
            Assert.Null(diagnostics.ExitCode);
            Assert.DoesNotContain("exited", diagnostics.Describe(null, null)!);

            diagnostics.NoteExit(2);

            Assert.Equal(2, diagnostics.ExitCode);
            var text = diagnostics.Describe(null, null)!;
            Assert.Contains("launcher process exited with code 2", text);
            // Never the bare claim: the agent may well still be serving the session.
            Assert.DoesNotContain("the agent exited", text);
        }

        [Fact]
        public void NoteExit_KeepsTheFirstObservation()
        {
            var diagnostics = Kiro();
            diagnostics.NoteExit(2);
            diagnostics.NoteExit(0);

            Assert.Equal(2, diagnostics.ExitCode);
        }

        /// <summary>
        /// The wire wins over the probe. <c>agentInfo</c> describes the program on the other end of the
        /// pipe; the probe runs a SEPARATE process that can genuinely disagree (the Claude adapter
        /// spawns its own vendored binary). The probe is fire-and-forget, so it can land after
        /// initialize has already supplied the better answer — this is that race.
        /// </summary>
        [Fact]
        public void SetVersionIfUnknown_NeverOverwritesWhatTheAgentSaidAboutItself()
        {
            var diagnostics = Kiro();
            diagnostics.Version = "@agentclientprotocol/claude-agent-acp 0.63.0";

            diagnostics.SetVersionIfUnknown("claude 2.1.220");

            Assert.Equal("@agentclientprotocol/claude-agent-acp 0.63.0", diagnostics.Version);
        }

        [Fact]
        public void SetVersionIfUnknown_FillsTheHoleWhenTheAgentSaidNothing()
        {
            var diagnostics = Kiro();

            diagnostics.SetVersionIfUnknown("kiro-cli-chat 2.13.0");

            Assert.Equal("kiro-cli-chat 2.13.0", diagnostics.Version);
        }

        /// <summary>The agent's own log directory is the one diagnostic surface we neither own nor
        /// duplicate, so a user who has exhausted ours still has somewhere to look.</summary>
        [Fact]
        public void Describe_RelaysTheAgentsOwnLogDirectory()
        {
            var diagnostics = Kiro();
            diagnostics.LogDirectory = @"C:\Users\me\.kiro\logs\20260807T075217588";

            Assert.Contains(
                @"agent's own logs: C:\Users\me\.kiro\logs\20260807T075217588",
                diagnostics.Describe(null, null)!);
        }

        /// <summary>
        /// Our own stack goes last and is labelled as ours. It is the least interesting part of a
        /// backend failure and the most interesting part of a crash in this code, and the reader has to
        /// be able to tell which half they are looking at.
        /// </summary>
        [Fact]
        public void Describe_PutsOurOwnExceptionDetailLastAndNamesItAsOurs()
        {
            var diagnostics = Kiro();
            diagnostics.Add("agent said something");

            var text = diagnostics.Describe(null, "System.InvalidOperationException: boom")!;

            Assert.Contains("code-wicket exception detail", text);
            Assert.True(
                text.IndexOf("agent said something", StringComparison.Ordinal)
                    < text.IndexOf("code-wicket exception detail", StringComparison.Ordinal),
                "our stack must come after the agent's own output");
        }

        /// <summary>
        /// The session-start path crosses the engine wire as a plain exception message — no Details
        /// field, no expander — so it gets one fact, and silence rather than "version unknown" noise
        /// when even that isn't known yet.
        /// </summary>
        [Fact]
        public void ShortSuffix_IsTheVersionOrNothingAtAll()
        {
            var diagnostics = Kiro();
            Assert.Null(diagnostics.ShortSuffix());

            diagnostics.Version = "kiro-cli-chat 2.0.1";
            Assert.Equal("kiro-cli-chat 2.0.1", diagnostics.ShortSuffix());

            diagnostics.NoteExit(2);
            Assert.Equal("kiro-cli-chat 2.0.1, exited with code 2", diagnostics.ShortSuffix());
        }

        /// <summary>Concurrency is real here: stderr arrives on process-pool callbacks while a failing
        /// turn reads the panel on another thread.</summary>
        [Fact]
        public void Add_IsSafeUnderConcurrentWriters()
        {
            var diagnostics = Kiro();

            System.Threading.Tasks.Parallel.For(0, 2000, i => diagnostics.Add("line " + i));

            var text = diagnostics.Describe(null, null)!;
            Assert.Contains("output this session", text);
            Assert.Equal(
                AgentDiagnostics.MaxLines,
                text.Split('\n').Count(l => l.StartsWith("line ", StringComparison.Ordinal)));
        }
    }
}
