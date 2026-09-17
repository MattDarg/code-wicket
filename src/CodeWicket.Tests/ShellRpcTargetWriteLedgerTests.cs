using System;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The shell's RPC target is the one point every agent write passes through in the shell process,
    /// and it feeds the ledger the IDE's build and test tools check (issue #257). Two routes, measured
    /// on the wire: a backend that writes for itself reports the diff as an agent event; a backend on
    /// the client-fs route writes through <c>WriteAsync</c> here (and usually reports the diff too).
    /// </summary>
    public sealed class ShellRpcTargetWriteLedgerTests
    {
        private const string Written = @"C:\ws\proj\Canary.cs";

        private sealed class FakeEdits : IEditApplier
        {
            public Task<string> ReadTextFileAsync(string path, int? line = null, int? limit = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            // The applier resolves the agent's spelling to the real path — that spelling is the one recorded.
            public Task<FileWriteResult?> WriteTextFileAsync(string path, string content, CancellationToken cancellationToken = default) =>
                Task.FromResult<FileWriteResult?>(new FileWriteResult(Written, string.Empty, content));

            public Task ShowDiffPreviewAsync(string path, string oldText, string newText, int? reportedLine = null, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }

        private sealed class FakeIdeServices : IIdeServices
        {
            public IWorkspaceContext Workspace => throw new NotSupportedException();
            public IEditApplier Edits { get; } = new FakeEdits();
            public IToolCatalog Tools => throw new NotSupportedException();
            public Core.IPermissionHandler Permissions => throw new NotSupportedException();
        }

        [Fact]
        public void AnEditEventRecordsItsPathAndIsStillForwarded()
        {
            var ledger = new AgentWriteLedger();
            AgentEventDto? forwarded = null;
            var target = new ShellRpcTarget(new FakeIdeServices(), ev => forwarded = ev, _ => { }, ledger);

            target.OnAgentEvent(new AgentEventDto { Type = "edit", ToolCallId = "t1", Path = Written, OldText = "", NewText = "x" });

            Assert.True(ledger.Contains(Written));
            Assert.NotNull(forwarded);
            Assert.Equal("edit", forwarded!.Type);
        }

        /// <summary>Only a write is a write: a tool row, a text chunk or an edit with no path records nothing.</summary>
        [Fact]
        public void OtherEventsAndAPathlessEditRecordNothing()
        {
            var ledger = new AgentWriteLedger();
            var target = new ShellRpcTarget(new FakeIdeServices(), _ => { }, _ => { }, ledger);

            target.OnAgentEvent(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "Write File" });
            target.OnAgentEvent(new AgentEventDto { Type = "text", Text = Written });
            target.OnAgentEvent(new AgentEventDto { Type = "edit", ToolCallId = "t2", Path = null });

            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public async Task AClientFsWriteRecordsTheResolvedPath()
        {
            var ledger = new AgentWriteLedger();
            var target = new ShellRpcTarget(new FakeIdeServices(), _ => { }, _ => { }, ledger);

            var response = await target.WriteAsync(new WriteFileRequest("proj/Canary.cs", "content"));

            Assert.Equal(Written, response.ResolvedPath);
            Assert.True(ledger.Contains(Written));
            Assert.Equal(1, ledger.Count);
        }

        /// <summary>A host with no ledger — the Desktop and Console hosts — is unchanged.</summary>
        [Fact]
        public async Task NoLedgerIsFine()
        {
            var target = new ShellRpcTarget(new FakeIdeServices(), _ => { }, _ => { });

            target.OnAgentEvent(new AgentEventDto { Type = "edit", ToolCallId = "t1", Path = Written });
            var response = await target.WriteAsync(new WriteFileRequest("proj/Canary.cs", "content"));

            Assert.Equal(Written, response.ResolvedPath);
        }
    }
}
