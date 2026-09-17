using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using CodeWicket.Core.Ide;

namespace CodeWicket.Shell
{
    /// <summary>
    /// <see cref="ICommandPty"/> over Windows ConPTY (<c>CreatePseudoConsole</c>, Win10 1809+):
    /// the spawned command sees a real console (echo, isatty, working prompts) while we own both
    /// pty ends — output streams to a callback (the terminal-mirror pane + the agent capture),
    /// input comes from the pane's keystrokes, and timeout/cancel still tree-kills. In Shell so it
    /// stays VS-free and the net10 test project can exercise a REAL pty offline.
    /// Lifecycle gotcha encoded in Dispose: the pty's internal conhost holds the output pipe's
    /// write end, so the reader only sees EOF after <c>ClosePseudoConsole</c> — close order is
    /// pty first, then handles.
    /// </summary>
    public sealed class ConPtyCommandSession : ICommandPty
    {
        /// <summary>Factory shaped for <see cref="CommandPtyFactory"/>: null on ANY failure so the
        /// caller falls back to redirected pipes (older OS, policy, API errors).</summary>
        public static ICommandPty? TryStart(
            string commandLine, string workingDirectory,
            IReadOnlyDictionary<string, string>? environment,
            int columns, int rows, Action<string> onOutput)
        {
            try
            {
                return new ConPtyCommandSession(commandLine, workingDirectory, environment, columns, rows, onOutput);
            }
            catch
            {
                return null;
            }
        }

        private readonly object _gate = new object();
        private readonly IntPtr _pty;                 // HPCON
        private readonly IntPtr _attributeList;       // freed on dispose
        private readonly IntPtr _environmentBlock;    // freed on dispose (Zero when inheriting)
        private readonly SafeFileHandle _inputWrite;  // our end: keystrokes -> process
        private readonly SafeFileHandle _outputRead;  // our end: process VT output
        private readonly FileStream _input;
        private readonly IntPtr _processHandle;
        private readonly int _processId;
        private readonly TaskCompletionSource<int> _exited =
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RegisteredWaitHandle _exitWait;
        private readonly ManualResetEvent _exitEvent;
        private bool _readerOwnsOutput; // once the reader task exists, IT disposes _outputRead
        private bool _disposed;

        private ConPtyCommandSession(
            string commandLine, string workingDirectory,
            IReadOnlyDictionary<string, string>? environment,
            int columns, int rows, Action<string> onOutput)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                throw new ArgumentException("commandLine is required", nameof(commandLine));
            columns = Math.Max(20, columns);
            rows = Math.Max(5, rows);

            // Pipe pairs: (ptyInRead -> pty) and (pty -> ptyOutWrite); we keep the other ends.
            //
            // Two statements, not one `||`, because of what the short-circuit left behind. Both pairs
            // were created before the SafeFileHandle wrappers below, so between here and there nothing
            // OWNS them: if the first call succeeded and the second failed, the throw abandoned two live
            // kernel handles with no finalizer to reclaim them. TryStart swallows the exception and
            // falls back to redirected pipes, so the caller never learns, and every retry leaks another
            // pair for the life of the process.
            if (!CreatePipe(out var ptyInRead, out var inputWrite, IntPtr.Zero, 0))
                throw new InvalidOperationException("CreatePipe failed");
            if (!CreatePipe(out var outputRead, out var ptyOutWrite, IntPtr.Zero, 0))
            {
                CloseHandle(ptyInRead);
                CloseHandle(inputWrite);
                throw new InvalidOperationException("CreatePipe failed");
            }
            _inputWrite = new SafeFileHandle(inputWrite, ownsHandle: true);
            _outputRead = new SafeFileHandle(outputRead, ownsHandle: true);

            var hr = CreatePseudoConsole(
                new COORD { X = (short)columns, Y = (short)rows }, ptyInRead, ptyOutWrite, 0, out _pty);
            // The pty duplicated its ends; ours are no longer needed regardless of hr.
            CloseHandle(ptyInRead);
            CloseHandle(ptyOutWrite);
            if (hr != 0)
                throw new InvalidOperationException($"CreatePseudoConsole failed (0x{hr:x8})");

