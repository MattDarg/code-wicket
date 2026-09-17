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
    /// A read tool row must name the file it read (issue #102). Backends disagree about whose job that
    /// is: the Claude adapter opens the row "Read File" and enriches the title to "Read &lt;path&gt;" on a
    /// later update, while Kiro's v3 engine sends "Read File" on the open AND again on the completion,
    /// never naming the file — though its rawInput and ACP <c>locations</c> both carry the path. So the
    /// row shows the target itself when, and only when, the title doesn't already say it.
    /// <para>
    /// The rawInput strings here are copied verbatim from a live capture (kiro-cli
    /// <c>--agent-engine v3</c> via the Console <c>kiro-read</c> proof, and a claude-agent-acp Read from
    /// acp.log) — including v3's explicit <c>"offset":null</c>, which is not what an ACP reader would
    /// guess. Driven through the session replay so it exercises <c>ChatViewModel.Apply</c>, the one path
    /// live events and restored transcripts share.
    /// </para>
    /// </summary>
    public sealed class ToolRowFileTargetTests : IDisposable
    {
        private readonly string _root;

        public ToolRowFileTargetTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cwkt-toolrow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best effort scratch cleanup */ }
        }

        private string Path_(params string[] parts) => System.IO.Path.Combine(new[] { _root }.Concat(parts).ToArray());

        /// <summary>Kiro v3's read tool_call, exactly as captured.</summary>
        private string KiroV3ReadInput(string absolutePath) =>
            "{\"path\":\"" + absolutePath.Replace("\\", "\\\\") + "\",\"offset\":null,\"limit\":null}";

        // ---- the reported defect -------------------------------------------------------------

        [Fact]
        public void KiroV3ReadRow_NamesTheFileItRead() => RunSta(() =>
        {
            var row = ReplayToolRow(
                new AgentEventDto
                {
                    Type = "toolStart",
                    ToolCallId = "tooluse_1",
                    Title = "Read File",
                    Kind = "read",
                    RawInputJson = KiroV3ReadInput(Path_("src", "Foo.cs")),
                });

            Assert.True(row.HasTargetPath);
            Assert.Equal("src/Foo.cs", row.TargetDisplayPath);
        });

        /// <summary>
        /// v3 repeats the same generic title on the completion frame. That resets the row's title, so the
        /// target label has to survive it — otherwise the file name appears while the call runs and
        /// vanishes the moment it finishes, which is the worst of both.
        /// </summary>
        [Fact]
        public void KiroV3CompletionFrame_KeepsTheFileNamed() => RunSta(() =>
        {
            var input = KiroV3ReadInput(Path_("src", "Foo.cs"));
            var row = ReplayToolRow(
                new AgentEventDto { Type = "toolStart", ToolCallId = "t", Title = "Read File", Kind = "read", RawInputJson = input },
                new AgentEventDto { Type = "toolUpdate", ToolCallId = "t", Title = "Read File", RawInputJson = input });

            Assert.Equal("src/Foo.cs", row.TargetDisplayPath);
        });

        // ---- and what must NOT change --------------------------------------------------------

        /// <summary>
        /// Claude names the file in the title itself. Repeating it beside the title would be noise on
        /// every read row it sends — which is most of them.
        /// </summary>
        [Fact]
        public void ClaudeReadRow_DoesNotRepeatAPathItsTitleAlreadyNames() => RunSta(() =>
        {
            var row = ReplayToolRow(
                new AgentEventDto
                {
                    Type = "toolStart",
                    ToolCallId = "toolu_1",
                    Title = @"Read src\Foo.cs",
                    Kind = "read",
                    RawInputJson = "{\"file_path\":\"" + Path_("src", "Foo.cs").Replace("\\", "\\\\") + "\"}",
                });

            Assert.False(row.HasTargetPath);
            Assert.Null(row.TargetDisplayPath);
        });

        /// <summary>
        /// The Claude adapter's real sequence: a placeholder row with <c>rawInput:{}</c>, then an update
        /// carrying both the arguments and a title that names the file. The label earns its place on the
        /// placeholder and must give it back when the title catches up.
        /// </summary>
        [Fact]
        public void AnEnrichedTitleTakesTheLabelBack() => RunSta(() =>
        {
            var path = Path_("src", "Foo.cs");
            var row = ReplayToolRow(
                new AgentEventDto { Type = "toolStart", ToolCallId = "t", Title = "Read File", Kind = "read", RawInputJson = "{}" },
                new AgentEventDto
                {
                    Type = "toolUpdate",
                    ToolCallId = "t",
                    Title = @"Read src\Foo.cs (from line 40)",
                    RawInputJson = "{\"file_path\":\"" + path.Replace("\\", "\\\\") + "\",\"offset\":40}",
                });

            Assert.False(row.HasTargetPath);
        });

        /// <summary>
        /// The label is derived from the title, so a title that arrives on its own — an enrichment that
        /// re-sends no arguments, which is the only frame that wouldn't re-attach the targets — has to
        /// re-raise it. Nothing else notices that change, and a bound row would keep showing a path its
        /// title now repeats.
        /// </summary>
        [Fact]
        public void ATitleChangeAlone_RepublishesTheLabel()
        {
            var row = new ToolItemViewModel("t", "Read File", "read");
            row.AttachFileTargets(new[] { (@"C:\ws\src\Foo.cs", 0) }, null, @"C:\ws");
            Assert.True(row.HasTargetPath);

            var raised = false;
            row.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(ToolItemViewModel.TargetDisplayPath);
            row.Title = @"Read src\Foo.cs";

            Assert.True(raised);
            Assert.False(row.HasTargetPath);
        }

        /// <summary>
        /// The read route publishes the copyable path, which it did not.
        /// <c>PathToCopy</c> is <c>_diffPath ?? FilePath</c>, so it genuinely changes here, and
        /// <c>HasPathToCopy</c> flips false to true the moment the first target lands.
        /// </summary>
        /// <remarks>
        /// The consequence is not a stale string but a missing menu item: "Copy path" binds its
        /// Visibility to <c>HasPathToCopy</c> through <c>PlacementTarget.DataContext</c>, and
        /// <c>PlacementTarget</c> does not change once the menu has been opened on that row. Open it on
        /// a placeholder Read row (Claude sends <c>rawInput:{}</c>), close it, let the enriching update
        /// arrive with the path: with no notification the item stays Collapsed for the row's whole life.
        /// The diff route has always raised both — this is the same row reaching the same state by the
        /// other road.
        /// </remarks>
        [Fact]
        public void AReadTargetPublishesTheCopyablePath()
        {
            var row = new ToolItemViewModel("t", "Read File", "read");

            var raised = new List<string?>();
            row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            row.AttachFileTargets(new[] { (@"C:\ws\src\Foo.cs", 0) }, null, @"C:\ws");

            Assert.True(row.HasPathToCopy);
            Assert.Contains(nameof(ToolItemViewModel.PathToCopy), raised);
            Assert.Contains(nameof(ToolItemViewModel.HasPathToCopy), raised);
        }

        /// <summary>
        /// ...and re-queries the commands, which a property change does not do.
        /// <c>RelayCommand</c> deliberately does not hook <c>CommandManager.RequerySuggested</c>, and
        /// <c>MenuItem</c> is an <c>ICommandSource</c> that LATCHES <c>IsEnabled</c> from the last
        /// <c>CanExecuteChanged</c> it saw. Without this the menu item on an enriched row appears —
        /// Visibility is bound to a property that IS raised — and is greyed out, offering something the
        /// row can now do.
        /// </summary>
        [Fact]
        public void AReadTargetRequeriesTheRowCommands()
        {
            var row = new ToolItemViewModel("t", "Read File", "read");
            var requeried = 0;
            row.OpenFileCommand.CanExecuteChanged += (_, _) => requeried++;
            row.CopyPathCommand.CanExecuteChanged += (_, _) => requeried++;

            row.AttachFileTargets(new[] { (@"C:\ws\src\Foo.cs", 0) }, (_, _) => Task.CompletedTask, @"C:\ws");

            Assert.True(row.CopyPathCommand.CanExecute(null));
            Assert.True(requeried >= 2);
        }

        /// <summary>
        /// A finalized re-send that carries no line must not blank one already resolved — 70% of edits
        /// report no line at all, so an unconditional assignment loses the answer far more often than it
        /// sets one. The mirror of the edit card's bug, which froze the first value instead.
        /// </summary>
        [Fact]
        public void AFoldedDiffKeepsItsLineWhenAReSendOmitsOne()
        {
            // Read through the diff callback, which is where the line actually goes — the row keeps it
            // privately, and asserting on a getter added for the test would pin the getter.
            int? opened = null;
            var row = new ToolItemViewModel("t", "Edit", "edit");
            Assert.True(row.TryAttachDiff(
                @"C:\ws\a.cs", "was", "now",
                (_, _, _, line) => { opened = line; return Task.CompletedTask; }, line: 42));

            row.TryAttachDiff(@"C:\ws\a.cs", "was", "now, really", null, line: null);
            row.OpenDiffCommand.Execute(null);

            Assert.Equal(42, opened);
        }

        /// <summary>And a re-send that DOES carry one replaces it, which is the point of a re-send.</summary>
        [Fact]
        public void AFoldedDiffTakesALineAReSendActuallyCarries()
        {
            int? opened = null;
            var row = new ToolItemViewModel("t", "Edit", "edit");
            Assert.True(row.TryAttachDiff(
                @"C:\ws\a.cs", "was", "now",
                (_, _, _, line) => { opened = line; return Task.CompletedTask; }, line: 42));

            row.TryAttachDiff(@"C:\ws\a.cs", "was", "now, really", null, line: 99);
            row.OpenDiffCommand.Execute(null);

            Assert.Equal(99, opened);
        }

        /// <summary>
        /// The edit CARD's half of the same rule, which had it exactly backwards. Its own doc says "a
        /// null line keeps the current one" — but <c>??=</c> keeps the FIRST value, so a provisional
        /// line was frozen and the finalized report, the whole reason a re-send exists, was discarded.
        /// </summary>
        [Fact]
        public void AnEditCardTakesTheLineAFinalizedReSendCarries()
        {
            int? opened = null;
            var card = new EditItemViewModel(
                @"C:\ws.cs", "was", "now",
                (_, _, _, line) => { opened = line; return Task.CompletedTask; },
                line: 1);

            card.Update("was", "now, really", line: 42);
            card.OpenDiffCommand.Execute(null);

            Assert.Equal(42, opened);
        }

        /// <summary>...and a re-send carrying none still keeps what is there, as the doc says.</summary>
        [Fact]
        public void AnEditCardKeepsItsLineWhenAReSendOmitsOne()
        {
            int? opened = null;
            var card = new EditItemViewModel(
                @"C:\ws.cs", "was", "now",
                (_, _, _, line) => { opened = line; return Task.CompletedTask; },
                line: 42);

            card.Update("was", "now, really", line: null);
            card.OpenDiffCommand.Execute(null);

            Assert.Equal(42, opened);
        }

        // ---- shapes the label has to survive -------------------------------------------------

        /// <summary>
        /// Kiro batches several files into one read (its <c>operations</c> array) and the row opens all
        /// of them on click, so the label has to say there are more — the tooltip lists them, but nobody
        /// hovers a row that looks like it read one file.
        /// </summary>
        [Fact]
        public void ABatchedRead_NamesTheFirstAndCountsTheRest() => RunSta(() =>
        {
            var row = ReplayToolRow(
                new AgentEventDto
                {
                    Type = "toolStart",
                    ToolCallId = "t",
                    Title = "Read File",
                    Kind = "read",
                    RawInputJson =
                        "{\"operations\":[" +
                        "{\"path\":\"" + Path_("src", "Foo.cs").Replace("\\", "\\\\") + "\",\"mode\":\"Line\"}," +
                        "{\"path\":\"" + Path_("src", "Bar.cs").Replace("\\", "\\\\") + "\",\"mode\":\"Line\"}," +
                        "{\"path\":\"" + Path_("src", "Baz.cs").Replace("\\", "\\\\") + "\",\"mode\":\"Line\"}]}",
                });

            Assert.Equal("src/Foo.cs  +2 more", row.TargetDisplayPath);
        });

        /// <summary>
        /// Kiro <b>v3</b> batches the same way but sends a flat <c>paths</c> array of STRINGS, where the
        /// older engine sends an array of objects. Reported from a real session as a column of rows all
        /// reading "Read Files" and naming nothing: an unrecognised shape does not throw and does not
        /// warn, it silently stops labelling the row and stops making it clickable, leaving the paths
        /// visible only in the detail panel the user has to open by hand.
        /// </summary>
        [Fact]
        public void AV3BatchedRead_NamesTheFirstAndCountsTheRest() => RunSta(() =>
        {
            var row = ReplayToolRow(
                new AgentEventDto
                {
                    Type = "toolStart",
                    ToolCallId = "t",
                    Title = "Read Files",
                    Kind = "read",
                    RawInputJson =
                        "{\"paths\":[" +
                        "\"" + Path_("src", "Foo.cs").Replace("\\", "\\\\") + "\"," +
                        "\"" + Path_("src", "Bar.cs").Replace("\\", "\\\\") + "\"," +
                        "\"" + Path_("src", "Baz.cs").Replace("\\", "\\\\") + "\"]," +
                        "\"start_line\":null,\"end_line\":null," +
                        "\"explanation\":\"Reading CrimsSimulator files.\"}",
                });

            Assert.Equal("src/Foo.cs  +2 more", row.TargetDisplayPath);
            Assert.True(row.CanOpenFile);
        });

        /// <summary>
        /// v3 names the start line <c>start_line</c> where Claude's Read calls it <c>offset</c>, so the
        /// row has to accept both spellings or a v3 read opens at the top of the file. The LABEL never
        /// carries a line — that is by design, it names the file — so the line is asserted where it is
        /// actually used: the open.
        /// </summary>
        [Fact]
        public void AV3Read_OpensAtStartLine() => RunSta(() =>
        {
            var opened = new List<(string Path, int Line)>();
            var row = ReplayToolRow(
                (path, line) => { opened.Add((path, line)); return Task.CompletedTask; },
                new AgentEventDto
                {
                    Type = "toolStart", ToolCallId = "t", Title = "Read Files", Kind = "read",
                    RawInputJson =
                        "{\"paths\":[\"" + Path_("src", "Foo.cs").Replace("\\", "\\\\") + "\"]," +
                        "\"start_line\":42,\"end_line\":80}",
                });

            Assert.Equal("src/Foo.cs", row.TargetDisplayPath);
            row.OpenFileCommand.Execute(null);

            var target = Assert.Single(opened);
            Assert.Equal(42, target.Line);
        });

        /// <summary>
        /// A file outside the workspace stays absolute, exactly as the edit card shows one — a read of
        /// something elsewhere on disk should look different from a read inside the solution.
        /// </summary>
        [Fact]
        public void AFileOutsideTheWorkspace_StaysAbsolute() => RunSta(() =>
        {
            var outside = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwkt-elsewhere", "Other.cs");
            var row = ReplayToolRow(
                new AgentEventDto
                {
                    Type = "toolStart", ToolCallId = "t", Title = "Read File", Kind = "read",
                    RawInputJson = KiroV3ReadInput(outside),
                });

            Assert.Equal(outside, row.TargetDisplayPath);
        });

        /// <summary>
        /// Copy and the markdown export go out to a bug report or a colleague, where "Read File" with no
        /// file is the same defect one step further from anyone who can see the screen.
        /// </summary>
        [Fact]
        public void CopyAndExport_CarryTheFileName() => RunSta(() =>
        {
            var row = ReplayToolRow(
                new AgentEventDto
                {
                    Type = "toolStart", ToolCallId = "t", Title = "Read File", Kind = "read",
                    RawInputJson = KiroV3ReadInput(Path_("src", "Foo.cs")),
                });

            Assert.Contains("src/Foo.cs", row.CopyText);
            Assert.Contains("src/Foo.cs", TranscriptMarkdown.Build("t", new[] { (ChatItemViewModel)row }));
        });

        // ---- harness -------------------------------------------------------------------------

        /// <summary>
        /// Replays the given events through the real restore path and returns the single tool row they
        /// produced. Using the session store (rather than poking the view-model) keeps the test on
        /// <c>ChatViewModel.Apply</c>, which is also what live events run through.
        /// </summary>
        private ToolItemViewModel ReplayToolRow(params AgentEventDto[] events)
            => ReplayToolRow(null, events);

        private ToolItemViewModel ReplayToolRow(
            Func<string, int, Task>? openFile, params AgentEventDto[] events)
        {
            var store = new FileSessionStore(System.IO.Path.Combine(_root, "sessions"));
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
                new StartSessionRequest("fake", null, _root, "Prompt", null),
                sessionStore: store,
                openFile: openFile ?? ((_, _) => Task.CompletedTask));
            vm.RestoreMostRecentSession();

            return Assert.Single(vm.Items.OfType<ToolItemViewModel>());
        }

        /// <summary>A ChatViewModel needs an engine to construct; the replay path never calls one.</summary>
        private sealed class OfflineEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new System.Collections.Generic.List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not open a backend session.");

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not prompt.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not summarize.");
        }

        // WPF text elements demand an STA thread; xunit runs MTA.
        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);
    }
}
