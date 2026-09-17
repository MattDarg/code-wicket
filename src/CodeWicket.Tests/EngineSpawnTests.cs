using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// <see cref="EngineClient.Spawn"/> exists so the VS host can get the engine running before it has
    /// an <see cref="Core.Ide.IIdeServices"/> to attach with — the engine starts each backend's model
    /// probe the moment it is up, and that probe is ~1.5 s of kiro-cli the first session otherwise waits
    /// for. The cost of the split is a window where the process is UNOWNED: no <c>EngineClient</c>
    /// exists, so no <c>Dispose</c> can reach it, and everything in between (VsIdeServices.CreateAsync,
    /// the permission chain, the chat view) can throw.
    ///
    /// So <see cref="EngineClient.PendingEngine.Kill"/> is the reaper for a startup that fails after
    /// the spawn, and "the engine is left running behind a window that never opened" is an invisible
    /// failure — the pane shows its error, and the orphan is only found in Task Manager. Driven against
    /// cmd.exe rather than the real engine so it needs no build layout.
    ///
    /// <para><b>What these pin, precisely.</b> The CONTRACT — after <c>Kill</c> the process is gone —
    /// and not the kill itself. Found by injecting the bug: with the <c>Process.Kill()</c> call deleted
    /// these still passed, because disposing the <see cref="Process"/> closes the redirected stdin and
    /// cmd.exe exits on EOF. **So does the engine**, whose <c>host.Completion</c> finishes when the
    /// connection closes — which is worth knowing on its own: the orphan risk is smaller than it looks,
    /// and the explicit kill is defence for a child that ignores EOF rather than the only thing
    /// standing between us and a stray process. That residual is NOT covered: every stand-in available
    /// here exits on stdin EOF too, so there is nothing to distinguish the two mechanisms with.</para>
    /// </summary>
    public sealed class EngineSpawnTests
    {
        // Redirected stdin with nothing written to it, so it sits alive until killed — a stand-in for
        // an engine waiting to be spoken to.
        private static string IdleExecutable =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        [Fact]
        public void Kill_ReapsAProcessThatWasNeverAttached()
        {
            Assert.True(File.Exists(IdleExecutable), $"test needs {IdleExecutable}");

            var pending = EngineClient.Spawn(IdleExecutable);
            var pid = pending.Process.Id;
            Assert.False(pending.Process.HasExited);

            pending.Kill();

            // Polled: Kill returns before Windows has finished tearing the process down. Would also
            // pass on the EOF path alone — see the type comment for what that does and doesn't pin.
            Assert.True(Gone(pid), $"pid {pid} still running after Kill");
        }

        [Fact]
        public void Kill_IsSafeTwice_AndAfterTheProcessAlreadyExited()
        {
            // The failure path can run in a catch that is itself unwinding, so a second Kill (or one
            // against a process that died on its own) must not throw and mask the original exception.
            var pending = EngineClient.Spawn(IdleExecutable);
            var pid = pending.Process.Id;

            pending.Kill();
            Assert.True(Gone(pid));

            pending.Kill(); // must be a no-op, not an ObjectDisposedException
        }

        [Fact]
        public void Spawn_RejectsAMissingExecutable_WithTheSameErrorLaunchGave()
        {
            // The guard moved out of Launch into Spawn. Without it Process.Start throws a
            // Win32Exception instead, and the startup-failure pane would report "The system cannot find
            // the file specified" with no path in it.
            var missing = Path.Combine(Path.GetTempPath(), "cwkt-no-such-engine-" + Guid.NewGuid().ToString("N") + ".exe");
            Assert.Throws<FileNotFoundException>(() => EngineClient.Spawn(missing));
        }

        private static bool Gone(int pid)
        {
            for (var i = 0; i < 100; i++)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.HasExited)
                        return true;
                }
                catch (ArgumentException)
                {
                    return true; // no such process
                }
                Thread.Sleep(50);
            }
            return false;
        }
    }
}