            try
            {
                // Attach the pty to the child via the STARTUPINFOEX attribute list.
                var attrSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
                _attributeList = Marshal.AllocHGlobal(attrSize);
                if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref attrSize) ||
                    !UpdateProcThreadAttribute(_attributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                        _pty, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new InvalidOperationException("could not build the process attribute list");

                var startup = new STARTUPINFOEX();
                startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                startup.lpAttributeList = _attributeList;
                // STARTF_USESTDHANDLES with NULL handles, deliberately: without it CreateProcess
                // CLONES the parent's std handles into a console-subsystem child even with
                // bInheritHandles false — when the parent's stdout is a pipe (test host, headless),
                // the child then writes to that pipe and BYPASSES the pty (probe-verified
                // 2026-07-18: output reached the parent console, only conhost init sequences hit
                // the pty). Null std handles make console init bind them to the pty instead —
                // the same suppression node-pty applies.
                startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;

                _environmentBlock = BuildEnvironmentBlock(environment);
                if (!CreateProcessW(
                        null, commandLine, IntPtr.Zero, IntPtr.Zero, bInheritHandles: false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                        _environmentBlock,
                        string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
                        ref startup, out var pi))
                    throw new InvalidOperationException($"CreateProcess failed ({Marshal.GetLastWin32Error()})");
                _processHandle = pi.hProcess;
                _processId = pi.dwProcessId;
                CloseHandle(pi.hThread);

                _input = new FileStream(_inputWrite, FileAccess.Write);

                // Exit signal without parking a thread: a wait registration on the process handle.
                _exitEvent = new ManualResetEvent(false)
                {
                    SafeWaitHandle = new SafeWaitHandle(_processHandle, ownsHandle: false),
                };
                _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) =>
                {
                    GetExitCodeProcess(_processHandle, out var code);
                    _exited.TrySetResult(unchecked((int)code));
                }, null, -1, executeOnlyOnce: true);

                // Reader: raw pty bytes -> stateful UTF-8 decode (multibyte sequences split across
                // reads) -> callback. Ends at EOF, which arrives once ClosePseudoConsole runs.
                // The reader OWNS this stream (and thereby _outputRead): Dispose must never close
                // it directly — disposing a stream under a blocked Read throws
                // ObjectDisposedException into the reader (seen live in Visual Studio, 2026-07-18).
                // Dispose closes the pty; EOF ends the loop; the reader cleans up after itself.
                var output = new FileStream(_outputRead, FileAccess.Read);
                _readerOwnsOutput = true;
                _ = Task.Run(() =>
                {
                    var decoder = Encoding.UTF8.GetDecoder();
                    var bytes = new byte[4096];
                    var chars = new char[8192];
                    try
                    {
                        int n;
                        while ((n = output.Read(bytes, 0, bytes.Length)) > 0)
                        {
                            var count = decoder.GetChars(bytes, 0, n, chars, 0);
                            if (count > 0)
                            {
                                try { onOutput(new string(chars, 0, count)); }
                                catch { /* sink faults must not kill the reader */ }
                            }
                        }
                    }
                    catch
                    {
                        // Pipe broken during teardown — expected shutdown path.
                    }
                    finally
                    {
                        output.Dispose();
                    }
                });
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void WriteInput(byte[] data)
        {
            if (data is null || data.Length == 0)
                return;
            lock (_gate)
            {
                if (_disposed)
                    return;
                try
                {
                    _input.Write(data, 0, data.Length);
                    _input.Flush();
                }
                catch
                {
                    // Process gone mid-keystroke — benign.
                }
            }
        }

