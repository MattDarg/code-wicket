using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Exercises a REAL Windows pseudo-console (the run_command Phase 4 interactive path) —
    /// ConPtyCommandSession lives in Shell precisely so these can run offline. Windows-only by
    /// nature; generous timeouts because conhost startup is measured in hundreds of ms.
    /// </summary>
    public sealed class ConPtyCommandSessionTests
    {
        private sealed class Capture
        {
            private readonly StringBuilder _text = new StringBuilder();
            public void Add(string chunk) { lock (_text) _text.Append(chunk); }
            public string Text { get { lock (_text) return _text.ToString(); } }

            public async Task<string> WaitForAsync(string marker, TimeSpan timeout)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    var text = Text;
                    if (text.Contains(marker))
                        return text;
                    await Task.Delay(100);
                }
                return Text;
            }
        }

        private static IReadOnlyDictionary<string, string> InheritedEnvironmentWith(
            params (string Key, string Value)[] extra)
        {
            // The pty env block REPLACES the inherited environment, so a usable one must carry the
            // parent's variables (SystemRoot etc.) plus the extras — same as the dev-env capture.
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
                env[(string)e.Key] = (string)(e.Value ?? string.Empty);
            foreach (var (key, value) in extra)
                env[key] = value;
            return env;
        }

        [Fact]
        public async Task CommandOutputAndExitCodeAreCaptured()
        {
            var capture = new Capture();
            using var session = ConPtyCommandSession.TryStart(
                "cmd.exe /d /c echo pty-hello-marker", Environment.CurrentDirectory,
                environment: null, 100, 30, capture.Add);
            Assert.NotNull(session);

            var exit = await session!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exit);

            var text = await capture.WaitForAsync("pty-hello-marker", TimeSpan.FromSeconds(5));
            Assert.Contains("pty-hello-marker", text);
        }

        [Fact]
        public async Task NonZeroExitCodeIsReported()
        {
            var capture = new Capture();
            using var session = ConPtyCommandSession.TryStart(
                "cmd.exe /d /c exit 42", Environment.CurrentDirectory,
                environment: null, 80, 25, capture.Add);
            Assert.NotNull(session);
            Assert.Equal(42, await session!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        }

        [Fact]
        public async Task EnvironmentBlockIsApplied()
        {
            var capture = new Capture();
            using var session = ConPtyCommandSession.TryStart(
                "cmd.exe /d /c echo [%CWKT_PTY_TEST%]", Environment.CurrentDirectory,
                InheritedEnvironmentWith(("CWKT_PTY_TEST", "env-marker-xyz")), 100, 30, capture.Add);
            Assert.NotNull(session);

            await session!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var text = await capture.WaitForAsync("[env-marker-xyz]", TimeSpan.FromSeconds(5));
            Assert.Contains("[env-marker-xyz]", text);
        }

        [Fact]
        public async Task TypedInputReachesTheProcess()
        {
            // The whole point of the pty: an interactive cmd session accepts typed input. The
            // typed line is echoed by the console AND its output appears.
            var capture = new Capture();
            using var session = ConPtyCommandSession.TryStart(
                "cmd.exe /d /q /k", Environment.CurrentDirectory,
                environment: null, 100, 30, capture.Add);
            Assert.NotNull(session);

            // Wait for the prompt so cmd is ready to read.
            await capture.WaitForAsync(">", TimeSpan.FromSeconds(10));
            session!.WriteInput(Encoding.UTF8.GetBytes("echo typed-input-marker\r"));
            var text = await capture.WaitForAsync("typed-input-marker", TimeSpan.FromSeconds(10));
            Assert.Contains("typed-input-marker", text);

            session.WriteInput(Encoding.UTF8.GetBytes("exit\r"));
            Assert.Equal(0, await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        }

        [Fact]
        public void UnstartableCommandReturnsNull()
        {
            var session = ConPtyCommandSession.TryStart(
                "definitely-not-a-real-executable-xyz.exe", Environment.CurrentDirectory,
                environment: null, 80, 25, _ => { });
            Assert.Null(session);
        }

        [Fact]
        public async Task KillTerminatesAHungProcess()
        {
            var capture = new Capture();
            using var session = ConPtyCommandSession.TryStart(
                "cmd.exe /d /q /k", Environment.CurrentDirectory, // interactive: never exits alone
                environment: null, 80, 25, capture.Add);
            Assert.NotNull(session);
            await capture.WaitForAsync(">", TimeSpan.FromSeconds(10));

            session!.Kill();
            var exit = await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotEqual(0, exit);
        }
    }

    public sealed class AnsiTextTests
    {
        [Theory]
        [InlineData("plain text", "plain text")]
        [InlineData("keep\r\nlines\r\n", "keep\r\nlines\r\n")]
        [InlineData("\x1b[32mgreen\x1b[0m plain", "green plain")]
        [InlineData("\x1b[1;31;40mstyled\x1b[m", "styled")]
        // NB: never write "\x07b" — C#'s \x escape is variable-length and eats following hex
        // digits ("\x07b" is '{'); \a is the safe BEL spelling.
        [InlineData("\x1b]0;window title\aafter", "after")]            // OSC + BEL
        [InlineData("\x1b]0;title\x1b\\after", "after")]               // OSC + ST
        [InlineData("\x1b[2J\x1b[H\x1b[?25lcleared", "cleared")]       // clear/cursor/private modes
        [InlineData("a\ab", "ab")]                                     // stray BEL
        [InlineData("\x1b(Bcharset", "charset")]                       // two-char escape
        [InlineData("", "")]
        public void Strip_RemovesSequencesKeepsText(string input, string expected)
            => Assert.Equal(expected, AnsiText.Strip(input));

        [Fact]
        public void Strip_NullYieldsEmpty() => Assert.Equal(string.Empty, AnsiText.Strip(null));

        [Fact]
        public void Strip_TruncatedSequenceAtEndDoesNotThrow()
            => Assert.Equal("text", AnsiText.Strip("text\x1b["));
    }
}
