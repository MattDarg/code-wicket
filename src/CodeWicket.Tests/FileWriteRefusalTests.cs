using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What the agent is told when Visual Studio refuses a write (issue #189).
    /// <para>
    /// These assert WORDING, which is unusual and is the point: the message is the whole of the
    /// behaviour, its reader is a model, and a hedged message was measured on the wire producing a
    /// worse outcome than saying nothing. Given "this usually means the file is read-only or needs
    /// checking out", Kiro told the user the file was *open in the editor* and offered to write it with
    /// a shell command instead — a cause we never suggested and a workaround that defeats the refusal.
    /// A sentence that declines to commit gets replaced by one that does.
    /// </para>
    /// </summary>
    public sealed class FileWriteRefusalTests
    {
        private const string Path = @"C:\ws\temp.txt";

        /// <summary>
        /// The checkable case states the fact, and states it as a fact. No "usually", no "likely" — the
        /// read-only attribute was read, so there is nothing to hedge about.
        /// </summary>
        [Fact]
        public void AReadOnlyFileIsNamedAsTheReason()
        {
            var message = FileWriteRefusal.Describe(Path, readOnly: true);

            Assert.Contains(Path, message);
            Assert.Contains("read-only", message);
            Assert.DoesNotContain("usually", message);
            Assert.DoesNotContain("likely", message);
        }

        /// <summary>
        /// And it closes the door a refusal alone leaves open. An agent told only that a write failed
        /// reaches for the next tool that might not fail — here, a shell command, which would carry out
        /// exactly the change the user had just refused.
        /// </summary>
        [Fact]
        public void AReadOnlyRefusalForbidsWorkingAroundIt()
        {
            var message = FileWriteRefusal.Describe(Path, readOnly: true);

            Assert.Contains("shell command", message);
            Assert.Contains("Do not write the file another way", message);
        }

        /// <summary>
        /// Where the fact does not hold we describe the refusal and decline to name a cause. Offering a
        /// guess here is what produced the bad outcome above — the model firms it up — so the sentence
        /// says the editor did not tell us, which is true and is not a hypothesis to inherit.
        /// </summary>
        [Fact]
        public void AWritableFileGetsNoInventedCause()
        {
            var message = FileWriteRefusal.Describe(Path, readOnly: false);

            Assert.Contains(Path, message);
            Assert.Contains("without saying why", message);
            Assert.DoesNotContain("read-only and Visual Studio was not permitted", message);
        }

        /// <summary>
        /// "We could not tell" is not "it is writable". The attribute read can itself fail, and reporting
        /// that as the negative would assert a fact nobody established — the same mistake as the hedge,
        /// with a straight face.
        /// </summary>
        [Fact]
        public void AnUncheckableFileIsNotReportedAsWritable()
        {
            Assert.Equal(
                FileWriteRefusal.Describe(Path, readOnly: false),
                FileWriteRefusal.Describe(Path, readOnly: null));
        }

        /// <summary>
        /// Both shapes say the file is UNCHANGED. It is the one thing the agent most needs and cannot
        /// otherwise know: a failed write that might have half-landed is a different situation from one
        /// that did not happen, and only we can say which this was.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void EveryRefusalSaysTheFileIsUnchanged(bool readOnly)
        {
            Assert.Contains("unchanged", FileWriteRefusal.Describe(Path, readOnly));
        }
    }
}
