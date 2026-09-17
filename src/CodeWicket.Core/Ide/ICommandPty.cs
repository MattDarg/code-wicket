using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// A spawned command attached to a pseudo-terminal — the interactive upgrade for
    /// <c>run_command</c>: real TTY semantics
    /// (echo, isatty, prompts) while the host still owns the process. Contract-only here so the
    /// tool catalog needs no Win32 knowledge; the host wires the concrete implementation via
    /// <see cref="CommandPtyFactory"/> (the ConPTY one lives in the Shell). Hosts that wire
    /// nothing keep the plain redirected-pipes path.
    /// </summary>
    public interface ICommandPty : IDisposable
    {
        /// <summary>Feed user keystrokes (raw bytes, as the terminal produced them) to the process.</summary>
        void WriteInput(byte[] data);

        /// <summary>Resize the pseudo-terminal (the pane's dimensions changed).</summary>
        void Resize(int columns, int rows);

        /// <summary>Completes with the process exit code.</summary>
        Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);

        /// <summary>Best-effort kill of the whole process tree (timeout/cancel path).</summary>
        void Kill();
    }

    /// <summary>
    /// Starts <paramref name="commandLine"/> on a pseudo-terminal of the given size, streaming
    /// decoded output (raw VT text) to <paramref name="onOutput"/> as it arrives. A null
    /// <paramref name="environment"/> inherits the host's. Returns null when a pty can't be
    /// created (unsupported OS, API failure) — the caller falls back to redirected pipes.
    /// </summary>
    public delegate ICommandPty? CommandPtyFactory(
        string commandLine,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        int columns,
        int rows,
        Action<string> onOutput);
}
