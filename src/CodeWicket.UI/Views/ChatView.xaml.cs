using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodeWicket.Core;
using CodeWicket.Shell;
using CodeWicket.UI.Controls;
using CodeWicket.UI.Input;
using CodeWicket.UI.ViewModels;

namespace CodeWicket.UI.Views
{
    public partial class ChatView : UserControl
    {
        // Coalesces rapid zoom changes into a single config write ~600ms after the last one, so a
        // burst of wheel ticks doesn't hammer the disk (or the ExtensionConfig.Changed fan-out).
        private readonly DispatcherTimer _saveZoomTimer;

        // Drives the transient zoom readout: fade in, hold, fade out. Built once and restarted on each
        // change, so a burst of wheel ticks keeps the pill up (each Begin resets it to the fade-in) and
        // the hold only starts counting from the last one.
        private readonly Storyboard _zoomFlash;
        private readonly Storyboard _typingDots;

        // The shared zoom scale (a resource applied as the LayoutTransform of the transcript, banners,
        // and input box). One instance drives all of them, so mutating it here zooms the whole surface.
        private readonly System.Windows.Media.ScaleTransform _zoom;

        // Set for the duration of the Ctrl+Shift+V paste so the pasting handler stands down. A
        // field rather than a parameter because the handler is invoked by WPF, not by us.
        private bool _pasteRaw;

        // The user's chosen resting height for the message box (the grip on its top edge). Held
        // separately from the box's applied MinHeight because a short pane clamps what's applied
        // without discarding the preference — re-dock taller and the box gets its size back.
        private double _inputHeight = DefaultInputHeight;

        public ChatView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            DataObject.AddPastingHandler(InputBox, InputBox_Pasting);

            // Owning the Paste COMMAND is what makes an image paste possible at all (issue #118), and
            // the handler above is not a substitute — measured: with the handler alone, a paste that
            // carries no text form never raises Pasting.
            //
            // A screen capture puts NO text form on the clipboard: Snipping Tool publishes exactly
            // "Bitmap, System.Drawing.Bitmap, PNG" and nothing else. WPF's TextBoxBase looks for a
            // format it can apply, finds none, and so (a) reports CanExecute=false, greying the
            // context menu's Paste out, and (b) returns WITHOUT ever raising DataObject.Pasting. The
            // pasting handler is therefore never invoked for precisely the case the feature exists
            // for, and Ctrl+V does nothing at all.
            //
            // The mistake behind the broken build is worth keeping: the self-check drove a synthesized
            // DataObject carrying BOTH a PNG and a UnicodeText path — modelling "a screenshot
            // clipboard usually carries text too", which is true of an Explorer copy and a browser
            // copy and false of the capture tool the issue is about. The harness supplied the very
            // thing that made the code path reachable.
            //
            // A command binding covers every route to a paste at once — Ctrl+V, Shift+Insert and the
            // context menu — rather than intercepting one keystroke and leaving the others dead.
            InputBox.CommandBindings.Add(new CommandBinding(
                ApplicationCommands.Paste, InputBox_PasteExecuted, InputBox_PasteCanExecute));

            _zoom = (System.Windows.Media.ScaleTransform)FindResource("ZoomTransform");

            _saveZoomTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(600) };
            _saveZoomTimer.Tick += (_, _) => FlushZoom();

