using System;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Shell;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// Mirrors agent command output into a real VS terminal pane ("Code Wicket Terminal") via
    /// VS's brokered ITerminalService — cosmetics ON TOP of the chat-row output, never the primary
    /// path (the service is internal/undocumented). Grown from
    /// the Phase 0 spike (verified in Visual Studio, 2026-07-17). Behavior contract:
    /// - Lazy: the pane is created on the first write, never at startup (first creation takes ~10s
    ///   loading the terminal package; the TerminalMirrorStream buffers through it, bounded).
    /// - Quiet, but never silent: every failure logs once, tells the host (so the chat can say the
    ///   mirror is unavailable — issue #51), and disables the mirror for the session; chat output
    ///   itself is unaffected.
    /// - Recoverable: the user closing the pane (TerminalClosed) just resets; the next write
    ///   recreates. Toggling Enabled off closes the pane; back on recreates on the next write.
    /// The terminal contracts are bound at RUNTIME via <see cref="VsTerminalService"/> — never at
    /// compile time, or the feature's presence would depend on the build machine having VS (that was
    /// issue #51: the released VSIX had the mirror compiled out entirely).
    /// </summary>
    internal sealed class TerminalMirror : IDisposable
    {
        private const string PaneName = Branding.TerminalPaneName;

        private readonly Action<string> _log;
        private readonly Action<string> _onUnavailable;
        private readonly object _gate = new object();

        private VsTerminalService _service;      // acquired once, kept for the session
        private TerminalMirrorStream _stream;    // fresh per pane incarnation
        private Guid _terminalId;
        private Task _creation;                  // single-flight pane creation; null = no pane pending/alive

        // The ONE connect, shared by every pane creation for the life of this mirror. Separate from
        // _creation on purpose: _creation is retired whenever the pane is (a toggle in Options, or the
        // user closing it) WITHOUT cancelling or awaiting the task still running, so two creations can
        // legitimately be in flight — and the `if (_service is null)` check below was then racing, with
        // both passing it. The comment on that line says "only this single-flight creation task ever
        // writes the field", which is the invariant that could not hold. The loser's proxy was
        // overwritten and leaked, its TerminalClosed/TerminalResized handlers still attached and never
        // disposed. The duplicate PANE is already caught downstream by the ReferenceEquals(_stream,
        // stream) orphan check; the proxy was not, so the connect is what has to be single-flight.
        private Task<VsTerminalService> _serviceConnect;
        private bool _enabled;
        private bool _failedForSession;          // a hard failure switches the mirror off until restart
        private bool _disposed;

        /// <param name="log">Diagnostic sink (engine.log).</param>
        /// <param name="onUnavailable">
        /// Called once, with a short reason, when the mirror gives up for the session — the host
        /// surfaces it in the chat so "the terminal never shows" is self-diagnosing rather than a
        /// log-only mystery.
        /// </param>
        public TerminalMirror(Action<string> log, Action<string> onUnavailable = null)
        {
            _log = log ?? (_ => { });
            _onUnavailable = onUnavailable ?? (_ => { });
        }

        /// <summary>Config-driven switch. Off closes any open pane; on takes effect at the next write.</summary>
        public bool Enabled
        {
            get { lock (_gate) return _enabled; }
            set
            {
                Guid closeId;
                lock (_gate)
                {
                    if (_enabled == value)
                        return;
                    _enabled = value;
                    if (value)
                        return;
                    closeId = ResetPaneLocked();
                }
                CloseQuietly(closeId);
            }
        }

        /// <summary>
        /// Pane keystrokes (the user typing into the terminal) are forwarded here — Phase 4 wires
        /// this to the active run_command pty. Null = input swallowed. Read live per keystroke, so
        /// wiring order doesn't matter.
        /// </summary>
        public Action<byte[]> UserInputSink { get; set; }

        /// <summary>Raised (columns, rows) when the pane is resized — forwarded to the active pty.</summary>
        public Action<int, int> SizeChanged { get; set; }

        /// <summary>Separator line between mirrored commands (title is sanitized against ANSI injection).</summary>
        public void WriteHeader(string title) => WriteRaw(TerminalMirrorText.Header(title));

        /// <summary>Mirror a chunk of command output (LF-normalized for the terminal).</summary>
        public void Write(string text) => WriteRaw(TerminalMirrorText.NormalizeNewlines(text));

        /// <summary>Terminal-ready text (a pty's VT stream) — written verbatim, no normalization.</summary>
        public void WriteRaw(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            lock (_gate)
            {
                if (!_enabled || _failedForSession || _disposed)
                    return;

                // The stream exists from the first write onward and doubles as the buffer while the
                // pane is still being created, so nothing written during the ~10s creation is lost.
                if (_stream is null)
                    _stream = new TerminalMirrorStream
                    {
                        // Live property read per keystroke; the sink itself must never block.
                        OnUserInput = data => UserInputSink?.Invoke(data),
                    };
                _stream.Push(text);

                if (_creation is null)
                    _creation = CreatePaneAsync(_stream);
            }
        }

        private async Task CreatePaneAsync(TerminalMirrorStream stream)
        {
            try
            {
                if (_service is null)
                {
                    Task<VsTerminalService> connect;
                    lock (_gate)
                        connect = _serviceConnect ??= VsTerminalService.ConnectAsync(CancellationToken.None);

                    var service = await connect;
                    // Which of the two acquisition shapes answered. Named on success, not just on
                    // failure: when VS moves the service again the useful question is which shape THIS
                    // install offered, and only a log from a working session establishes the baseline.
                    _log($"[terminal-mirror] ITerminalService acquired via {service.AcquiredVia}");
                    // One subscription for the session; recreation reuses the same proxy. Assigning
                    // the same instance twice is now harmless — every creation awaits the same task, so
                    // there is only ever one proxy to subscribe.
                    service.TerminalClosed = OnTerminalClosed;
                    service.TerminalResized = OnTerminalResized;
                    _service = service;
                }

                // allowUserInput true so mouse selection works — copy (right-click or the toolbar
                // Copy button) copies the selection, and selection is an input gesture, so with
                // input off the pane is uncopyable (Copy no-ops without a selection). The
                // 2026-07-17 input-freeze is understood and fixed: a keystroke's WriteAsync
                // deadlocked on base Stream's shared async semaphore behind the parked read loop —
                // TerminalMirrorStream now overrides every async/APM member so nothing touches
                // that semaphore and writes complete instantly (typed input is swallowed; the
                // pane can't be driven).
                // Created WITHOUT focus (issue #124) — the pane used to appear mid-sentence and take
                // the keyboard out of the chat box. ShowNoActivateAsync is still what makes it
                // VISIBLE; it just never was the thing that kept the focus, since creation had
                // already taken it.
                var id = await _service.CreateRendererAsync(
                    stream, PaneName, allowUserInput: true, autoResize: true,
                    cancellationToken: CancellationToken.None);
                await _service.ShowNoActivateAsync(id, CancellationToken.None);

                var orphaned = false;
                lock (_gate)
                {
                    // Disabled or reset while creation was in flight -> this pane is an orphan.
                    if (!_enabled || _disposed || !ReferenceEquals(_stream, stream))
                        orphaned = true;
                    else
                        _terminalId = id;
                }
                if (orphaned)
                    CloseQuietly(id);
                else if (_service.FocusSuppressionUnavailable is { } why)
                    // Says so out loud: the pane taking the keyboard reads to the user as a bug in the
                    // chat box, and nothing else in the log would name the terminal as the cause.
                    _log($"[terminal-mirror] pane created ({id}) WITH focus — could not suppress: {why}");
                else
                    _log($"[terminal-mirror] pane created ({id})");
            }
            catch (Exception ex)
            {
                Guid closeId;
                lock (_gate)
                {
                    _failedForSession = true;
                    closeId = ResetPaneLocked();
                }
                CloseQuietly(closeId);
                _log("[terminal-mirror] disabled for this session: " + ex);
                // Short reason for the chat notice; the log above keeps the full exception.
                try { _onUnavailable(ex.Message); }
                catch { /* the notice is best-effort */ }
            }
        }

        private void OnTerminalResized(Guid terminalId, int columns, int rows)
        {
            lock (_gate)
            {
                if (terminalId != _terminalId || _terminalId == Guid.Empty)
                    return;
            }
            try { SizeChanged?.Invoke(columns, rows); }
            catch { /* size forwarding is cosmetic */ }
        }

        private void OnTerminalClosed(Guid terminalId)
        {
            lock (_gate)
            {
                if (terminalId != _terminalId || _terminalId == Guid.Empty)
                    return;
                // User closed the pane: retire this incarnation quietly; the next write recreates.
                _stream?.Dispose();
                _stream = null;
                _terminalId = Guid.Empty;
                _creation = null;
            }
        }

        // Retires the current pane state and returns the terminal id to close (Guid.Empty if none).
        // Callers close outside the lock.
        private Guid ResetPaneLocked()
        {
            var id = _terminalId;
            _stream?.Dispose();
            _stream = null;
            _terminalId = Guid.Empty;
            _creation = null;
            return id;
        }

        private void CloseQuietly(Guid terminalId)
        {
            if (terminalId == Guid.Empty)
                return;
            var service = _service;
            if (service is null)
                return;
            _ = Task.Run(async () =>
            {
                try { await service.CloseAsync(terminalId, CancellationToken.None); }
                catch { /* pane may already be gone; never surface */ }
            });
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                // Disposing the stream EOFs the renderer; the pane itself is left for VS to reclaim
                // (an async CloseAsync here would defeat ISB001's dispose-flow analysis, and an
                // orphaned pane on window teardown is harmless for a cosmetic mirror).
                ResetPaneLocked();
            }
            // Unsubscribes the events and disposes the brokered proxy.
            try { _service?.Dispose(); }
            catch { /* proxy may already be dead */ }
            _service = null;
        }
    }
}
