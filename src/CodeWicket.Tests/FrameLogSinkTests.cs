using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CodeWicket.Core;
using CodeWicket.Engine.Mcp;
using CodeWicket.Core.Ide;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The ACP frame tee: one file, both directions, one timestamp per frame (issue #211).
    /// <para>
    /// Both halves of the old shape cost real mistakes. Reading the agent→client file alone produced
    /// three confident wrong conclusions in a single session — a request leak that was our replies
    /// sitting in the other file, a turn that had "ended" because the prompt opening it was outbound,
    /// and a claim about <em>when</em> something happened from a file carrying no times at all.
    /// </para>
    /// </summary>
    [Collection(McpLogCollection.Name)]
    public class FrameLogSinkTests
    {
        private static readonly DateTime When = new DateTime(2026, 9, 4, 19, 59, 47, 235);

        // ---- stamping -----------------------------------------------------------------------------

        /// <summary>
        /// A buffer can carry SEVERAL complete frames — the tee flushes everything up to the last
        /// newline in one call. Each has to be stamped on its own, or one timestamp stands for a group
        /// and the file is no more readable in time than it was before.
        /// </summary>
        [Fact]
        public void EveryFrameInOneBufferIsStampedSeparately()
        {
            var stamped = FrameLogSink.Stamp(
                FrameLogSink.Inbound, "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n", When);

            var lines = stamped.Split('\n').Where(l => l.Length > 0).ToList();
            Assert.Equal(3, lines.Count);
            Assert.All(lines, l => Assert.StartsWith("[2026-09-04 19:59:47.235] <- {", l));
        }

        /// <summary>The whole point: which way a frame went is on the line.</summary>
        [Fact]
        public void TheDirectionIsOnEveryLine()
        {
            Assert.Contains("-> {\"m\":1}", FrameLogSink.Stamp(FrameLogSink.Outbound, "{\"m\":1}\n", When));
            Assert.Contains("<- {\"m\":1}", FrameLogSink.Stamp(FrameLogSink.Inbound, "{\"m\":1}\n", When));
        }

        /// <summary>
        /// The stamp matches DiagnosticLog's, so this file lines up against engine.log — which is what
        /// the stamping work in #82 assumed was already possible.
        /// </summary>
        [Fact]
        public void TheStampMatchesTheOtherLogs()
        {
            Assert.StartsWith("[2026-09-04 19:59:47.235] ", FrameLogSink.Stamp(FrameLogSink.Inbound, "{}\n", When));
        }

        /// <summary>
        /// A frame stream is newline-TERMINATED, so splitting leaves an empty tail on almost every
        /// call. Stamping it would put a bare timestamp between every pair of frames.
        /// </summary>
        [Fact]
        public void NothingIsWrittenForTheEmptyTailOrABlankLine()
        {
            Assert.Equal(
                "[2026-09-04 19:59:47.235] <- {\"a\":1}\n",
                FrameLogSink.Stamp(FrameLogSink.Inbound, "{\"a\":1}\n", When));
            Assert.Equal(string.Empty, FrameLogSink.Stamp(FrameLogSink.Inbound, "\n\n", When));
        }

        // ---- reading it back ------------------------------------------------------------------------

        /// <summary>
        /// The proofs parse this file, so what the writer adds the reader has to remove — exactly,
        /// including for a payload that would confuse a lazier strip.
        /// </summary>
        [Theory]
        [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\"}")]
        [InlineData("{\"a\":[1,2,3]}")]
        [InlineData("{\"text\":\"[2026-09-04 19:59:47.235] <- not really a prefix\"}")]
        public void AStampedFrameReadsBackByteForByte(string frame)
        {
            foreach (var direction in new[] { FrameLogSink.Outbound, FrameLogSink.Inbound })
            {
                var line = FrameLogSink.Stamp(direction, frame + "\n", When).TrimEnd('\n');
                Assert.Equal(frame, FrameLogSink.StripPrefix(line));
            }
        }

        /// <summary>
        /// A log written before this change - or by an older build - still reads. The prefix is
        /// stripped when it is there and the line is left alone when it is not.
        /// </summary>
        [Fact]
        public void AnUnstampedLineIsLeftAlone()
        {
            const string raw = "{\"jsonrpc\":\"2.0\",\"id\":1}";
            Assert.Equal(raw, FrameLogSink.StripPrefix(raw));
        }

        /// <summary>
        /// A stamp with no direction marker is not one of ours, and a payload that merely opens with a
        /// bracket must not have its first characters eaten.
        /// </summary>
        [Theory]
        [InlineData("[2026-09-04 19:59:47.235] something else entirely")]
        [InlineData("[not a stamp] {\"a\":1}")]
        [InlineData("[1,2,3]")]
        public void OnlyOurOwnPrefixIsRemoved(string line)
        {
            Assert.Equal(line, FrameLogSink.StripPrefix(line));
        }

        // ---- one file -------------------------------------------------------------------------------

        /// <summary>
        /// The CONNECTION wires both directions to one sink - which is the actual defect, and is not
        /// what the sink-level test below proves.
        /// </summary>
        /// <remarks>
        /// Written after prove-check reported PINS NOTHING: an injection that sent the outbound tee
        /// back to its own "&lt;path&gt;.send" file left every other test green, because they build the
        /// sink by hand and never touch <c>ProcessAcpConnection.Sending</c>. The wiring is the thing
        /// that was wrong for the life of the old shape, so it is the thing that has to be driven.
        /// <para>It needs a real child process, since that is what the connection is. <c>cmd /c exit</c>
        /// is the cheapest one on the only platform this ships to, and nothing is asked of it beyond
        /// owning the redirected handles.</para>
        /// </remarks>
        /// <summary>
        /// The same claim for the OTHER tee. <c>CWKT_MCP_LOG</c> wrote two files until the ACP merge
        /// was carried across to it, and the half nobody read was OUR OWN ANSWERS — so a bridge that
        /// served a tool looked, in the file, like it had been asked and never replied.
        /// </summary>
        /// <remarks>
        /// Drives the real <see cref="McpToolServer"/> rather than the sink, for the reason the ACP
        /// test above records: the WIRING is what was wrong, and every sink-level test here builds its
        /// sink by hand and would stay green over two files. It also pins the part that is not merely
        /// cosmetic — the MCP tees used to be RAW BYTE tees, and pointing two of those at one path
        /// interleaves mid-frame, so "one file" is only correct while they are frame-aligned. A whole
        /// stamped frame in each direction is what proves that held.
        /// </remarks>
        [Fact]
        public void TheMcpServerSendsBothDirectionsToTheSameFile()
        {
            var path = Path.Combine(
                Path.GetTempPath(), "cwkt-mcp-wire-" + Guid.NewGuid().ToString("N") + ".log");
            var previous = Environment.GetEnvironmentVariable("CWKT_MCP_LOG");
            Environment.SetEnvironmentVariable("CWKT_MCP_LOG", path);
            using var answered = new ManualResetEventSlim(false);
            using var finish = new ManualResetEventSlim(false);
            try
            {
                // Both streams are SIGNALLED rather than polled, and the input does not end on its own.
                // Written the obvious way - a MemoryStream request and a poll on the reply's
                // length - it passes alone and fails in the gate matrix, which is #218 exactly: a
                // MemoryStream reaches EOF the instant the request is read, so the connection can tear
                // down before the reply is dispatched, and which of those wins is decided by how busy
                // the machine is. Nothing about the tee was ever involved.
                var toAgent = new SignalOnWriteStream(new MemoryStream(), answered);
                var fromAgent = new HeldOpenRequestStream(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}\n",
                    finish);

                using (var server = new McpToolServer(toAgent, fromAgent, new NoTools()))
                {
                    server.Start();
                    Assert.True(
                        answered.Wait(TimeSpan.FromSeconds(30)),
                        "the server never wrote an answer, so there is no outbound frame to look for");
                    finish.Set();   // let the request stream report EOF and the connection close
                }

                Assert.True(File.Exists(path), "the tee wrote nothing at all");
                Assert.False(
                    File.Exists(path + ".send"),
                    "our answers went to their own file again - the half of the conversation this "
                    + "bridge is judged by is the half it sends");

                var text = File.ReadAllText(path);
                Assert.Contains("<- ", text);   // what we read: the agent's request
                Assert.Contains("-> ", text);   // what we sent: our answer
                Assert.Contains("tools/list", text);
            }
            finally
            {
                Environment.SetEnvironmentVariable("CWKT_MCP_LOG", previous);
                try { File.Delete(path); } catch { }
                try { File.Delete(path + ".send"); } catch { }
            }
        }

        /// <summary>Sets an event the first time anything is written through it.</summary>
        private sealed class SignalOnWriteStream : Stream
        {
            private readonly Stream _inner;
            private readonly ManualResetEventSlim _written;

            public SignalOnWriteStream(Stream inner, ManualResetEventSlim written)
            {
                _inner = inner;
                _written = written;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
                if (count > 0)
                    _written.Set();
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        /// <summary>
        /// Serves one request frame, then BLOCKS rather than reporting EOF until the test releases it.
        /// That is the whole point: an input stream that ends immediately lets the connection close
        /// underneath the dispatch it just started.
        /// </summary>
        private sealed class HeldOpenRequestStream : Stream
        {
            private readonly byte[] _request;
            private readonly ManualResetEventSlim _finish;
            private int _offset;

            public HeldOpenRequestStream(string request, ManualResetEventSlim finish)
            {
                _request = Encoding.UTF8.GetBytes(request);
                _finish = finish;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_offset < _request.Length)
                {
                    var n = Math.Min(count, _request.Length - _offset);
                    Array.Copy(_request, _offset, buffer, offset, n);
                    _offset += n;
                    return n;
                }

                _finish.Wait(TimeSpan.FromSeconds(30));
                return 0;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>An empty catalog: this test is about the tee's wiring, not about any tool.</summary>
        private sealed class NoTools : IToolCatalog
        {
            public IReadOnlyList<ToolDescriptor> Tools { get; } = Array.Empty<ToolDescriptor>();

            public Task<ToolResult> InvokeAsync(
                string toolName, string argumentsJson, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }

        [Fact]
        public void TheConnectionSendsBothDirectionsToTheSameFile()
        {
            var path = Path.Combine(
                Path.GetTempPath(), "cwkt-wire-" + Guid.NewGuid().ToString("N") + ".log");
            var previous = Environment.GetEnvironmentVariable("CWKT_ACP_LOG");
            Environment.SetEnvironmentVariable("CWKT_ACP_LOG", path);
            try
            {
                using (var connection = new ProcessAcpConnection(
                    "cmd.exe", new[] { "/c", "exit" }, null, Path.GetTempPath()))
                {
                    var frame = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"session/prompt\"}\n");
                    connection.Sending.Write(frame, 0, frame.Length);
                    _ = connection.Receiving;
                }

                Assert.True(File.Exists(path), "the tee wrote nothing at all");
                Assert.False(
                    File.Exists(path + ".send"),
                    "the outbound direction went to its own file again - reading either alone is half "
                    + "the conversation, which is the whole of issue #211");
                Assert.Contains("-> ", File.ReadAllText(path));
            }
            finally
            {
                Environment.SetEnvironmentVariable("CWKT_ACP_LOG", previous);
                try { File.Delete(path); } catch { }
                try { File.Delete(path + ".send"); } catch { }
            }
        }

        /// <summary>
        /// The issue itself: both directions land in ONE file, in the order they crossed the wire.
        /// Asserted end to end through the real tee streams, because the defect was never in the
        /// formatting — it was that a reader of one file could not see the other half.
        /// </summary>
        [Fact]
        public void BothDirectionsShareOneFileInOrder()
        {
            var path = Path.Combine(
                Path.GetTempPath(), "cwkt-tee-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                using (var sink = FrameLogSink.Open(path))
                {
                    using (var send = new FrameTeeWriteStream(Stream.Null, sink))
                    {
                        var request = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"session/prompt\"}\n");
                        send.Write(request, 0, request.Length);
                    }

                    var replyBytes = Encoding.UTF8.GetBytes("{\"id\":1,\"result\":{}}\n");
                    using (var receive = new FrameTeeReadStream(new MemoryStream(replyBytes), sink))
                    {
                        var buffer = new byte[256];
                        while (receive.Read(buffer, 0, buffer.Length) > 0)
                        {
                        }
                    }
                }

                var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
                Assert.Equal(2, lines.Count);

                // The request went out first and is marked as outbound; the reply came back after it.
                // Neither of those is visible in a file holding one direction, which is the whole bug.
                Assert.Contains("-> ", lines[0]);
                Assert.Contains("session/prompt", lines[0]);
                Assert.Contains("<- ", lines[1]);
                Assert.Contains("\"result\"", lines[1]);
            }
            finally
            {
                try { File.Delete(path); } catch { /* scratch */ }
            }
        }
    }
}