            // A debounced write is a write that hasn't happened yet, so it has to be forced out before
            // this view can go away: a VS tool window unloads its content whenever its tab is switched
            // away, and the frame is torn down entirely by a close or by Code Wicket > Restart. Land
            // inside the 600ms window and the zoom change is simply lost, with the next open restoring
            // the older value — which reads as "my zoom didn't stick" rather than as a dropped write.
            Unloaded += (_, _) => FlushZoom();

            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1700))));
            Storyboard.SetTarget(fade, ZoomFlash);
            Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
            _zoomFlash = new Storyboard();
            _zoomFlash.Children.Add(fade);

            // The typing dots. Built here rather than in XAML so the animation can be STOPPED, which is
            // the whole point (issue #86) — see the comment beside the ellipses. Targets are set by
            // object reference, so nothing depends on Storyboard.TargetName resolving through a style's
            // namescope.
            _typingDots = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            AddDotFade(TDot1, 0);
            AddDotFade(TDot2, 0.18);
            AddDotFade(TDot3, 0.36);
            // Keyed on IsVisible, not on the view-model: the panel is hidden by a MultiDataTrigger over
            // two view-model properties, and a second copy of that condition here is a copy that can go
            // out of step. IsVisible is false for Hidden as well as Collapsed, which is what this uses.
            TypingDots.IsVisibleChanged += OnTypingDotsVisibilityChanged;

            // Restore the persisted zoom + message-box height (both clamped, in case the config was
            // hand-edited out of range). The height is re-clamped whenever the pane resizes, so a
            // size chosen in a tall window can't crowd the transcript out of a short one.
            var config = ExtensionConfig.Load();

            // Experimental cache margin (issue #86). Null leaves WPF's default alone, so this is inert
            // unless someone is running the A/B. Set here rather than in XAML because the whole point is
            // varying it without a rebuild — see ExtensionConfig.TranscriptCacheLengthPages.
            TranscriptItems.BitmapCacheRows = config.TranscriptBitmapCache;
            if (config.TranscriptCacheLengthPages is { } pages && pages >= 0)
            {
                VirtualizingPanel.SetCacheLengthUnit(TranscriptItems, VirtualizationCacheLengthUnit.Page);
                VirtualizingPanel.SetCacheLength(TranscriptItems, new VirtualizationCacheLength(pages, pages));
            }

            ApplyZoom(config.ChatZoom, persist: false);
            ApplyInputHeight(config.ChatInputHeight, persist: false);
            SizeChanged += (_, _) => UpdateInputBoxSize();
        }

        private void AddDotFade(UIElement dot, double beginSeconds)
        {
            var fade = new DoubleAnimation(0.3, 1, new Duration(TimeSpan.FromMilliseconds(500)))
            {
                AutoReverse = true,
                BeginTime = TimeSpan.FromSeconds(beginSeconds),
            };
            Storyboard.SetTarget(fade, dot);
            Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
            _typingDots.Children.Add(fade);
        }

        /// <summary>
        /// Runs the dots only while they are on screen.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>An animation that is never stopped keeps WPF's animated render loop running for the life
        /// of the window</b>, whatever the element's visibility — measured at ~120 render operations a
        /// second on an idle pane, and ~1.44s of forced full-transcript render passes inside an 11.3s
        /// scrollbar drag (issue #86, the <c>[dispatch]</c> trace). Stopping it is not tidiness.
        /// </para>
        /// <para>
        /// <b><see cref="Storyboard.Remove(FrameworkElement)"/>, not <c>Stop</c>.</b> Stop halts the
        /// clock but leaves the animation ATTACHED to the property, which still overrides the style's
        /// own opacity and still reports <c>IsAnimated</c>. Remove detaches it and gives the dots back
        /// their resting value. The difference is invisible on screen — a hidden element — which is why
        /// <c>--smoke</c> asserts the value source rather than the opacity.
        /// </para>
        /// </remarks>
        private void OnTypingDotsVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is true)
                _typingDots.Begin(this, true);
            else
                _typingDots.Remove(this);
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ChatViewModel oldVm)
            {
                oldVm.PropertyChanged -= OnViewModelPropertyChanged;
                oldVm.NavigationReset -= OnNavigationReset;
                UnhookFollowedItems();
                _viewStack.Clear();
            }

            if (e.NewValue is ChatViewModel newVm)
            {
                newVm.PropertyChanged += OnViewModelPropertyChanged;
                newVm.NavigationReset += OnNavigationReset;
                HookFollowedItems(newVm.CurrentItems);
            }
        }

        /// <summary>
        /// The collection the follow logic is watching - whichever list the transcript is drawing, so a
        /// sub-agent's calls streaming into an open sub-view scroll it exactly as messages scroll the
        /// conversation (issue #148). Tracked in a field because the collection is swapped, not replaced:
        /// unhooking has to name the one that WAS hooked, and asking the view-model afterwards gets the
        /// new one.
        /// </summary>
        private INotifyCollectionChanged? _followedItems;

        private void HookFollowedItems(System.Collections.IEnumerable items)
        {
            if (items is not INotifyCollectionChanged incc)
                return;
            _followedItems = incc;
            incc.CollectionChanged += OnItemsChanged;
        }

        private void UnhookFollowedItems()
        {
            if (_followedItems is null)
                return;
            _followedItems.CollectionChanged -= OnItemsChanged;
            _followedItems = null;
        }

        /// <summary>
        /// The transcript's scroll viewer, which now lives inside the virtualised ItemsControl's template
        /// rather than wrapping it (see the ChatView.xaml note on virtualisation). Resolved on first use
        /// because a template isn't applied until the control is measured.
        /// </summary>
        private ScrollViewer? TranscriptScroll
        {
            get
            {
                if (_transcriptScroll is null)
                {
                    TranscriptItems.ApplyTemplate();
                    _transcriptScroll = TranscriptItems.Template?.FindName("PART_TranscriptScroll", TranscriptItems) as ScrollViewer;
                    if (_transcriptScroll is not null)
                        _transcriptScroll.ScrollChanged += TranscriptScroll_ScrollChanged;
                }

                return _transcriptScroll;
            }
        }

        private ScrollViewer? _transcriptScroll;

        /// <summary>
        /// The transcript scroller, for the Desktop host's perf harness — a scrollbar drag can only be
        /// measured by driving the offset the way the thumb does, and the scroller lives inside a
        /// template so nothing outside this class can reach it by name.
        /// </summary>
        internal ScrollViewer? TranscriptScrollForDiagnostics => TranscriptScroll;

        /// <summary>Re-pins the follow and rides to the newest content — the smoke's way to reach a known start state.</summary>
        internal void JumpToLatestForDiagnostics()
        {
            FollowingTranscript = true;
            _parkedOnPrompt = false;
            ScrollTranscriptToEnd();
        }

        /// <summary>
        /// Whether the transcript is following the newest content. Cleared when the user scrolls away
        /// from the bottom and restored when they scroll back, so the agent can never yank the view off
        /// something being read mid-turn.
        /// </summary>
        private bool _followingTranscript = true;

        /// <summary>
        /// Follow state, routed through a property so the jump-to-latest affordance can track it. Every
        /// assignment goes through here; the field is only the backing store.
        /// </summary>
        private bool FollowingTranscript
        {
            get => _followingTranscript;
            set
            {
                if (_followingTranscript == value)
                    return;
                _followingTranscript = value;
                SetValue(ShowJumpToLatestPropertyKey, !value);
            }
        }

        private static readonly DependencyPropertyKey ShowJumpToLatestPropertyKey =
            DependencyProperty.RegisterReadOnly(
                nameof(ShowJumpToLatest), typeof(bool), typeof(ChatView), new PropertyMetadata(false));

        public static readonly DependencyProperty ShowJumpToLatestProperty =
            ShowJumpToLatestPropertyKey.DependencyProperty;

        /// <summary>
        /// Whether the "jump to latest" button is offered — i.e. the transcript has stopped following the
        /// newest content. The standard affordance for a live conversation (Slack, Discord, Teams, VS
        /// Code's chat), and here it is load-bearing rather than decorative: re-pinning deliberately
        /// requires reaching the TRUE bottom (see <see cref="RepinThresholdPx"/>), which a wheel always
        /// does but a thumb drag stopping a few pixels short does not. This is what keeps "I cannot get
        /// following back" impossible by construction instead of by choosing a lenient threshold.
        /// </summary>
        public bool ShowJumpToLatest => (bool)GetValue(ShowJumpToLatestProperty);

        /// <summary>Re-engages following and rides the content back down to the newest message.</summary>
        private void JumpToLatest_Click(object sender, RoutedEventArgs e)
        {
            // Hand the focus on BEFORE the button hides. A click focuses it, and the line below
            // collapses it, at which point the framework has to put that focus somewhere of its own
            // choosing — so doing it here needs no theory about where that would have been. That theory
            // is where a focus fix goes wrong (see ReclaimAbandonedFocus), and this gesture is
            // the one case that needs none: the button always goes away, and it is holding the focus.
            if (JumpToLatestButton.IsKeyboardFocusWithin)
                InputBox.Focus();

            FollowingTranscript = true;
            ScrollTranscriptToEnd();
            // Still asked for, as a backstop: a host where the click did NOT focus the button leaves
            // nothing for the line above to do, and this fires only if the focus ends up abandoned.
            ReclaimAbandonedFocus();
        }

        /// <summary>
        /// Puts the keyboard focus back in the message box if the control that was holding it has just
        /// hidden itself and WPF has abandoned it.
        /// </summary>
        /// <remarks>
        /// The chat pane has two controls that vanish as a direct result of being clicked — the
        /// jump-to-latest pill and the permission banner's options. WPF has to re-home the focus each
        /// was holding, and what it does is drop it on the ROOT VISUAL of the presentation source.
        /// Focus on a container rather than a control makes the next arrow key directional navigation
        /// instead of a caret move, so it lands on whatever focusable thing happens to be nearest —
        /// reported as Up moving the selection into the permission banner after clicking the pill. The
        /// destination varies with what is on screen, which is what makes it read as random rather than
        /// as a rule.
        /// <para>
        /// <b>The root visual is not always a <see cref="Window"/>, and assuming it was is why the first
        /// fix worked in the Desktop host and did nothing in VS.</b> A VS tool window's content lives in
        /// an <c>HwndSource</c> whose root visual is the CONTENT — measured in the Desktop smoke's
        /// hosted-focus probe, which builds that same shape: root visual <c>ChatView</c>, and the focus
        /// after the click lands on it. A test that only ever ran against a Window-hosted view could not
        /// see the difference, and did not. Hence <see cref="IsFocusAbandoned"/> asks the source what
        /// its root is instead of naming a type.
        /// </para>
        /// <para>
        /// Deliberately conditional and deliberately deferred. Conditional, because focus sitting on a
        /// real control is the user's and must not be taken — only the abandoned case is ours to place.
        /// Deferred to Input priority, because the hiding, WPF's fallback and this all hang off the same
        /// property change and the order between them is a subscription-order accident; letting the
        /// dispatcher drain first makes it an observation rather than a race. The composer is where
        /// focus lives in this pane, and it is what the user most likely wants next — <c>Focus()</c>
        /// rather than <see cref="FocusInput"/>, so a half-typed message keeps its caret position.
        /// </para>
        /// </remarks>
        private void ReclaimAbandonedFocus() =>
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (IsFocusAbandoned(Keyboard.FocusedElement, PresentationSource.FromVisual(this)?.RootVisual))
                        InputBox.Focus();
                }),
                DispatcherPriority.Input);

        /// <summary>
        /// Whether the keyboard focus is on a container the framework dumped it on rather than on a
        /// control that means to hold it.
        /// </summary>
        /// <remarks>
        /// Asks the presentation source what its root is rather than naming a type, because the answer
        /// differs per host and getting that wrong is a silent no-op: the Desktop host roots its tree in
        /// a <see cref="Window"/>, a VS tool window roots it in the content itself. <see cref="Window"/>
        /// is still named as well, for a tree not yet connected to a source (no root to compare with)
        /// and for a focus that has landed on a different window than ours. Static and internal so the
        /// decision can be pinned per host shape without needing that host.
        /// </remarks>
        internal static bool IsFocusAbandoned(object? focused, DependencyObject? rootVisual) =>
            focused is null
            || focused is Window
            || (rootVisual is not null && ReferenceEquals(focused, rootVisual));

        /// <summary>Whether a deferred re-aim at the bottom is already queued (see ScrollTranscriptToEnd).</summary>
        private bool _secondPassQueued;

        /// <summary>
        /// Whether the pointer is being held down inside the transcript: a scrollbar thumb drag, a click
        /// on its track or line buttons, or a text selection dragged past the top edge. Any upward move
        /// while it is held is the user moving the view; nothing else that moves the view is.
        /// </summary>
        private bool _pointerDownInTranscript;

        /// <summary>
        /// Armed by a navigation key pressed inside the transcript and consumed by the next scroll change,
        /// so the move that key produced is attributed to the user. One-shot rather than a window: a key
        /// that scrolls does so in the same layout pass, and a key that doesn't must not leave intent
        /// lying around for an unrelated move later.
        /// </summary>
        private bool _keyScrollGesture;

        /// <summary>
        /// Whether a scroll happening right now is the user's doing.
        /// </summary>
        /// <remarks>
        /// Follow-the-bottom cannot infer this from the offset — an upward move is
        /// the user, unless the numbers say it was the scroller clamping itself, unless the extent grew
        /// at the same time... Each such rule is right about the case it was written for and wrong about
        /// the next one, because it reconstructs a fact (did the user scroll?) from a measurement that
        /// does not carry it: the scroller reports the same delta whether the user dragged the thumb,
        /// the content shrank underneath them, the viewport changed, or WPF brought a newly focused
        /// element into view. Issue #90 is two more of those cases.
        /// <para>
        /// So the fact is taken from the input instead, where it is not a guess. A wheel is read from
        /// the wheel event; the thumb, the track, and a selection drag are all "the button is down in
        /// here"; the keyboard arms a one-shot. What is left over — clamps, viewport changes, extent
        /// re-estimation, bring-into-view — is by construction NOT the user, and is handled by putting
        /// the view back rather than by obeying it.
        /// </para>
        /// </remarks>
        private bool IsUserScrolling =>
            (_pointerDownInTranscript && Mouse.LeftButton == MouseButtonState.Pressed) || _keyScrollGesture;

        private void TranscriptItems_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
            _pointerDownInTranscript = true;

        private void TranscriptItems_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
            _pointerDownInTranscript = false;

        /// <summary>
        /// Every press in the pane clears the flag before
        /// <see cref="TranscriptItems_PreviewMouseLeftButtonDown"/> can set it - tunnelling reaches the
        /// root first, so the two together mean "the button that is down NOW went down in the
        /// transcript", which is what <see cref="IsUserScrolling"/> reads it as.
        /// </summary>
        /// <remarks>
        /// The up-handler alone could not say that: it runs only for a release routed through
        /// <c>TranscriptItems</c>. Press on a tool row header (a Border, which captures nothing), drag
        /// out of the VS window and release over another app, and the flag stays true for the session -
        /// <c>IsUserScrolling</c> is then true whenever the left button is held ANYWHERE in the pane. So
        /// dragging the composer's resize grip shrinks the viewport, the scroller clamps, and the clamp
        /// arrives looking like a deliberate scroll away from the bottom. The follow stops, for good,
        /// because the user resized their message box. Exactly the class issue #90 says is by
        /// construction not the user, and must be corrected rather than obeyed.
        /// </remarks>
        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _pointerDownInTranscript = false;
            base.OnPreviewMouseLeftButtonDown(e);
        }

        // The keys that move a scroll viewer. Shift/Ctrl variants included by not testing modifiers:
        // Ctrl+Home is still the user going to the top, and a caret move that drags the view with it is
        // still the user going to look at something.
        private void TranscriptItems_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Up or Key.PageUp or Key.Home or Key.Down or Key.PageDown or Key.End)
                _keyScrollGesture = true;
        }

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Re-engage following when the user takes an action that means "show me the newest": sending
            // a prompt, or the transcript being replaced wholesale (New session, switching conversations).
            // Otherwise someone who scrolled up to read something earlier, then sent a message, would
            // watch their own prompt vanish into content they can't see.
            if (e.Action == NotifyCollectionChangedAction.Reset
                || e.NewItems?.OfType<MessageItemViewModel>().Any(m => m.IsUser) == true)
                FollowingTranscript = true;

            ScrollTranscriptToEnd();
        }

        // Keep the typing indicator in view when it appears (it toggles on a property, not an item add).
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // The transcript's list has been swapped for another scope's - follow the new one instead
            // (issue #148). Watched here rather than done inside the navigation funnel because the
            // view-model is the one that decides what CurrentItems is, and a restored or reset
            // navigation changes it without any gesture of the view's having caused it.
            if (e.PropertyName == nameof(ChatViewModel.CurrentItems) && sender is ChatViewModel navVm)
            {
                UnhookFollowedItems();
                HookFollowedItems(navVm.CurrentItems);
            }

            if (e.PropertyName == nameof(ChatViewModel.IsAgentTyping) && sender is ChatViewModel vm && vm.IsAgentTyping)
                ScrollTranscriptToEnd();

            // The banner leaving takes its buttons with it, one of which the user has just clicked.
            // Watched here rather than handled on the option button because the banner has more ways to
            // go than being answered — the turn ending cancels it, a session switch clears it — and all
            // of them strand the focus the same way.
            if (e.PropertyName == nameof(ChatViewModel.HasPendingPermission)
                && sender is ChatViewModel banner && !banner.HasPendingPermission)
                ReclaimAbandonedFocus();

            // A prompt arrived (or was answered): park on the row it is about, or release the park.
            if (e.PropertyName == nameof(ChatViewModel.HighlightedItem) && sender is ChatViewModel prompt)
            {
                if (prompt.HighlightedItem is { } target)
                    ParkOnPermissionTarget(target);
                else
                    ReleasePromptPark();
            }
        }

        /// <summary>
        /// True while the view is deliberately held on a pending permission prompt's row instead of
        /// riding the newest content. NOT the same as having stopped following: the follow flag stays
        /// exactly as it was, because it records whether the USER has taken control and they have not —
        /// we moved the view, not them (issue #90's rule, applied to a scroll of our own making).
        /// </summary>
        private bool _parkedOnPrompt;

        /// <summary>
        /// Shows the row a permission prompt is about — but only when the transcript is following.
        /// <para>Following is the pane running in "auto mode": the user has handed us the viewport and
        /// we get to decide what is worth showing, and a call that is BLOCKED waiting for them is worth
        /// showing — everything else in the transcript is still moving and can afford to be missed for a
        /// moment, while that one row cannot proceed at all. Scrolled away, the opposite holds: they are
        /// looking at something particular, and moving them off it is exactly the yank issue #90's rule
        /// forbids. They get the banner's "Show in transcript" instead, and choose for
        /// themselves.</para>
        /// <para>The park does not clear the follow, so answering the prompt resumes riding the newest
        /// content with no "resume" logic at all — and if the user scrolls while parked, that is their
        /// input, which clears the follow through the ordinary path and leaves them where they put it.</para>
        /// <para>
        /// <b>This scroll is the ONLY one the park will get, which is why what it ASKS FOR matters so
        /// much</b> (issue #218). <see cref="ScrollTranscriptToEnd"/> early-returns while parked — the
        /// guard that stops the park being yanked back to the end — so nothing here catches up
        /// afterwards the way the follow does everywhere else. A reveal that lands approximately right
        /// stays approximately right for as long as the user is looking at it.
        /// </para>
        /// </summary>
        private void ParkOnPermissionTarget(ChatItemViewModel item)
        {
            if (!FollowingTranscript)
                return;

            _parkedOnPrompt = true;
            RevealItem(item, stopsFollowing: false);
        }

        /// <summary>Ends the park and rides back down to the newest content, if the user still wants that.</summary>
        private void ReleasePromptPark()
        {
            if (!_parkedOnPrompt)
                return;

            _parkedOnPrompt = false;
            ScrollTranscriptToEnd();
        }

        /// <summary>
        /// Scrolls an item into view, INCLUDING one nested inside a sub-agent's row (issue #125). The
        /// transcript's own <see cref="BringItemIntoView"/> can only take a top-level item, so on a Task
        /// row holding twenty calls it would land on the parent's header while the call in question sits
        /// far below — showing the user something other than what they are being asked to approve.
        /// So: realise the top-level container first, then find the nested item's OWN element inside it.
        /// <para>
        /// <b><paramref name="stopsFollowing"/> is passed THROUGH, never decided here</b> (issue #180).
        /// This method is shared by the two callers the distinction exists to separate — an explicit
        /// gesture (<see cref="GoToItem"/>) and a scroll we perform on our own initiative (parking on a
        /// permission prompt) — and hard-coding the non-gesture value collapsed them: the plan strip
        /// scrolled to its card, the <c>ScrollChanged</c> that scroll itself raised found the follow still
        /// set, and re-aimed straight back to the bottom. The click looked like it did nothing unless the
        /// user had scrolled away first, which cleared the follow through the ordinary path and is exactly
        /// the workaround that got reported.
        /// </para>
        /// <para>
        /// The park was immune, which is why this survived since #148: <see cref="ShowPermissionTarget_Click"/>
        /// sets <see cref="_parkedOnPrompt"/> before it calls in, and <see cref="ScrollTranscriptToEnd"/>
        /// early-returns on that — so the sibling gesture was shielded by the park rather than by the
        /// flag, and the missing value could never show.
        /// </para>
        /// </summary>
        private void RevealItem(ChatItemViewModel item, bool stopsFollowing)
        {
            var container = BringItemIntoView(item, stopsFollowing);
            // Nothing further to do when the container IS the item's own row - which is true of a
            // top-level item at the root, and equally of a call sitting directly in the sub-view that has
            // been drilled into. Asking whether the item has a Parent answered the first and got the
            // second wrong, drawing a second search for an element that is the one already in hand.
            if (container is null || ReferenceEquals(container.DataContext, item))
                return;

            // The child's element only exists once the parent's ItemsControl has laid out.
            UpdateLayout();
            var nested = FindElementFor(container, item);
            // Whole where it fits, its start where it cannot — the same rule as the top-level row, and
            // stated in both places rather than shared, because the two are asking about different
            // elements against the same viewport (issue #218). A sub-agent's call is the shape most
            // likely to fit and least likely to be the last thing on screen.
            if (nested is not null)
            {
                var fits = TranscriptScroll is { } viewport && nested.ActualHeight <= viewport.ViewportHeight;
                nested.BringIntoView(new Rect(0, 0, nested.ActualWidth, fits ? nested.ActualHeight : 1));
            }
        }

        /// <summary>The realised element whose DataContext is <paramref name="item"/>, at any depth.</summary>
        private static FrameworkElement? FindElementFor(DependencyObject root, object item)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && ReferenceEquals(fe.DataContext, item)
                    && fe is not ContentPresenter)
                    return fe;
                if (FindElementFor(child, item) is { } nested)
                    return nested;
            }

            return null;
        }

        /// <summary>
        /// The banner's "Show in transcript". The half of this that is the USER's choice: offered
        /// always, and the only route when they have scrolled away, where we deliberately do not move
        /// them on their behalf. Parks too — they asked to see the row, so it should stay seen.
        /// </summary>
        private void ShowPermissionTarget_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ChatViewModel vm || vm.HighlightedItem is not { } target)
                return;

            _parkedOnPrompt = true;
            GoToItem(target);
        }

        /// <summary>
        /// Follows the transcript as it grows, which item-added events alone cannot do.
        /// </summary>
        /// <remarks>
        /// Streamed text does NOT add items: <c>Items.Add</c> runs once per message and every later delta
        /// appends to that same view-model, raising PropertyChanged rather than CollectionChanged. So a
        /// long reply (a thinking block especially) grew downward out of the viewport and the view only
        /// caught up when the NEXT item arrived — which read as the chat sitting still through the turn
        /// and then lurching to the bottom at the end. Watching the extent catches every growth source
        /// at once: streamed deltas, a tool row's live output, a card being expanded.
        ///
        /// The viewport shrinking counts too. The typing indicator sits in its own row beneath the
        /// scroller, so it appearing steals height from the viewport and would otherwise push the newest
        /// line out of sight at exactly the moment it matters.
        /// </remarks>
        private void TranscriptScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (sender is not ScrollViewer scroll)
                return;

            // Only the user stops the follow. An upward move that no gesture produced is not a decision
            // to read history — it is the scroller clamping an offset it can no longer honour, a
            // viewport changing size, a virtualising panel re-estimating its extent, or WPF bringing a
            // newly focused element into view. Obeying those is what issue #90 reports twice over.
            var gesture = IsUserScrolling;
            _keyScrollGesture = false; // one-shot, consumed by the move it produced (or by the next one)

            if (e.VerticalChange < 0 && gesture)
                FollowingTranscript = false;
            else if (e.VerticalChange > 0 && IsAtBottom(scroll))
                FollowingTranscript = true;

            // ...and while following, the bottom is where we belong, whatever moved us off it. That
            // covers every source of growth at once — streamed deltas, a tool row's live output, a card
            // expanding, the typing indicator taking its row — as well as the moves above that were
            // declined rather than obeyed. Nothing here is conditioned on how far off the bottom we are,
            // deliberately: the previous version only re-aimed from an offset still near the old bottom,
            // so any single event that moved the real bottom further than the slack left a gap that
            // nothing afterwards ever closed, and the transcript stopped following while still LOOKING
            // parked at the end.
            //
            // A transcript with nothing left to scroll is being shown WHOLE, so there is nowhere to have
            // scrolled away to and the follow is back on, whatever cleared it (issue #195). Last, because
            // it is an invariant rather than a reading of this event: at this size the branches above are
            // describing a move of a few pixels, and one of them can still be a gesture.
            //
            // The same fact as the wheel handler's guard, arriving by the other route. There the
            // transcript already fits and the gesture moves nothing, so the follow is never cleared;
            // here it was cleared while there WAS something to scroll and the content then SHRANK back
            // to fitting — the reported shape being a row expanded, read, and collapsed again. Nothing
            // in a scroll change's direction can see that: the shrink arrives as an upward clamp or as
            // no move at all, and neither re-pins. What was left was the pill offered on a pane with no
            // scrollbar, jumping to a bottom already on screen.
            if (!CanScrollAway(scroll))
                FollowingTranscript = true;

            // Through the two-pass scroll, not a bare ScrollToEnd: a single pass lands on the extent
            // ESTIMATE that was current when it was issued, and the realisation it triggers then corrects
            // that estimate, leaving us short of the real bottom.
            if (FollowingTranscript && !IsAtBottom(scroll))
                ScrollTranscriptToEnd();
        }

        /// <summary>
        /// Whether the transcript can be scrolled away from its end at all — i.e. whether there is enough
        /// hidden content for "not following" to describe anything the user can see.
        /// </summary>
        /// <remarks>
        /// One predicate for the two places that ask it, deliberately: the wheel declines to CLEAR the
        /// follow when this is false, and a scroll change RESTORES it when this is false, and those are
        /// the same rule read in the two directions. Spelled twice they could drift into disagreeing
        /// about the same pane — and the state that leaves behind is a jump-to-latest that jumps nowhere,
        /// which no amount of scrolling can clear because there is nothing left to scroll.
        /// <para>
        /// Measured against <see cref="RepinThresholdPx"/> rather than zero, for the same reason the
        /// bottom is: a gap too small to see is not somewhere the user can be scrolled away to.
        /// </para>
        /// </remarks>
        private static bool CanScrollAway(ScrollViewer scroll) => scroll.ScrollableHeight > RepinThresholdPx;

        /// <summary>
        /// How close to the bottom counts as being at it — deliberately a couple of pixels, and the only
        /// threshold left.
        /// </summary>
        /// <remarks>
        /// There used to be a second, far more generous one (24px or 15% of the viewport) for deciding
        /// whether the follow SURVIVED, because that question was answered by comparing the offset to an
        /// extent that is only an estimate while items are unrealised, and the slack absorbed the error.
        /// Taking the gesture from the input instead removed the question: follow state is now a fact
        /// carried from the wheel, the pointer and the keyboard, and the offset only ever has to answer
        /// "are we at the end", where there is no estimate error to absorb — the scroller clamps at its
        /// own bottom, so arriving there is exact.
        /// <para>
        /// The two must never be re-merged into one value. A slack this tight would make a small scroll
        /// away invisible; a slack that generous would swallow a wheel notch (48px against over 100px on
        /// a normal pane), so the first notches of a SLOW scroll up would still read as "at the bottom"
        /// and any upward scroll change arriving in that window would re-pin the follow, yanking the
        /// view back down on the next delta. Scrolling fast cleared the zone before that could happen,
        /// which is why that bug only ever showed up when scrolling gently.
        /// </para>
        /// </remarks>
        private const double RepinThresholdPx = 4;

        private static bool IsAtBottom(ScrollViewer scroll) =>
            scroll.VerticalOffset >= scroll.ScrollableHeight - RepinThresholdPx;

        /// <summary>
        /// Scrolls to the newest item — twice, deliberately. A virtualising panel reports an ESTIMATED
        /// extent derived from the items it has realised so far, so the first ScrollToEnd aims at a
        /// bottom that is only approximately right; realising the items it scrolls past corrects the
        /// estimate, and the second pass (after layout has run) lands on the real one. Without it a long
        /// transcript settles a little short of the end, which reads as "it didn't scroll to my message".
        /// Skipped entirely when the user has scrolled up to read something.
        /// </summary>
        private void ScrollTranscriptToEnd()
        {
            var scroll = TranscriptScroll;
            if (scroll is null || !FollowingTranscript || _parkedOnPrompt)
                return;

            scroll.ScrollToEnd();

            // One pending second pass at a time. Streaming calls this per delta, and without the guard a
            // fast reply queues a lambda per chunk that all do the same thing at the same priority.
            if (_secondPassQueued)
                return;

            _secondPassQueued = true;
            // Re-checked inside the lambda, not just at the call: the user can scroll away during the
            // gap, and an unconditional second pass would drag them back.
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _secondPassQueued = false;
                    if (FollowingTranscript && !_parkedOnPrompt)
                        scroll.ScrollToEnd();
                }),
                DispatcherPriority.Loaded);
        }

        // Clicking anywhere on a tool row's header acts on the row: a row carrying an attached diff
        // (an agent edit folded into its tool call) opens the host's native diff viewer, a read row
        // with a file target opens that file in the editor — the raw input/output detail stays
        // reachable via the chevron, which handles (and stops) its own clicks; any other row toggles
        // its detail (the title, kind label, and the arrow-like left kind glyph users instinctively
        // try to expand).
        private void ToolHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ToolItemViewModel vm)
                return;

            // A nested row sits INSIDE its parent's header-bearing panel, so without this the same
            // click runs twice - once on the child, then again on the parent as it bubbles - and
            // clicking a sub-agent's read would collapse the sub-agent row out from under it (#125).
            e.Handled = true;

            if (vm.CanOpenDiff)
                vm.OpenDiffCommand.Execute(null);
            else if (vm.CanOpenFile)
                vm.OpenFileCommand.Execute(null);
            else if (vm.CanExpand)
                vm.IsExpanded = !vm.IsExpanded;
        }

        /// <summary>
        /// Puts the caret in the message box. The host calls this after opening the chat window from
        /// the keyboard shortcut, so the shortcut lands the user typing rather than merely showing the
        /// pane. Dispatched at Input priority (as the other focus handlers here are) because the tool
        /// window frame is still being shown and activated when the command runs, and focus set in the
        /// middle of that is discarded.
        /// </summary>
        public void FocusInput() =>
            Dispatcher.BeginInvoke(
                new System.Action(() =>
                {
                    InputBox.Focus();
                    InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
                }),
                System.Windows.Threading.DispatcherPriority.Input);

        // Clicking the pinned plan strip scrolls the live plan card back into view (the strip exists
        // because a long turn pushes the card off the top of the transcript).
        private void PlanBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not ChatViewModel vm || vm.ActivePlan is null)
                return;
            // Through GoToItem, because this is the user asking to be taken somewhere. The plan card is
            // a top-level item, so drilled into a sub-agent's calls it is not in the list on screen at
            // all - and BringItemIntoView is scope-relative by design, so it would correctly answer
            // "not here" and the strip would silently do nothing (issue #148).
            GoToItem(vm.ActivePlan);
        }

        /// <summary>
        /// Scrolls a transcript item into view, realising it first if virtualisation has thrown its
        /// container away. Returns the realised container, or null if the item isn't in the transcript.
        /// </summary>
        /// <remarks>
        /// Under virtualisation an off-screen item usually has NO container — which is exactly the case
        /// callers care about, since an item already on screen needs no help. So asking the panel to
        /// realise it by index has to come first: <c>ContainerFromIndex</c> only answers once it has.
        /// <para>
        /// Stops the follow, because every caller is an explicit "take me to this item" — the plan strip
        /// jumping back to its card, a menu acting on a row. Without that the re-aim would drag the view
        /// straight back to the bottom and the jump would look like it did nothing. This is the ONE
        /// programmatic scroll that carries user intent, which is why it says so here rather than
        /// leaving <see cref="TranscriptScroll_ScrollChanged"/> to guess from a direction: WPF's own
        /// <c>BringIntoView</c>, raised for whatever element takes focus, is the same move with the
        /// opposite meaning and must NOT stop the follow.
        /// </para>
        /// </remarks>
        internal FrameworkElement? BringItemIntoView(ChatItemViewModel item) =>
            BringItemIntoView(item, stopsFollowing: true);

        /// <param name="stopsFollowing">
        /// Whether this scroll is the USER saying "take me there". True for every gesture — the plan
        /// strip, a menu — and false for the one scroll we perform on our own initiative, parking on a
        /// permission prompt: the follow flag records whether the user has taken control, and they have
        /// not, so claiming otherwise would leave the transcript stuck once they answer (issue #90).
        /// </param>
        private FrameworkElement? BringItemIntoView(ChatItemViewModel item, bool stopsFollowing)
        {
            if (DataContext is not ChatViewModel vm)
                return null;

            // A sub-agent's call is nested inside its parent's row, so it is not in the transcript's own
            // list at all (issue #125). Scroll to the row that IS - the ancestor the child is drawn
            // inside - rather than answering "no such item" and silently not scrolling.
            //
            // Scope-relative, and that is the whole of this method's part in navigation (issue #148): the
            // transcript draws one list, so "what do I scroll to" is a question about the list on screen,
            // and an item in a DIFFERENT scope has no answer here. Returning null is the honest one, and
            // it is what keeps this from ever navigating: taking the user somewhere else is a gesture
            // they have to make, not a side effect of something wanting to be seen (GoToItem is the
            // gesture's own entry point).
            var target = item.AncestorWithin(vm.CurrentScope);
            if (target is null)
                return null;

            var index = vm.CurrentItems.IndexOf(target);
            if (index < 0)
                return null;

            if (stopsFollowing)
                FollowingTranscript = false;

            // BringIndexIntoView is protected; BringIndexIntoViewPublic is the sanctioned way in.
            TranscriptPanel?.BringIndexIntoViewPublic(index);
            UpdateLayout();

            var container = TranscriptItems.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
            // Ask for the item's TOP EDGE, not the item. BringIntoView() scrolls the least distance that
            // makes the whole element visible, so for anything taller than the viewport that lands on its
            // BOTTOM — the caller said "take me to this item" and arrives at the end of it. Rows used to
            // be short enough that the two agreed; an expanded sub-agent row holding twenty calls is not
            // (issue #125). A one-pixel strip is the whole request: show me where this starts.
            //
            // ...but ONLY where the two disagree, and for a row that FITS they do not (issue #218). This
            // BringIntoView is the authoritative scroll — BringIndexIntoViewPublic above realises the
            // container and leaves the offset wherever realisation happened to put it — so a one-pixel
            // strip at the top is a request that is ALREADY SATISFIED by a row whose bottom is off the
            // end, and the call then does nothing. Whether it needed to do anything is a race: on a
            // quiet machine realisation lands the row flush and the strip is right by luck; under load
            // it lands ~28px short and the strip agrees, leaving the end of the row behind the banner
            // and the busy bar. That is the whole of the reported bug, and it is why the offline check
            // for it only fails under run-gates.ps1 and never on a solo smoke run.
            //
            // So the rect NAMES which case this is: the whole row where it fits, its start where it
            // cannot. #125's rule is untouched — the change only fires where that rule never applied.
            if (container is not null)
            {
                var fits = TranscriptScroll is { } viewport && container.ActualHeight <= viewport.ViewportHeight;
                container.BringIntoView(new Rect(
                    0, 0, container.ActualWidth, fits ? container.ActualHeight : 1));
            }

            // The newest item is a legitimate target — the plan card can be the last thing in the
            // transcript — and landing on the end while refusing to follow it would offer a jump-to-
            // latest that jumps nowhere. Ask where we ended up rather than trying to predict it.
            if (stopsFollowing && TranscriptScroll is { } scroll && IsAtBottom(scroll))
                FollowingTranscript = true;

            return container;
        }

        // ==== Navigation: opening a sub-agent's calls as their own transcript (issue #148) ==========
        //
        // The view-model owns WHICH ITEMS are on screen (ChatViewModel.CurrentItems / NavPath); this
        // half owns WHERE YOU WERE LOOKING, one saved frame per navigation step. They are separate
        // because the follow flag, the permission park and the scroll offset are all facts about a
        // viewport, and the view-model has no viewport - but they must be reset TOGETHER, which is what
        // ChatViewModel.NavigationReset exists for.
        //
        // Every entry point routes through here rather than binding a command straight to the
        // view-model, and that ordering is load-bearing: the outgoing position has to be captured BEFORE
        // the transcript's ItemsSource swaps, because afterwards the containers it is measured against
        // belong to the other scope. A command would bypass the capture and fail SILENTLY - the symptom
        // is landing at the top of the restored view on the way back, intermittently, which reads as a
        // scroll bug rather than as a wiring one.

        /// <summary>Where the user was looking in one navigation frame.</summary>
        /// <remarks>
        /// A plain class: this project has no <c>IsExternalInit</c> polyfill and must not gain one.
        /// </remarks>
        private sealed class NavFrame
        {
            public bool Following;
            public bool ParkedOnPrompt;

            /// <summary>The item the viewport was resting on, and how far above its top edge sat.</summary>
            public ChatItemViewModel? Anchor;
            public double AnchorDelta;

            /// <summary>The raw offset - a fallback for when the anchor cannot be found again.</summary>
            public double Offset;
        }

        /// <summary>
        /// One frame per open scope: <c>_viewStack[d]</c> is where the view was when depth <c>d</c> was
        /// left, so it is always exactly as deep as the view-model's <c>NavPath</c>.
        /// </summary>
        private readonly List<NavFrame> _viewStack = new();

        /// <summary>The scroll positions saved per frame die with the conversation that produced them.</summary>
        private void OnNavigationReset(object? sender, EventArgs e)
        {
            _viewStack.Clear();
            _parkedOnPrompt = false;
            FollowingTranscript = true;
        }

        /// <summary>
        /// Opens a row's calls as the transcript. The funnel both entry points go through - the overflow
        /// control under an expanded fan-out, and "Open transcript" on the row's context menu.
        /// </summary>
        private void OpenChildTranscript(ChatItemViewModel? row)
        {
            if (DataContext is not ChatViewModel vm)
                return;

            var frame = CaptureFrame();          // BEFORE the swap - see the note above
            if (!vm.OpenTranscript(row))
                return;

            _viewStack.Add(frame);

            // A sub-view opens at its newest call, following, exactly as the row it replaced drew its
            // newest five: a fan-out that is still streaming carries on streaming, and a finished one
            // opens where the user's eye already was. Scrolling up to read it from the start is then an
            // ordinary gesture, and one the transcript already knows how to keep out of our way.
            _parkedOnPrompt = false;
            FollowingTranscript = true;
            ScrollTranscriptToEnd();
        }

        /// <summary>
        /// Goes to <paramref name="depth"/> - 0 being the conversation - and restores the view as it was
        /// when that depth was left.
        /// </summary>
        private void NavigateToDepth(int depth)
        {
            if (DataContext is not ChatViewModel vm || depth < 0 || depth >= _viewStack.Count)
                return;

            var frame = _viewStack[depth];
            _viewStack.RemoveRange(depth, _viewStack.Count - depth);
            if (!vm.NavigateTo(depth))
                return;

            RestoreFrame(frame);
        }

        /// <summary>
        /// Records where the viewport is resting, as an ITEM plus how far it sits above the top edge -
        /// not as a raw offset.
        /// </summary>
        /// <remarks>
        /// A virtualising panel reports an ESTIMATED extent derived from the containers it has realised,
        /// which is why <see cref="ScrollTranscriptToEnd"/> needs two passes at all and why the smoke has
        /// measured an estimate of 1686 against a true 2105 - a fifth out. Restoring a raw offset into a
        /// re-realised panel therefore lands somewhere unrelated but plausible; an anchor lands on the
        /// right ITEM, to within about one item's height. The offset is kept only as the fallback for an
        /// anchor that cannot be found again.
        /// </remarks>
        private NavFrame CaptureFrame()
        {
            var frame = new NavFrame
            {
                Following = FollowingTranscript,
                ParkedOnPrompt = _parkedOnPrompt,
                Offset = TranscriptScroll?.VerticalOffset ?? 0,
            };

            if (TranscriptScroll is not { } scroll || TranscriptPanel is not { } panel)
                return frame;

            // The last realised container whose top edge is at or above the viewport's - i.e. the item
            // the user is reading down from. Falls back to the first realised one, which is the case
            // when the viewport starts mid-way through a single very tall item.
            foreach (var child in panel.Children)
            {
                if (child is not FrameworkElement fe || fe.DataContext is not ChatItemViewModel item)
                    continue;

                double top;
                try
                {
                    top = fe.TransformToAncestor(scroll).Transform(default).Y;
                }
                catch (InvalidOperationException)
                {
                    continue;   // container not connected to the scroller yet
                }

                if (frame.Anchor is null || top <= 0)
                {
                    frame.Anchor = item;
                    frame.AnchorDelta = top;
                }

                if (top > 0)
                    break;
            }

            return frame;
        }

        /// <summary>Puts the viewport back where <paramref name="frame"/> says it was.</summary>
        private void RestoreFrame(NavFrame frame)
        {
            if (DataContext is not ChatViewModel vm || TranscriptScroll is not { } scroll)
                return;

            // Both flags first, so nothing below is undone by the follow re-aiming as it goes.
            _parkedOnPrompt = frame.ParkedOnPrompt;
            FollowingTranscript = frame.Following;

            if (frame.Following)
            {
                // The bottom IS the anchor when the view was following, and it is the one position that
                // survives the content having grown while the user was away.
                ScrollTranscriptToEnd();
                return;
            }

            var index = frame.Anchor is null ? -1 : vm.CurrentItems.IndexOf(frame.Anchor);
            if (index < 0)
            {
                scroll.ScrollToVerticalOffset(frame.Offset);
                return;
            }

            TranscriptPanel?.BringIndexIntoViewPublic(index);
            UpdateLayout();

            if (TranscriptItems.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
            {
                scroll.ScrollToVerticalOffset(frame.Offset);
                return;
            }

            try
            {
                var top = container.TransformToAncestor(scroll).Transform(default).Y;
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + (top - frame.AnchorDelta));
            }
            catch (InvalidOperationException)
            {
                scroll.ScrollToVerticalOffset(frame.Offset);
            }
        }

        /// <summary>
        /// "Take me to this item, wherever it is" - the one thing here that MAY navigate, and only ever
        /// from an explicit gesture of the user's.
        /// </summary>
        /// <remarks>
        /// The split from <see cref="BringItemIntoView(ChatItemViewModel)"/> is issue #148's
        /// "never auto-navigate" rule expressed as an API shape rather than as a habit: anything that
        /// merely wants an item seen calls the other one and gets nothing when the item is elsewhere,
        /// so no amount of new callers can start yanking the user between views. The callers are the
        /// pinned plan strip and the permission banner's "Show in transcript", both of them the user
        /// asking.
        /// <para>Returns to the ROOT rather than hunting for the item's own scope: at the root every item
        /// is reachable in place, because a nested row's ancestors open around it
        /// (<see cref="ChatItemViewModel.ExpandAncestors"/>), so the user is moved the shortest distance
        /// that can show them the thing they asked for.</para>
        /// <para>
        /// <b>And it stops the follow, because that is what being a gesture MEANS here</b> (issue #180):
        /// the follow flag records whether the USER has taken control, and asking to be taken somewhere is
        /// taking control just as surely as turning the wheel. Landing at the bottom re-pins (see
        /// <see cref="BringItemIntoView(ChatItemViewModel, bool)"/>'s tail), so a jump to the newest item
        /// still does not leave a pill that jumps nowhere.
        /// </para>
        /// <para>
        /// The consequence for "Show in transcript" is deliberate: the view now STAYS on the row after the
        /// prompt is answered, where it used to ride back down the moment the park released. That is the
        /// reading its own doc already claims — <i>they asked to see the row, so it should stay seen</i>
        /// — and the way back is the one every other cleared follow has, which is the pill.
        /// </para>
        /// </remarks>
        internal void GoToItem(ChatItemViewModel item)
        {
            if (DataContext is ChatViewModel vm && item.AncestorWithin(vm.CurrentScope) is null)
                NavigateToDepth(0);

            item.ExpandAncestors();
            RevealItem(item, stopsFollowing: true);
        }

        /// <summary>The overflow control under an expanded fan-out: "Open all 26 calls".</summary>
        /// <summary>
        /// Opens or closes one MCP server's tool list in the roster panel.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A click handler rather than a bound command, and not by preference: this project's
        /// <c>RelayCommand</c> takes a parameterless delegate and DISCARDS the command parameter, so a
        /// <c>CommandParameter="{Binding}"</c> would compile, bind, run, and toggle nothing at all.
        /// </para>
        /// <para>
        /// <b>The click is marked handled</b>, or it carries on to the popup and the row the user just
        /// opened is torn down underneath them - the same trap as a nested tool row's click collapsing
        /// its own parent (issue #125).
        /// </para>
        /// </remarks>
        private void McpRowChevron_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: DetailRow row } && DataContext is ChatViewModel vm)
                vm.ToggleMcpRow(row);

            e.Handled = true;
        }

        private void OpenAllChildren_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
                OpenChildTranscript(fe.DataContext as ChatItemViewModel);
        }

        /// <summary>
        /// The row's context menu: "Open transcript". Not merely a shortcut for the overflow control -
        /// for two whole cases it is the ONLY way in, since that control needs the row to be expanded AND
        /// to have something hidden, so a collapsed row offers nothing until it is opened and a fan-out
        /// of six or fewer never engages the cap at all.
        /// </summary>
        private void OpenTranscript_Click(object sender, RoutedEventArgs e)
        {
            // Resolved exactly as the sibling menu items' bindings do: a ContextMenu is its own visual
            // tree, so the row is the menu's placement target rather than anything in this one's parent
            // chain.
            if (sender is MenuItem { Parent: ContextMenu menu })
                OpenChildTranscript((menu.PlacementTarget as FrameworkElement)?.DataContext as ChatItemViewModel);
        }

        /// <summary>A breadcrumb click - the way back, at any depth.</summary>
        private void Breadcrumb_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: NavCrumbViewModel crumb })
                NavigateToDepth(crumb.Depth);
        }

        /// <summary>The navigation stack's depth, for the Desktop host's self-check.</summary>
        internal int NavDepthForDiagnostics => _viewStack.Count;

        /// <summary>
        /// Drives the navigation funnel for the Desktop host's self-check and screenshot modes - the
        /// SAME entry the two gestures use, so a check cannot pass over a funnel the UI has stopped
        /// reaching.
        /// </summary>
        internal void OpenChildTranscriptForDiagnostics(ChatItemViewModel? row) => OpenChildTranscript(row);

        internal void NavigateToDepthForDiagnostics(int depth) => NavigateToDepth(depth);

        /// <summary>The transcript's virtualising items panel, once the template and panel exist.</summary>
        private VirtualizingStackPanel? TranscriptPanel
        {
            get
            {
                if (_transcriptPanel is null && TranscriptScroll is not null)
                    _transcriptPanel = FindVisualChild<VirtualizingStackPanel>(TranscriptItems);
                return _transcriptPanel;
            }
        }

        private VirtualizingStackPanel? _transcriptPanel;

        /// <summary>
        /// The transcript's items panel, for the perf harness — so it can sweep
        /// <see cref="VirtualizingPanel.CacheLengthProperty"/>, which decides how much beyond the
        /// viewport each scroll step has to realise.
        /// </summary>
        internal VirtualizingStackPanel? TranscriptPanelForDiagnostics => TranscriptPanel;

        /// <summary>
        /// Lowest realised transcript item index, for the perf harness. Under virtualisation this tracks
        /// roughly what is at the top of the viewport, which is how a drag's SMOOTHNESS gets measured:
        /// sweep the thumb evenly and watch whether this advances evenly with it.
        /// </summary>
        /// <remarks>
        /// Scans for the minimum rather than taking <c>Children[0]</c>: under
        /// <see cref="VirtualizationMode.Recycling"/> a container is reused in place, so the children
        /// collection's order is not guaranteed to match item order and the first entry can be a
        /// stale-index leftover. Taking the minimum is order-independent and can't invent a lurch.
        /// </remarks>
        internal int FirstRealizedIndexForDiagnostics()
        {
            var panel = TranscriptPanel;
            if (panel is null)
                return -1;

            var lowest = -1;
            foreach (UIElement child in panel.Children)
            {
                var index = TranscriptItems.ItemContainerGenerator.IndexFromContainer(child);
                if (index >= 0 && (lowest < 0 || index < lowest))
                    lowest = index;
            }

            return lowest;
        }

        private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    return match;
                if (FindVisualChild<T>(child) is { } nested)
                    return nested;
            }

            return null;
        }

        // Clicking anywhere on an edit row opens its native diff (when a diff viewer is wired). Mirrors the
        // tool-row click-to-act feel; replaces the old hyperlink on the filename.
        private void EditRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is EditItemViewModel vm && vm.CanOpenDiff)
                vm.OpenDiffCommand.Execute(null);
        }

        // When the command-rule field appears (user clicked "always" on a command), focus it and select
        // its text so it's immediately editable — which also lights up the focus border.
        private void CommandPatternBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is TextBox tb && tb.IsVisible)
                tb.Dispatcher.BeginInvoke(
                    new System.Action(() => { tb.Focus(); tb.SelectAll(); }),
                    System.Windows.Threading.DispatcherPriority.Input);
        }

        // When the inline rename box appears (pencil clicked), focus it and select all so it's
        // immediately editable.
        private void SessionTitleBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is TextBox tb && tb.IsVisible)
                tb.Dispatcher.BeginInvoke(
                    new System.Action(() => { tb.Focus(); tb.SelectAll(); }),
                    System.Windows.Threading.DispatcherPriority.Input);
        }

        // Leaving the field commits the rename (the binding writes on LostFocus) and ends editing.
        private void SessionTitleBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox { DataContext: SessionSummaryViewModel session })
                session.IsEditing = false;
        }

        // Enter commits the rename; Escape cancels without writing the edit back.
        private void SessionTitleBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox tb || tb.DataContext is not SessionSummaryViewModel session)
                return;

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                session.IsEditing = false;
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget(); // discard the edit
                session.IsEditing = false;
            }
        }

        // Ctrl+MouseWheel zooms the transcript, matching the VS editor / Copilot Chat gesture. The
        // scale is applied via the transcript's LayoutTransform so text re-wraps rather than clips.
        private const double MinZoom = 0.5;
        private const double MaxZoom = 3.0;
        private const double ZoomStep = 0.1;
        private const double DefaultZoom = 1.0;

        private void TranscriptScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                // A wheel-up is the user asking to go back and read something, and taking it from the
                // GESTURE is what makes it a fact rather than a guess — nothing but a user turns a
                // wheel. The pointer and keyboard handlers above do the same for the thumb, the track
                // and the arrow keys.
                //
                // ...unless there is nowhere to go. A wheel over a transcript that fits its viewport
                // moves nothing, so the user is still looking at the newest message — and because
                // nothing moved, no scroll change follows to put the state back, leaving "jump to
                // latest" offered forever on a chat that is already at its end (issue #90). Measured
                // against the same threshold as the bottom itself: a gap too small to see is not
                // somewhere the user can scroll away to.
                //
                // Preview is the right event even though the inner hosts swallow the bubbling one: this
                // handler sits on the ItemsControl, tunnelling reaches ancestors first, so it fires for a
                // wheel over an assistant message before ForwardWheelToTranscript ever re-raises it. That
                // is the same ordering Ctrl+zoom below relies on.
                if (e.Delta > 0 && TranscriptScroll is { } wheelScroll && CanScrollAway(wheelScroll))
                    FollowingTranscript = false;
                return; // let the ScrollViewer scroll as normal
            }

            e.Handled = true; // swallow the wheel so the transcript doesn't also scroll while zooming
            ApplyZoom(_zoom.ScaleX + (e.Delta > 0 ? ZoomStep : -ZoomStep), persist: true);
        }

        // Inner scroll-capable controls (the assistant-markdown FlowDocumentScrollViewer, the
        // user/thinking TextBox) mark the bubbling MouseWheel handled, so a plain wheel over them never
        // reaches the outer transcript ScrollViewer and the transcript won't scroll. Those hosts grow to
        // their content (they never scroll internally), so swallow the wheel here and re-raise it as a
        // bubbling event from the parent — bypassing the inner control's own handlers — so it reaches
        // TranscriptScroll. Ctrl+wheel is already consumed by the transcript's tunneling zoom handler
        // above (Preview fires ancestor-first), so this only ever sees a plain wheel.
        /// <summary>
        /// Whether <paramref name="host"/> has a scroller of its own that can still move in the wheel's
        /// direction. False for a host sized to its content (no scroller, or nothing to scroll), and
        /// false at the end of travel - which is exactly when forwarding is the right answer.
        /// </summary>
        private static bool CanScrollFurther(DependencyObject host, int delta)
        {
            var scroller = FindDescendant<ScrollViewer>(host);
            if (scroller is null || scroller.ScrollableHeight <= 0)
                return false;

            // Wheel up is a positive delta and moves the offset DOWN toward zero.
            return delta > 0
                ? scroller.VerticalOffset > 0
                : scroller.VerticalOffset < scroller.ScrollableHeight;
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    return match;
                if (FindDescendant<T>(child) is { } deeper)
                    return deeper;
            }

            return null;
        }

        private void ForwardWheelToTranscript(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || sender is not FrameworkElement element)
                return;

            // The premise of forwarding is that these hosts grow to their content and never scroll
            // internally - true of three of the four call sites, and NOT of the backend-failure Details
            // box, which is MaxHeight-capped with its own scrollbar. Handling on PREVIEW meant the wheel
            // never reached that box's scroller: a stderr dump or a stack trace longer than the cap
            // could only be read by dragging the thumb, on the one control whose whole purpose is to be
            // read and pasted into a bug report (issue #82).
            //
            // Asked per wheel rather than per control, because "can this scroll" is a fact about the
            // content at that moment and not about the element - which also fixes the end-of-travel
            // case, where a nested scroller that CANNOT move still swallows the wheel and the transcript
            // sits refusing to move under the pointer.
            if (CanScrollFurther(element, e.Delta))
                return;

            e.Handled = true;
            var target = element.Parent as UIElement ?? element;
            target.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = target,
            });
        }

        /// <summary>Formats a scale as the percentage the readout shows (1.25 → "125%").</summary>
        internal static string FormatZoom(double zoom) =>
            Math.Round(zoom * 100).ToString(System.Globalization.CultureInfo.CurrentCulture) + "%";

        private void FlashZoom(double zoom)
        {
            ZoomFlashText.Text = FormatZoom(zoom);
            _zoomFlash.Begin(); // restarts if already running
        }

        /// <summary>
        /// Applies a zoom shortcut directly, bypassing the key handler. Exists for the headless hosts —
        /// <c>Keyboard.Modifiers</c> reads the real keyboard, so a self-check can't synthesise Ctrl+plus.
        /// </summary>
        internal void ApplyZoomShortcut(ZoomShortcut action)
        {
            if (action != ZoomShortcut.None)
                ApplyZoom(ZoomFor(action), persist: true);
        }

        private double ZoomFor(ZoomShortcut action) => action switch
        {
            ZoomShortcut.In => _zoom.ScaleX + ZoomStep,
            ZoomShortcut.Out => _zoom.ScaleX - ZoomStep,
            _ => DefaultZoom,
        };

        internal enum ZoomShortcut { None, In, Out, Reset }

        /// <summary>
        /// Ctrl+plus / Ctrl+minus / Ctrl+0 (both the main row and the numpad), the keyboard route to the
        /// zoom the Ctrl+MouseWheel gesture drives. Static and unit-tested (<c>ChatZoomShortcutTests</c>)
        /// because the interesting part is which combinations we deliberately DON'T claim.
        /// <para>
        /// Two of these shadow bindings from VS's global table (checked against the VSSDK's own vsct):
        /// <c>Ctrl+-</c> is <c>cmdidShellNavBackward</c> (Navigate Backward) and <c>Ctrl+=</c> is
        /// SelToGoBack — the latter only in the text editor, so it's free here. Shadowing is contained by
        /// the handler being on ChatView rather than the window root: it can only fire while keyboard
        /// focus is inside the chat, which is the same scoping a VS keybinding table would give us, and
        /// Navigate Backward keeps working everywhere else. What we must NOT do is widen that: Shift is
        /// refused on minus, because <c>Ctrl+Shift+-</c> is Navigate FORWARD and nothing about the chat
        /// pane earns the right to eat it. Alt is refused throughout (<c>Ctrl+Alt+-</c> is Peek Navigate
        /// Backward, <c>Ctrl+Alt+0</c> a window layout). Shift IS allowed on plus, since <c>Ctrl++</c> is
        /// physically Ctrl+Shift+= on most layouts and is what a user means by "Ctrl and plus".
        /// </para>
        /// </summary>
        internal static ZoomShortcut ResolveZoomShortcut(Key key, ModifierKeys modifiers)
        {
            if ((modifiers & ModifierKeys.Control) == 0 || (modifiers & ModifierKeys.Alt) != 0)
                return ZoomShortcut.None;

            var shift = (modifiers & ModifierKeys.Shift) != 0;
            return key switch
            {
                Key.OemPlus or Key.Add => ZoomShortcut.In,
                Key.OemMinus or Key.Subtract when !shift => ZoomShortcut.Out,
                Key.D0 or Key.NumPad0 when !shift => ZoomShortcut.Reset,
                _ => ZoomShortcut.None,
            };
        }

        /// <summary>Writes a pending zoom change now. No-op when nothing is pending.</summary>
        private void FlushZoom()
        {
            if (!_saveZoomTimer.IsEnabled)
                return;

            _saveZoomTimer.Stop();
            ExtensionConfig.SaveChatZoom(_zoom.ScaleX);
        }

        private void ZoomShortcut_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var action = ResolveZoomShortcut(e.Key, Keyboard.Modifiers);
            if (action == ZoomShortcut.None)
                return;

            // Handled even when the zoom is already clamped at its limit: the gesture was ours either
            // way, and letting a no-op fall through would fire VS's Navigate Backward instead.
            e.Handled = true;
            ApplyZoomShortcut(action);
        }

        // Clamps, applies the scale, and (when the change is user-driven) flashes the readout and
        // schedules a debounced save.
        private void ApplyZoom(double zoom, bool persist)
        {
            zoom = zoom < MinZoom ? MinZoom : zoom > MaxZoom ? MaxZoom : zoom;

            // Flashed before the unchanged-check, so pressing Ctrl+plus at the ceiling still answers with
            // "300%" rather than nothing — at a limit, silence reads as a broken shortcut. `persist` is
            // what distinguishes a user gesture from the restore in the constructor, which must not flash.
            if (persist)
                FlashZoom(zoom);

            if (zoom == _zoom.ScaleX)
                return;

            _zoom.ScaleX = zoom;
            _zoom.ScaleY = zoom;
            UpdateInputBoxSize(); // the box's pane-relative ceiling is expressed in pre-zoom units

            if (persist)
            {
                _saveZoomTimer.Stop(); // restart the debounce window on each change
                _saveZoomTimer.Start();
            }
        }

        // The message box is resizable by the grip on its top edge. Copilot Chat isn't, but we clean
        // pasted terminal output at the paste seam specifically so the user can review it before
        // sending — and a 400-line CI trace isn't reviewable through six lines of viewport.
        private const double DefaultInputHeight = 28;    // one line: the resting size before any drag
        private const double DefaultInputCeiling = 120;  // ~6 lines: how far content alone may grow it
        private const double MaxInputHeight = 2000;      // backstop for a hand-edited config
        private const double MaxInputPaneFraction = 0.6; // the box may never take more of the pane than this

        private double _dragStartPointerY;
        private double _dragStartHeight;

        private void InputGrip_DragStarted(object sender, DragStartedEventArgs e)
        {
            _dragStartPointerY = Mouse.GetPosition(this).Y;
            // Continue from the size on screen, not the stored preference: content may have grown the
            // box past its floor, or a short pane may have clamped it below.
            _dragStartHeight = InputBox.ActualHeight > 0 ? InputBox.ActualHeight : _inputHeight;
        }

        // Dragging up makes the box taller. The pointer is measured against this control rather than
        // read from e.VerticalChange: the grip itself moves as the box grows, and Thumb reports its
        // delta relative to itself, so a growing box feeds back into the next measurement. ChatView
        // holds still, so the offset from where the drag began stays exact. Its coordinates are
        // post-zoom while the box's bounds are pre-zoom, hence the divide.
        private void InputGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            var scale = _zoom.ScaleX > 0 ? _zoom.ScaleX : 1.0;
            var moved = (Mouse.GetPosition(this).Y - _dragStartPointerY) / scale;
            ApplyInputHeight(_dragStartHeight - moved, persist: false);
        }

        // Persisted on mouse-up rather than per-tick: a drag has a definite end, unlike a burst of
        // zoom wheel ticks, so this needs no debounce timer.
        private void InputGrip_DragCompleted(object sender, DragCompletedEventArgs e) =>
            ExtensionConfig.SaveChatInputHeight(_inputHeight);

        private void InputGrip_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ApplyInputHeight(DefaultInputHeight, persist: true); // matches Ctrl+0 for zoom
        }

        private void ApplyInputHeight(double height, bool persist)
        {
            if (double.IsNaN(height) || double.IsInfinity(height))
                height = DefaultInputHeight;

            _inputHeight = Math.Max(DefaultInputHeight, Math.Min(height, MaxInputHeight));
            UpdateInputBoxSize();

            if (persist)
                ExtensionConfig.SaveChatInputHeight(_inputHeight);
        }

        private void UpdateInputBoxSize()
        {
            var (floor, max) = ComputeInputBounds(_inputHeight, ActualHeight, _zoom.ScaleX);

            // Guarded so a no-op resize doesn't invalidate layout (this runs on every SizeChanged).
            if (InputBox.MinHeight != floor)
                InputBox.MinHeight = floor;
            if (InputBox.MaxHeight != max)
                InputBox.MaxHeight = max;
        }

        /// <summary>
        /// The message box's size policy: the chosen height becomes a <em>floor</em> (MinHeight) rather
        /// than a fixed size, so auto-grow-with-content survives — an undragged box still starts at one
        /// line, and a dragged one can still stretch further for a long paste. Both bounds are capped at
        /// a fraction of the pane, since the input's row is Auto-sized and would otherwise squeeze the
        /// transcript away in a short window; the caller's preference is left untouched by that cap, so
        /// re-docking taller gives the size back. Static and unit-tested (<c>ChatInputSizingTests</c>) —
        /// the arithmetic is the part worth pinning, and it needs no WPF tree.
        /// </summary>
        /// <param name="desiredHeight">The user's chosen resting height, in pre-zoom units.</param>
        /// <param name="paneHeight">The chat pane's height (post-zoom); 0 = not laid out yet, so no cap.</param>
        /// <param name="zoom">The transcript zoom scale, which the box's bounds sit underneath.</param>
        internal static (double Floor, double Ceiling) ComputeInputBounds(double desiredHeight, double paneHeight, double zoom)
        {
            var scale = zoom > 0 ? zoom : 1.0;
            var cap = paneHeight > 0
                ? Math.Max(DefaultInputHeight, paneHeight * MaxInputPaneFraction / scale)
                : double.PositiveInfinity;

            var floor = Math.Min(Math.Max(desiredHeight, DefaultInputHeight), cap);
            return (floor, Math.Min(Math.Max(floor, DefaultInputCeiling), cap));
        }

        // Right-click "Copy" on a message bubble (assistant markdown viewer, or the user/thinking
        // SelectableEmojiText control): copies the current selection when there is one, else the whole
        // block — the old "Copy all" affordance folded into a single item so no selection is needed.
        private void CopyBlock_Click(object sender, RoutedEventArgs e)
        {
            var target = (((MenuItem)sender).Parent as ContextMenu)?.PlacementTarget;
            switch (target)
            {
                case FlowDocumentScrollViewer viewer when !string.IsNullOrEmpty(viewer.Selection?.Text):
                    ApplicationCommands.Copy.Execute(null, viewer); // routes to the viewer's rich copy
                    break;
                case SelectableEmojiText emoji when emoji.HasSelection:
                    // Routes through the control's emoji-aware ChatClipboard copy interceptor.
                    ApplicationCommands.Copy.Execute(null, emoji);
                    break;
                default:
                    // Nothing selected → copy the whole item (the view model's CopyText).
                    if ((target as FrameworkElement)?.DataContext is ChatItemViewModel item)
                        item.CopyCommand.Execute(null);
                    break;
            }
        }

        // Left-clicking the header copy/export button opens its ContextMenu (one menu definition
        // serves both the click and a plain right-click on the button).
        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { ContextMenu: ContextMenu menu } fe)
            {
                menu.PlacementTarget = fe;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            }
        }

        // "Export as Markdown…": serializes the transcript (ChatViewModel.BuildTranscriptMarkdown)
        // and writes it wherever the user picks. The dialog lives here, not in the view model, so the
        // VM stays host-agnostic and testable.
        private void ExportChat_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ChatViewModel vm)
                return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export conversation",
                Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
                DefaultExt = ".md",
                FileName = SuggestExportFileName(vm.CurrentSessionTitle),
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                System.IO.File.WriteAllText(dialog.FileName, vm.BuildTranscriptMarkdown());
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("Couldn't write the file: " + ex.Message, "Export conversation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Session titles are free text (first-prompt derived), so strip anything the filesystem rejects.
        private static string SuggestExportFileName(string title)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var chars = title.Trim().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (System.Array.IndexOf(invalid, chars[i]) >= 0)
                    chars[i] = '_';
            }
            var name = new string(chars).Trim();
            return (name.Length == 0 ? "conversation" : name) + ".md";
        }

        /// <summary>
        /// Drops the release-point menu under the pill. A <see cref="ContextMenu"/> rather than a
        /// hand-built Popup so the items get VS's own menu theming, checkmarks and keyboard handling for
        /// free; opening it from Click (not right-click) is what makes the chevron tell the truth.
        /// <para>
        /// <see cref="ContextMenu.PlacementTarget"/> must be set explicitly: a menu opened in code has no
        /// idea what it belongs to, so without it WPF places it at the pointer and the DataContext it
        /// inherits is the wrong one — the bindings would silently resolve to nothing.
        /// </para>
        /// </summary>
        /// <summary>
        /// Opens the composer's "Add" menu, rebuilt from the view-model's registered context sources
        /// (issue #73, rung 2).
        ///
        /// <para><b>Built here rather than bound in XAML</b> because the list is a host's decision -
        /// the VS shell registers the debugger read, the Desktop host a fake, a future build a
        /// selection and a diagnostics slice - and each item needs its own source's command plus a
        /// tooltip that changes with availability. An ItemsSource-bound ContextMenu with an
        /// ItemContainerStyle would do it, at the cost of a style whose setters are harder to read
        /// than the six lines below.</para>
        ///
        /// <para><b>The refresh is the point of doing it on Click.</b> Availability is a live fact -
        /// the debugger being stopped - and RelayCommand does not hook CommandManager, so nothing
        /// would re-ask on its own. The menu being summoned IS the moment the answer is wanted, and
        /// the only moment it can be stale.</para>
        /// </summary>
        private void AddContextButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.ContextMenu is not { } menu)
                return;
            if (DataContext is not ChatViewModel vm)
                return;

            vm.RefreshContextSources();

            menu.Items.Clear();

            // Attaching an image FILE heads the menu and is offered by every host: it needs no IDE
            // behind it, being the peer of Ctrl+V rather than of the debugger read, and it is the one
            // item here that is never disabled. It is built in place rather than registered as a
            // ChatContextSource because a source produces a TEXT block (ChatContextCapture) while this
            // produces an attachment - a different payload with a different chip - and forcing it
            // through that registry would mean widening every source's contract for one item. The chip
            // itself still comes from ChatViewModel.AttachImage, the same single path a paste takes, so
            // there remains exactly one place a picture becomes an attachment.
            var attachImage = new MenuItem
            {
                Header = "Image…",
                ToolTip = "Pick an image file to send with this message",
                Tag = AttachImageMenuItemTag,
            };
            attachImage.Click += AttachImage_Click;
            menu.Items.Add(attachImage);

            // Directly under it, and that adjacency is the point: the two items are the same gesture over
            // the same file with different answers, so putting them side by side is where the distinction
            // is cheapest to learn. The LABEL is what carries it - "File path..." says what lands - because
            // this one types into the box while its neighbours attach a chip, which is a real difference in
            // kind and the only place in this menu it occurs. A file goes as a path because the agent reads
            // it and gets the CURRENT bytes (issue #95); an image cannot be read back as pixels, which is
            // the whole of why it is the exception.
            var insertFilePath = new MenuItem
            {
                Header = "File path…",
                ToolTip = "Insert a file's path, for the agent to read",
                Tag = InsertFilePathMenuItemTag,
            };
            insertFilePath.Click += InsertFilePath_Click;
            menu.Items.Add(insertFilePath);

            if (vm.ContextSources.Count > 0)
                menu.Items.Add(new Separator());

            foreach (var source in vm.ContextSources)
            {
                // The item stays SHOWN and disabled rather than hidden when the source has nothing to
                // give: a gesture that vanishes teaches nothing, while a greyed one whose tooltip
                // says "the debugger isn't stopped" teaches what would enable it. Same rule as the
                // Send button, which greys rather than disappearing.
                menu.Items.Add(new MenuItem
                {
                    Header = source.Label,
                    ToolTip = source.Description,
                    Command = source.Command,
                    DataContext = source,
                });
            }

            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        /// <summary>
        /// How the "Image…" item is found again once the menu has been built in code - by a check, and
        /// by anything else that has to name it. A <see cref="FrameworkElement.Tag"/> rather than the
        /// header text, which is display copy and would take the assertion with it when reworded.
        /// </summary>
        internal const string AttachImageMenuItemTag = "attach-image";

        /// <summary>How the "File path…" item is found again, on the same rule as its neighbour.</summary>
        internal const string InsertFilePathMenuItemTag = "insert-file-path";

        /// <summary>
        /// "Add ▾ → Image…": picks image files and attaches them exactly as a paste would.
        ///
        /// <para>The dialog lives here rather than in the view-model for the reason the export dialog's
        /// does: the view-model stays host-agnostic and testable, and the seam beneath it
        /// (<see cref="TryAttachImageFile"/>) is what a self-check drives, a check being no more able to
        /// summon a modal file dialog than it is allowed to write to the real clipboard.</para>
        ///
        /// <para><b>A refusal is reported.</b> The filter is a hint - the user can pick "All files", and
        /// the format is decided by the magic bytes - so a pick can legitimately yield nothing
        /// attachable. Saying nothing would leave a message that LOOKS as though it carries the picture,
        /// which is the one outcome issue #118 rules out. The files that did attach are kept, because
        /// dropping those too would punish the good half of a multiple selection.</para>
        /// </summary>
        private void AttachImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Attach image",
                Filter = ClipboardImages.FileDialogFilter,
                CheckFileExists = true,
                Multiselect = true,
            };
            if (dialog.ShowDialog() != true)
                return;

            var names = new List<string>();
            var reasons = new List<string>();
            foreach (var path in dialog.FileNames)
            {
                if (TryAttachImageFile(path, out var refusal))
                    continue;

                names.Add(System.IO.Path.GetFileName(path));
                reasons.Add(refusal ?? "could not be attached.");
            }

            if (names.Count == 0)
                return;

            // One file gets its reason as a sentence; several get a line each, because a shared
            // paragraph would have to describe every cause at once - which is exactly how the old
            // message came to tell a valid, too-large PNG that it was not a PNG.
            var body = names.Count == 1
                ? reasons[0]
                : string.Join(Environment.NewLine, names.Select((n, i) => n + " — " + reasons[i]));

            ShowAttachRefusal("Couldn't attach " + string.Join(", ", names) + ".", body);
        }

        /// <summary>
        /// The one place an attach refusal is shown, so the picker and the paste cannot drift into
        /// describing the same file two different ways — the property <c>ClipboardImages.TryReadFile</c>
        /// already claims for the two gestures ("a different GESTURE, not a different payload"),
        /// carried through to what happens when the answer is no.
        /// </summary>
        private static void ShowAttachRefusal(string header, string body) =>
            MessageBox.Show(
                header + Environment.NewLine + Environment.NewLine + body,
                "Attach image", MessageBoxButton.OK, MessageBoxImage.Warning);

        /// <summary>
        /// Attaches one image file to the composer, returning whether it took it. The seam the picker
        /// runs on - <see cref="TryPasteImageFrom"/>'s twin - and it ends in the same
        /// <c>ChatViewModel.AttachImage</c>, so a picked image and a pasted one are the same chip,
        /// written to disk at the same moment and carried on the wire by the same code.
        /// </summary>
        internal bool TryAttachImageFile(string? path) => TryAttachImageFile(path, out _);

        /// <summary>
        /// As above, carrying out the sentence that says why a refusal happened, for the picker to
        /// show. Null where there is nothing to tell the user about the FILE — no view-model bound is
        /// our problem, not theirs, and the caller supplies its own words for it.
        /// </summary>
        internal bool TryAttachImageFile(string? path, out string? refusal)
        {
            refusal = null;
            if (DataContext is not ChatViewModel vm)
                return false;
            if (!ClipboardImages.TryReadFile(path, out var image, out refusal))
                return false;

            vm.AttachImage(image);
            return true;
        }

        /// <summary>
        /// "Add ▾ → File path…": picks files and types their paths into the message, exactly as dropping
        /// them would.
        ///
        /// <para><b>It is the drop with no pointer.</b> Both gestures end in
        /// <see cref="InsertIntoComposer"/>, so the spelling, the padding, the single undo unit and the
        /// caret are one implementation - a second route that composed its own insertion would be a
        /// second answer to how a path enters a message, and those drift.</para>
        ///
        /// <para><b>It appends, and does not use the caret.</b> A drop carries an aim - the pointer IS
        /// the user saying where - which is what <see cref="InsertionIndex"/> exists to recover. A menu
        /// click carries no such signal, so the caret is wherever it was last left, possibly scrolled out
        /// of sight and possibly mid-word in a half-typed sentence. The end of the message is the only
        /// place that cannot surprise, and it is what a drop landing outside the box already does.</para>
        ///
        /// <para>No refusal notice, unlike its image neighbour: the dialog is told the file must exist,
        /// and every file that exists has a path. There is nothing this can be handed that it has to
        /// turn down.</para>
        /// </summary>
        private void InsertFilePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Insert file path",
                Filter = "All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = true,
            };
            if (dialog.ShowDialog() != true)
                return;

            TryInsertFilePaths(dialog.FileNames);
        }

        /// <summary>
        /// Appends the given files' paths to the message, returning whether it put anything there. The
        /// seam the picker runs on, for the same reason <see cref="TryDropData"/> is one: a self-check
        /// cannot summon a modal file dialog.
        /// <para>
        /// The spelling comes from <c>ChatViewModel.ComposerPathFor</c>, which is the drop's own
        /// canonicalizer. That is not tidiness: <b>one canonical spelling per file is an invariant</b> -
        /// edits dedupe on <c>toolCallId|path</c> - so a second route producing a differently-cased or
        /// differently-rooted spelling of the same file splits one file into two everywhere downstream.
        /// </para>
        /// </summary>
        internal bool TryInsertFilePaths(IReadOnlyList<string>? paths)
        {
            if (DataContext is not ChatViewModel vm || paths is null)
                return false;

            var payloads = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(vm.ComposerPathFor)
                .ToList();

            // The end of the text, never the caret - see InsertFilePath_Click for why.
            return payloads.Count != 0 && InsertIntoComposer(payloads, InputBox.Text.Length);
        }

        private void PendingReleaseButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.ContextMenu is not { } menu)
                return;

            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // Enter sends; Shift+Enter inserts a newline; Ctrl+Shift+V pastes verbatim.
        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.V &&
                (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
                (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                // The escape hatch from InputBox_Pasting, for the paste whose escape sequences ARE
                // the subject ("why is my program emitting this?"). Ctrl+Z is not a substitute: the
                // pasting handler swaps the data object, so undo restores the pre-paste state
                // rather than the raw text. WPF binds paste to Ctrl+V and Shift+Insert only, so
                // this gesture is ours and there's nothing to double up with.
                _pasteRaw = true;
                try { InputBox.Paste(); }
                finally { _pasteRaw = false; }
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Enter)
                return;

            if (DataContext is not ChatViewModel vm)
                return;

            var gesture = EnterGestures.For(Keyboard.Modifiers);
            if (gesture == EnterGesture.Newline)
                return;

            // Ctrl+Enter sends NOW, interrupting a running turn. This is what plain Enter did mid-turn
            // before messages could be held, kept reachable under a modifier: a gesture that used to
            // interrupt must not quietly become one that waits, and a user who means "stop that and do
            // this" needs a key for it. With nothing running it is an ordinary send.
            // One ladder, one modifier per rung: Enter queues for the turn's end, Ctrl+Enter
            // brings it to the agent's next safe point, Ctrl+Shift+Enter cuts in now. Shift alone is
            // newline and is handled before this.
            //
            // Ctrl+Shift+Enter is ours to take: VS binds it to Edit.LineOpenBelow scoped to the TEXT
            // EDITOR, and this box is a tool window, so the scopes don't meet (checked in Options →
            // Keyboard, 2026-08-10).
            var command = gesture switch
            {
                EnterGesture.Now => vm.SendNowCommand,
                EnterGesture.NextStep => vm.SteerCommand,
                _ => vm.SendCommand,
            };

            // Only swallow the keystroke if we're actually going to act on it. Handling it
            // unconditionally made Enter a dead key whenever the command was disabled — mid-turn it
            // neither sent, nor inserted a newline, nor gave any feedback at all (issue #70). Letting
            // it through instead means the worst case is a newline in the box, which at least looks
            // like the key did something.
            if (!command.CanExecute(null))
                return;

            e.Handled = true;
            command.Execute(null);
        }

        // Cleans terminal/CI-log text on its way into the input box: a pasted GitLab job trace
        // otherwise shows its ANSI escapes as missing-glyph boxes ("▯[32;1m") and runs its
        // CR-overwritten lines together — and the agent gets the same noise, since the prompt is
        // whatever's in this box.
        //
        // Done at paste time rather than in ChatViewModel.SendAsync on purpose. The cleaned text
        // lands in the box where the user can see, edit or delete it before sending, so nothing is
        // transformed behind their back and the transcript and the prompt can't disagree. It also
        // leaves room for the raw-paste gesture above, which a send-time pass could not: by then
        // the box is one flat string with no record of which region came from where.
        private void InputBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            // TEXT ONLY. Images are taken one layer up, in the Paste command's Executed handler, which
            // is the only place that can see them: this event is not raised at all when the clipboard
            // carries no text format, and a screen capture puts none there. See the command binding in
            // the constructor for the measurement.
            if (_pasteRaw || !e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, autoConvert: true))
                return;

            if (e.SourceDataObject.GetData(DataFormats.UnicodeText, autoConvert: true) is not string text ||
                !TerminalText.NeedsCleaning(text))
                return; // ordinary text is passed through untouched, CRLF endings and all

            var cleaned = new DataObject();
            cleaned.SetData(DataFormats.UnicodeText, TerminalText.Clean(text));
            e.DataObject = cleaned;
            e.FormatToApply = DataFormats.UnicodeText;
        }

        /// <summary>
        /// Offers Paste when the clipboard holds either text (the built-in answer) or an image (ours).
        /// Without the image half, a capture-only clipboard greys the context menu's Paste out and
        /// leaves Ctrl+V with nothing to route to — the reported symptom.
        /// </summary>
        private void InputBox_PasteCanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = ClipboardOffersSomethingToPaste();
            e.Handled = true;
        }

        // The answer and the clipboard revision it was computed for. The validity flag is separate
        // because a real sequence number can legitimately be 0, so no sentinel value is available.
        private static uint _pasteAnswerSequence;
        private static bool _pasteAnswer;
        private static bool _pasteAnswerValid;

        /// <summary>
        /// The Paste answer, recomputed only when the clipboard has actually changed.
        /// </summary>
        /// <remarks>
        /// <c>CanExecute</c> is re-queried on every <c>RequerySuggested</c>, which the
        /// <c>CommandManager</c> raises on essentially every keystroke - and the honest answer costs two
        /// CROSS-PROCESS clipboard operations: <c>Clipboard.ContainsText</c>, plus a
        /// <c>Clipboard.GetDataObject</c> that is an <c>OleGetClipboard</c> handing back a COM proxy
        /// into the clipboard OWNER's process, which <see cref="ClipboardImages.HasImage"/> then queries
        /// three times. Copy a large image out of a browser, switch to VS and type: every keystroke
        /// makes blocking calls into that browser from the VS UI thread, and if the owner is suspended
        /// or not pumping messages each one blocks until OLE gives up.
        /// <para>
        /// A try/catch cannot help, because this is a stall rather than a throw (the #177 shape). The
        /// fix is to stop asking. <c>ClipboardImages.HasImage</c> documents this exact constraint and
        /// keeps ITSELF to format presence for it; the cost was all on this side, in what the caller
        /// handed it. <c>GetClipboardSequenceNumber</c> is a cheap local counter that changes whenever
        /// anything writes to the clipboard, so the answer is recomputed exactly when it could have
        /// changed - and it is a fact about machine state, hence static.
        /// </para>
        /// </remarks>
        private static bool ClipboardOffersSomethingToPaste()
        {
            var sequence = GetClipboardSequenceNumber();
            if (_pasteAnswerValid && sequence == _pasteAnswerSequence)
                return _pasteAnswer;

            _pasteAnswer = SafeClipboardHasText() || ClipboardImages.HasImage(SafeGetClipboardData());
            _pasteAnswerSequence = sequence;
            _pasteAnswerValid = true;
            return _pasteAnswer;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        /// <summary>
        /// Image first, then the ordinary text paste. The order is the feature: a screenshot clipboard
        /// can also carry a text form — Explorer publishes the file's path, a browser the page's
        /// markup — so pasting text first drops a path into the box and silently discards the picture.
        /// <para>
        /// <see cref="TextBoxBase.Paste"/> is called directly rather than re-issuing the command, so
        /// there is no re-entrancy; it still raises <c>DataObject.Pasting</c>, which is what keeps the
        /// terminal-output cleaning working for text.
        /// </para>
        /// </summary>
        private void InputBox_PasteExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;
            if (TryPasteImageFrom(SafeGetClipboardData(), out var refusal))
                return;

            // Refused something that WAS an image: say so, in the picker's words and its dialog. The
            // text fall-through below is not a fallback here — a clipboard holding a picture we would
            // not take usually holds no text either, so pasting on would do nothing at all and leave
            // the user believing the picture went in. Only a payload that was never an image falls
            // through, which is the ordinary text paste and stays silent.
            if (refusal is not null)
            {
                ShowAttachRefusal("Couldn't attach the pasted image.", refusal);
                return;
            }

            InputBox.Paste();
        }

        /// <summary>
        /// Lifts an image off a data object into the composer's attachment strip, returning whether it
        /// took one. The seam the Executed handler runs on, so a check can drive it with a data object
        /// of its own rather than through the real clipboard — which a self-check must not disturb.
        /// </summary>
        internal bool TryPasteImageFrom(IDataObject? source) => TryPasteImageFrom(source, out _);

        /// <summary>
        /// As above, carrying out the sentence for a payload that WAS an image and was refused
        /// anyway. Null where the clipboard simply held no image, which is the ordinary text paste.
        /// </summary>
        internal bool TryPasteImageFrom(IDataObject? source, out string? refusal)
        {
            refusal = null;
            if (DataContext is not ChatViewModel vm)
                return false;
            if (!ClipboardImages.TryRead(source, out var image, out refusal))
                return false;

            vm.AttachImage(image);
            return true;
        }

        /// <summary>
        /// What a drag landing on the chat should be taken to mean.
        /// </summary>
        internal enum DropClaim
        {
            /// <summary>Not ours — let it route on, which for a text drop on the box is the point.</summary>
            None,

            /// <summary>File paths, transcribed into the composer.</summary>
            Files,

            /// <summary>Loose text, routed into the composer and cleaned on the way.</summary>
            TextIntoComposer,
        }

        /// <summary>
        /// The whole feature as a three-input truth table.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Files are claimed everywhere, INCLUDING over the input box</b> — that is the reported bug. A
        /// <c>TextBox</c>'s editor only accepts text formats, so it rejects a <c>FileDrop</c> outright and
        /// the drop appears to do nothing; everywhere else the drag routes past our content to Visual
        /// Studio, which opens the file in an editor tab.
        /// </para>
        /// <para>
        /// <b>Text is claimed only OUTSIDE the box.</b> Over it, the TextBox's own handling is what we
        /// want: dragging a selection within the box moves it, and WPF routes a text drop through the
        /// paste pipeline, so <c>DataObject.Pasting</c> still fires and terminal output is still cleaned.
        /// Claiming text there would quietly take both of those away.
        /// </para>
        /// <para>
        /// Extracted and static for the reason <c>EnterGestures.For</c> is: written inline, this shape was
        /// wrong and untestable, and a whole branch sat dead under a full set of passing checks that drove
        /// the commands rather than the gesture.
        /// </para>
        /// </remarks>
        internal static DropClaim ClaimDrop(bool hasFiles, bool hasText, bool overInputBox)
        {
            if (hasFiles)
                return DropClaim.Files;
            if (hasText && !overInputBox)
                return DropClaim.TextIntoComposer;
            return DropClaim.None;
        }

        /// <summary>
        /// The text a drop adds and where it goes: the payloads joined by single spaces, padded against
        /// whatever is already on each side of <paramref name="at"/> so a drop into the middle of a
        /// sentence does not weld itself to the words around it.
        /// </summary>
        /// <param name="existingText">The box's current content.</param>
        /// <param name="at">The insertion point, already clamped to <paramref name="existingText"/>.</param>
        /// <param name="payloads">The spellings to insert, in order.</param>
        /// <returns>The string to insert, where to insert it, and where the caret should end up.</returns>
        internal static (string Insert, int At, int CaretAfter) ComposeInsertion(
            string existingText, int at, IReadOnlyList<string> payloads)
        {
            existingText ??= string.Empty;
            at = Math.Max(0, Math.Min(at, existingText.Length));

            if (payloads is null || payloads.Count == 0)
                return (string.Empty, at, at);

            var body = string.Join(" ", payloads);

            // Pad only where there is something to be welded to. An empty box, or a drop that lands
            // against whitespace the user already typed, must not gain a stray leading or trailing space.
            var before = at > 0 && !char.IsWhiteSpace(existingText[at - 1]) ? " " : string.Empty;
            var after = at < existingText.Length && !char.IsWhiteSpace(existingText[at]) ? " " : string.Empty;

            var insert = before + body + after;
            return (insert, at, at + before.Length + body.Length);
        }

        /// <summary>
        /// Turns "the character nearest the pointer" into "where the text goes", by deciding which SIDE
        /// of that character the drop landed on.
        /// </summary>
        /// <remarks>
        /// <c>GetCharacterIndexFromPoint</c> answers a different question than it looks like it does: it
        /// names the character CLOSEST to the point, never a gap between two of them. Dropped past the end
        /// of the text it therefore returns the index of the final character, and inserting there puts the
        /// payload BEFORE it — reported from the field as a second dropped path landing before the last
        /// character of the first. Comparing the pointer against the midpoint of that character's own box
        /// is what recovers the gap the user was aiming at.
        /// <para>
        /// Split out and pure for the same reason <see cref="ClaimDrop"/> is: the offline checks drove
        /// either an explicit index or an append, so the conversion this fixes was the one step nothing
        /// exercised, and it shipped wrong.
        /// </para>
        /// </remarks>
        /// <param name="nearestChar">What <c>GetCharacterIndexFromPoint</c> returned; negative if it found nothing.</param>
        /// <param name="pointX">The drop's X, in the box's own coordinates.</param>
        /// <param name="charBounds">That character's bounding box, from <c>GetRectFromCharacterIndex</c>.</param>
        /// <param name="textLength">Length of the text being inserted into, the upper bound of the answer.</param>
        internal static int InsertionIndex(int nearestChar, double pointX, Rect charBounds, int textLength)
        {
            // No character under or near the pointer — an empty box, or a hit outside the text entirely.
            // The end is the only defensible answer, and it is what an append would have done anyway.
            if (nearestChar < 0)
                return textLength;

            var index = nearestChar;

            // A zero-width rect is the caret position at the very end rather than a character, so there is
            // no "past the middle" to be on; the clamp below catches it either way.
            if (!charBounds.IsEmpty && charBounds.Width > 0 && pointX > charBounds.X + (charBounds.Width / 2))
                index++;

            return Math.Max(0, Math.Min(index, textLength));
        }

        /// <summary>
        /// Claims a drag for the composer, so that Visual Studio does not get it. The tunnelling pair is
        /// on the ChatView ROOT, which is in the route for a drop over any descendant — transcript,
        /// header, banners and input box alike — so one handler covers the whole pane and
        /// <c>e.Handled</c> is what stops the drag routing on to VS's own file-opening handler.
        /// <para>
        /// Judged on FORMAT PRESENCE only. This runs on every mouse-move of the drag, and the filesystem
        /// may not be touched here — see <see cref="FileDropPaths.CouldCarryFiles"/>.
        /// </para>
        /// </summary>
        private void Chat_PreviewDragOver(object sender, DragEventArgs e)
        {
            var claim = ClaimDrop(
                FileDropPaths.CouldCarryFiles(e.Data), HasText(e.Data), IsOverInputBox(e));
            if (claim == DropClaim.None)
                return;

            var effect = Preferred(e.AllowedEffects);
            if (effect == DragDropEffects.None)
                return; // nothing we can honestly advertise; let it route on

            e.Effects = effect;
            e.Handled = true;
        }

        /// <summary>
        /// Takes the drop the cursor promised to take.
        /// </summary>
        /// <remarks>
        /// <b><c>e.Handled</c> is set before the decode, and stays set even when the decode yields
        /// nothing.</b> The claim made during the drag is optimistic by construction — format presence,
        /// no filesystem — so handing the drop back at this point would open an editor tab immediately
        /// after the Copy cursor said we would take it. A quiet no-op is the honest outcome, and the
        /// <c>[drop]</c> log line is what makes it diagnosable rather than mysterious.
        /// </remarks>
        private void Chat_PreviewDrop(object sender, DragEventArgs e)
        {
            var overInputBox = IsOverInputBox(e);
            var claim = ClaimDrop(
                FileDropPaths.CouldCarryFiles(e.Data), HasText(e.Data), overInputBox);
            if (claim == DropClaim.None)
                return;

            e.Handled = true;
            TryDropData(e.Data, overInputBox ? e.GetPosition(InputBox) : (Point?)null);
        }

        /// <summary>
        /// Lifts a drag's payload into the composer, returning whether it put anything there. The seam the
        /// drop handler runs on, so a self-check can drive it with a data object of its own — WPF gives
        /// <see cref="DragEventArgs"/> no public constructor, and a real OLE drag would need a modal loop
        /// that a headless run cannot survive.
        /// </summary>
        /// <param name="data">The drag's payload.</param>
        /// <param name="pointInInputBox">
        /// Where the drop landed, in the input box's own coordinates, or null when it landed elsewhere in
        /// the pane (in which case the payload goes to the end of whatever is already typed).
        /// </param>
        internal bool TryDropData(IDataObject? data, Point? pointInInputBox)
        {
            if (DataContext is not ChatViewModel vm)
                return false;

            // The claim is re-made here rather than trusted from the handler, and the seam would be a lie
            // without it: "over the input box" IS "we were given a point", so this has the same fact the
            // handler had. A self-check driving the seam must meet the same refusal a real text drop on
            // the box meets, or it would report drag-to-move working while it was silently taken away.
            var claim = ClaimDrop(
                FileDropPaths.CouldCarryFiles(data), HasText(data), pointInInputBox is not null);
            if (claim == DropClaim.None)
                return false;

            var payloads = claim == DropClaim.Files
                ? FileDropPaths.Decode(data).Select(vm.ComposerPathFor).ToList()
                : DroppedText(data);

            if (payloads.Count == 0)
                return false;

            var at = pointInInputBox is { } point ? DropIndexAt(point) : InputBox.Text.Length;
            return InsertIntoComposer(payloads, at);
        }

        /// <summary>
        /// Puts already-composed payloads into the message box at <paramref name="at"/>. The tail both
        /// insertion gestures share — a drop, which aims with the pointer, and the "File path…" picker,
        /// which has nothing to aim with and appends.
        /// </summary>
        /// <remarks>
        /// Through <c>SelectedText</c> rather than the view-model's <c>InputText</c>, because that is what
        /// puts the insertion on the box's undo stack — Ctrl+Z after a drop is the whole reason.
        /// <c>BeginChange</c>/<c>EndChange</c> makes a multi-file insertion ONE undo unit rather than one
        /// per path, and the box's <c>UpdateSourceTrigger=PropertyChanged</c> binding carries the text to
        /// the view-model with no second code path.
        /// <para>
        /// <c>Focus()</c> is not gated on success: an insertion into a tool window VS has not activated
        /// must still land, even if the keyboard focus stays where it was.
        /// </para>
        /// </remarks>
        private bool InsertIntoComposer(IReadOnlyList<string> payloads, int at)
        {
            var (insert, index, caretAfter) = ComposeInsertion(InputBox.Text, at, payloads);
            if (insert.Length == 0)
                return false;

            InputBox.BeginChange();
            try
            {
                InputBox.Select(index, 0);
                InputBox.SelectedText = insert;
            }
            finally
            {
                InputBox.EndChange();
            }

            InputBox.CaretIndex = Math.Min(caretAfter, InputBox.Text.Length);
            InputBox.Focus();
            return true;
        }

        /// <summary>
        /// Where in the box a drop at <paramref name="point"/> should insert. The WPF half of
        /// <see cref="InsertionIndex"/>, kept next to nothing else so the arithmetic stays testable.
        /// </summary>
        private int DropIndexAt(Point point)
        {
            var nearest = InputBox.GetCharacterIndexFromPoint(point, snapToText: true);
            var bounds = nearest >= 0 ? InputBox.GetRectFromCharacterIndex(nearest) : Rect.Empty;
            return InsertionIndex(nearest, point.X, bounds, InputBox.Text.Length);
        }

        /// <summary>
        /// Text dropped anywhere but the input box, cleaned of terminal escapes on the way in.
        /// </summary>
        /// <remarks>
        /// The cleaning has to be re-applied BY HAND here, and it is easy to assume otherwise:
        /// <c>InputBox_Pasting</c> reaches dragged text today only because WPF routes a text drop ON the
        /// TextBox through the paste pipeline. The moment this inserts via <c>SelectedText</c>,
        /// <c>DataObject.Pasting</c> is never raised — so without this the cleaning would disappear
        /// silently, and only over the drag route.
        /// </remarks>
        private static List<string> DroppedText(IDataObject? data)
        {
            var text = ReadText(data);
            return string.IsNullOrWhiteSpace(text)
                ? new List<string>()
                : new List<string> { TerminalText.Clean(text) };
        }

        // "Over the input box" is geometric rather than a walk up from e.OriginalSource: for a drag over a
        // TextBox that source is some inner TextBoxView or ScrollViewer and the walk is fragile, while
        // GetPosition transforms into the box's own coordinate space and so survives the composer's zoom
        // LayoutTransform for free. It is also the very point GetCharacterIndexFromPoint needs, so the
        // position is measured once rather than twice.
        private bool IsOverInputBox(DragEventArgs e)
        {
            if (!InputBox.IsVisible)
                return false;
            var point = e.GetPosition(InputBox);
            return new Rect(0, 0, InputBox.ActualWidth, InputBox.ActualHeight).Contains(point);
        }

        // Copy is what every chat client shows for this gesture. Link reads as "create a shortcut", which
        // is not what happens, so it is only taken when the source refuses Copy - and if it offers
        // neither, we do not claim the drag at all rather than advertise an effect it will not honour.
        private static DragDropEffects Preferred(DragDropEffects allowed)
        {
            if ((allowed & DragDropEffects.Copy) == DragDropEffects.Copy)
                return DragDropEffects.Copy;
            if ((allowed & DragDropEffects.Link) == DragDropEffects.Link)
                return DragDropEffects.Link;
            return DragDropEffects.None;
        }

        private static bool HasText(IDataObject? data) => !string.IsNullOrEmpty(ReadText(data));

        // The drag source is a DIFFERENT PROCESS and may die mid-gesture, so nothing read off its data
        // object may throw into a drag handler.
        private static string? ReadText(IDataObject? data)
        {
            if (data is null)
                return null;
            try
            {
                return data.GetDataPresent(DataFormats.UnicodeText, autoConvert: true)
                    ? data.GetData(DataFormats.UnicodeText, autoConvert: true) as string
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The clipboard is shared machine state and can be transiently locked by whatever last wrote
        // to it. Neither of these may throw: one answers CanExecute (queried constantly by the
        // CommandManager) and the other is on the paste path itself.
        private static IDataObject? SafeGetClipboardData()
        {
            try { return Clipboard.GetDataObject(); }
            catch (Exception) { return null; }
        }

        private static bool SafeClipboardHasText()
        {
            try { return Clipboard.ContainsText(); }
            catch (Exception) { return false; }
        }
    }
}
