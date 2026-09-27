using System;
using System.Linq;
using CodeWicket.Shell.Sessions;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Every phase a send can be blocking in says why, and says the right one of the two things. The wording is the behaviour here - a control disabled with no reason reads as broken,
    /// and a reason naming the wrong recourse sends the user looking for a button that will not help - so it
    /// is asserted rather than left to review.
    /// </summary>
    public sealed class SendPhasesTests
    {
        // The two sets, named once. The theories below draw their rows from these and so does the
        // coverage check, so a phase can never be in a theory and out of the classified set.
        private static readonly SendPhase[] UserPhases =
        {
            SendPhase.Deciding,
            SendPhase.CheckingRoot,
            SendPhase.AskingRecapFailed,
            SendPhase.AskingRefused,
        };

        private static readonly SendPhase[] SystemPhases =
        {
            SendPhase.Summarizing,
            SendPhase.Preparing,
            SendPhase.WaitingForTools,
        };

        public static TheoryData<SendPhase> WaitingOnTheUser() => Rows(UserPhases);

        public static TheoryData<SendPhase> WaitingOnTheSystem() => Rows(SystemPhases);

        private static TheoryData<SendPhase> Rows(SendPhase[] phases)
        {
            var data = new TheoryData<SendPhase>();
            foreach (var phase in phases)
                data.Add(phase);
            return data;
        }

        /// <summary>
        /// A banner is on screen and only an answer moves the send on, so the reason points AT it. Telling the
        /// user to wait here would be false - nothing is running - and offering Stop would name a second way
        /// out beside the Cancel that is already in front of them.
        /// </summary>
        [Theory]
        [MemberData(nameof(WaitingOnTheUser))]
        public void APhaseWaitingOnTheUserPointsAtTheQuestion(SendPhase phase)
        {
            Assert.True(SendPhases.WaitsOnUser(phase));
            Assert.Equal(SendPhases.AnswerTheQuestion, SendPhases.BlockReason(phase));
        }

        /// <summary>
        /// A call of ours is out with no banner to answer, so the only two things the user can do are the two
        /// the sentence names. "Answer the question above" would name a banner that is not there.
        /// </summary>
        [Theory]
        [MemberData(nameof(WaitingOnTheSystem))]
        public void APhaseWaitingOnTheSystemOffersTheWaitOrStop(SendPhase phase)
        {
            Assert.False(SendPhases.WaitsOnUser(phase));
            Assert.Equal(SendPhases.WaitOrStop, SendPhases.BlockReason(phase));
        }

        /// <summary>
        /// Every phase is classified, by enumerating the enum rather than by listing them again: a phase added
        /// later without a reason would otherwise disable a control silently, which is the exact failure the
        /// reason exists to prevent. The two theories above cover the members; this covers the SET.
        /// </summary>
        [Fact]
        public void EveryPhaseSaysWhy()
        {
            var listed = UserPhases.Concat(SystemPhases).ToList();

            foreach (SendPhase phase in Enum.GetValues(typeof(SendPhase)))
            {
                Assert.True(listed.Contains(phase), $"{phase} is in no theory, so nothing says what it blocks for");
                Assert.False(string.IsNullOrWhiteSpace(SendPhases.BlockReason(phase)), $"{phase} blocks with no reason");
            }
        }

        /// <summary>
        /// The two sentences are different sentences. Collapsed to one - which is the tempting simplification,
        /// both being "you cannot do that yet" - the reason stops telling the user which of the two things to
        /// do, and the property carrying it stops being worth having.
        /// </summary>
        [Fact]
        public void TheTwoReasonsAreNotTheSameSentence() =>
            Assert.NotEqual(SendPhases.AnswerTheQuestion, SendPhases.WaitOrStop);
    }
}
