using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Pins the pure parts of the terminal mirror: the text
    /// shaping in <see cref="TerminalMirrorText"/> and the bounded blocking buffer stream the VS
    /// terminal renderer reads from (<see cref="TerminalMirrorStream"/>). The pane plumbing itself
    /// is VSIX-only and verified in Visual Studio.
    /// </summary>
    public class TerminalMirrorTextTests
    {
        [Theory]
        [InlineData("a\nb", "a\r\nb")]
        [InlineData("a\r\nb", "a\r\nb")]           // existing CRLF untouched (no CRCRLF)
        [InlineData("a\nb\nc\n", "a\r\nb\r\nc\r\n")]
        [InlineData("no newline", "no newline")]
        [InlineData("progress\rrewrite", "progress\rrewrite")] // lone CR is a terminal idiom, keep it
        [InlineData("", "")]
        public void NormalizeNewlines_MakesBareLfCrLf(string input, string expected)
            => Assert.Equal(expected, TerminalMirrorText.NormalizeNewlines(input));

        [Fact]
        public void NormalizeNewlines_NullYieldsEmpty()
            => Assert.Equal(string.Empty, TerminalMirrorText.NormalizeNewlines(null));

        [Fact]
        public void Header_WrapsTitleAndEndsWithCrLf()
        {
            var header = TerminalMirrorText.Header("dotnet build");
            Assert.Contains("── dotnet build ──", header);
            Assert.EndsWith("\r\n", header);
        }

        [Fact]
        public void Header_BlankTitleFallsBack()
            => Assert.Contains("── command ──", TerminalMirrorText.Header("   "));

        [Fact]
        public void SanitizeTitle_StripsControlCharacters()
        {
            // Titles are agent-controlled: an embedded ESC must not smuggle ANSI into our pane,
            // and newlines must not break the one-line separator.
            var dirty = "evil\x1b[31mred\r\ntitle";
            var clean = TerminalMirrorText.SanitizeTitle(dirty);
            Assert.DoesNotContain('\x1b', clean);
            Assert.DoesNotContain('\r', clean);
            Assert.DoesNotContain('\n', clean);
            Assert.Contains("evil", clean);
            Assert.Contains("title", clean);
        }

        [Fact]
        public void SanitizeTitle_ClampsLongTitles()
        {
            var clean = TerminalMirrorText.SanitizeTitle(new string('x', 500));
            Assert.True(clean.Length <= 81); // 80 + the ellipsis
            Assert.EndsWith("…", clean);
        }
    }

    public class TerminalMirrorStreamTests
    {
        private static string ReadAll(TerminalMirrorStream stream)
        {
            using var collected = new MemoryStream();
            var buffer = new byte[8192];
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                collected.Write(buffer, 0, n);
            return Encoding.UTF8.GetString(collected.ToArray());
        }

        /// <summary>
        /// A cancelled read must not poison the stream for every read after it.
        /// </summary>
        /// <remarks>
        /// <c>_dataAvailable</c> caches one waiter so concurrent readers share a wake, and only
        /// <c>WakeReadersLocked</c> ever cleared it — so cancelling a read while the pane is IDLE left
        /// an already-cancelled TCS in the field. The next <c>ReadAsync</c>, with a perfectly good
        /// token, took that same instance through <c>??=</c> and awaited a task that was already
        /// cancelled: it threw instantly, and went on throwing. The VS terminal renderer's read loop
        /// reads that as the stream ending, so ONE cancelled read killed the mirror for the rest of the
        /// session — recoverable only if new output happened to arrive first and null the field.
        /// <para>
        /// The second read is given a real timeout rather than an infinite token, so a regression fails
        /// as a timeout rather than hanging the suite.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task ACancelledReadDoesNotPoisonTheNextOne()
        {
            var stream = new TerminalMirrorStream();
            var buffer = new byte[64];

            // Nothing buffered, so this parks on the shared waiter and is then cancelled.
            using (var cancelled = new System.Threading.CancellationTokenSource())
            {
                var parked = stream.ReadAsync(buffer, 0, buffer.Length, cancelled.Token);
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parked);
            }

            // A fresh read, a fresh token: it must park (not throw) and complete when output arrives.
            using var fresh = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            var read = stream.ReadAsync(buffer, 0, buffer.Length, fresh.Token);
            stream.Push("still here");

            var n = await read;
            Assert.Equal("still here", Encoding.UTF8.GetString(buffer, 0, n));
        }

        [Fact]
        public void PushedTextIsReadBackInOrder()
        {
            var stream = new TerminalMirrorStream();
            stream.Push("hello ");
            stream.Push("world");
            stream.Dispose();
            Assert.Equal("hello world", ReadAll(stream));
        }

        [Fact]
        public void ReadHonorsSmallBuffers()
        {
            var stream = new TerminalMirrorStream();
            stream.Push("abcdef");
            stream.Dispose();
            var buffer = new byte[4];
            Assert.Equal(4, stream.Read(buffer, 0, 4));
            Assert.Equal("abcd", Encoding.UTF8.GetString(buffer, 0, 4));
            Assert.Equal(2, stream.Read(buffer, 0, 4));
            Assert.Equal("ef", Encoding.UTF8.GetString(buffer, 0, 2));
            Assert.Equal(0, stream.Read(buffer, 0, 4));
        }

        [Fact]
        public async Task ReadBlocksUntilDataArrives()
        {
            var stream = new TerminalMirrorStream();
            var buffer = new byte[16];
            var read = Task.Run(() => stream.Read(buffer, 0, buffer.Length));

            await Task.Delay(100);
            Assert.False(read.IsCompleted); // still parked, nothing pushed yet

            stream.Push("late");
            var n = await read.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("late", Encoding.UTF8.GetString(buffer, 0, n));
            stream.Dispose();
        }

        [Fact]
        public async Task DisposeWakesABlockedReaderWithEof()
        {
            var stream = new TerminalMirrorStream();
            var read = Task.Run(() => stream.Read(new byte[16], 0, 16));
            await Task.Delay(50);
            stream.Dispose();
            Assert.Equal(0, await read.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public async Task ReadAsyncCompletesWhenDataArrives()
        {
            var stream = new TerminalMirrorStream();
            var buffer = new byte[16];
            var read = stream.ReadAsync(buffer, 0, buffer.Length);

            await Task.Delay(100);
            Assert.False(read.IsCompleted); // parked without burning a thread

            stream.Push("async");
            var n = await read.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("async", Encoding.UTF8.GetString(buffer, 0, n));
            stream.Dispose();
        }

        [Fact]
        public async Task DisposeCompletesAPendingReadAsyncWithEof()
        {
            var stream = new TerminalMirrorStream();
            var read = stream.ReadAsync(new byte[16], 0, 16);
            await Task.Delay(50);
            stream.Dispose();
            Assert.Equal(0, await read.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public async Task ReadAsyncHonorsCancellation()
        {
            var stream = new TerminalMirrorStream();
            using var cts = new System.Threading.CancellationTokenSource();
            var read = stream.ReadAsync(new byte[16], 0, 16, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => read.WaitAsync(TimeSpan.FromSeconds(5)));
            stream.Dispose();
        }

        [Fact]
        public async Task BeginReadEndReadRoundTripsWithoutBaseStreamPlumbing()
        {
            // The devenv freeze entered through base BeginReadInternal (shared async semaphore);
            // our override must serve APM reads from the same TCS machinery as ReadAsync.
            var stream = new TerminalMirrorStream();
            var buffer = new byte[16];
            var completed = new TaskCompletionSource<int>();
            stream.BeginRead(buffer, 0, buffer.Length, ar => completed.TrySetResult(stream.EndRead(ar)), null);

            stream.Push("apm");
            var n = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("apm", Encoding.UTF8.GetString(buffer, 0, n));
            stream.Dispose();
        }

        [Fact]
        public void WriteAsyncCompletesSynchronously()
        {
            // The UI thread blocked inside WriteAsync (via the base semaphore) in the freeze;
            // the override must complete instantly regardless of reader state.
            var stream = new TerminalMirrorStream();
            var pending = stream.ReadAsync(new byte[8], 0, 8); // a parked reader must not matter
            Assert.True(stream.WriteAsync(new byte[] { 1, 2 }, 0, 2).IsCompleted);
            Assert.False(pending.IsCompleted);
            stream.Dispose();
        }

        [Fact]
        public void PushAfterDisposeIsIgnored()
        {
            var stream = new TerminalMirrorStream();
            stream.Dispose();
            stream.Push("ghost");
            Assert.Equal(0, stream.Read(new byte[8], 0, 8));
        }

        [Fact]
        public void OverflowDropsOldestKeepsNewestAndMarksTheGap()
        {
            var stream = new TerminalMirrorStream();
            var chunk = 100 * 1024;
            var count = 15; // 1.5MB total against the 1MB cap
            for (var i = 0; i < count; i++)
                stream.Push($"[chunk-{i:00}]" + new string('x', chunk));
            stream.Dispose();

            var text = ReadAll(stream);
            Assert.True(text.Length <= TerminalMirrorStream.MaxBufferedBytes + 256,
                $"buffered {text.Length} bytes, expected <= cap");
            Assert.Contains("older output dropped", text);   // the gap is announced
            Assert.DoesNotContain("[chunk-00]", text);       // oldest gone
            Assert.Contains($"[chunk-{count - 1:00}]", text); // newest survives
        }

        [Fact]
        public void OverflowNeverDropsTheChunkBeingRead()
        {
            var stream = new TerminalMirrorStream();
            stream.Push("in-flight");
            var buffer = new byte[2];
            Assert.Equal(2, stream.Read(buffer, 0, 2)); // reader is now mid-chunk ("in" consumed)

            for (var i = 0; i < 15; i++)
                stream.Push(new string('y', 100 * 1024));
            stream.Dispose();

            // The remainder of the partially-read chunk must still arrive intact.
            var rest = ReadAll(stream);
            Assert.StartsWith("-flight", rest);
        }
    }

    public class TerminalMirrorTeeTests
    {
        private readonly List<string> _headers = new List<string>();
        private readonly StringBuilder _output = new StringBuilder();
        private readonly TerminalMirrorTee _tee;

        public TerminalMirrorTeeTests()
            => _tee = new TerminalMirrorTee(t => _headers.Add(t), t => _output.Append(t));

        private static AgentEventDto Ev(string type, string? id = null, string? title = null,
            string? kind = null, string? text = null, string? message = null,
            string? errorText = null, bool? success = null, string? rawInputJson = null) => new()
        {
            Type = type, ToolCallId = id, Title = title, Kind = kind,
            Text = text, Message = message, ErrorText = errorText, Success = success,
            RawInputJson = rawInputJson,
        };

        [Fact]
        public void FirstChunkWritesHeaderOnce_KiroStyle()
        {
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Running: git status", kind: "execute"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "On branch main\n"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "clean\n"));

            Assert.Equal(new[] { "Running: git status" }, _headers);
            Assert.Equal("On branch main\nclean\n", _output.ToString());
        }

        [Fact]
        public void NewlinelessChunksAreJoinedWithOne_KiroStyle()
        {
            // Kiro's chunks are discrete blocks WITHOUT trailing newlines (the tool row joins them
            // with "\n"); written back-to-back they fused two dir-listing rows mid-line (2026-07-17).
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Running: dir", kind: "execute"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "d----  Properties"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "d----  Shared"));

            Assert.Equal("d----  Properties\r\nd----  Shared", _output.ToString());
        }

        [Fact]
        public void NewlineTerminatedChunksAreNotDoubleSpaced()
        {
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "line one\n"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "line two\n"));

            Assert.Equal("line one\nline two\n", _output.ToString());
        }

        /// <summary>
        /// A failure's stderr starts on its own line, like every other chunk boundary.
        /// <c>_mirrored</c> holds a bool rather than being a set precisely so the boundary can be
        /// restored, and the completion handler read it only through <c>Remove</c>'s return — throwing
        /// the payload away and fusing stderr onto the last line of stdout, which is the 2026-07-17
        /// symptom reappearing on the path nobody re-checked.
        /// </summary>
        [Fact]
        public void AFailuresStderrDoesNotFuseOntoANewlinelessLastChunk()
        {
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Running: dir", kind: "execute"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "d----  Properties"));
            _tee.OnEvent(Ev("toolDone", id: "t1", success: false, errorText: "Access is denied."));

            Assert.Contains("Properties\r\nAccess is denied.", _output.ToString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// ...and the marker too, for a failure that carries no stderr at all — otherwise
        /// "[command failed]" lands on the end of the user's last line of output.
        /// </summary>
        [Fact]
        public void AFailureMarkerDoesNotFuseOntoANewlinelessLastChunk()
        {
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Running: dir", kind: "execute"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "d----  Properties"));
            _tee.OnEvent(Ev("toolDone", id: "t1", success: false));

            Assert.DoesNotContain("Properties\x1b[31m", _output.ToString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// The other direction, which is what stops the fix being "always write a newline": a chunk
        /// that already ended with one must not gain a blank line before the stderr.
        /// </summary>
        [Fact]
        public void ANewlineTerminatedChunkGainsNoBlankLineBeforeStderr()
        {
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Running: dir", kind: "execute"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "d----  Properties\n"));
            _tee.OnEvent(Ev("toolDone", id: "t1", success: false, errorText: "Access is denied."));

            Assert.DoesNotContain("Properties\n\r\nAccess", _output.ToString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// A completed call takes its command with it. Only the early-return branch cleared
        /// <c>_commands</c>, so a backend reusing a tool call id inside one turn got the PREVIOUS
        /// call's command in its separator header.
        /// </summary>
        [Fact]
        public void ACompletedCallDoesNotLeaveItsCommandForTheNextOne()
        {
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Tool call", kind: "execute",
                rawInputJson: "{\"command\":\"git status\"}"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "clean\n"));
            _tee.OnEvent(Ev("toolDone", id: "t1", success: true));

            // Same id, no command this time: the header must fall back to the title, not reuse the old
            // command.
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Running: dir", kind: "execute"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "listing\n"));

            Assert.DoesNotContain("git status", _headers[_headers.Count - 1], StringComparison.Ordinal);
        }

        [Fact]
        public void EnrichingUpdateSuppliesTheRealTitle_ClaudeStyle()
        {
            // Claude opens with a generic placeholder and sends the real title on toolUpdate,
            // before any output exists — the lazy header must pick up the enriched title.
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Tool call", kind: null));
            _tee.OnEvent(Ev("toolUpdate", id: "t1", title: "dotnet build", kind: "execute"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "Build started\n"));

            Assert.Equal(new[] { "dotnet build" }, _headers);
        }

        [Fact]
        public void StreamedCompletionSkipsResultTextButSeparates()
        {
            // Kiro's completion result is a superset of what already streamed - repeating it would
            // duplicate the whole command output in the pane.
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Running: git status", kind: "execute"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "On branch main\n"));
            _tee.OnEvent(Ev("toolDone", id: "t1", message: "On branch main\nclean\n", success: true));

            Assert.Equal("On branch main\n\r\n", _output.ToString());
        }

        [Fact]
        public void UnstreamedExecuteCompletionMirrorsItsResult_ClaudeStyle()
        {
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Bash", kind: "execute"));
            _tee.OnEvent(Ev("toolDone", id: "t1", message: "hello from claude", success: true));

            Assert.Equal(new[] { "Bash" }, _headers);
            Assert.StartsWith("hello from claude\n", _output.ToString());
        }

        [Fact]
        public void UnstreamedNonExecuteCompletionStaysOutOfThePane()
        {
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Read file", kind: "read"));
            _tee.OnEvent(Ev("toolDone", id: "t1", message: "file contents here", success: true));

            Assert.Empty(_headers);
            Assert.Equal(string.Empty, _output.ToString());
        }

        [Fact]
        public void FailureAppendsStderrAndMarker()
        {
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Running: dotnet build", kind: "execute"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "Build started\n"));
            _tee.OnEvent(Ev("toolDone", id: "t1", errorText: "error CS1002: ; expected", success: false));

            var text = _output.ToString();
            Assert.Contains("error CS1002", text);
            Assert.Contains("[command failed]", text);
        }

        [Fact]
        public void TurnDoneResetsPerCallState()
        {
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Running: git status", kind: "execute"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "a\n"));
            _tee.OnEvent(Ev("turnDone"));

            // An id-reusing backend must not leak last turn's title, and the header fires afresh.
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "b\n"));
            Assert.Equal(new[] { "Running: git status", "command" }, _headers);

            // And a stale execute kind must not leak either: t1's completion after the reset (with
            // no new toolStart) is kind-unknown -> not mirrored as a Claude-style result.
            _tee.OnEvent(Ev("turnDone"));
            _output.Clear();
            _tee.OnEvent(Ev("toolDone", id: "t1", message: "late result", success: true));
            Assert.DoesNotContain("late result", _output.ToString());
        }

        [Fact]
        public void EventsWithoutIdsAreIgnored()
        {
            _tee.OnEvent(Ev("toolOutput", text: "orphan\n"));
            _tee.OnEvent(Ev("toolDone", message: "orphan", success: true));
            Assert.Empty(_headers);
            Assert.Equal(string.Empty, _output.ToString());
        }

        /// <summary>
        /// Issue #156, from a live frame. A Claude <c>Bash</c> call mirrored as a prose header with the
        /// same prose beneath it and the command nowhere on screen.
        /// </summary>
        /// <remarks>
        /// Two fields collide here. <c>ev.Title</c> is the row's DISPLAY title, which #125 deliberately
        /// swaps to <c>_meta.claudeCode.title</c> — prose — because the ACP title is the raw command and
        /// prose reads better on a transcript row. And the adapter emits a content block whose text IS
        /// <c>rawInput.description</c>, which arrives as toolOutput. A terminal wants neither: it exists
        /// to show what ran.
        /// </remarks>
        [Fact]
        public void AClaudeBashCall_NamesTheCommandAndDropsTheEchoedLabel()
        {
            const string prose = "Show diff for WorkflowRepositoryTests.cs";
            const string command = "git diff -- Bookshelf.Tests/Workflow/WorkflowRepositoryTests.cs";

            _tee.OnEvent(Ev("toolStart", id: "t1", title: prose, kind: "execute",
                rawInputJson: "{\"command\":\"" + command + "\",\"description\":\"" + prose + "\"}"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: prose));          // the adapter's echo
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "diff --git a/x b/x\n"));

            Assert.Equal(new[] { command }, _headers);
            Assert.Equal("diff --git a/x b/x\n", _output.ToString());
        }

        /// <summary>
        /// The header still falls back to the title when rawInput names no command — Kiro's shell rows,
        /// and anything whose arguments are shaped differently.
        /// </summary>
        [Fact]
        public void WithNoCommandInRawInput_TheTitleStillNamesTheHeader()
        {
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Running: git status", kind: "execute",
                rawInputJson: "{\"path\":\"x.cs\"}"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "On branch main\n"));

            Assert.Equal(new[] { "Running: git status" }, _headers);
            Assert.Equal("On branch main\n", _output.ToString());
        }

        /// <summary>
        /// Unparseable rawInput must not throw into the event stream, nor lose the header. A mirror is a
        /// convenience; a backend sending something unexpected may not take the session down.
        /// </summary>
        [Fact]
        public void MalformedRawInput_FallsBackWithoutThrowing()
        {
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "Running: git status", kind: "execute",
                rawInputJson: "{not json"));
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "out\n"));

            Assert.Equal(new[] { "Running: git status" }, _headers);
            Assert.Equal("out\n", _output.ToString());
        }

        /// <summary>
        /// The real Claude frame order, from a live capture: the opening <c>tool_call</c> carries an
        /// EMPTY rawInput and the command only arrives on a later <c>tool_call_update</c>.
        /// </summary>
        /// <remarks>
        /// So a command is only ever LEARNED, never unlearned. Storing whatever the latest frame parsed
        /// to would let an empty rawInput erase one already known, and the header would fall back to the
        /// prose title without anything failing. Frame order happens to save us today, which is exactly
        /// why it should not be what this depends on.
        /// </remarks>
        [Fact]
        public void TheCommandArrivesLate_AndAnEmptyRawInputNeverErasesIt()
        {
            const string prose = "Re-check diff and status";
            const string command = "git diff -- Foo.cs; git status --short";

            _tee.OnEvent(Ev("toolStart", id: "t1", title: prose, kind: "execute", rawInputJson: "{}"));
            _tee.OnEvent(Ev("toolUpdate", id: "t1", rawInputJson: "{\"command\":\"" + command + "\"}"));
            _tee.OnEvent(Ev("toolUpdate", id: "t1", rawInputJson: "{}"));   // must not unlearn it
            _tee.OnEvent(Ev("toolOutput", id: "t1", text: "diff --git a/x b/x"));

            Assert.Equal(new[] { command }, _headers);
        }

        /// <summary>
        /// Only the call's OWN label is dropped. Real output that happens to repeat a different call's
        /// title still reaches the pane — the suppression is per tool-call id, not a global filter.
        /// </summary>
        [Fact]
        public void OutputMatchingAnotherCallsLabel_IsStillMirrored()
        {
            _tee.OnEvent(Ev("toolStart", id: "t1", title: "echo hello", kind: "execute",
                rawInputJson: "{\"command\":\"echo hello\"}"));
            _tee.OnEvent(Ev("toolStart", id: "t2", title: "cat notes", kind: "execute",
                rawInputJson: "{\"command\":\"cat notes\"}"));
            _tee.OnEvent(Ev("toolOutput", id: "t2", text: "echo hello"));

            Assert.Equal(new[] { "cat notes" }, _headers);
            Assert.Equal("echo hello", _output.ToString());
        }
    }
}
