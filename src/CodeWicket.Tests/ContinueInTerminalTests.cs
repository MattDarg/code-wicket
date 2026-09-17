using System;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.Providers.Acp;
using CodeWicket.Providers.ClaudeCode;
using CodeWicket.Providers.Kiro;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Handing a conversation back to a terminal (issue #108, the reverse direction). The data was
    /// never the problem — the backend's CLI already holds every conversation this extension creates,
    /// in its own store — so all of this is about saying the right thing and not offering it when we
    /// cannot.
    /// </summary>
    public class ContinueInTerminalTests
    {
        [Fact]
        public void TheCommandCarriesTheDirectoryItMustRunFrom()
        {
            // The cd is load-bearing, not decoration. Claude keys its session store by a HASH of the
            // working directory, so the same command run from anywhere else reports the conversation as
            // missing — which reads as our id being wrong rather than the folder.
            var command = ChatViewModel.BuildResumeCommand(
                "claude --resume {id}", "abc-123", @"C:\src\my solution");

            Assert.Equal("cd \"C:\\src\\my solution\"\nclaude --resume abc-123", command);
        }

        [Fact]
        public void ThePathIsQuotedEvenWhenItDoesNotNeedToBe()
        {
            // A path with no space survives quoting; the one WITH a space is the one nobody tries
            // before pasting.
            var command = ChatViewModel.BuildResumeCommand("claude --resume {id}", "abc-123", @"C:\src\repo");

            Assert.Contains("cd \"C:\\src\\repo\"", command, StringComparison.Ordinal);
        }

        [Fact]
        public void WithNoKnownDirectoryTheCommandStandsAlone()
        {
            // Better than a `cd` to a guess: the user knows where they are, and a wrong cd would send
            // them somewhere the conversation genuinely is not.
            var command = ChatViewModel.BuildResumeCommand("claude --resume {id}", "abc-123", null);

            Assert.Equal("claude --resume abc-123", command);
        }

        [Fact]
        public void ClaudeOffersAResumeCommandAndKiroDeliberatelyDoesNot()
        {
            // Kiro's flag has not been verified by anyone here, and an unverified command is worse than
            // none: the user pastes it, it fails or opens the wrong conversation, and the failure looks
            // like the IDE's. Null hides the affordance entirely — so this asserts a DECISION, and the
            // day someone runs the flag they change this test on purpose.
            var claude = (IResumeCommandTemplate)new ClaudeCodeAgentProvider(new ClaudeCodeProviderOptions());
            var kiro = (IResumeCommandTemplate)new KiroAgentProvider(new KiroProviderOptions());

            Assert.Equal("claude --resume {id}", claude.ResumeCommandTemplate);
            Assert.Null(kiro.ResumeCommandTemplate);
        }

        [Fact]
        public void TheTemplateReachesTheHostThroughTheProviderList()
        {
            // It rides the provider list rather than a call of its own because the shell needs it while
            // a menu is opening, which is no place for a round trip.
            var provider = new ClaudeCodeAgentProvider(new ClaudeCodeProviderOptions());

            var dto = DtoMapping.ToDto(provider);

            Assert.Equal("claude --resume {id}", dto.ResumeCommand);
        }

        [Fact]
        public void AProviderWithNoCommandLineReportsNone()
        {
            // A backend that is not a CLI at all has nothing to offer, and says so by not implementing
            // the interface rather than by returning an empty string.
            var dto = DtoMapping.ToDto(new NotACliProvider());

            Assert.Null(dto.ResumeCommand);
        }

        private sealed class NotACliProvider : IAgentProvider
        {
            public string ProviderId => "in-process";

            public string DisplayName => "In-process";

            public System.Collections.Generic.IReadOnlyList<ModelInfo> Models => Array.Empty<ModelInfo>();

            public AgentCapabilities Capabilities => AgentCapabilities.None;

            public System.Threading.Tasks.Task<IAgentSession> StartSessionAsync(
                SessionOptions options, Core.Ide.IIdeServices ide,
                System.Threading.CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("Never started in this test.");
        }
    }
}
