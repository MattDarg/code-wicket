using System;
using System.IO;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Providers.Acp;
using CodeWicket.Providers.Kiro;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The pre-launch gate that keeps a signed-out kiro-cli from being started at all. Without it,
    /// kiro-cli answers a missing login by opening an interactive browser flow inside the headless,
    /// timeout-killed process we spawn: the page opens, whatever it needs from the user goes into a
    /// redirected pipe, and the polling process is killed — a login that can never complete. Observed
    /// live 2026-07-29 from the model-catalog shell-out alone, i.e. from merely opening the chat window.
    /// </summary>
    public class AcpPreflightTests
    {
        // Deliberately unlaunchable: if the gate ever stops running first, Process.Start fails with a
        // different exception and these tests say so instead of passing by luck.
        private const string UnlaunchableCli = "cwkt-nonexistent-cli-for-tests";

        [Fact]
        public async Task ARefusalStopsTheCliFromBeingLaunchedAndSaysWhy()
        {
            using var workspace = new TempWorkspace();
            var provider = new AcpAgentProvider(Config(() => "Kiro isn't signed in. Run 'kiro-cli login' in a terminal."));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.StartSessionAsync(workspace.Options, workspace.Ide));

            Assert.Contains("kiro-cli login", ex.Message);
        }

        [Fact]
        public async Task NoRefusalProceedsToTheLaunch()
        {
            // The gate must not become a second way for a session to fail. With nothing to refuse, the
            // launch happens and fails on its own terms — anything but our refusal message.
            using var workspace = new TempWorkspace();
            var provider = new AcpAgentProvider(Config(() => null));

            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => provider.StartSessionAsync(workspace.Options, workspace.Ide));

            Assert.DoesNotContain("kiro-cli login", ex.Message);
        }

        [Fact]
        public async Task AGateIsOptional()
        {
            using var workspace = new TempWorkspace();
            var provider = new AcpAgentProvider(Config(preflight: null));

            // No gate configured: reaches the launch exactly as before this existed.
            await Assert.ThrowsAnyAsync<Exception>(
                () => provider.StartSessionAsync(workspace.Options, workspace.Ide));
        }

        [Fact]
        public void TheModelCatalogRunsNonInteractively()
        {
            // The one flag that keeps merely opening the chat window from launching an OAuth browser
            // login we then kill mid-redirect. It reads like boilerplate and is one token from being
            // tidied away, so it is pinned rather than left to the comment.
            Assert.Contains("--no-interactive", KiroModelCatalog.ListModelsArgs);
        }

        [Fact]
        public void AnUnanswerableLoginCheckIsNotReportedAsSignedOut()
        {
            // "Can't tell" must never become "you aren't signed in": that gates a session AND sends the
            // user off to fix a login that may be perfectly fine. A CLI that isn't there can't answer.
            Assert.False(KiroLoginState.IsSignedOut(UnlaunchableCli));
        }

        [Fact]
        public void OnlyTheClisNoLoginWordingCountsAsSignedOut()
        {
            // Verbatim from kiro-cli 2.13.0 with no credentials.
            Assert.True(KiroLoginState.SaysSignedOut(
                "Not logged in. Set the KIRO_API_KEY environment variable or run `kiro-cli login` first."));

            // Every other failure must fail OPEN. A proxied network dropping the probe is the realistic
            // one, and reporting it as "you aren't signed in" would send the user to re-authenticate a
            // login that was never broken.
            Assert.False(KiroLoginState.SaysSignedOut("error sending request: dispatch failure (io error)"));
            Assert.False(KiroLoginState.SaysSignedOut("The request was throttled by the service"));
            Assert.False(KiroLoginState.SaysSignedOut(string.Empty));
        }

        private static AcpAgentConfig Config(Func<string?>? preflight) => new()
        {
            ProviderId = "test",
            DisplayName = "Test",
            CliPath = UnlaunchableCli,
            LaunchArgs = new[] { "acp" },
            PreflightCheck = preflight,
        };

        private sealed class TempWorkspace : IDisposable
        {
            private readonly string _root;

            public TempWorkspace()
            {
                _root = Path.Combine(Path.GetTempPath(), "cwkt-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_root);
                Ide = new StubIdeServices(_root);
                Options = new SessionOptions { WorkspaceRootPath = _root };
            }

            public IIdeServices Ide { get; }

            public SessionOptions Options { get; }

            public void Dispose()
            {
                try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
            }
        }
    }
}
