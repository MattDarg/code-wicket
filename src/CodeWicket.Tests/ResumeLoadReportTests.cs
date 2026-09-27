using CodeWicket.Core;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What the resume line may claim, and the one claim it must not make.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured in Visual Studio, 2026-09-17.</b> Every refused reload logged two lines for one
    /// episode: the provider's "resume of 'X' failed, starting fresh: Session not found", and then
    /// "loaded with NOTHING replayed - the backend reported success for a conversation it does not hold".
    /// The second contradicts the first. Five instances, with a clean control: false at 21:39:00,
    /// 21:41:09 and 21:41:51, each preceded by the failure; TRUE at 22:08:23 and 22:19:05, where it
    /// stood alone because the load really had succeeded and replayed nothing (issue #185).</para>
    /// <para><b>So the succeeded-but-empty wording is asserted verbatim here</b>, not only the refused
    /// case. That sentence is the only honest account of #185's silent shape, and the risk in fixing one
    /// route is breaking the other.</para>
    /// </remarks>
    public sealed class ResumeLoadReportTests
    {
        private const string Id = "sess_82e72e03";

        /// <summary>The false claim: the backend FAILED the load, so it reported no success.</summary>
        [Fact]
        public void ARefusedLoadDoesNotClaimTheBackendReportedSuccess()
        {
            var line = ResumeLoadReport.Describe(Id, replayed: 0, refusedBecause: "Session not found: " + Id);

            Assert.DoesNotContain("reported success", line);
            Assert.Contains("refused", line);
            Assert.Contains(Id, line);
        }

        /// <summary>
        /// A refusal with no count is still a refusal: it must not fall through to a sentence about counts.
        /// </summary>
        [Fact]
        public void ARefusedLoadThatReportsNoCountStillReadsAsRefused()
        {
            var line = ResumeLoadReport.Describe(Id, replayed: null, refusedBecause: "Session not found");

            Assert.DoesNotContain("does not report what it replayed", line);
            Assert.Contains("refused", line);
        }

        /// <summary>
        /// The OTHER direction, and the reason this test class exists: a load that genuinely succeeded and
        /// replayed nothing keeps its sentence exactly. Fixing the refused route must not touch this one.
        /// </summary>
        [Fact]
        public void ALoadThatSucceededAndReplayedNothingKeepsItsWordingVerbatim() =>
            Assert.Equal(
                "[resume] 'sess_82e72e03': loaded with NOTHING replayed - the backend reported success for a conversation it does not hold",
                ResumeLoadReport.Describe(Id, replayed: 0, refusedBecause: null));

        [Fact]
        public void ABackendThatDoesNotSayKeepsItsWording() =>
            Assert.Equal(
                "[resume] 'sess_82e72e03': the backend does not report what it replayed",
                ResumeLoadReport.Describe(Id, replayed: null, refusedBecause: null));

        [Fact]
        public void AReplayedLoadCountsWhatCameBack() =>
            Assert.Equal(
                "[resume] 'sess_82e72e03': loaded, 8 conversation frame(s) replayed",
                ResumeLoadReport.Describe(Id, replayed: 8, refusedBecause: null));
    }
}
