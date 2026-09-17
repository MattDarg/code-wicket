using System;
using System.Collections.Generic;
using System.IO;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What the SUMMARY carries about attached images (issue #118).
    /// <para>
    /// This text is the whole of a new agent's knowledge of the earlier conversation — the summary
    /// route is the one that can carry a conversation onto a different backend, so it is the hand-off.
    /// It omitted attachments entirely: a screenshot plus "fix this" became <c>User: fix this</c>, and
    /// an image-only message a blank <c>User:</c> line. That is the export's defect on a path where
    /// nobody can see it, because the reader is not a person: a human notices a missing chip, an agent
    /// just reasons confidently from an account with the subject removed.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Every fixture here records what the product records — a NAME, through
    /// <see cref="AttachmentStore.StoredForm"/> — and that is the whole reason this class now needs a
    /// redirected attachment root.</b> They were absolute paths, which was the truth until the store
    /// began recording names; afterwards the suite went on asserting a shape nothing writes, stayed
    /// green, and covered a hand-off that had started reporting every surviving image as gone. A test
    /// supplying the input production no longer produces cannot fail for the right reason — the
    /// clipboard fixture's lesson from #118, one layer over.
    /// </remarks>
    [Collection(StoragePathCollection.Name)]
    public class SummaryHandoffImageTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _root;
        private readonly string _file;
        private readonly string _stored;

        public SummaryHandoffImageTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cwkt-handoff-" + Guid.NewGuid().ToString("N"));
            _root = Path.Combine(_dir, "attachments");
            Directory.CreateDirectory(_root);
            AttachmentStore.RedirectTo(_root);

            // Shaped like a real one (<conversation>-<content hash>.png), though only its resolution
            // is under test, so the bytes need not be a PNG.
            _file = Path.Combine(_root, "handoff-1a2b3c4d5e6f7788.png");
            File.WriteAllText(_file, "not really a png, only its path matters");
            _stored = AttachmentStore.StoredForm(_file);
        }

        public void Dispose()
        {
            AttachmentStore.RedirectTo(null!);
            try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best-effort cleanup */ }
        }

        private static PersistedSession Session(params TranscriptEntry[] log)
        {
            var session = new PersistedSession { WorkspaceRootPath = "C:\\ws" };
            session.Log.AddRange(log);
            return session;
        }

        private TranscriptEntry UserWithImage(string text, string? stored = null) => new TranscriptEntry
        {
            Role = "user",
            Text = text,
            Attachments = new List<AttachmentEntry>
            {
                new AttachmentEntry
                {
                    Name = "dialog-bug.png",
                    MimeType = "image/png",
                    Path = stored ?? _stored,
                },
            },
        };

        [Fact]
        public void TheSummaryIsToldAnImageWasPartOfTheMessage()
        {
            var text = ChatViewModel.TranscriptText(Session(UserWithImage("why is this cut off?")));

            Assert.Contains("[image attached:", text);
            Assert.Contains("why is this cut off?", text);
        }

        /// <summary>
        /// THE CASE THIS CLASS EXISTS FOR, and the one that regressed. The log holds a name; what the
        /// reader needs is somewhere it can go and look.
        /// </summary>
        /// <remarks>
        /// Statting the stored value directly answers false — a bare name resolves against the
        /// process's working directory, which in the VSIX is devenv's install folder — so every image
        /// still sitting in the store was handed over as "no longer available". Nothing failed and
        /// nothing was logged: the receiving agent is simply told the evidence is gone, and it is not
        /// a reader that can notice a picture missing from a page.
        /// </remarks>
        [Fact]
        public void AStoredNameIsResolvedToAPathTheReaderCanUse()
        {
            // Not merely "there was an image": a capable agent can go and READ it, which is the same
            // move the no-image-support fallback makes and for the same reason.
            var text = ChatViewModel.TranscriptText(Session(UserWithImage("look")));

            Assert.Contains(_file, text);
            Assert.DoesNotContain("no longer available", text);
        }

        /// <summary>
        /// A conversation saved before the store recorded names holds an absolute path. The same
        /// resolve reads that shape too, so an old log still hands over something usable.
        /// </summary>
        [Fact]
        public void ALogFromBeforeTheChangeStillHandsOverItsPath()
        {
            var elsewhere = Path.Combine(_dir, "old-root");
            Directory.CreateDirectory(elsewhere);
            var absolute = Path.Combine(elsewhere, "pre-rename.png");
            File.WriteAllText(absolute, "an image saved while the log stored full paths");

            var text = ChatViewModel.TranscriptText(Session(UserWithImage("look", absolute)));

            Assert.Contains(absolute, text);
        }

        [Fact]
        public void AReclaimedImageIsStatedAsGoneRatherThanOmitted()
        {
            // Retention outlives no conversation quietly: "there was an image and it is gone" is a
            // better starting point for the next agent than silence, which reads as "there was none".
            var text = ChatViewModel.TranscriptText(Session(UserWithImage("look", "vanished.png")));

            Assert.Contains("dialog-bug.png", text);
            Assert.Contains("no longer available", text);
        }

        [Fact]
        public void AnImageOnlyMessageIsNoLongerABlankUserLine()
        {
            // It used to summarize as "User:" and nothing else — a turn that says the user said
            // nothing, when what they did was show the agent a picture.
            var text = ChatViewModel.TranscriptText(Session(UserWithImage(string.Empty)));

            Assert.Contains("[image attached:", text);
            Assert.DoesNotContain("User: \r\n", text);
            Assert.DoesNotContain("User: \n", text);
        }

        [Fact]
        public void AMessageWithNoImagesIsUnchanged()
        {
            // The overwhelmingly common turn must not gain any decoration at all.
            var text = ChatViewModel.TranscriptText(
                Session(new TranscriptEntry { Role = "user", Text = "just a question" }));

            Assert.Equal("User: just a question", text.Trim());
        }

        [Fact]
        public void AssistantTurnsStillReadAsBefore()
        {
            var text = ChatViewModel.TranscriptText(Session(
                new TranscriptEntry { Role = "user", Text = "hello" },
                new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "text", Text = "hi there" } },
                new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "turnDone" } }));

            Assert.Contains("User: hello", text);
            Assert.Contains("Assistant: hi there", text);
        }
    }
}