        /// <remarks>
        /// Decided under <c>_gate</c>, like every other mutator. Read outside it, <c>_disposed</c> could
        /// be false here and true a moment later, with <c>ClosePseudoConsole</c> already run — handing a
        /// freed HPCON to kernel32. The <c>catch</c> is no help there: it covers managed marshalling, not
        /// a native use-after-close, and a pane resize racing a teardown is an ordinary thing for a user
        /// to do (drag the splitter while the command finishes).
        /// <para>
        /// The lock is what orders the two rather than the flag. <c>Dispose</c> needs the same gate to
        /// SET <c>_disposed</c>, so a resize that got in first completes before the close can be reached,
        /// and one that did not sees the flag and leaves. Holding it across the close instead would put
        /// a call that waits for the pty to drain under the same lock <c>WriteInput</c> takes.
        /// </para>
        /// </remarks>
        public void Resize(int columns, int rows)
        {
            if (columns < 1 || rows < 1)
                return;
            lock (_gate)
            {
                if (_disposed || _pty == IntPtr.Zero)
                    return;
                try { ResizePseudoConsole(_pty, new COORD { X = (short)columns, Y = (short)rows }); }
                catch { /* cosmetic */ }
            }
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            if (!cancellationToken.CanBeCanceled)
                return _exited.Task;
            return WaitCoreAsync(cancellationToken);
        }

        private async Task<int> WaitCoreAsync(CancellationToken cancellationToken)
        {
            var cancelled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken)))
            {
                var winner = await Task.WhenAny(_exited.Task, cancelled.Task).ConfigureAwait(false);
                return await winner.ConfigureAwait(false);
            }
        }

        public void Kill()
        {
            try
            {
                using var killer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {_processId} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                killer?.WaitForExit(5000);
            }
            catch
            {
                try { TerminateProcess(_processHandle, unchecked((uint)-1)); } catch { }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                // Inside the gate so no in-flight WriteInput (same gate) races the close.
                try { _input?.Dispose(); } catch { }
            }

            // Order matters: closing the pty is what EOFs the reader, which then disposes its own
            // stream (and _outputRead with it) — never close that handle under a blocked Read.
            try { if (_pty != IntPtr.Zero) ClosePseudoConsole(_pty); } catch { }
            // WAIT for the callback, don't just unregister it. Unregister(null) returns without
            // waiting for one already running, and that callback calls GetExitCodeProcess(_processHandle)
            // — which is closed a few lines below. A process exiting concurrently with teardown could
            // therefore read a closed handle, and Windows reuses handle values, so the answer might be
            // another object's exit code reported through _exited as this command's. Bounded, because a
            // teardown that cannot finish is worse than a stale exit code.
            try
            {
                if (_exitWait is not null)
                {
                    var unregistered = new ManualResetEvent(false);
                    if (_exitWait.Unregister(unregistered) && unregistered.WaitOne(1000))
                        unregistered.Dispose();
                    // On a timeout it is deliberately NOT disposed: the runtime still holds it and
                    // signalling a disposed handle is worse than leaking one on a path that is already
                    // failing.
                }
            }
            catch { }
            try { _exitEvent?.Dispose(); } catch { }
            if (!_readerOwnsOutput)
            {
                // Constructor failed before the reader existed: nobody else will close it.
                try { _outputRead?.Dispose(); } catch { }
            }
            try { if (_processHandle != IntPtr.Zero) CloseHandle(_processHandle); } catch { }
            if (_attributeList != IntPtr.Zero)
            {
                try { DeleteProcThreadAttributeList(_attributeList); } catch { }
                Marshal.FreeHGlobal(_attributeList);
            }
            if (_environmentBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(_environmentBlock);
            _exited.TrySetResult(unchecked((int)0xC000013A)); // teardown before exit: report as aborted
        }

        // "k=v\0k=v\0\0", UTF-16, keys sorted case-insensitively (CreateProcess documented shape).
        private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string>? environment)
        {
            if (environment is null)
                return IntPtr.Zero;
            var sb = new StringBuilder();
            foreach (var kv in environment.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            sb.Append('\0');
            return Marshal.StringToHGlobalUni(sb.ToString());
        }

        private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        private const int STARTF_USESTDHANDLES = 0x00000100;

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(
            string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, int dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
