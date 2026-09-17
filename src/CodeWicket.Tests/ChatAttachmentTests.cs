using System;
using System.Collections.Generic;
using System.IO;
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
    /// A pasted image's journey through the composer (issue #118): from the attachment strip, onto the
    /// wire as an image block where the backend takes one, and onto disk-plus-a-path where it does not.
    /// <para>
    /// The rule these mostly pin is the negative one: <b>a message must never reach the agent looking
    /// as though it carried the image when it did not.</b> Every degradation here is therefore either
    /// a working fallback or a visible notice, and never silence.
    /// </para>
    /// </summary>
    [Collection(StoragePathCollection.Name)]
    public class ChatAttachmentTests : IDisposable
    {
        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };

        private readonly string _scratch = Path.Combine(
            Path.GetTempPath(), "cwkt-attachment-tests", Guid.NewGuid().ToString("N"));

        /// <summary>A per-test session store, so the restore checks have something to reopen.</summary>
        private readonly FileSessionStore _store;

        public ChatAttachmentTests()
        {
            _store = new FileSessionStore(Path.Combine(_scratch, "sessions"));
            // A send WRITES its attachments, so without this the checks would scatter test images
            // through the developer's own LocalAppData — the harness inflicting on the real machine
            // exactly what it is exercising. Same rule as ExtensionConfig.RedirectTo.
            AttachmentStore.RedirectTo(_scratch);
        }

        public void Dispose()
        {
            AttachmentStore.RedirectTo(null!);
            try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort cleanup */ }
        }

        [Fact]
        public void APastedImageIsSentAsAnAttachmentAlongsideTheText() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsImages = true };
            var vm = Started(engine);

            vm.AttachImage("snip.png", "image/png", Png);
            vm.InputText = "what's wrong here?";
            vm.SendCommand.Execute(null);
            DrainDispatcher();

            var sent = Assert.Single(engine.Attachments);
            var attachment = Assert.Single(sent!);
            Assert.Equal("image/png", attachment.MimeType);
            Assert.Equal(Convert.ToBase64String(Png), attachment.Data);
            Assert.Contains("what's wrong here?", engine.Prompts.Last());
        });

        [Fact]
        public void TheComposerStripIsClearedWhenTheMessageIsTaken() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsImages = true };
            var vm = Started(engine);

            vm.AttachImage("snip.png", "image/png", Png);
            Assert.True(vm.HasPendingAttachments);

            vm.InputText = "look";
            vm.SendCommand.Execute(null);
            DrainDispatcher();

            Assert.False(vm.HasPendingAttachments);
            Assert.Empty(vm.PendingAttachments);
        });

        [Fact]
        public void AnImageOnItsOwnIsAWholeMessage() => RunSta(() =>
        {
            // Pasting a screenshot and pressing Enter is a complete gesture. Requiring a word to
            // justify the picture would be a gate with nothing behind it.
            var engine = new StubEngine { SupportsImages = true };
            var vm = Started(engine);

            vm.AttachImage("snip.png", "image/png", Png);

            Assert.True(vm.SendCommand.CanExecute(null));

            vm.SendCommand.Execute(null);
            DrainDispatcher();

            Assert.Single(engine.Attachments);
        });

        [Fact]
        public void AnEmptyComposerStillCannotBeSent() => RunSta(() =>
        {
            var vm = Started(new StubEngine { SupportsImages = true });

            Assert.False(vm.SendCommand.CanExecute(null));
        });

        [Fact]
        public void AttachingRefreshesAllThreeSendGesturesNotJustEnter() => RunSta(() =>
        {
            // They share one gate. Refreshing only Send is how Ctrl+Enter ends up disabled on a message
            // Enter would have taken.
            var vm = Started(new StubEngine { SupportsImages = true });

            vm.AttachImage("snip.png", "image/png", Png);

            Assert.True(vm.SendCommand.CanExecute(null));
            Assert.True(vm.SteerCommand.CanExecute(null));
            Assert.True(vm.SendNowCommand.CanExecute(null));
        });

        [Fact]
        public void TheMessageInTheTranscriptCarriesItsImages() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsImages = true };
            var vm = Started(engine);

            vm.AttachImage("snip.png", "image/png", Png);
            vm.InputText = "see attached";
            vm.SendCommand.Execute(null);
            DrainDispatcher();

            var message = vm.Items.OfType<MessageItemViewModel>()
                .Last(m => m.Role == MessageRole.User);

            Assert.True(message.HasAttachments);
            Assert.Equal("snip.png", Assert.Single(message.Attachments).Name);
        });

        [Fact]
        public void ASentAttachmentCanNoLongerBeRemoved() => RunSta(() =>
        {
            // The × belongs to the composer. A button still shown in the transcript but silently doing
            // nothing would be worse than no button.
            var engine = new StubEngine { SupportsImages = true };
            var vm = Started(engine);

            vm.AttachImage("snip.png", "image/png", Png);
            Assert.True(vm.PendingAttachments[0].CanRemove);

            vm.InputText = "see attached";
            vm.SendCommand.Execute(null);
            DrainDispatcher();

            var message = vm.Items.OfType<MessageItemViewModel>().Last(m => m.Role == MessageRole.User);
            Assert.False(message.Attachments[0].CanRemove);
        });

        [Fact]
        public void AHeldMessageKeepsItsImagesAndDeliversThemWithTheBatch() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsImages = true };
            var vm = Started(engine);

            // Start a turn, so the next message is held rather than sent.
            vm.InputText = "get to work";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            Assert.True(vm.IsBusy);
            engine.Attachments.Clear();

            vm.AttachImage("snip.png", "image/png", Png);
            vm.InputText = "and this too";
            vm.SendCommand.Execute(null);
            DrainDispatcher();

            var held = Assert.Single(vm.PendingMessages);
            Assert.True(held.HasAttachments);
            // Nothing has gone yet — the whole point of the tray.
            Assert.Empty(engine.Attachments);

            engine.CompleteTurn();
            DrainDispatcher();

            Assert.Single(Assert.Single(engine.Attachments)!);
        });

        [Fact]
        public void ABackendThatTakesNoImagesGetsAPathAndTheUserIsTold() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsImages = false };
            var vm = Started(engine);

            vm.AttachImage("snip.png", "image/png", Png);
            vm.InputText = "what's wrong here?";
            vm.SendCommand.Execute(null);
            DrainDispatcher();

            // No image block went (it would be dropped or rejected), but the message still carries the
            // image — as the path it was saved to, which is the manual workaround issue #118 describes,
            // done for the user rather than by them.
            Assert.Null(engine.Attachments.Last());
            var prompt = engine.Prompts.Last();
            Assert.Contains("<attached-images>", prompt);
            Assert.Contains(".png", prompt);
            Assert.Contains("what's wrong here?", prompt);

            // And it is not silent: a degradation the user cannot see is one they cannot work around.
            Assert.Contains(
                vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("doesn't accept images", StringComparison.OrdinalIgnoreCase));
        });

        [Fact]
        public void TheFallbackNoticeIsSaidOncePerSessionButThePathIsSentEveryTime() => RunSta(() =>
        {
            // The limitation belongs to the backend, so repeating it on every message would be nagging;
            // the fallback itself is per-message and must not be.
            var engine = new StubEngine { SupportsImages = false };
            var vm = Started(engine);

            for (var i = 0; i < 2; i++)
            {
                vm.AttachImage("snip.png", "image/png", Png);
                vm.InputText = "message " + i;
                vm.SendCommand.Execute(null);
                DrainDispatcher();
                engine.CompleteTurn();
                DrainDispatcher();
            }

            Assert.Equal(
                1,
                vm.Items.OfType<NoticeItemViewModel>()
                    .Count(n => n.Text.Contains("doesn't accept images", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(2, engine.Prompts.Count(p => p.Contains("<attached-images>")));
        });

        [Fact]
        public void AnUnsendableAndUnsaveableImageIsReportedAsAnError() => RunSta(() =>
        {
            // The one case with no way through: the backend won't take the bytes and there is no file
            // to name. Saying nothing would let the user believe their screenshot went.
            var engine = new StubEngine { SupportsImages = false };
            var vm = Started(engine);

            // Bytes-less: nothing to save, so nothing to point at. (The same shape as an attachment
            // restored from a transcript, which is why that one is never re-sent either.)
            vm.AttachImage("gone.png", "image/png", bytes: null);
            vm.InputText = "look at this";
            vm.SendCommand.Execute(null);
            DrainDispatcher();

            Assert.Contains(
                vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Kind == NoticeKind.Error
                     && n.Text.Contains("couldn't be sent", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("<attached-images>", engine.Prompts.Last());
        });

        [Fact]
        public void ARestoredAttachmentIsNotSentAgain() => RunSta(() =>
        {
            // A restored one is a picture of something already said: the transcript kept its path, not
            // its bytes. Sending it again would re-upload an image the agent has already seen.
            var engine = new StubEngine { SupportsImages = true };
            var vm = Started(engine);

            vm.AttachImage("old.png", "image/png", bytes: null, filePath: "C:\\old.png");
            vm.InputText = "and again";
            vm.SendCommand.Execute(null);
            DrainDispatcher();

            Assert.Null(Assert.Single(engine.Attachments));
        });

        [Fact]
        public void ARestoredMessageKeepsItsImageEvenWhenTheFileIsGone() => RunSta(() =>
        {
            // Retention sweeps the attachment directory on its own schedule, so a conversation reopened
            // weeks later outlives its pictures. The chip must still be there and still be named: a
            // message that DID carry an image must never read as though it did not — the same rule that
            // governs the send path, applied to the transcript's own memory of it.
            var engine = new StubEngine { SupportsImages = true };
            var vm = Started(engine);

            vm.AttachImage("snip.png", "image/png", Png);
            vm.InputText = "what is wrong here?";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            engine.CompleteTurn();
            DrainDispatcher();

            var saved = vm.Items.OfType<MessageItemViewModel>()
                .Last(m => m.Role == MessageRole.User).Attachments[0].FilePath;
            Assert.NotNull(saved);

            // The sweep, simulated exactly: the file goes, the transcript entry keeps its path.
            File.Delete(saved!);

            var reopened = new ChatViewModel(
                new StubEngine { SupportsImages = true },
                new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null),
                sessionStore: _store);
            reopened.RestoreMostRecentSession();

            var restored = reopened.Items.OfType<MessageItemViewModel>().Last(m => m.Role == MessageRole.User);
            Assert.True(restored.HasAttachments);
            Assert.Equal("snip.png", restored.Attachments[0].Name);
            // No bytes, no file: the thumbnail cannot decode, and the template falls back to naming it
            // rather than leaving a hole where a picture was.
            Assert.False(restored.Attachments[0].HasThumbnail);
        });

        [Fact]
        public void ARestoredMessageSTILLSHOWSItsImageWhileTheFileIsThere() => RunSta(() =>
        {
            // The other half, and the one that proves the check above is about the missing file rather
            // than about restore losing attachments altogether.
            var engine = new StubEngine { SupportsImages = true };
            var vm = Started(engine);

            vm.AttachImage("snip.png", "image/png", RealPng());
            vm.InputText = "look";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            engine.CompleteTurn();
            DrainDispatcher();

            var reopened = new ChatViewModel(
                new StubEngine { SupportsImages = true },
                new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null),
                sessionStore: _store);
            reopened.RestoreMostRecentSession();

            var restored = reopened.Items.OfType<MessageItemViewModel>().Last(m => m.Role == MessageRole.User);
            Assert.True(restored.Attachments[0].HasThumbnail);
        });

        /// <summary>A real, decodable 2x2 PNG — the thumbnail path needs bytes a codec accepts.</summary>
        private static byte[] RealPng()
        {
            var pixels = new byte[2 * 2 * 4];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = 0x80;
            var source = System.Windows.Media.Imaging.BitmapSource.Create(
                2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 2 * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        // ---- helpers --------------------------------------------------------------------------

        private ChatViewModel Started(StubEngine engine)
        {
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null),
                sessionStore: _store);

            // Open the session so the capability discovered in the handshake is known, then clear what
            // that first turn recorded.
            vm.InputText = "hello";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            engine.CompleteTurn();
            DrainDispatcher();
            engine.Prompts.Clear();
            engine.Attachments.Clear();
            return vm;
        }

        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        private sealed class StubEngine : IEngineConnection
        {
            private TaskCompletionSource<PromptResponse>? _turn;

            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public bool SupportsImages { get; set; }

            public List<string> Prompts { get; } = new();

            /// <summary>What each prompt carried — null for a prompt with no attachments at all.</summary>
            public List<IReadOnlyList<PromptAttachmentDto>?> Attachments { get; } = new();

            public void CompleteTurn()
            {
                var turn = _turn;
                _turn = null;
                turn?.TrySetResult(new PromptResponse("end_turn"));
            }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new StartSessionResponse("c1", SupportsImages: SupportsImages));

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
            {
                Prompts.Add(text);
                Attachments.Add(attachments);
                _turn = new TaskCompletionSource<PromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _turn.Task;
            }

            public Task CancelAsync(CancellationToken cancellationToken = default)
            {
                CompleteTurn();
                return Task.CompletedTask;
            }

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
            {
                CompleteTurn();
                return Task.FromResult(new SteerResponse(nameof(Core.SteerOutcome.Injected)));
            }

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
