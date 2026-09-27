using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Ipc;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The plain-test pump keeps a body's context-capturing continuations on one thread: what lets the
    /// session and delivery tests assert ordering without a dispatcher.
    /// </summary>
    public sealed class SingleThreadPumpTests
    {
        /// <summary>
        /// Both awaits genuinely complete on pool threads (a timer, and <c>Task.Run</c>), so a pump that
        /// did not marshal back would see its continuations land elsewhere.
        /// </summary>
        [Fact]
        public void ContinuationsResumeOnThePumpThread()
        {
            var pumpThread = Environment.CurrentManagedThreadId;
            var seen = new List<int>();

            SingleThreadPump.Run(async () =>
            {
                await Task.Delay(1);
                seen.Add(Environment.CurrentManagedThreadId);
                await Task.Run(() => { });
                seen.Add(Environment.CurrentManagedThreadId);
            });

            Assert.Equal(new[] { pumpThread, pumpThread }, seen);
        }

        [Fact]
        public void ABodysExceptionReachesTheCaller()
        {
            var failure = Assert.Throws<InvalidOperationException>(() => SingleThreadPump.Run(async () =>
            {
                await Task.Delay(1);
                throw new InvalidOperationException("from the body");
            }));

            Assert.Equal("from the body", failure.Message);
        }

        /// <summary>
        /// A body left waiting on a start the script never completes FAILS, inside its budget, and says
        /// what it was waiting on - rather than hanging dotnet test, and with it prove-check and
        /// prove-manifest, with no verdict. Run on its own thread with a bounded join, because the defect
        /// this guards is a hang: with the budget removed the pump never returns, and the join is what
        /// turns that into a failure instead of a stalled suite.
        /// </summary>
        [Fact]
        public void ABodyLeftWaitingFailsWithinItsBudgetAndSaysWhatItWaitsOn()
        {
            var engine = new ScriptedEngine { StartNeverCompletes = true };
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    SingleThreadPump.Run(
                        async () => await engine.StartSessionAsync(
                            new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null)),
                        TimeSpan.FromMilliseconds(200),
                        engine.DescribePending);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            })
            {
                IsBackground = true,
            };

            thread.Start();

            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "the pump never returned: a body left waiting hangs the run");
            var timeout = Assert.IsType<TimeoutException>(failure);
            Assert.Contains("a session start that never completes", timeout.Message, StringComparison.Ordinal);
        }
    }
}
