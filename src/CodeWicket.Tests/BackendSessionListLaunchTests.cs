using System.Collections.Generic;
using System.Linq;
using CodeWicket.Core;
using CodeWicket.Providers.Kiro;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What a process launched to LIST the backend's stored conversations is started with (issue #108).
    /// <para><b>The flags that select which store gets read must reach a listing process, and one of
    /// them is easy to lose.</b> Kiro's <c>--agent-engine</c> picks the engine, and v3 keeps its own
    /// sessions — so a listing launched without it reads a different engine's history and comes back
    /// empty. That is indistinguishable from the user genuinely having no CLI sessions: nothing errors,
    /// nothing logs, the picker section just stays empty for the one group of users it was built for.
    /// The same failure as asking from the wrong working directory, and just as quiet.</para>
    /// <para>So <c>BuildLaunchArgs</c> is derived FROM <c>BuildBaseLaunchArgs</c> rather than written
    /// beside it, which makes divergence impossible rather than merely discouraged, and these tests
    /// pin both halves of that.</para>
    /// </summary>
    public class BackendSessionListLaunchTests
    {
        [Fact]
        public void AListingLaunchCarriesTheEngineFlag()
        {
            // The load-bearing one. Point BuildBaseLaunchArgs back at Config.LaunchArgs and this fails
            // while everything else still passes — and the product failure it stands for is an empty
            // list rather than an error.
            var provider = new KiroAgentProvider(new KiroProviderOptions { AgentEngine = "v3" });

            Assert.Equal(new[] { "acp", "--agent-engine", "v3" }, provider.BuildBaseLaunchArgs().ToArray());
        }

        [Fact]
        public void AListingLaunchCarriesTheAgentFlag()
        {
            var provider = new KiroAgentProvider(new KiroProviderOptions { Agent = "my-agent" });

            Assert.Equal(new[] { "acp", "--agent", "my-agent" }, provider.BuildBaseLaunchArgs().ToArray());
        }

        [Fact]
        public void SessionArgsAreTheListingArgsPlusTheModel()
        {
            // The invariant that keeps the two in step: whatever the base args are, a session's args
            // start with them. A future flag added to only one of the two breaks this.
            var provider = new KiroAgentProvider(new KiroProviderOptions { Agent = "my-agent" });

            var baseArgs = provider.BuildBaseLaunchArgs();
            var sessionArgs = provider.BuildLaunchArgs(new SessionOptions { ModelId = "claude-sonnet-4" });

            Assert.Equal(baseArgs, sessionArgs.Take(baseArgs.Count).ToList());
            Assert.Equal(new[] { "--model", "claude-sonnet-4" }, sessionArgs.Skip(baseArgs.Count).ToArray());
        }

        [Fact]
        public void TheAgentFlagIsNotPassedToV3ButRequestedOverTheWire()
        {
            // v3 exits 2 on --agent at launch (measured 2026-09-11), which killed the session before the
            // handshake. The name goes over ACP instead, where a wrong one is a notice.
            var provider = new KiroAgentProvider(new KiroProviderOptions { AgentEngine = "v3", Agent = "my-agent" });

            Assert.DoesNotContain("--agent", provider.BuildBaseLaunchArgs());
            Assert.DoesNotContain("--agent", provider.BuildLaunchArgs(new SessionOptions()));
            Assert.Equal("my-agent", provider.RequestedSessionModeId);
        }

        [Fact]
        public void TheAgentFlagStaysOnTheLaunchLineForV2()
        {
            // v2 selects the agent from the flag (proven: the shadowed agent opened as currentModeId),
            // so nothing is re-sent over the wire.
            var provider = new KiroAgentProvider(new KiroProviderOptions { Agent = "my-agent" });

            Assert.Contains("--agent", provider.BuildBaseLaunchArgs());
            Assert.Null(provider.RequestedSessionModeId);
        }

        [Fact]
        public void TheModelFlagIsNotAListingConcern()
        {
            // A model is a property of a conversation; listing has none. Beyond being meaningless, v3
            // refuses --model at launch outright, so leaking it into a listing would kill the CLI.
            var provider = new KiroAgentProvider(new KiroProviderOptions { AgentEngine = "v3" });

            Assert.DoesNotContain("--model", provider.BuildBaseLaunchArgs());
        }
    }
}
