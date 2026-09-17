using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Issue #23: clicking Stop cancels the agent's ACP turn, but the agent abandoning its MCP
    /// tools/call never cancels OUR tool invocation — a hung run_tests shellout kept running until
    /// its internal 15-minute timeout. ShellRpcTarget must track in-flight invocations and
    /// CancelActiveToolInvocations (wired into EngineClient.CancelAsync/Dispose) must cut them short.
    /// </summary>
    public sealed class ShellRpcTargetCancellationTests
    {
        /// <summary>A tool catalog whose single tool blocks until its token cancels — mirroring the
        /// real VsToolCatalog contract of catching the OCE and returning an error result.</summary>
        private sealed class BlockingToolCatalog : IToolCatalog
        {
            public readonly TaskCompletionSource<bool> Started =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public IReadOnlyList<ToolDescriptor> Tools { get; } =
                new[] { new ToolDescriptor("block_forever", "blocks until cancelled", "{}") };

            public async Task<ToolResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            {
                Started.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return new ToolResult(IsError: false, "{\"unreachable\":true}");
                }
                catch (OperationCanceledException)
                {
                    return new ToolResult(IsError: true, "{\"error\":\"tool invocation was cancelled\"}");
                }
            }
        }

        private sealed class FakeIdeServices : IIdeServices
        {
            public FakeIdeServices(IToolCatalog tools) => Tools = tools;
            public IWorkspaceContext Workspace => throw new NotSupportedException();
            public IEditApplier Edits => throw new NotSupportedException();
            public IToolCatalog Tools { get; }
            public Core.IPermissionHandler Permissions => throw new NotSupportedException();
        }

        [Fact]
        public async Task CancelActiveToolInvocations_CutsShortABlockedInvocation()
        {
            var catalog = new BlockingToolCatalog();
            var target = new ShellRpcTarget(new FakeIdeServices(catalog), _ => { }, _ => { });

            var invocation = target.ToolsInvokeAsync(new InvokeToolRequest("block_forever", "{}"));
            await catalog.Started.Task; // the tool is genuinely in flight

            target.CancelActiveToolInvocations();

            var result = await invocation;
            Assert.True(result.IsError);
            Assert.Contains("cancelled", result.ContentJson);
        }

        [Fact]
        public async Task WireToken_AlsoCancelsTheInvocation()
        {
            var catalog = new BlockingToolCatalog();
            var target = new ShellRpcTarget(new FakeIdeServices(catalog), _ => { }, _ => { });

            using var wire = new CancellationTokenSource();
            var invocation = target.ToolsInvokeAsync(new InvokeToolRequest("block_forever", "{}"), wire.Token);
            await catalog.Started.Task;

            wire.Cancel();

            var result = await invocation;
            Assert.True(result.IsError);
        }

        [Fact]
        public async Task Cancel_AfterCompletion_IsANoOp()
        {
            var catalog = new BlockingToolCatalog();
            var target = new ShellRpcTarget(new FakeIdeServices(catalog), _ => { }, _ => { });

            var invocation = target.ToolsInvokeAsync(new InvokeToolRequest("block_forever", "{}"));
            await catalog.Started.Task;
            target.CancelActiveToolInvocations();
            await invocation;

            // The completed invocation was untracked and its CTS disposed — a later sweep must not throw.
            target.CancelActiveToolInvocations();
        }
    }
}
