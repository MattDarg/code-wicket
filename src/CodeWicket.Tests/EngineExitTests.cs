using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Shell;
using Nerdbank.Streams;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Issue #299, through the real <see cref="EngineClient"/> and a real process: an engine that exits
    /// before anyone speaks to it must turn every later call into an <see cref="EngineExitedException"/>
    /// carrying its exit code and stderr, not StreamJsonRpc's "connection … lost".
    /// <para>The stand-in is a <c>.cmd</c> that writes one line of .NET's refusal to stderr and exits with
    /// the apphost's own code — measured to report the full 32-bit <c>-2147450730</c> through
    /// <c>Process.ExitCode</c>, the same as the real apphost with its framework forced to 99.0.0. The real
    /// apphost is exercised by the Desktop <c>--screenshot</c> repro recorded on the issue; this needs no
    /// build layout.</para>
    /// </summary>
    public sealed class EngineExitTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-engine-exit-" + Guid.NewGuid().ToString("N"));

        public EngineExitTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        /// <summary>
        /// The apphost is gone in 16–42 ms, and the VSIX attaches seconds later — so this attaches only
        /// AFTER the process has exited. An exit watched from the attach (a <c>Process.Exited</c> handler
        /// added then) is never told, which is the shape this pins.
        /// </summary>
        [Fact]
        public async Task AnEngineThatExitedBeforeAttachIsReportedByItsExit()
        {
            var engine = Script("missing-runtime.cmd",
                "@echo You must install or update .NET to run this application. 1>&2",
                "@echo Framework: 'Microsoft.NETCore.App', version '10.0.0' (x64) 1>&2",
                "@exit /b -2147450730");

            var pending = EngineClient.Spawn(engine);
            Assert.True(pending.Process.WaitForExit(10_000), "stand-in did not exit");

            using var client = EngineClient.Attach(pending, new StubIdeServices(_dir));

            var ex = await Assert.ThrowsAsync<EngineExitedException>(() => client.ListProvidersAsync());
            Assert.Equal(EngineExitDescription.FrameworkMissingFailure, ex.Exit.ExitCode);
            Assert.Equal(EngineExitKind.FrameworkMissing, ex.Exit.Kind);
            Assert.Contains("You must install or update .NET to run this application.", ex.Exit.StderrTail.Select(l => l.Trim()));
            Assert.Contains("Framework: 'Microsoft.NETCore.App', version '10.0.0' (x64)", ex.Exit.StderrTail.Select(l => l.Trim()));

            // Every call after, not only the first: the send path reported "connection lost" each time.
            var again = await Assert.ThrowsAsync<EngineExitedException>(() => client.PromptAsync("hello"));
            Assert.Equal(EngineExitDescription.FrameworkMissingFailure, again.Exit.ExitCode);
        }

        /// <summary>
        /// The call is on the wire BEFORE the exit, the shape of an engine that dies while the first
        /// request is out, and a real process takes it end to end.
        /// <para><b>This does not pin the wait for the exit record, and cannot.</b> With that wait deleted
        /// it still passed (<c>prove-check.ps1</c>, 2026-09-12): on this machine the record beats the
        /// connection-lost exception to the check every time. A grandchild meant to hold stderr open
        /// past the exit was tried and inherited stdout too, so the call failed no earlier than the
        /// record. <see cref="ALostConnectionIsHeldForAnExitRecordedAfterIt"/> pins the wait, with the
        /// order made rather than hoped for.</para>
        /// </summary>
        [Fact]
        public async Task ACallInFlightWhenTheEngineExitsIsReportedByTheExit()
        {
            var engine = Script("slow-refusal.cmd",
                "@echo You must install or update .NET to run this application. 1>&2",
                "@ping -n 2 127.0.0.1 >nul",
                "@exit /b -2147450730");

            var pending = EngineClient.Spawn(engine);
            using var client = EngineClient.Attach(pending, new StubIdeServices(_dir));
            var call = client.ListProvidersAsync();
            Assert.False(pending.Process.HasExited, "the stand-in exited before the call was on the wire, so this measured nothing");

            var ex = await Assert.ThrowsAsync<EngineExitedException>(() => call);
            Assert.Equal(EngineExitDescription.FrameworkMissingFailure, ex.Exit.ExitCode);
            Assert.Contains("You must install or update .NET to run this application.", ex.Exit.StderrTail.Select(l => l.Trim()));
        }

        /// <summary>
        /// A lost connection is HELD for the exit rather than raced against it. On a real process the
        /// pipe closes a moment before the exit is recorded — the record waits for stderr's end, so
        /// .NET's account is whole — and a check that raced the two would report StreamJsonRpc's
        /// "connection … lost" whenever the exception won.
        /// <para>The real transport over in-memory streams, with the exit recorded half a second AFTER
        /// the connection is gone: the order a process cannot be made to produce on demand. Without
        /// the wait the call surfaces as the raw <c>ConnectionLostException</c>.</para>
        /// </summary>
        [Fact]
        public async Task ALostConnectionIsHeldForAnExitRecordedAfterIt()
        {
            var (shellEnd, engineEnd) = FullDuplexStream.CreatePair();
            var watch = new EngineClient.ProcessWatch();
            using var client = EngineClient.Attach(shellEnd, shellEnd, new StubIdeServices(_dir), watch: watch);

            var call = client.ListProvidersAsync();
            engineEnd.Dispose(); // the engine's end of the pipe is gone

            await Task.Delay(500);
            watch.Exited.TrySetResult(new EngineExit(
                EngineExitDescription.FrameworkMissingFailure, new[] { "You must install or update .NET to run this application." }));

            var ex = await Assert.ThrowsAsync<EngineExitedException>(() => call);
            Assert.Equal(EngineExitDescription.FrameworkMissingFailure, ex.Exit.ExitCode);
            Assert.IsType<StreamJsonRpc.ConnectionLostException>(ex.InnerException);
        }

        /// <summary>
        /// The classification is written to engine.log unconditionally, BELOW .NET's own lines, so a log
        /// collected from a user's machine answers the question without anyone decoding an HRESULT.
        /// </summary>
        [Fact]
        public async Task TheExitIsClassifiedInTheLogBelowDotnetsOwnLines()
        {
            var engine = Script("missing-runtime-logged.cmd",
                "@echo You must install or update .NET to run this application. 1>&2",
                "@exit /b -2147450730");
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();

            var pending = EngineClient.Spawn(engine, log: lines.Enqueue);
            using var client = EngineClient.Attach(pending, new StubIdeServices(_dir), log: lines.Enqueue);
            await Assert.ThrowsAsync<EngineExitedException>(() => client.ListProvidersAsync());

            var log = lines.ToList();
            var dotnet = log.FindIndex(l => l.Contains("You must install or update .NET"));
            var classified = log.FindIndex(l => l.Contains(
                "engine exit: exit code 0x80008096 (-2147450730) (FrameworkMissingFailure): .NET found no compatible version"));
            Assert.True(dotnet >= 0, "the stand-in's stderr never reached the log");
            Assert.True(classified > dotnet, $"classification at {classified}, .NET's line at {dotnet}:\n{string.Join("\n", log)}");
        }

        [Fact]
        public async Task AnOrdinaryExitIsStillAnExitButClaimsNoCause()
        {
            var engine = Script("crash.cmd", "@echo [engine] unhandled 1>&2", "@exit /b 3");

            var pending = EngineClient.Spawn(engine);
            Assert.True(pending.Process.WaitForExit(10_000), "stand-in did not exit");
            using var client = EngineClient.Attach(pending, new StubIdeServices(_dir));

            var ex = await Assert.ThrowsAsync<EngineExitedException>(() => client.ListProvidersAsync());
            Assert.Equal(3, ex.Exit.ExitCode);
            Assert.Equal(EngineExitKind.Other, ex.Exit.Kind);
        }

        private string Script(string name, params string[] lines)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
            return path;
        }
    }
}
