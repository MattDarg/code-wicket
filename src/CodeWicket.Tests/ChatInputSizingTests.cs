using CodeWicket.UI.Views;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The chat message box's size policy (the grip on its top edge). The gesture itself needs a real
    /// pointer, but the arithmetic behind it is what can silently go wrong — so it lives in a static
    /// method and is pinned here: the dragged height is a FLOOR (auto-grow-with-content survives), and
    /// nothing may let the box crowd the transcript out of a short pane.
    /// </summary>
    public sealed class ChatInputSizingTests
    {
        // A comfortably tall pane: 0.6 * 900 = 540, well above every height used below, so these cases
        // exercise the policy rather than the cap.
        const double TallPane = 900;

        [Fact]
        public void Undragged_box_keeps_the_pre_grip_bounds()
        {
            // 0 = never dragged. One line, growing to ~6 with content — exactly the old hardcoded XAML.
            var (floor, ceiling) = ChatView.ComputeInputBounds(desiredHeight: 0, TallPane, zoom: 1.0);

            Assert.Equal(28, floor);
            Assert.Equal(120, ceiling);
        }

        [Fact]
        public void Dragged_height_becomes_a_floor_not_a_fixed_size()
        {
            // Dragged shorter than the content ceiling: the box rests at 80 but a long paste can still
            // push it to 120. Losing this is the whole point — a fixed size would kill auto-grow.
            var (floor, ceiling) = ChatView.ComputeInputBounds(desiredHeight: 80, TallPane, zoom: 1.0);

            Assert.Equal(80, floor);
            Assert.Equal(120, ceiling);
        }

        [Fact]
        public void Dragging_past_the_content_ceiling_raises_it_too()
        {
            // Otherwise a box dragged to 300 would rest at 300 but refuse to show more than 120 of text.
            var (floor, ceiling) = ChatView.ComputeInputBounds(desiredHeight: 300, TallPane, zoom: 1.0);

            Assert.Equal(300, floor);
            Assert.Equal(300, ceiling);
        }

        [Fact]
        public void A_short_pane_caps_both_bounds()
        {
            // A height chosen in a tall window, restored into a 200px-tall pane: the input's row is
            // Auto-sized, so an uncapped 300 would take the whole pane and leave no transcript.
            var (floor, ceiling) = ChatView.ComputeInputBounds(desiredHeight: 300, paneHeight: 200, zoom: 1.0);

            Assert.Equal(120, floor);   // 0.6 * 200
            Assert.Equal(120, ceiling);
        }

        [Fact]
        public void The_cap_never_squeezes_the_box_below_one_line()
        {
            // 0.6 * 30 = 18, under a single line. A pane this short is degenerate; the box must stay
            // usable rather than collapse to nothing.
            var (floor, ceiling) = ChatView.ComputeInputBounds(desiredHeight: 300, paneHeight: 30, zoom: 1.0);

            Assert.Equal(28, floor);
            Assert.Equal(28, ceiling);
        }

        [Fact]
        public void Zoom_shrinks_the_cap_in_pre_zoom_units()
        {
            // The bounds sit under the zoom transform while the pane height doesn't, so at 2x every
            // pre-zoom unit costs two pane pixels: the same 0.6 pane share is half as many units.
            var (floor, ceiling) = ChatView.ComputeInputBounds(desiredHeight: 300, paneHeight: 400, zoom: 2.0);

            Assert.Equal(120, floor);   // 0.6 * 400 / 2
            Assert.Equal(120, ceiling);
        }

        [Fact]
        public void An_unmeasured_pane_caps_nothing()
        {
            // The view-model restores the stored height in the constructor, before any layout pass;
            // capping against a zero height there would clamp every box to one line on startup.
            var (floor, ceiling) = ChatView.ComputeInputBounds(desiredHeight: 300, paneHeight: 0, zoom: 1.0);

            Assert.Equal(300, floor);
            Assert.Equal(300, ceiling);
        }
    }
}
