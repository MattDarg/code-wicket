using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// An agent edit reaches the transcript as one of two shapes, and both must offer the same
    /// things. Which shape you get is decided in <c>AcpMapper</c> by whether the call's OPENING frame
    /// already carries an edit:
    /// <list type="bullet">
    /// <item><b>Kiro v3</b> — the opening <c>tool_call</c> yields an edit (its blank-diff fallback), so
    /// <c>ToolCallStarted</c> is suppressed, no tool row is ever built, and the edit renders as an
    /// <c>EditItemViewModel</c> card — which has always had "Open file" and "Copy path".</item>
    /// <item><b>Claude Code</b> — opens as a bare placeholder (<c>rawInput:{file_path}</c>), so a tool
    /// row exists by the time the diff arrives and the diff FOLDS into it. That row could be diffed and
    /// never opened: it knew the file, used it for the diff, and offered no way to go there.</item>
    /// </list>
    /// The event sequence below is the captured Claude shape (acp.log, 2026-08-08): row first, diff on a
    /// later update for the same <c>toolCallId</c>, the edit line arriving with it.
    /// <para>Driven through the session replay so it exercises <c>ChatViewModel.Apply</c> — the one path
    /// live events and restored transcripts share.</para>
    /// </summary>
    public sealed class FoldedEditRowTests : IDisposable
    {
        private const string EditCallId = "toolu_01827J9KspHqhX8Xuxe9nPH6";

        private readonly string _root;
        private readonly List<(string Path, string OldText, string NewText, int? Line)> _openedAtDiff = new();
        private readonly List<(string Path, int Line)> _openedPlain = new();

        public FoldedEditRowTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cwkt-folded-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best effort scratch cleanup */ }
        }

        /// <summary>The captured Claude Edit sequence: a row opens, then the diff folds into it.</summary>
        private static AgentEventDto[] ClaudeEdit(string path) => new[]
        {
            new AgentEventDto
            {
                Type = "toolStart",
                ToolCallId = EditCallId,
                Title = @"Edit Bookshelf.Tests\Message\TepMessageSerializationTests.cs",
                Kind = "edit",
            },
            new AgentEventDto
            {
                Type = "edit",
                ToolCallId = EditCallId,
                Path = path,
                OldText = "var json = JsonSerializer.Serialize(orderedDictionary)",
                NewText = "var json = JsonSerializer.Serialize(orderedDictionary);",
                EditLine = 21,
            },
        };

        [Fact]
        public void AClaudeEditFoldsIntoItsToolRowRatherThanAddingACard()
        {
            var vm = Replay(ClaudeEdit(Path.Combine(_root, "Tep.cs")));

            Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Empty(vm.Items.OfType<EditItemViewModel>());
            Assert.True(vm.Items.OfType<ToolItemViewModel>().Single().HasDiff);
        }

        /// <summary>
        /// The gap this fixes. The row knows the file — it diffs it — so "Open file" has to be offered,
        /// or the same act behaves differently depending on which backend produced it.
        /// </summary>
        [Fact]
        public void AFoldedEditRowCanOpenItsFile()
        {
            var row = ReplayToolRow(ClaudeEdit(Path.Combine(_root, "Tep.cs")));

            Assert.True(row.CanOpenFile);
            Assert.True(row.OpenFileCommand.CanExecute(null));
        }

        /// <summary>
        /// It must take the DIFF route, not the read-target one. The distinction is the whole point of
        /// opening a card at the edit: a reported line is where the edit was when it was made, so the host re-locates
        /// the change in the file as it stands now. Routing through the plain open-file callback would
        /// quietly flatten that back to the reported line — or to 0.
        /// </summary>
        [Fact]
        public void OpeningAFoldedEditGoesThroughTheDiffRouteWithItsText()
        {
            var path = Path.Combine(_root, "Tep.cs");
            var row = ReplayToolRow(ClaudeEdit(path));

            row.OpenFileCommand.Execute(null);

            var opened = Assert.Single(_openedAtDiff);
            Assert.Equal(path, opened.Path);
            Assert.Equal(21, opened.Line);
            Assert.Contains("orderedDictionary);", opened.NewText);
            Assert.Empty(_openedPlain); // never the reported-line route
        }

        /// <summary>
        /// A host that can't locate a change in the file — only the VS one can, since it has to read
        /// the file to do it — must still offer "Open file", degrading to the reported line. Found by
        /// the Desktop smoke, not by a unit test: the tests above wire both callbacks, so the row was
        /// only ever exercised in its best case, while the Desktop host wires neither onto a folded row
        /// and showed no "Open file" at all. The edit CARD has always degraded this way.
        /// </summary>
        [Fact]
        public void WithoutADiffLocatingHostTheRowStillOpensAtTheReportedLine()
        {
            var path = Path.Combine(_root, "Tep.cs");
            var row = ReplayToolRow(ClaudeEdit(path), diffLocatingHost: false);

            Assert.True(row.CanOpenFile);
            row.OpenFileCommand.Execute(null);

            var opened = Assert.Single(_openedPlain);
            Assert.Equal(path, opened.Path);
            Assert.Equal(21, opened.Line); // the reported line, since nothing can re-locate it
        }

        [Fact]
        public void AFoldedEditRowCopiesTheEditedPath()
        {
            var path = Path.Combine(_root, "Tep.cs");
            var row = ReplayToolRow(ClaudeEdit(path));

            Assert.True(row.HasPathToCopy);
            Assert.Equal(path, row.PathToCopy);
        }

        /// <summary>
        /// A row that acted on no file offers neither item. Both are bound to visibility rather than
        /// enablement, so getting this wrong doesn't grey something out — it puts "Open file" on a
        /// build or a search, where it would do nothing at all.
        /// </summary>
        [Fact]
        public void ARowThatTouchedNoFileOffersNeitherItem()
        {
            var row = ReplayToolRow(new AgentEventDto
            {
                Type = "toolStart",
                ToolCallId = "call_build",
                Title = "@code-wicket/build_solution",
                Kind = "other",
            });

            Assert.False(row.CanOpenFile);
            Assert.False(row.HasPathToCopy);
        }

        /// <summary>
        /// Regression guard for the other route: a READ row still opens through the plain callback at
        /// the reported line. The two resolve their destination differently and must not be merged.
        /// </summary>
        [Fact]
        public void AReadRowStillOpensThroughThePlainRoute()
        {
            var path = Path.Combine(_root, "Read.cs");
            File.WriteAllText(path, "line1\nline2\n");

            var row = ReplayToolRow(new AgentEventDto
            {
                Type = "toolStart",
                ToolCallId = "call_read",
                Title = "Read File",
                Kind = "read",
                RawInputJson = "{\"path\":\"" + path.Replace("\\", "\\\\") + "\"}",
            });

            Assert.True(row.CanOpenFile);
            row.OpenFileCommand.Execute(null);

            Assert.Single(_openedPlain);
            Assert.Empty(_openedAtDiff);
        }

        // ---- issue #178: a folded edit must NAME the file it edited -------------------------------

        /// <summary>
        /// The reported defect. The row carried the path — <c>PathToCopy</c> and <c>CanOpenFile</c> both
        /// read it, which is exactly why the right-click menu worked — while the LABEL beside the title
        /// consulted <c>_fileTargets</c> and nothing else, and only a READ ever fills that. So a folded
        /// edit rendered as a bare title with the file it edited nowhere on screen.
        /// <para>
        /// Titled with Kiro v3's generic "Write File" rather than Claude's self-naming title, because a
        /// title that already names the file is deliberately suppressed by the test below — pinning this
        /// on the Claude shape would assert nothing.
        /// </para>
        /// </summary>
        [Fact]
        public void AFoldedEditRowNamesTheFileItEdited()
        {
            var row = ReplayToolRow(KiroV3EmptyWrite(Path.Combine(_root, "temp.txt")));

            Assert.True(row.HasTargetPath);
            Assert.Equal("temp.txt", row.TargetDisplayPath);
        }

        /// <summary>
        /// Rendered relative to the workspace root, like every read row. <c>TryAttachDiff</c> has to be
        /// GIVEN that root — nothing else on the edit route supplies it — or the same row shows an
        /// absolute path where a read shows a relative one, which is the kind of difference nobody
        /// notices is a bug and everybody notices is untidy.
        /// </summary>
        [Fact]
        public void AFoldedEditPathIsWorkspaceRelativeWithTheFullPathAvailable()
        {
            var path = Path.Combine(_root, "src", "Deep.cs");
            var row = ReplayToolRow(KiroV3EmptyWrite(path));

            Assert.Equal("src/Deep.cs", row.TargetDisplayPath);
            Assert.Equal(path, row.FileTargetToolTip); // the full path is still reachable
        }

        /// <summary>
        /// The suppression that makes the label worth having survives the new route: Claude titles its
        /// edit with the file, so repeating it beside the title would say the same thing twice. Asserted
        /// in this direction as well as the one above, or the getter could pass by never showing a path.
        /// </summary>
        [Fact]
        public void AFoldedEditWhoseTitleAlreadyNamesTheFileDoesNotRepeatIt()
        {
            var row = ReplayToolRow(ClaudeEdit(Path.Combine(_root, "TepMessageSerializationTests.cs")));

            Assert.False(row.HasTargetPath);
            Assert.Null(row.TargetDisplayPath);
        }

        /// <summary>
        /// The pasted row too — <c>CopyText</c> shares the same head, and a paste of one of these is
        /// usually on its way into a bug report, where a title naming no file is exactly as unhelpful as
        /// it is on screen.
        /// <para>
        /// Asserted on the HEAD LINE, not on the whole text, and that is the whole value of the check.
        /// Searching all of <c>CopyText</c> passes with the fix injected out —
        /// because the row's expandable detail is the call's rawInput, which carries the path anyway. It
        /// cannot separate "the label names the file" from "the argument dump happens to mention it",
        /// which is the one distinction it exists for. Caught by <c>prove-check.ps1</c>, not by reading.
        /// </para>
        /// </summary>
        [Fact]
        public void AFoldedEditRowPastesTheFileItEditedOnItsHeadLine()
        {
            var row = ReplayToolRow(KiroV3EmptyWrite(Path.Combine(_root, "temp.txt")));

            var head = row.CopyText!.Split('\n')[0];
            Assert.Equal("Write File  temp.txt", head);
        }

        // ---- issue #189: a failed edit CARD must say what failed ----------------------------------

        /// <summary>
        /// The reported defect's second half. Under Kiro v3 an ordinary write renders as a CARD (its
        /// opening frame already carries the edit), and the <c>toolDone</c> card branch set the pencil's
        /// colour and stopped — so a write that failed showed red and gave the user nowhere to ask why,
        /// with the reason already in hand one step from the screen.
        /// </summary>
        [Fact]
        public void AFailedEditCardKeepsTheErrorReachable()
        {
            var card = ReplayEditCard(
                new AgentEventDto
                {
                    Type = "edit",
                    ToolCallId = "tooluse_v3write",
                    Path = Path.Combine(_root, "temp.txt"),
                    OldText = "",
                    NewText = "hello",
                },
                new AgentEventDto
                {
                    Type = "toolDone",
                    ToolCallId = "tooluse_v3write",
                    Success = false,
                    ErrorText = "Unspecified error (Exception from HRESULT: 0x80004005 (E_FAIL))",
                });

            Assert.Equal(ToolStatus.Failed, card.Status);
            Assert.True(card.HasErrorDetail);
            Assert.Contains("0x80004005", card.ErrorDetail);
            Assert.True(card.CanExpand); // and there is a chevron to reach it by
        }

        /// <summary>
        /// A completed write's own account reaches the card as well — captured 2026-09-03, v3 answers
        /// <c>{"message":"Created the …\\temp.txt file."}</c>, which is the only thing it says about the
        /// call at all.
        /// </summary>
        [Fact]
        public void ASucceededEditCardKeepsTheBackendsMessage()
        {
            var card = ReplayEditCard(
                new AgentEventDto
                {
                    Type = "edit",
                    ToolCallId = "tooluse_v3write",
                    Path = Path.Combine(_root, "temp.txt"),
                    OldText = "",
                    NewText = "hello",
                },
                new AgentEventDto
                {
                    Type = "toolDone",
                    ToolCallId = "tooluse_v3write",
                    Success = true,
                    Message = "Created the temp.txt file.",
                });

            Assert.Equal("Created the temp.txt file.", card.Detail);
            Assert.True(card.CanExpand);
        }

        /// <summary>
        /// The permission sentence alone opens the card. It was hover-only, which is to say
        /// undiscoverable — and the rule it broke ("the sentence in the expanded row is there for every
        /// settled row") is the one that matters most on v3, where the card IS the write.
        /// </summary>
        [Fact]
        public void ASettledEditCardCanBeExpandedForThePermissionSentenceAlone()
        {
            var card = ReplayEditCard(
                new AgentEventDto
                {
                    Type = "edit",
                    ToolCallId = "tooluse_v3write",
                    Path = Path.Combine(_root, "temp.txt"),
                    OldText = "",
                    NewText = "hello",
                },
                new AgentEventDto { Type = "toolDone", ToolCallId = "tooluse_v3write", Success = true });

            Assert.True(card.PermissionSettled);
            Assert.True(card.HasPermissionSummary);
            Assert.True(card.CanExpand);
        }

        /// <summary>A card whose call never settled stays closed — nothing to say yet is not nothing to say.</summary>
        [Fact]
        public void AnUnsettledEditCardOffersNoExpander()
        {
            var card = ReplayEditCard(new AgentEventDto
            {
                Type = "edit",
                ToolCallId = "tooluse_v3write",
                Path = Path.Combine(_root, "temp.txt"),
                OldText = "",
                NewText = "hello",
            });

            Assert.False(card.CanExpand);
        }

        // ---- helpers ------------------------------------------------------------------------------

        /// <summary>
        /// Kiro v3's write as it arrives when the content is EMPTY: the opening <c>tool_call</c> carries
        /// no <c>content</c> array (v3 never sends one for a write) and its <c>_meta.kiro.preview</c>
        /// holds a <c>file</c> and nothing else, so <c>ExtractKiroFragmentEdit</c> declines it and the
        /// call opens as a generic tool row titled "Write File" — the shape issue #178 reported. The
        /// diff then folds in on the later frame. Captured 2026-09-03.
        /// </summary>
        private static AgentEventDto[] KiroV3EmptyWrite(string path) => new[]
        {
            new AgentEventDto
            {
                Type = "toolStart",
                ToolCallId = "tooluse_5dMADk9VmxMADYIKK4lYJ9",
                Title = "Write File",
                Kind = "edit",
                RawInputJson = "{\"path\":\"" + path.Replace("\\", "\\\\") + "\",\"text\":\"\"}",
            },
            new AgentEventDto
            {
                Type = "edit",
                ToolCallId = "tooluse_5dMADk9VmxMADYIKK4lYJ9",
                Path = path,
                OldText = "",
                NewText = "",
            },
        };

        private EditItemViewModel ReplayEditCard(params AgentEventDto[] events) =>
            Assert.Single(Replay(events).Items.OfType<EditItemViewModel>());

        private ToolItemViewModel ReplayToolRow(params AgentEventDto[] events) =>
            Assert.Single(Replay(events).Items.OfType<ToolItemViewModel>());

        private ToolItemViewModel ReplayToolRow(AgentEventDto[] events, bool diffLocatingHost) =>
            Assert.Single(Replay(events, diffLocatingHost).Items.OfType<ToolItemViewModel>());

        private ChatViewModel Replay(params AgentEventDto[] events) => Replay(events, true);

        private ChatViewModel Replay(AgentEventDto[] events, bool diffLocatingHost)
        {
            var store = new FileSessionStore(Path.Combine(_root, "sessions"));
            var session = new PersistedSession
            {
                WorkspaceRootPath = _root,
                AgentWorkingDirectory = _root,
                Title = "Replay",
            };
            foreach (var ev in events)
                session.Log.Add(new TranscriptEntry { Role = "agent", Event = ev });
            store.Save(session);

            var vm = new ChatViewModel(
                new OfflineEngine(),
                new StartSessionRequest("claude-code", null, _root, "Prompt", null),
                openDiff: (_, _, _, _) => Task.CompletedTask,
                sessionStore: store,
                openFile: (p, line) => { _openedPlain.Add((p, line)); return Task.CompletedTask; },
                openFileAtDiff: diffLocatingHost
                    ? (p, oldText, newText, line) =>
                    {
                        _openedAtDiff.Add((p, oldText, newText, line));
                        return Task.CompletedTask;
                    }
                    : null);
            vm.RestoreMostRecentSession();
            return vm;
        }

        private sealed class OfflineEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A replay must not open a backend session.");

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A replay must not prompt.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A replay must not steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A replay must not summarize.");
        }
    }
}
