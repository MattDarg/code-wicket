using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A sub-agent's calls can be opened as their own transcript, and the way back is a stack (issue
    /// #148).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The premise is a measurement, not a preference.</b> Nesting bounded an expanded fan-out with a
    /// five-child cap, but the overflow control under it dropped that cap in place — putting the whole
    /// fan-out back inside one container of a non-virtualizing <c>ItemsControl</c>, which the field log
    /// priced at a single tool row measuring <b>52–64 ms</b> against ~0.5 ms for an ordinary row, render
    /// passes over that one row at <b>1691–3352 ms</b>, and dispatcher duty at <b>73–89 %</b> with no
    /// agent traffic in the window at all. Drilling in hands the same calls to the transcript's
    /// virtualising panel, so the unbounded inline case stops existing rather than being deferred behind
    /// a click.
    /// </para>
    /// <para>
    /// <b>What these tests cannot reach, and what covers it.</b> Everything here is view-model state, and
    /// the whole claim of the feature is that the sub-view VIRTUALISES — which is invisible from here and
    /// would stay green if the binding were pointed back at a non-virtualizing control. That is the
    /// Desktop host's <c>--smoke</c> phase, which counts realised child containers on a 200-child fan-out.
    /// Likewise the scroll positions restored on the way back live in the view, and are asserted there.
    /// </para>
    /// </remarks>
    public sealed class TranscriptNavigationTests
    {
        // ---- the scope-relative ancestor walk ---------------------------------------------------

        /// <summary>
        /// With no scope the walk is the old root-relative one, which is why <c>RootAncestor</c> is
        /// implemented as this call rather than as a second traversal: two walks with one meaning is how
        /// the scope-relative and root-relative answers come to disagree.
        /// </summary>
        [Fact]
        public void A_null_scope_walks_all_the_way_to_the_top()
        {
            var (root, mid, leaf) = ThreeDeep();

            Assert.Same(root, leaf.AncestorWithin(null));
            Assert.Same(root, leaf.RootAncestor);
            Assert.Same(root, root.AncestorWithin(null));
        }

        /// <summary>Inside a scope, the answer is the child of that scope on the path down to the item.</summary>
        [Fact]
        public void Within_a_scope_the_walk_stops_at_that_scopes_own_child()
        {
            var (root, mid, leaf) = ThreeDeep();

            Assert.Same(mid, leaf.AncestorWithin(mid.Parent));   // scope = root
            Assert.Same(leaf, leaf.AncestorWithin(mid));
        }

        /// <summary>
        /// An item outside the scope has NO answer, and saying so is the point. The transcript draws one
        /// list, so anything that scrolls to an item has to name a member of the list on screen; returning
        /// the nearest thing that happens to be showing would scroll the user to something they did not
        /// ask about and report success.
        /// </summary>
        [Fact]
        public void An_item_in_another_scope_is_not_within_this_one()
        {
            var (root, mid, leaf) = ThreeDeep();
            var elsewhere = Row("other");
            root.AddChild(elsewhere);

            Assert.Null(leaf.AncestorWithin(elsewhere));
            Assert.Null(root.AncestorWithin(mid));   // a scope is not inside itself
        }

        // ---- the stack -------------------------------------------------------------------------

        [Fact]
        public void Opening_a_row_swaps_the_transcript_for_its_children()
        {
            RunSta(() =>
            {
                var vm = NewViewModel();
                var fanOut = FanOut(vm, "task-1", 26);

                Assert.Same(vm.Items, vm.CurrentItems);
                Assert.True(vm.OpenTranscript(fanOut));

                Assert.Same(fanOut.Children, vm.CurrentItems);
                Assert.Same(fanOut, vm.CurrentScope);
                Assert.True(vm.IsDrilledIn);
                Assert.Equal(new[] { "Conversation", "Task 1" }, vm.Breadcrumb.Select(c => c.Label));
                Assert.True(vm.Breadcrumb[1].IsCurrent);
            });
        }

        /// <summary>
        /// A row with no calls cannot be opened, so no entry point can ever land on an empty sub-view.
        /// </summary>
        [Fact]
        public void A_row_with_no_calls_cannot_be_opened()
        {
            RunSta(() =>
            {
                var vm = NewViewModel();
                var bare = Row("bare");
                vm.Items.Add(bare);

                Assert.False(bare.CanOpenTranscript);
                Assert.False(vm.OpenTranscript(bare));
                Assert.False(vm.IsDrilledIn);
                Assert.Same(vm.Items, vm.CurrentItems);
            });
        }

        /// <summary>
        /// Navigation is a STACK, not a toggle — a sub-agent's own sub-agent is an ordinary child row, so
        /// depth 2 is reached by the same gesture as depth 1. A toggle would be wrong there and wrong
        /// silently: it would look like Back had worked while jumping two levels.
        /// </summary>
        [Fact]
        public void Back_from_depth_two_returns_one_level_not_to_the_root()
        {
            RunSta(() =>
            {
                var vm = NewViewModel();
                var outer = FanOut(vm, "task-1", 3);
                var inner = (ToolItemViewModel)outer.Children[1];
                inner.AddChild(Row("leaf"));

                Assert.True(vm.OpenTranscript(outer));
                Assert.True(vm.OpenTranscript(inner));
                Assert.Equal(2, vm.NavPath.Count);
                Assert.Equal(3, vm.Breadcrumb.Count);

                Assert.True(vm.NavigateBack());

                Assert.Same(outer, vm.CurrentScope);
                Assert.Same(outer.Children, vm.CurrentItems);
                Assert.True(vm.IsDrilledIn);
            });
        }

        /// <summary>A crumb navigates to its own depth, however many frames that pops at once.</summary>
        [Fact]
        public void A_crumb_pops_every_frame_above_its_depth()
        {
            RunSta(() =>
            {
                var vm = NewViewModel();
                var outer = FanOut(vm, "task-1", 3);
                var inner = (ToolItemViewModel)outer.Children[1];
                inner.AddChild(Row("leaf"));
                vm.OpenTranscript(outer);
                vm.OpenTranscript(inner);

                Assert.True(vm.NavigateTo(0));

                Assert.Empty(vm.NavPath);
                Assert.Null(vm.CurrentScope);
                Assert.Same(vm.Items, vm.CurrentItems);
                Assert.Single(vm.Breadcrumb);
                Assert.True(vm.Breadcrumb[0].IsCurrent);
            });
        }

        /// <summary>Navigating to the view already on screen, or past the end of the stack, does nothing.</summary>
        [Fact]
        public void Navigating_nowhere_is_refused_rather_than_rebuilding_the_view()
        {
            RunSta(() =>
            {
                var vm = NewViewModel();
                var fanOut = FanOut(vm, "task-1", 3);
                vm.OpenTranscript(fanOut);

                Assert.False(vm.NavigateTo(1));    // already here
                Assert.False(vm.NavigateTo(9));
                Assert.False(vm.NavigateTo(-1));
                Assert.Same(fanOut, vm.CurrentScope);

                Assert.True(vm.NavigateBack());
                Assert.False(vm.NavigateBack());   // nothing left to pop
            });
        }

        // ---- the root crumb's dot ---------------------------------------------------------------

        /// <summary>
        /// The pane never navigates on the user's behalf, so the way back has to say the conversation
        /// moved on while they were away.
        /// </summary>
        [Fact]
        public void The_root_crumb_reports_a_conversation_that_moved_on()
        {
            RunSta(() =>
            {
                var vm = NewViewModel();
                var fanOut = FanOut(vm, "task-1", 3);
                vm.OpenTranscript(fanOut);
                Assert.False(vm.RootHasNewActivity);

                vm.Items.Add(new NoticeItemViewModel("the turn produced something"));

                Assert.True(vm.RootHasNewActivity);
                Assert.True(vm.Breadcrumb[0].HasNewActivity);
                Assert.Same(fanOut.Children, vm.CurrentItems);   // ...and it did NOT navigate
            });
        }

        /// <summary>
        /// A fan-out streaming into the scope the user is WATCHING is not news from elsewhere. It appends
        /// to that row's children rather than to the conversation, which is what makes the distinction
        /// structural rather than a guess about what the user is looking at.
        /// </summary>
        [Fact]
        public void Work_inside_the_open_scope_is_not_reported_as_activity_elsewhere()
        {
            RunSta(() =>
            {
                var vm = NewViewModel();
                var fanOut = FanOut(vm, "task-1", 3);
                vm.OpenTranscript(fanOut);

                fanOut.AddChild(Row("child-late"));

                Assert.False(vm.RootHasNewActivity);
            });
        }

        [Fact]
        public void Arriving_back_at_the_root_delivers_the_news()
        {
            RunSta(() =>
            {
                var vm = NewViewModel();
                var fanOut = FanOut(vm, "task-1", 3);
                vm.OpenTranscript(fanOut);
                vm.Items.Add(new NoticeItemViewModel("something"));
                Assert.True(vm.RootHasNewActivity);

                vm.NavigateTo(0);

                Assert.False(vm.RootHasNewActivity);
                Assert.False(vm.Breadcrumb[0].HasNewActivity);
            });
        }

        // ---- a conversation being replaced ------------------------------------------------------

        /// <summary>
        /// Switching conversations from the history popup drops every frame. Without this the breadcrumb
        /// names rows from the PREVIOUS conversation over an empty sub-view, and Back pops toward a scope
        /// that no longer exists.
        /// <para><c>LoadSession</c> clears the transcript inline rather than through
        /// <c>ClearTranscript</c>, so it needs its own reset — and it is the likeliest way to reach a
        /// stale frame, being the one conversation swap a user makes mid-read.</para>
        /// </summary>
        [Fact]
        public void Switching_conversations_drops_every_navigation_frame()
        {
            RunSta(() =>
            {
                var store = new FakeStore();
                var vm = NewViewModel(store);
                var fanOut = FanOut(vm, "task-1", 3);
                vm.OpenTranscript(fanOut);
                var resets = 0;
                vm.NavigationReset += (_, _) => resets++;

                vm.RestoreMostRecentSession();   // the history popup's own path

                Assert.Empty(vm.NavPath);
                Assert.Same(vm.Items, vm.CurrentItems);
                Assert.Single(vm.Breadcrumb);
                Assert.Equal(1, resets);
            });
        }

        [Fact]
        public void Starting_a_new_session_drops_every_navigation_frame()
        {
            RunSta(() =>
            {
                var vm = NewViewModel();
                var fanOut = FanOut(vm, "task-1", 3);
                vm.OpenTranscript(fanOut);
                var resets = 0;
                vm.NavigationReset += (_, _) => resets++;

                vm.NewSessionCommand.Execute(null);

                Assert.Empty(vm.NavPath);
                Assert.Same(vm.Items, vm.CurrentItems);
                Assert.False(vm.IsDrilledIn);
                Assert.Equal(1, resets);
            });
        }

        /// <summary>
        /// The signal fires even from the root, because whether the VIEW still has frames left over is
        /// not a fact this side holds: it keeps its own stack of scroll anchors, and popping those
        /// against a different conversation's content lands somewhere unrelated but plausible.
        /// </summary>
        [Fact]
        public void The_reset_signal_fires_even_when_already_at_the_root()
        {
            RunSta(() =>
            {
                var vm = NewViewModel();
                var resets = 0;
                vm.NavigationReset += (_, _) => resets++;

                vm.NewSessionCommand.Execute(null);

                Assert.Equal(1, resets);
            });
        }

        // ---- what navigation must NOT touch ------------------------------------------------------

        /// <summary>
        /// <c>Items</c> stays the whole conversation, holding only top-level rows, whatever is on screen.
        /// The nav stack is a view over the item tree and never an owner of transcript data — the moment
        /// a scope's items lived outside their parent row, <c>Items</c> would stop being the conversation
        /// and the export rule below would break from the other direction.
        /// </summary>
        [Fact]
        public void Drilling_in_does_not_move_any_item_out_of_the_conversation()
        {
            RunSta(() =>
            {
                var vm = NewViewModel();
                var fanOut = FanOut(vm, "task-1", 26);
                var before = vm.Items.ToList();

                vm.OpenTranscript(fanOut);

                Assert.Equal(before, vm.Items.ToList());
                Assert.DoesNotContain(fanOut.Children, c => vm.Items.Contains(c));
            });
        }

        /// <summary>
        /// Export and copy mean "the whole conversation" and read <c>Items</c>, never the view. One
        /// identifier away from <c>CurrentItems</c>, this would produce a plausible-looking file holding
        /// only the sub-agent's calls — and be found weeks later.
        /// </summary>
        [Fact]
        public void The_export_is_the_whole_conversation_even_while_drilled_in()
        {
            RunSta(() =>
            {
                var vm = NewViewModel();
                vm.Items.Add(new MessageItemViewModel(MessageRole.User, "the question a reader needs"));
                var fanOut = FanOut(vm, "task-1", 26);

                vm.OpenTranscript(fanOut);
                var markdown = vm.BuildTranscriptMarkdown();

                Assert.Contains("the question a reader needs", markdown, StringComparison.Ordinal);
                Assert.Contains("Read 0", markdown, StringComparison.Ordinal);   // and the nested calls too
            });
        }

        // ---- harness -----------------------------------------------------------------------------

        private static ToolItemViewModel Row(string id) => new(id, "Read " + id, "read");

        /// <summary>A launch row in the transcript with <paramref name="children"/> calls under it.</summary>
        private static ToolItemViewModel FanOut(ChatViewModel vm, string id, int children)
        {
            var parent = new ToolItemViewModel(id, "Task 1", "think") { IsSubagentLaunch = true };
            for (var i = 0; i < children; i++)
                parent.AddChild(new ToolItemViewModel(id + "-c" + i, "Read " + i, "read"));
            vm.Items.Add(parent);
            return parent;
        }

        /// <summary>root → mid → leaf, the shape a recursive fan-out produces.</summary>
        private static (ToolItemViewModel Root, ToolItemViewModel Mid, ToolItemViewModel Leaf) ThreeDeep()
        {
            var root = Row("root");
            var mid = Row("mid");
            var leaf = Row("leaf");
            root.AddChild(mid);
            mid.AddChild(leaf);
            return (root, mid, leaf);
        }

        private static ChatViewModel NewViewModel(ISessionStore? store = null) => new(
            new StubEngine(),
            new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null),
            sessionStore: store);

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);

        /// <summary>A store holding one empty conversation, which is all a reset test needs to load.</summary>
        private sealed class FakeStore : ISessionStore
        {
            private readonly PersistedSession _session = new()
            {
                Id = "saved-1",
                Title = "Another conversation",
            };

            public IReadOnlyList<SessionSummary> List(string workspaceRootPath) =>
                new[] { new SessionSummary(_session.Id, _session.Title, DateTime.UtcNow, 0, 0, Array.Empty<string>()) };

            public PersistedSession? Load(string workspaceRootPath, string sessionId) =>
                sessionId == _session.Id ? _session : null;

            public void Save(PersistedSession session) { }

            public void Delete(string workspaceRootPath, string sessionId) { }
        }

        private sealed class StubEngine : IEngineConnection
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
                => throw new InvalidOperationException("These tests never open a session.");

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never prompt.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
