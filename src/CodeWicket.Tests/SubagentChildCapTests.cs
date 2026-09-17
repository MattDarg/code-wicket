using System.Linq;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A sub-agent row draws at most <see cref="ToolItemViewModel.ChildCap"/> children (issue #125).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, not guessed.</b> Nesting shipped drawing every child of an expanded row into a plain
    /// <c>ItemsControl</c> — a <c>StackPanel</c>, so non-virtualizing — inside ONE container of the
    /// transcript's virtualizing panel. The field log named the cost precisely: a single tool row
    /// measuring <b>50–64ms</b> against 0.5–2.5ms for every ordinary row in every earlier session, and
    /// from the moment that row appeared, render passes went from ~100–600ms to a flat ~1800ms and
    /// stayed there. Over the 133s of that session, 33% of all wall-clock was inside
    /// <c>MediaContext.RenderMessageHandler</c>, against ~1% in every session before it — with no agent
    /// traffic at all in the window, so it was purely the user expanding and scrolling.
    /// </para>
    /// <para>
    /// <b>The cap is a DISPLAY bound and <see cref="ToolItemViewModel.Children"/> stays whole</b> — the
    /// rule <c>AcpMapper.ClampResult</c> exists for. The export walks <c>Children</c>, the collapsed
    /// row's summary counts it, and a reveal indexes into it, so a row that only KEPT five would drop a
    /// sub-agent's twenty-first call out of the transcript the user copies. Pinned below, because the
    /// obvious implementation (cap the collection) passes every visual check and loses data silently.
    /// </para>
    /// <para>
    /// <b>And the reveal is the half that fails quietly.</b> The permission banner points at an arbitrary
    /// nested call; with a cap, opening the row is no longer enough to show it, so a banner naming a call
    /// outside the window would open onto five other calls and highlight nothing — no error, exactly the
    /// failure <see cref="ChatItemViewModel.ExpandAncestors"/> was written to prevent. That is why the
    /// reveal raises the cap, and why it raises it only far enough.
    /// </para>
    /// <para>
    /// <b>The overflow control no longer widens the row - it NAVIGATES</b> (issue #148). "Show all"
    /// reinstated the unbounded case on demand, which is the very cost measured above; opening the calls
    /// as their own transcript hands them to the virtualising panel instead. So the widening path below
    /// is exercised through the REVEAL, which is the only thing that still raises a row's cap - and was
    /// always the reason the cap has to be restorable at all.
    /// </para>
    /// <para>
    /// <b>The window is anchored to the NEWEST call, and that is a permission decision before it is a
    /// reading one.</b> A blocked call is by definition the most recent, so a first-N window would have
    /// forced every banner on a large fan-out to raise the cap to its full length: the cap would have
    /// held everywhere except the one moment the pane is blocked and the user is waiting on it. Note
    /// that first-N and last-N agree on every COUNT, so the tests below pin the identity of the drawn
    /// rows wherever the direction is what is under test.
    /// </para>
    /// </remarks>
    public sealed class SubagentChildCapTests
    {
        private static ToolItemViewModel Row(string id = "parent") =>
            new ToolItemViewModel(id, "Task", "other");

        /// <summary>
        /// A fan-out the user has OPENED. The window only exists for a row that is drawing, so a test
        /// about what is drawn has to open the row — a closed one deliberately holds nothing.
        /// </summary>
        private static ToolItemViewModel FanOut(int children)
        {
            var parent = Closed(children);
            parent.IsExpanded = true;
            return parent;
        }

        /// <summary>A fan-out as it arrives: streamed into a row nobody has opened.</summary>
        private static ToolItemViewModel Closed(int children)
        {
            var parent = Row();
            for (var i = 0; i < children; i++)
                parent.AddChild(new ToolItemViewModel("child-" + i, "Read " + i, "read"));
            return parent;
        }

        [Fact]
        public void A_big_fan_out_draws_only_the_cap()
        {
            var parent = FanOut(26);

            Assert.Equal(ToolItemViewModel.ChildCap, parent.VisibleChildren.Count);
            Assert.True(parent.HasHiddenChildren);
            Assert.Equal("Open all 26 calls \u2192", parent.OpenAllChildrenLabel);
        }

        /// <summary>
        /// The cap keeps the NEWEST calls. Pinned as the identity of the drawn rows, not just their
        /// count, because first-five and last-five agree on every count and differ on everything the
        /// choice was made for.
        /// </summary>
        [Fact]
        public void The_cap_keeps_the_newest_calls()
        {
            var parent = FanOut(26);

            Assert.Equal(
                new[] { "Read 21", "Read 22", "Read 23", "Read 24", "Read 25" },
                parent.VisibleChildren.Cast<ToolItemViewModel>().Select(c => c.Title));
        }

        /// <summary>
        /// The case that decided the direction. A permission prompt names the call that is BLOCKED,
        /// which is the most recent one - so revealing it must already be satisfied and must not have
        /// to drop the cap. Under a first-N window this test is what fails: reaching call 26 of 26
        /// means drawing all 26, so the cap would have held everywhere except the one moment the pane
        /// is blocked and the user is waiting on it.
        /// </summary>
        [Fact]
        public void The_call_a_banner_would_name_is_already_drawn()
        {
            var parent = FanOut(26);
            var blocked = parent.Children[25];

            blocked.ExpandAncestors();

            Assert.Contains(blocked, parent.VisibleChildren);
            Assert.Equal(ToolItemViewModel.ChildCap, parent.VisibleChildren.Count);
        }

        /// <summary>
        /// While a fan-out streams, the row has to keep showing what the sub-agent is doing NOW. A
        /// window pinned to the oldest calls looks frozen for the whole turn.
        /// </summary>
        [Fact]
        public void A_streaming_fan_out_keeps_showing_its_newest_call()
        {
            var parent = Row();
            parent.IsExpanded = true;
            for (var i = 0; i < 40; i++)
            {
                var child = new ToolItemViewModel("child-" + i, "Read " + i, "read");
                parent.AddChild(child);
                Assert.Same(child, parent.VisibleChildren[parent.VisibleChildren.Count - 1]);
            }

            Assert.Equal(ToolItemViewModel.ChildCap, parent.VisibleChildren.Count);
        }

        /// <summary>Whatever the window is showing, it stays in the order the calls happened.</summary>
        [Fact]
        public void The_drawn_calls_stay_in_order()
        {
            var parent = FanOut(26);
            parent.Children[10].ExpandAncestors();

            Assert.Equal(
                parent.Children.SkipWhile(c => !ReferenceEquals(c, parent.VisibleChildren[0])),
                parent.VisibleChildren);
        }

        /// <summary>
        /// Closing the row puts the cap back. The reason is the reveal, not the button: a banner
        /// raises the cap without the user asking, so leaving it raised means answering a prompt
        /// silently leaves an expensive row behind for the rest of the session.
        /// </summary>
        [Fact]
        public void Closing_the_row_puts_the_cap_back()
        {
            var parent = FanOut(26);
            parent.Children[0].ExpandAncestors();   // the widest a reveal goes: back to the first call
            Assert.Equal(26, parent.VisibleChildren.Count);

            parent.IsExpanded = false;
            parent.IsExpanded = true;

            Assert.Equal(ToolItemViewModel.ChildCap, parent.VisibleChildren.Count);
            Assert.True(parent.HasHiddenChildren);
        }

        /// <summary>
        /// A closed row draws nothing, so it holds nothing — not the cap, not one row. Everything the
        /// nested ItemsControl would realise comes from this collection, so an empty one is the only
        /// statement of that which does not depend on WPF honouring Visibility the way we expect.
        /// </summary>
        [Fact]
        public void A_closed_row_holds_nothing()
        {
            var parent = Closed(26);

            Assert.Empty(parent.VisibleChildren);
            Assert.False(parent.HasHiddenChildren);   // drawing nothing is not hiding something
            Assert.Null(parent.OpenAllChildrenLabel);
            Assert.Equal(26, parent.Children.Count);  // and none of it is lost
        }

        /// <summary>
        /// The saving that motivated it. A fan-out streams into a row nobody has opened — that IS the
        /// point of nesting - and a closed row maintaining a window it never draws spent two collection
        /// notifications per call for nothing.
        /// </summary>
        [Fact]
        public void Streaming_into_a_closed_row_costs_nothing()
        {
            var parent = Closed(0);

            var changes = CountCollectionChanges(parent, () =>
            {
                for (var i = 0; i < 26; i++)
                    parent.AddChild(new ToolItemViewModel("c" + i, "Read " + i, "read"));
            });

            Assert.Equal(0, changes);
            Assert.Equal(26, parent.Children.Count);
        }

        /// <summary>…and opening it then builds the window in one go, from whatever arrived meanwhile.</summary>
        [Fact]
        public void Opening_a_streamed_row_builds_the_window_once()
        {
            var parent = Closed(26);

            var changes = CountCollectionChanges(parent, () => parent.IsExpanded = true);

            Assert.Equal(1, changes);
            Assert.Equal(
                new[] { "Read 21", "Read 22", "Read 23", "Read 24", "Read 25" },
                parent.VisibleChildren.Cast<ToolItemViewModel>().Select(c => c.Title));
        }

        /// <summary>…and it is the newest five that come back, not whichever five happened to be first.</summary>
        [Fact]
        public void The_cap_that_comes_back_is_still_anchored_to_the_newest()
        {
            var parent = FanOut(26);
            parent.Children[2].ExpandAncestors();   // grows the window backwards to call 3
            parent.IsExpanded = false;
            parent.IsExpanded = true;

            Assert.Equal(
                new[] { "Read 21", "Read 22", "Read 23", "Read 24", "Read 25" },
                parent.VisibleChildren.Cast<ToolItemViewModel>().Select(c => c.Title));
        }

        /// <summary>A row under the cap opens onto everything, closes to nothing, and reopens whole.</summary>
        [Fact]
        public void A_row_under_the_cap_survives_a_close_and_reopen()
        {
            var parent = FanOut(4);
            Assert.Equal(4, parent.VisibleChildren.Count);

            parent.IsExpanded = false;
            Assert.Empty(parent.VisibleChildren);

            parent.IsExpanded = true;
            Assert.Equal(4, parent.VisibleChildren.Count);
            Assert.False(parent.HasHiddenChildren);
        }

        /// <summary>
        /// The launched row's note is ONE word, because it shares its line with the title and the title
        /// is the field that trims: everything else on the row docks right and keeps its width. What the
        /// shortened note stopped saying moved to its tooltip rather than being dropped — that this row
        /// will never settle, the backend naming no agent on the frame that reports a task finishing.
        /// </summary>
        [Fact]
        public void Only_a_launched_row_carries_a_note_and_it_is_one_word()
        {
            var launched = Row();
            launched.Status = ToolStatus.Launched;

            Assert.Equal("running", launched.StatusNote);
            Assert.True(launched.HasStatusNote);
            Assert.DoesNotContain("background", launched.StatusNote!, System.StringComparison.Ordinal);
            // The nuance is kept, just not on the row.
            Assert.Contains("background", launched.StatusNoteToolTip, System.StringComparison.Ordinal);

            foreach (var other in new[] { ToolStatus.Running, ToolStatus.Success, ToolStatus.Failed })
            {
                var row = Row();
                row.Status = other;
                Assert.Null(row.StatusNote);
                Assert.False(row.HasStatusNote);
            }
        }

        private static System.Collections.Generic.List<System.Collections.Specialized.NotifyCollectionChangedAction>
            RecordCollectionChanges(ToolItemViewModel row, System.Action act)
        {
            var seen = new System.Collections.Generic.List<System.Collections.Specialized.NotifyCollectionChangedAction>();
            System.Collections.Specialized.NotifyCollectionChangedEventHandler rec = (_, e) => seen.Add(e.Action);
            ((System.Collections.Specialized.INotifyCollectionChanged)row.VisibleChildren).CollectionChanged += rec;
            try { act(); } finally
            {
                ((System.Collections.Specialized.INotifyCollectionChanged)row.VisibleChildren).CollectionChanged -= rec;
            }
            return seen;
        }

        private static int CountCollectionChanges(ToolItemViewModel row, System.Action act)
        {
            var n = 0;
            System.Collections.Specialized.NotifyCollectionChangedEventHandler h = (_, __) => n++;
            ((System.Collections.Specialized.INotifyCollectionChanged)row.VisibleChildren).CollectionChanged += h;
            try { act(); } finally
            {
                ((System.Collections.Specialized.INotifyCollectionChanged)row.VisibleChildren).CollectionChanged -= h;
            }
            return n;
        }

        /// <summary>
        /// A cap change moves the window's FRONT edge, and doing that one element at a time re-indexes
        /// every container already generated inside a non-virtualizing ItemsControl — twenty-one times
        /// over, to widen five rows to twenty-six. So it goes as one notification.
        /// <para>Invisible to every other test here: the contents are identical either way, and only the
        /// number of notifications differs. This cost did not exist while the window was anchored to the
        /// OLDEST call, because the same jumps were then appends at the end.</para>
        /// </summary>
        [Fact]
        public void A_cap_change_is_one_notification_not_one_per_row()
        {
            var parent = FanOut(26);

            Assert.Equal(1, CountCollectionChanges(parent, () => parent.Children[0].ExpandAncestors()));
            Assert.Equal(26, parent.VisibleChildren.Count);

            Assert.Equal(1, CountCollectionChanges(parent, () => parent.IsExpanded = false));
            Assert.Empty(parent.VisibleChildren);
        }

        /// <summary>
        /// …and the streaming case must NOT batch. A call arriving slides the window by one, which is one
        /// remove and one add; rebuilding there would throw away every container on every call of a
        /// fan-out — the opposite of the fix, and it would look identical in every content assertion.
        /// </summary>
        [Fact]
        public void A_call_arriving_moves_the_window_rather_than_rebuilding_it()
        {
            var parent = FanOut(26);

            var changes = CountCollectionChanges(
                parent, () => parent.AddChild(new ToolItemViewModel("late", "Read late", "read")));

            Assert.Equal(2, changes);   // the oldest drawn row leaves, the new one arrives
            Assert.Equal(ToolItemViewModel.ChildCap, parent.VisibleChildren.Count);
        }

        /// <summary>
        /// The batched path is ONE Reset, not one notification per row and not a burst that happens to
        /// total one. Asserted on the ACTION as well as the count, because "exactly one notification"
        /// alone is also satisfied by a single bogus Add, and a Reset is what tells the control to
        /// rebuild once instead of re-indexing what it already has.
        /// </summary>
        [Fact]
        public void A_cap_change_raises_exactly_one_Reset()
        {
            var parent = FanOut(26);

            var widening = RecordCollectionChanges(parent, () => parent.Children[0].ExpandAncestors());
            Assert.Equal(new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Reset }, widening);

            var closing = RecordCollectionChanges(parent, () => parent.IsExpanded = false);
            Assert.Equal(new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Reset }, closing);
        }

        /// <summary>…while a call arriving stays a Remove plus an Add, which is the cheap edge move.</summary>
        [Fact]
        public void A_call_arriving_raises_a_remove_and_an_add()
        {
            var parent = FanOut(26);

            var slide = RecordCollectionChanges(
                parent, () => parent.AddChild(new ToolItemViewModel("late", "Read late", "read")));

            Assert.Equal(
                new[]
                {
                    System.Collections.Specialized.NotifyCollectionChangedAction.Remove,
                    System.Collections.Specialized.NotifyCollectionChangedAction.Add,
                },
                slide);
        }

        /// <summary>
        /// The whole point of the cap: what the non-virtualizing ItemsControl realises is bounded no
        /// matter how big the fan-out gets. 26 and 200 must draw the same number of rows.
        /// </summary>
        [Fact]
        public void The_drawn_count_does_not_grow_with_the_fan_out()
        {
            Assert.Equal(FanOut(26).VisibleChildren.Count, FanOut(200).VisibleChildren.Count);
        }

        /// <summary>
        /// The cap bounds DISPLAY and must never cut DATA. Everything downstream — the export, the
        /// collapsed row's own summary, a reveal's index — reads the full collection.
        /// </summary>
        [Fact]
        public void Every_child_is_kept_however_few_are_drawn()
        {
            var parent = FanOut(26);

            Assert.Equal(26, parent.Children.Count);
            Assert.Equal("26 calls", parent.ChildSummary);
            Assert.Equal(
                Enumerable.Range(0, 26).Select(i => "Read " + i),
                parent.Children.Cast<ToolItemViewModel>().Select(c => c.Title));
        }

        /// <summary>
        /// Hiding a single row behind a control costs a click and saves nothing, so the cap does not
        /// engage until it would hold back more than one. Both sides of the boundary, because an
        /// off-by-one here is invisible — the row just looks slightly different than intended.
        /// </summary>
        [Theory]
        [InlineData(1, 1)]
        [InlineData(5, 5)]
        [InlineData(6, 6)]   // the slack: one over the cap still draws whole
        [InlineData(7, 5)]   // two over: the cap engages
        [InlineData(26, 5)]
        public void The_cap_engages_only_when_it_hides_more_than_one(int children, int drawn)
        {
            Assert.Equal(drawn, FanOut(children).VisibleChildren.Count);
        }

        /// <summary>
        /// Widening all the way to the first call retires the overflow control: nothing is hidden any
        /// more, so there is nothing for it to offer.
        /// </summary>
        [Fact]
        public void Widening_to_every_child_retires_the_overflow_control()
        {
            var parent = FanOut(26);

            parent.Children[0].ExpandAncestors();   // the widest a reveal goes: back to the first call

            Assert.Equal(26, parent.VisibleChildren.Count);
            Assert.False(parent.HasHiddenChildren);
            Assert.Null(parent.OpenAllChildrenLabel);
        }

        /// <summary>
        /// A call arriving live must not push the row past the cap — children stream in during a turn,
        /// so this is the ordinary case, not an edge one.
        /// </summary>
        [Fact]
        public void Children_arriving_later_do_not_grow_the_drawn_set()
        {
            var parent = FanOut(26);
            parent.AddChild(new ToolItemViewModel("late", "Read late", "read"));

            Assert.Equal(ToolItemViewModel.ChildCap, parent.VisibleChildren.Count);
            Assert.Equal(27, parent.Children.Count);
            Assert.Equal("Open all 27 calls \u2192", parent.OpenAllChildrenLabel);
        }

        /// <summary>
        /// A call arriving after a full reveal is drawn - and the window SLIDES to take it, rather than
        /// growing to hold everything.
        /// </summary>
        /// <remarks>
        /// This is where the reveal differs from the "Show all" control it outlived (issue #148), and the
        /// difference is deliberate rather than incidental. That control widened the row FOREVER, so
        /// every later call was drawn too; a reveal widens only just far enough to show the call it was
        /// given, which is the rule that keeps a permission banner from becoming the most expensive
        /// gesture in the pane. So the widened window is a fixed size that then behaves like any other:
        /// the newest call arrives, the oldest drawn one leaves, and the overflow control comes back to
        /// say so.
        /// </remarks>
        [Fact]
        public void A_child_arriving_after_a_full_reveal_slides_the_window()
        {
            var parent = FanOut(26);
            parent.Children[0].ExpandAncestors();   // the widest a reveal goes: back to the first call

            var late = new ToolItemViewModel("late", "Read late", "read");
            parent.AddChild(late);

            Assert.Equal(26, parent.VisibleChildren.Count);
            Assert.Same(late, parent.VisibleChildren[parent.VisibleChildren.Count - 1]);
            Assert.True(parent.HasHiddenChildren);   // call 1 has slid out of the window
        }

        /// <summary>
        /// The banner's case. Revealing call 17 of 26 has to actually show call 17 — under a cap,
        /// expanding the row alone lands on calls 1-5 and highlights nothing.
        /// </summary>
        [Fact]
        public void Revealing_a_child_older_than_the_window_draws_it()
        {
            var parent = FanOut(26);
            var target = parent.Children[16];

            target.ExpandAncestors();

            Assert.True(parent.IsExpanded);
            Assert.Contains(target, parent.VisibleChildren);
        }

        /// <summary>
        /// …and only just far enough. A reveal that dropped the cap entirely would make the permission
        /// banner the most expensive gesture in the pane — it fires on a blocked call, which is exactly
        /// when the user is waiting.
        /// </summary>
        [Fact]
        public void Revealing_a_child_does_not_drop_the_cap_altogether()
        {
            var parent = FanOut(26);

            parent.Children[16].ExpandAncestors();

            // Call 17 of 26, so the window reaches back nine calls — not to all twenty-six.
            Assert.Equal(26 - 16, parent.VisibleChildren.Count);
            Assert.True(parent.HasHiddenChildren);
        }

        /// <summary>
        /// A reveal never SHRINKS what is drawn: pointing at call 2 after "Show all" must not fold the
        /// row back down to three rows under the user.
        /// </summary>
        [Fact]
        public void Revealing_a_child_leaves_a_dropped_cap_alone()
        {
            var parent = FanOut(26);
            parent.Children[0].ExpandAncestors();   // the widest a reveal goes: back to the first call

            parent.Children[24].ExpandAncestors();

            Assert.Equal(26, parent.VisibleChildren.Count);
        }

        /// <summary>
        /// Nesting is recursive (a sub-agent's own sub-agent), so a reveal has to raise the cap on every
        /// row on the path, not just the one directly holding the target.
        /// </summary>
        [Fact]
        public void A_reveal_raises_the_cap_on_every_row_on_the_path()
        {
            var root = FanOut(26);
            var mid = (ToolItemViewModel)root.Children[20];
            for (var i = 0; i < 26; i++)
                mid.AddChild(new ToolItemViewModel("leaf-" + i, "Read leaf " + i, "read"));
            var leaf = mid.Children[24];

            leaf.ExpandAncestors();

            Assert.Contains(mid, root.VisibleChildren);
            Assert.Contains(leaf, mid.VisibleChildren);
            Assert.True(root.IsExpanded);
            Assert.True(mid.IsExpanded);
        }
    }
}
