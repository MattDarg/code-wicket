using System;
using System.IO;
using System.Text;
using CodeWicket.Core;

namespace CodeWicket.Ide
{
    /// <summary>
    /// Always-on, best-effort record of unexpected IDE-tool faults, at
    /// <c>%LOCALAPPDATA%\&lt;product&gt;\logs\tool-error.log</c> (alongside engine.log — the same
    /// <see cref="StoragePaths.LogDirectory"/> that the Shell's <c>ExtensionConfig.LogDirectory</c>
    /// resolves to; this used to duplicate the literal because CodeWicket.Ide may reference only
    /// Core, which is now where the path lives).
    /// <para>
    /// The agent only ever sees a one-line error string, so an exception that escapes a tool handler used to
    /// leave nothing behind — issue #21 was reported as a bare "Not implemented (Exception from HRESULT:
    /// 0x80004001 (E_NOTIMPL))" with no tool name, no exception type and no stack, which is why it could only
    /// be answered with "try reinstalling". This writes the whole exception so the next one is diagnosable.
    /// </para>
    /// <para>
    /// Unlike <see cref="TestRunDebugLog"/> this is NOT opt-in (a fault the user has to reproduce with an env
    /// var set is a fault we don't get told about), so it self-limits: the file is restarted past 1 MB. Never
    /// throws — a diagnostic must not break a tool call.
    /// </para>
    /// </summary>
    internal static class ToolErrorLog
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly object Gate = new object();

        /// <summary>The log's location, quoted back to the agent so a report can point at it.</summary>
        public static string Path => System.IO.Path.Combine(Directory, "tool-error.log");

        /// <summary>
        /// The log directory. Internal rather than private because a sibling trace
        /// (<c>debug-eval.log</c>) belongs BESIDE tool-error.log, not inside it — one is errors, the
        /// other is timing — and both must land where the retention sweep already looks.
        /// </summary>
        internal static string Directory => StoragePaths.LogDirectory;

        public static void Write(string toolName, Exception exception)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path;
                var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {toolName ?? "(no tool)"} threw"
                    + Environment.NewLine + exception + Environment.NewLine + Environment.NewLine;

                lock (Gate)
                {
                    // Roll rather than trim: this file only grows on genuine faults, so losing the older half
                    // of a runaway log costs nothing next to unbounded growth in the user's profile.
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Exists && info.Length > MaxBytes)
                            File.Delete(path);
                    }
                    catch { /* couldn't measure or roll — append anyway */ }

                    File.AppendAllText(path, entry, Encoding.UTF8);
                }
            }
            catch { /* diagnostic must never throw */ }
        }
    }
}
