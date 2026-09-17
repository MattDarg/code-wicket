using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CodeWicket.Ide
{
    /// <summary>
    /// The AD7 debug-event subscription (issue #73, rung 3) — and the instrument that
    /// measures what subscribing actually costs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because there is no accessor.</b> Enumerated unfiltered, <c>IVsDebugger</c>
    /// through <c>IVsDebugger10</c> offer <c>GetMode</c>, the two advise pairs,
    /// <c>GetDataTipValue</c>/<c>CreateDataTip</c>, and launch/breakpoint/ENC plumbing — no thread
    /// accessor and no frame accessor anywhere. So the only route to an <see cref="IDebugThread2"/>,
    /// and therefore to a stack frame and an expression context, is to be told about one.
    /// </para>
    /// <para>
    /// <b>The subscription is the cost being weighed, so it reports itself.</b> Every event is counted
    /// by name, because "it delivers every debug event for the life of the IDE" was an assertion of
    /// mine and deserves a number rather than a repeat. The handler stays trivial — recognise, cache,
    /// return <c>S_OK</c> — since it runs on the debugger's own event thread and anything slow here
    /// slows the debuggee.
    /// </para>
    /// <para>
    /// <b>Two things this deliberately measures rather than assumes.</b> Events arrive on the
    /// debugger's event thread while the probe reads the cached thread from the UI thread, so whether
    /// a cross-thread <see cref="IDebugThread2"/> call is legal at all is a real question — an RPC or
    /// wrong-thread failure would show up as an HRESULT in the report, which is exactly the property
    /// that makes AD7 preferable to the layer beneath it. And a stop that happened BEFORE we advised
    /// leaves no cached thread, which is why <see cref="AdvisedAt"/> is reported beside the counts:
    /// "no thread" and "no stop since we started listening" are different answers.
    /// </para>
    /// </remarks>
    public sealed class Ad7DebugEvents : IDebugEventCallback2
    {
        private static readonly object Gate = new object();
        private static Ad7DebugEvents _instance;
        private static IVsDebugger _debugger;

        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);
        private IDebugThread2 _lastThread;
        private string _lastStopEvent;
        private int _total;
        private int _stops;

        /// <summary>When we started listening — so "no stop seen" can be told from "no stop happened".</summary>
        public static DateTime? AdvisedAt { get; private set; }

        public static bool IsAdvised => _instance != null;

        /// <summary>
        /// Advised at PACKAGE LOAD rather than when the chat window opens. The window is not the
        /// interesting moment: a user already stopped at a breakpoint when they open the pane would
        /// otherwise leave us with no thread until they stopped again, which reads as the feature being
        /// broken.
        /// </summary>
        public static void Advise()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            lock (Gate)
            {
                if (_instance != null)
                    return;

                _debugger = Package.GetGlobalService(typeof(SVsShellDebugger)) as IVsDebugger;
                if (_debugger is null)
                    return;

                var callback = new Ad7DebugEvents();
                if (ErrorHandler.Succeeded(_debugger.AdviseDebugEventCallback(callback)))
                {
                    _instance = callback;
                    AdvisedAt = DateTime.Now;
                }
            }
        }

        public static void Unadvise()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            lock (Gate)
            {
                if (_instance is null || _debugger is null)
                    return;

                try { _debugger.UnadviseDebugEventCallback(_instance); }
                catch (Exception) { /* shutting down */ }
                _instance = null;
                _debugger = null;
            }
        }

        /// <summary>The thread of the most recent event that carried one, or null.</summary>
        public static IDebugThread2 LastThread
        {
            get { lock (Gate) { return _instance?._lastThread; } }
        }

        /// <summary>The most recent event that looked like a STOP, or null.</summary>
        public static string LastStopEvent
        {
            get { lock (Gate) { return _instance?._lastStopEvent; } }
        }

        /// <summary>
        /// How many times execution has stopped since we started listening — the cheap half of a stop's
        /// identity.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It exists because a <c>&lt;debug-state&gt;</c> block the user pushed carries no expiry: read
        /// three turns later, after a debug run or a restart, it describes a stop that no longer exists and
        /// nothing in it says so. Observed live — a pushed block said <c>depth = 4</c> over four frames
        /// while the live pull said <c>depth = 2</c> over two, and the agent only caught it because it
        /// happened to have both. Paired with the process id, this makes that a fact to read rather than
        /// a coincidence to notice.
        /// </para>
        /// <para>
        /// A COUNT, not a timestamp: two stops seconds apart are still different stops, and the same stop
        /// an hour later is still the same one. A clock invites exactly the wrong comparison.
        /// </para>
        /// </remarks>
        public static int StopCount
        {
            get { lock (Gate) { return _instance?._stops ?? 0; } }
        }

        /// <summary>Event counts by name, busiest first, with the total.</summary>
        public static string DescribeTraffic()
        {
            lock (Gate)
            {
                if (_instance is null)
                    return "not advised";

                var lines = _instance._counts
                    .OrderByDescending(p => p.Value)
                    .Select(p => "       " + p.Value.ToString().PadLeft(5) + "  " + p.Key);
                return _instance._total + " event(s) since " + (AdvisedAt?.ToString("HH:mm:ss") ?? "?")
                       + Environment.NewLine + string.Join(Environment.NewLine, lines);
            }
        }

        /// <summary>
        /// The events that mean "execution has stopped and there is a frame to read". Matched by
        /// interface GUID because that is what <c>riidEvent</c> carries; anything unrecognised is
        /// counted under its raw GUID rather than dropped, so the traffic figure stays honest.
        /// </summary>
        private static readonly Dictionary<Guid, string> Known = new Dictionary<Guid, string>
        {
            { typeof(IDebugBreakpointEvent2).GUID, "Breakpoint*" },
            { typeof(IDebugStepCompleteEvent2).GUID, "StepComplete*" },
            { typeof(IDebugExceptionEvent2).GUID, "Exception*" },
            { typeof(IDebugEntryPointEvent2).GUID, "EntryPoint*" },
            { typeof(IDebugBreakEvent2).GUID, "Break*" },
            { typeof(IDebugModuleLoadEvent2).GUID, "ModuleLoad" },
            { typeof(IDebugThreadCreateEvent2).GUID, "ThreadCreate" },
            { typeof(IDebugThreadDestroyEvent2).GUID, "ThreadDestroy" },
            { typeof(IDebugOutputStringEvent2).GUID, "OutputString" },
            { typeof(IDebugProgramCreateEvent2).GUID, "ProgramCreate" },
            { typeof(IDebugProgramDestroyEvent2).GUID, "ProgramDestroy" },
            { typeof(IDebugLoadCompleteEvent2).GUID, "LoadComplete" },
        };

        /// <summary>Names ending in * are the ones that leave the debuggee stopped.</summary>
        private static bool IsStop(string name) => name != null && name.EndsWith("*", StringComparison.Ordinal);

        public int Event(
            IDebugEngine2 pEngine,
            IDebugProcess2 pProcess,
            IDebugProgram2 pProgram,
            IDebugThread2 pThread,
            IDebugEvent2 pEvent,
            ref Guid riidEvent,
            uint dwAttrib)
        {
            try
            {
                if (!Known.TryGetValue(riidEvent, out var name))
                    name = riidEvent.ToString("B");

                lock (Gate)
                {
                    _total++;
                    _counts.TryGetValue(name, out var count);
                    _counts[name] = count + 1;

                    // Cache the thread only from a STOP, which is what the sentence below always
                    // said and what the code did not do: a thread from a ThreadCreate is not a
                    // thread with a frame to read, and conflating them reports a confident wrong
                    // answer. The guard reached _lastStopEvent and not _lastThread, so a stop on
                    // thread A followed by an asynchronous ThreadCreate/ThreadDestroy/OutputString
                    // for thread B left LastThread naming B - and every frame reader goes through
                    // it: TopFrame, read_expression, execute_expression, and the ambient
                    // <debug-state> line. All threads are suspended at a stop, so B's stack reads
                    // cleanly and the wrong answer arrives looking entirely healthy.
                    if (pThread != null && IsStop(name))
                        _lastThread = pThread;
                    if (IsStop(name))
                    {
                        _stops++;
                        _lastStopEvent = name + " at " + DateTime.Now.ToString("HH:mm:ss");
                    }
                }
            }
            catch (Exception)
            {
                // This runs on the debugger's event thread. Never let our bookkeeping surface as a
                // debugging failure for the user.
            }

            return VSConstants.S_OK;
        }
    }
}
