using System;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using CodeWicket.Shell.Sessions;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Runs a test body on an STA thread — WPF text elements, the clipboard and anything touching a
    /// <c>Dispatcher</c> all demand one, and xunit's own threads are MTA.
    ///
    /// <para><b>Only one body runs at a time, process-wide, and that gate is the point of this type.</b>
    /// xunit parallelises test COLLECTIONS, and by default every test class is its own collection — so
    /// two STA bodies from two classes ran concurrently, on two STA threads, against state that is
    /// global to the process and in one case to the whole Windows session. Measured, not theorised:
    /// <c>EmojiTextTests.ClipboardKeepsParagraphBreaks</c> copied its own text and read back
    /// <c>"Markdown formatting is unavailable — …"</c>, the render-firewall notice belonging to a
    /// different test class. The Windows clipboard is per-USER, not per-process.</para>
    ///
    /// <para>It failed roughly one full run in four, always in a different place, and always passed on
    /// a re-run — which is what made it expensive: three separate sessions chased it as a product bug,
    /// two of them destroyed the evidence by re-running before reading it, and one wrote and discarded
    /// a scheduler change built on the theory that gate CONCURRENCY was to blame. It was concurrency,
    /// but inside the test process rather than between gate processes.</para>
    ///
    /// <para><b>Why the gate lives here rather than in a collection attribute.</b> The alternative is
    /// <c>[CollectionDefinition(DisableParallelization = true)]</c> applied to each STA class — 22 of
    /// them today, and a 23rd written without the attribute silently reintroduces the race, which is
    /// the failure mode this whole class of bug already has. A gate inside the one helper every STA
    /// test already calls cannot be forgotten by a test that uses the helper. The blunter fix
    /// (<c>parallelizeTestCollections: false</c> for the assembly) also works and was measured at
    /// 4s → 14s for the full suite; this keeps the ~1200 non-STA tests parallel and pays only for the
    /// STA ones being serial, which they now are anyway.</para>
    ///
    /// <para><b>Do not call this from inside an STA body.</b> The gate is held by the calling thread
    /// while the STA thread runs, so a nested call would wait on a lock its own caller holds and never
    /// return. No test nests today; a test that needs to would want one body, not two.</para>
    /// </summary>
    internal static class StaTest
    {
        // A semaphore rather than a lock: the wait is on one thread and the work on another, so there
        // is no owning-thread relationship to express, and Monitor's re-entrancy would not help anyway
        // (the nested caller would be the STA thread, which never holds it).
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        /// <param name="withDispatcherContext">
        /// Installs a <see cref="DispatcherSynchronizationContext"/> on the STA thread. Needed by tests
        /// whose subject awaits with <c>ConfigureAwait(true)</c> and therefore resumes on the UI
        /// context: a bare STA thread has none, so the continuation would run on the thread pool and a
        /// dispatcher drain would never see it. Opt-in rather than universal, because it changes when
        /// continuations run and only two classes were written against it.
        /// </param>
        /// <remarks>
        /// <para><b>An invariant breach fails the test, wherever it was raised.</b> The body runs inside
        /// an <see cref="Invariants"/> scope opened on the STA thread; it is an execution-context value,
        /// so it flows into every dispatcher operation and continuation the body starts, whichever
        /// thread resumes it, and any breach the view-model reports through
        /// <see cref="Invariants.Ambient"/> fails the run after the body. A test that expects one does
        /// not use this helper for that breach.</para>
        /// <para><b>Only work the body WAITS for is attributed to it</b>: the thread is joined as soon as
        /// the body returns, so a breach raised later by work it did not wait on is not seen here - the
        /// existing <c>Drain</c> discipline is what keeps that covered.</para>
        /// </remarks>
        public static void Run(Action action, bool withDispatcherContext = false)
        {
            Gate.Wait();
            try
            {
                Exception? failure = null;
                var breaches = new BreachRecorder();
                var thread = new Thread(() =>
                {
                    if (withDispatcherContext)
                    {
                        SynchronizationContext.SetSynchronizationContext(
                            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                    }

                    using (Invariants.OpenScope(breaches))
                    {
                        try { action(); }
                        catch (Exception ex) { failure = ex; }
                    }
                });

                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();

                if (failure is not null)
                    throw new Xunit.Sdk.XunitException("STA test body failed: " + failure);

                ThrowIfBreached(breaches);
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// Like <see cref="Run"/>, but gives up waiting after <paramref name="budget"/> and returns
        /// whether the body finished. Exists for issue #177, whose subject is a <b>hang</b>: the
        /// defect it guards makes <see cref="Run"/>'s unbounded <c>Join</c> never return, so a test
        /// written on it would hang the suite instead of failing it — and a gate that hangs tells
        /// <c>prove-check.ps1</c> nothing, which is the instrument every "load-bearing" claim here
        /// is measured with.
        ///
        /// <para><b>An overrunning body is abandoned, not stopped, and that is the defect itself.</b>
        /// A backtracking <c>Regex</c> cannot be cancelled or interrupted, so there is nothing to
        /// cancel it with; the thread is marked background so it can never hold the test process
        /// open, and the gate is released so the rest of the suite still runs. It burns a core until
        /// the process exits, which only ever happens on a run that is already reporting a failure.
        /// </para>
        /// </summary>
        public static bool RunWithin(Action action, TimeSpan budget)
        {
            Gate.Wait();
            try
            {
                Exception? failure = null;
                var breaches = new BreachRecorder();
                var done = new ManualResetEventSlim(false);
                var thread = new Thread(() =>
                {
                    using (Invariants.OpenScope(breaches))
                    {
                        try { action(); }
                        catch (Exception ex) { failure = ex; }
                        finally { done.Set(); }
                    }
                })
                {
                    IsBackground = true,
                };

                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();

                if (!done.Wait(budget))
                    return false;

                if (failure is not null)
                    throw new Xunit.Sdk.XunitException("STA test body failed: " + failure);

                ThrowIfBreached(breaches);
                return true;
            }
            finally
            {
                Gate.Release();
            }
        }

        private static void ThrowIfBreached(BreachRecorder breaches)
        {
            var recorded = breaches.Drain();
            if (recorded.Count == 0)
                return;

            throw new Xunit.Sdk.XunitException(
                $"{recorded.Count} invariant breach(es) reported during the STA test body:"
                + string.Concat(recorded.Select(b => Environment.NewLine + "  - " + b)));
        }
    }
}
