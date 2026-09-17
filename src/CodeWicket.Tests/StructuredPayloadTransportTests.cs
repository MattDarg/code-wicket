using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;
using CodeWicket.Providers.Acp;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The two TRANSPORT-side readers of a structured payload, for a kind that is not <c>run_tests</c>
    /// (issue #73). <see cref="TestRunPayloadClampTests"/> pins the same path for the original kind; this
    /// exists because a second kind is exactly what those readers can silently fail to know about.
    /// <para>
    /// Both ask <see cref="StructuredToolResult"/> "is this ours", never "is this a test run". A reader
    /// that names kinds is a reader someone has to remember to extend, and forgetting is not a build
    /// error — it is issue #83 happening again to the new kind: clamped mid-string, thrown where the
    /// card is parsed, and only on payloads large enough to notice in the field.
    /// </para>
    /// </summary>
    public sealed class StructuredPayloadTransportTests
    {
        // --- the mapper's clamp exemption -------------------------------------------------------------

        /// <summary>
        /// A breakpoint payload past the 2000-char display cap must reach the host whole and parseable.
        /// A full batch carries a path, a condition and the agent's reason per row, so this is an
        /// ordinary size rather than a contrived one.
        /// </summary>
        [Fact]
        public void ABreakpointPayloadPastTheCapIsNotClamped()
        {
            var payload = BreakpointPayload();
            Assert.True(payload.Length > 2000, "the fixture must exceed the cap it is testing");

            var result = MapCompletion(payload);

            Assert.Equal(payload, result);
            // Parsing IS the assertion — it is what the view-model does, and the defect's signature was
            // a cut that left the JSON string open.
            using var doc = JsonDocument.Parse(result!);
            Assert.Equal("breakpoints", doc.RootElement.GetProperty("resultKind").GetString());
        }

        /// <summary>
        /// The exemption is for our payloads, not for length: ordinary output is still cut, or a chatty
        /// tool buries the transcript and the persisted log.
        /// </summary>
        [Fact]
        public void OrdinaryOutputPastTheCapIsStillClamped()
        {
            var result = MapCompletion(new string('x', 5000));

            Assert.NotNull(result);
            Assert.True(result!.Length < 5000);
        }

        // --- the shell's side-channel -----------------------------------------------------------------

        /// <summary>
        /// Kiro never echoes an MCP tool result back in its ACP frames, so without this delivery the card
        /// never renders on that backend at all. The tool result itself must still be returned unchanged.
        /// </summary>
        [Fact]
        public async Task ABreakpointResultIsDeliveredToTheChatBySideChannel()
        {
            var payload = BreakpointPayload(rows: 2);
            var delivered = new List<AgentEventDto>();
            var target = new ShellRpcTarget(
                new FakeIdeServices(new FixedToolCatalog(payload)), ev => delivered.Add(ev), _ => { });

            var result = await target.ToolsInvokeAsync(new InvokeToolRequest("set_breakpoint", "{}"));

            var sent = Assert.Single(delivered);
            Assert.Equal("toolDone", sent.Type);
            Assert.Equal(payload, sent.Message);
            Assert.True(sent.Success);
            Assert.Equal(payload, result.ContentJson);
        }

        /// <summary>
        /// Two kinds must not arrive under one id. The view-model falls back to the tool-call id when
        /// deduping a side-channel delivery, so a shared value would make a breakpoint payload look like
        /// the same delivery as a test run and silently drop whichever came second.
        /// </summary>
        [Fact]
        public async Task TheTwoKindsArriveUnderDifferentIds()
        {
            var breakpoints = await DeliveredIdFor(BreakpointPayload(rows: 1));
            var testRun = await DeliveredIdFor("{\"resultKind\":\"testRun\",\"succeeded\":true}");

            Assert.NotNull(breakpoints);
            Assert.NotNull(testRun);
            Assert.NotEqual(testRun, breakpoints);
        }

        /// <summary>Ordinary tool output is not a card and must not fire a delivery at all.</summary>
        [Fact]
        public async Task OrdinaryToolOutputIsNotDelivered()
        {
            Assert.Null(await DeliveredIdFor("{\"opened\":\"C:\\\\ws\\\\A.cs\"}"));
        }

        // --- helpers ----------------------------------------------------------------------------------

        private static async Task<string?> DeliveredIdFor(string payload)
        {
            var delivered = new List<AgentEventDto>();
            var target = new ShellRpcTarget(
                new FakeIdeServices(new FixedToolCatalog(payload)), ev => delivered.Add(ev), _ => { });

            await target.ToolsInvokeAsync(new InvokeToolRequest("some_tool", "{}"));

            return delivered.SingleOrDefault()?.ToolCallId;
        }

        private static string? MapCompletion(string resultText)
        {
            var frame = JsonSerializer.Serialize(new
            {
                sessionUpdate = "tool_call_update",
                toolCallId = "t2",
                status = "completed",
                rawOutput = resultText,
            });
            var update = JsonDocument.Parse(frame).RootElement.Clone();
            return AcpMapper.Map(update).OfType<AgentEvent.ToolCallCompleted>().Single().ResultText;
        }

        /// <summary>
        /// The breakpoint result as the catalog will serialize it: the marker FIRST — every reader keys
        /// off the prefix — then the outcome and the rows, each carrying the agent's reason.
        /// </summary>
        private static string BreakpointPayload(int rows = 14)
        {
            var entries = Enumerable.Range(1, rows).Select(i => new
            {
                file = $"C:\\ws\\src\\Some\\Deeply\\Nested\\Project\\File{i}.cs",
                line = i * 7,
                condition = $"order.Items.Count == 0 && index == {i}",
                hitCount = (int?)null,
                printMessage = $"item {i}: count={{order.Items.Count}}",
                reason = "the loop drops one element on the " + new string('x', 60) + " path",
                status = "set",
            }).ToArray();

            return JsonSerializer.Serialize(new
            {
                resultKind = "breakpoints",
                requestId = "b7f1c0d2",
                set = rows,
                failed = 0,
                breakpoints = entries,
            });
        }

        private sealed class FixedToolCatalog : IToolCatalog
        {
            private readonly string _json;

            public FixedToolCatalog(string json) => _json = json;

            public IReadOnlyList<ToolDescriptor> Tools { get; } =
                new[] { new ToolDescriptor("set_breakpoint", "sets breakpoints", "{}") };

            public Task<ToolResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ToolResult(IsError: false, _json));
        }

        private sealed class FakeIdeServices : IIdeServices
        {
            public FakeIdeServices(IToolCatalog tools) => Tools = tools;
            public IWorkspaceContext Workspace => throw new NotSupportedException();
            public IEditApplier Edits => throw new NotSupportedException();
            public IToolCatalog Tools { get; }
            public IPermissionHandler Permissions => throw new NotSupportedException();
        }
    }
}
