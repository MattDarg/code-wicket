using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace CodeWicket.Engine.Mcp
{
    /// <summary>
    /// The engine's <c>--mcp-stdio</c> mode: a thin byte relay between this process's stdio and a
    /// local named pipe served by a running engine's <see cref="McpPipeHost"/>. The agent (Kiro)
    /// spawns this as its stdio MCP server; the bytes are pumped to the pipe where the real
    /// <see cref="McpToolServer"/> (with live IDE tools) answers. Exits when either side closes.
    /// </summary>
    internal static class McpStdioRelay
    {
        public static async Task<int> RunAsync(string pipeName)
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(10_000).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[mcp-relay] cannot connect to pipe '{pipeName}': {ex.Message}");
                return 1;
            }

            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();

            // Pump both directions; the relay is done as soon as either end stops.
            var toPipe = CopyAsync(stdin, pipe);
            var toStdout = CopyAsync(pipe, stdout);
            await Task.WhenAny(toPipe, toStdout).ConfigureAwait(false);
            return 0;
        }

        private static async Task CopyAsync(Stream from, Stream to)
        {
            var buffer = new byte[8192];
            try
            {
                int read;
                while ((read = await from.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    await to.FlushAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // A broken pipe / closed stdio just ends the relay.
            }
        }
    }
}
