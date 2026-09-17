using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeWicket.Core;

namespace CodeWicket.Shell
{
    /// <summary>
    /// Validates a <see cref="CustomAcpAgent"/> entry by launching its CLI and performing a real
    /// ACP <c>initialize</c> handshake — proving the path resolves, the process starts, and it
    /// speaks ACP — and reads the advertised <c>agentCapabilities</c> (currently
    /// <c>loadSession</c> → ResumeSession) so capability declarations can be discovered instead of
    /// hand-written. Used at settings-save time; deliberately raw line-JSON over stdio (one
    /// request/response) so it works on net472 without the StreamJsonRpc plumbing.
    /// </summary>
    public static class AcpAgentProbe
    {
        // Matches AcpAgentSession's handshake (protocol v1, client fs, no terminal). The client
        // name and version come from Branding rather than being spelled again here - a comment
        // asking two hand-built handshakes to agree is not a mechanism. That makes this
        // static readonly rather than const, which costs nothing: it is read once per probe.
        private static readonly string InitializeRequest =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{" +
            "\"protocolVersion\":1," +
            "\"clientCapabilities\":{\"fs\":{\"readTextFile\":true,\"writeTextFile\":true},\"terminal\":false}," +
            "\"clientInfo\":{\"name\":\"" + Branding.AcpClientName + "\",\"version\":\"" +
            Branding.AcpClientVersion + "\"}}}";

        public sealed class Result
        {
            public bool Ok { get; set; }

            /// <summary>Human-readable failure reason (for the settings UI), null on success.</summary>
            public string? Error { get; set; }

            /// <summary>Whether the agent advertised <c>agentCapabilities.loadSession</c>.</summary>
            public bool LoadSession { get; set; }

            /// <summary>"name version" from the initialize result, when the agent sends it.</summary>
            public string? AgentInfo { get; set; }
        }

        // Created on demand; falls back to the current directory if LocalAppData is unavailable.
        private static string ProbeWorkingDirectory()
        {
            try
            {
                var dir = System.IO.Path.Combine(StoragePaths.Local, "probe");
                System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                return Environment.CurrentDirectory;
            }
        }

        /// <summary>Never throws; failures come back as <see cref="Result.Error"/>.</summary>
        public static Result Probe(CustomAcpAgent agent, int timeoutMs = 20000)
        {
            Process? process = null;
            var stderr = new StringBuilder();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = agent.CliPath,
                    Arguments = BuildArguments(agent.Args),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // Stable per-user dir, deliberately NOT temp: corporate EDR flags processes
                    // launched with a temp working directory, and this probe spawns the user's CLI
                    // on every settings save. Same principle as the default workspace.
                    WorkingDirectory = ProbeWorkingDirectory(),
                };
                if (agent.Env is not null)
                    foreach (var kv in agent.Env)
                        psi.Environment[kv.Key] = kv.Value;

                process = Process.Start(psi);
                if (process is null)
                    return Fail($"'{agent.CliPath}' did not start.");

                process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
                process.BeginErrorReadLine();

                process.StandardInput.WriteLine(InitializeRequest);
                process.StandardInput.Flush();

                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (true)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                        return Fail($"'{agent.CliPath}' did not answer initialize within {timeoutMs / 1000}s.{StderrTail(stderr)}");

                    var lineTask = process.StandardOutput.ReadLineAsync();
                    if (!lineTask.Wait(remaining))
                        return Fail($"'{agent.CliPath}' did not answer initialize within {timeoutMs / 1000}s.{StderrTail(stderr)}");

                    var line = lineTask.Result;
                    if (line is null)
                        return Fail($"'{agent.CliPath}' exited before answering initialize.{StderrTail(stderr)}");
                    if (line.Length == 0)
                        continue;

                    // Skip anything that isn't the id:1 response (an eager agent could notify first).
                    var parsed = TryParseInitializeResponse(line, agent.CliPath);
                    if (parsed is not null)
                        return parsed;
                }
            }
            catch (Exception ex)
            {
                return Fail($"'{agent.CliPath}' failed to launch: {ex.Message}");
            }
            finally
            {
                if (process is not null)
                {
                    // EOF on stdin is the polite shutdown; kill is the backstop. npm .cmd shims wrap
                    // node, and net472 can't kill a tree — the child exits on the closed stdio.
                    try { process.StandardInput.Close(); } catch { /* ignore */ }
                    try { if (!process.WaitForExit(2000)) process.Kill(); } catch { /* ignore */ }
                    process.Dispose();
                }
            }
        }

        // Returns null when the line isn't the initialize response (keep reading).
        private static Result? TryParseInitializeResponse(string line, string cliPath)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || id.GetInt32() != 1)
                    return null;

                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
                    return Fail($"'{cliPath}' rejected initialize: {message}");
                }
                if (!root.TryGetProperty("result", out var result))
                    return Fail($"'{cliPath}' sent a malformed initialize response.");

                var loadSession =
                    result.TryGetProperty("agentCapabilities", out var caps) &&
                    caps.ValueKind == JsonValueKind.Object &&
                    caps.TryGetProperty("loadSession", out var ls) &&
                    ls.ValueKind == JsonValueKind.True;

                string? agentInfo = null;
                if (result.TryGetProperty("agentInfo", out var info) && info.ValueKind == JsonValueKind.Object)
                {
                    var name = info.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var version = info.TryGetProperty("version", out var v) ? v.GetString() : null;
                    agentInfo = string.IsNullOrEmpty(version) ? name : $"{name} {version}";
                }

                return new Result { Ok = true, LoadSession = loadSession, AgentInfo = agentInfo };
            }
            catch (JsonException)
            {
                return null; // Non-JSON noise on stdout; keep scanning for the response.
            }
        }

        private static Result Fail(string error) => new() { Ok = false, Error = error };

        private static string StderrTail(StringBuilder stderr)
        {
            var text = stderr.ToString().Trim();
            if (text.Length == 0)
                return string.Empty;
            if (text.Length > 400)
                text = text.Substring(text.Length - 400);
            return $" Agent stderr: {text}";
        }

        // net472 has no ProcessStartInfo.ArgumentList; minimal Windows-rules quoting.
        // internal so the quoting rules can be asserted directly; nothing outside the tests calls it.
        internal static string BuildArguments(string[] args)
        {
            var sb = new StringBuilder();
            foreach (var arg in args ?? Array.Empty<string>())
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                    sb.Append(arg);
                else
                    AppendQuoted(sb, arg);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Appends one argument in double quotes, following the Windows command-line rules the CRT
        /// parses back.
        /// </summary>
        /// <remarks>
        /// The rule that was missing is the one about BACKSLASHES: a run of them is doubled only when it
        /// precedes a quote — including the closing quote this method adds. Escaping embedded quotes
        /// alone, an argument ending in a separator serialised as <c>"C:\Program Files\my agent\"</c>,
        /// whose final <c>\"</c> escapes the very quote meant to close it. The CLI then receives one
        /// mangled argument with everything after it glued on, and the probe reports "did not answer
        /// initialize" — a launch failure wearing the costume of a protocol failure, on a path whose
        /// whole job is to tell the user why their custom agent will not start.
        /// </remarks>
        private static void AppendQuoted(StringBuilder sb, string arg)
        {
            sb.Append('"');
            for (var i = 0; i < arg.Length; i++)
            {
                var slashes = 0;
                while (i < arg.Length && arg[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                if (i == arg.Length)
                {
                    // Trailing run: doubled, because the closing quote follows it.
                    sb.Append('\\', slashes * 2);
                    break;
                }

                if (arg[i] == '"')
                    sb.Append('\\', slashes * 2 + 1).Append('"');
                else
                    sb.Append('\\', slashes).Append(arg[i]);
            }

            sb.Append('"');
        }
    }
}
