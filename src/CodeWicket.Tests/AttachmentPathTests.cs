using System;
using System.IO;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What a transcript records for an attachment, and how it is found again afterwards.
    /// </summary>
    /// <remarks>
    /// The transcript stores the FILE NAME and resolves it against the attachment root at read time,
    /// so the directory can move without stranding every image ever pasted. It moved at the Code
    /// Wicket rename, which is what turned this from a theoretical nicety into a real one: before the
    /// change, a saved conversation held an absolute path into the old root, and a user who tidied
    /// that root away got their history back with the pictures missing.
    /// <para>
    /// Storing a bare name is only unambiguous because the directory is deliberately FLAT — the
    /// retention sweep is non-recursive, so nothing may nest. If that ever changes, this does too.
    /// </para>
    /// </remarks>
    [Collection(StoragePathCollection.Name)]
    public sealed class AttachmentPathTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _current;
        private readonly string _old;

        public AttachmentPathTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cwkt-attach-" + Guid.NewGuid().ToString("N"));
            // Neutral names on purpose: what is under test is a root MOVE, not the one that
            // prompted it, so this keeps working after the rename's compatibility code is deleted.
            _current = Path.Combine(_dir, "new-root", "attachments");
            _old = Path.Combine(_dir, "old-root", "attachments");
            Directory.CreateDirectory(_current);
            Directory.CreateDirectory(_old);
            AttachmentStore.RedirectTo(_current);
        }

        public void Dispose()
        {
            AttachmentStore.RedirectTo(null!);
            try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
        }

        private string WriteInto(string root, string name)
        {
            var path = Path.Combine(root, name);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            return path;
        }

        [Fact]
        public void WhatIsStoredIsTheNameAlone()
        {
            Assert.Equal("a1b2.png", AttachmentStore.StoredForm(Path.Combine(_old, "a1b2.png")));
            Assert.Equal(string.Empty, AttachmentStore.StoredForm(null));
        }

        [Fact]
        public void AStoredNameResolvesAgainstTheCurrentRoot()
        {
            WriteInto(_current, "a1b2.png");

            Assert.Equal(Path.Combine(_current, "a1b2.png"), AttachmentStore.ResolveStored("a1b2.png"));
        }

        /// <summary>
        /// THE CASE THIS EXISTS FOR. A conversation saved before the rename holds an absolute path
        /// into a root that no longer has the file; the image is under the current root instead.
        /// </summary>
        /// <remarks>
        /// Without the resolve this returns the stored path, <c>File.Exists</c> answers false one
        /// layer up, and the restored conversation shows a chip where the picture was — with nothing
        /// logged and nothing failing.
        /// </remarks>
        [Fact]
        public void AnOldAbsolutePathIsFoundUnderTheNewRoot()
        {
            var moved = WriteInto(_current, "a1b2.png");
            var recordedBeforeTheMove = Path.Combine(_old, "a1b2.png");

            Assert.Equal(moved, AttachmentStore.ResolveStored(recordedBeforeTheMove));
        }

        /// <summary>
        /// A user who has NOT moved anything is still served by the stored path.
        /// </summary>
        [Fact]
        public void AnOldAbsolutePathStillWorksWhereNothingMoved()
        {
            var stillThere = WriteInto(_old, "c3d4.png");

            Assert.Equal(stillThere, AttachmentStore.ResolveStored(stillThere));
        }

        /// <summary>
        /// The current root WINS when the file is in both — a copy rather than a move must not pin the
        /// transcript to the location the user is about to delete.
        /// </summary>
        [Fact]
        public void TheCurrentRootIsPreferredOverTheStoredOne()
        {
            var copied = WriteInto(_current, "e5f6.png");
            var original = WriteInto(_old, "e5f6.png");

            Assert.Equal(copied, AttachmentStore.ResolveStored(original));
        }

        /// <summary>
        /// Null is a real answer: retention sweeps this directory on its own schedule, so a missing
        /// file is ordinary and already degrades to a plain chip.
        /// </summary>
        [Theory]
        [InlineData("gone.png")]
        [InlineData("")]
        [InlineData(null)]
        public void AMissingOrEmptyEntryResolvesToNull(string? stored)
        {
            Assert.Null(AttachmentStore.ResolveStored(stored));
        }

        /// <summary>A malformed stored value must not take down a transcript load.</summary>
        [Fact]
        public void AnUnusableStoredValueIsSurvived()
        {
            Assert.Null(AttachmentStore.ResolveStored("\0:<>|"));
        }
    }
}
