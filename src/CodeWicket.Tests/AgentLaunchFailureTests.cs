using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What the user is told when the agent program cannot be launched at all (issue #287).
    ///
    /// <para><b>The defect was a sentence with no subject.</b> <c>Process.Start</c> THROWS for a
    /// program that is not there — the <c>??</c> beside it only covers a null return, which means an
    /// already-running process was reused — so the one message that named the CLI was unreachable in
    /// exactly the case it exists for. What arrived instead was the OS's bare "The system cannot find
    /// the file specified", which the host prefixed into "Engine error: The system cannot find the
    /// file specified": no program, no path, no directory. Found live on 2026-09-12, when kiro-cli was
    /// removed from the machine mid-evening and every window afterwards looked healthy until a send.
    /// </para>
    ///
    /// <para>Wording is the behaviour, so these assert wording — the <c>FileWriteRefusal</c> rule, and
    /// for its reason: given a sentence that declines to say what it is about, a reader supplies the
    /// missing half and states it more confidently than we would have.</para>
    /// </summary>
    public class AgentLaunchFailureTests
    {
        private const int FileNotFound = 2;
        private const int PathNotFound = 3;
        private const int AccessDenied = 5;

        /// <summary>
        /// The case that prompted this: a bare program name is what the OS resolves through PATH, so
        /// PATH is what the message names. Without it the user has a failure and no subject to act on.
        /// </summary>
        [Fact]
        public void AMissingProgramOnPathNamesTheProgramAndPath()
        {
            var message = Describe(FileNotFound, "kiro-cli", @"C:\repo");

            Assert.Equal(
                @"no program named 'kiro-cli' was found on PATH (working directory 'C:\repo')", message);
        }

        /// <summary>
        /// A CONFIGURED path was never resolved through PATH, so saying it "is not on PATH" would send
        /// the user to edit an environment variable nothing consulted. It says where it actually
        /// looked — which is the whole rule here: name what was checked.
        /// </summary>
        [Fact]
        public void AMissingProgramAtAConfiguredPathNamesThatPathAndNotPath()
        {
            var message = Describe(FileNotFound, @"C:\tools\kiro-cli.exe", @"C:\repo");

            Assert.Contains(@"no program was found at 'C:\tools\kiro-cli.exe'", message);
            Assert.DoesNotContain("PATH", message);
        }

        /// <summary>A relative path is spelled, not resolved, so it is reported the same way.</summary>
        [Fact]
        public void ARelativePathIsReportedAsAPathRatherThanAsPath()
        {
            Assert.Contains(
                @"no program was found at 'tools\kiro-cli.exe'",
                Describe(FileNotFound, @"tools\kiro-cli.exe", @"C:\repo"));
        }

        /// <summary>A missing DIRECTORY is still an absence, and licenses the same word.</summary>
        [Fact]
        public void AMissingDirectoryIsAlsoReportedAsNotFound()
        {
            Assert.Contains(
                @"no program was found at 'D:\gone\kiro-cli.exe'",
                Describe(PathNotFound, @"D:\gone\kiro-cli.exe", @"C:\repo"));
        }

        /// <summary>
        /// Everything that is NOT a not-found keeps the OS's own sentence. This is the half that stops
        /// the fix becoming its own defect: retelling "Access is denied" as "not found" would be a
        /// confident wrong answer, and it is the one a reader would then repeat. The program is still
        /// named — that was always the missing part — but the cause stays the OS's to state.
        /// </summary>
        [Fact]
        public void AFailureThatIsNotAnAbsenceIsNotRetoldAsOne()
        {
            var message = Describe(AccessDenied, "kiro-cli", @"C:\repo");

            Assert.Contains("'kiro-cli' could not be started", message);
            Assert.Contains("Access is denied", message);
            Assert.DoesNotContain("no program", message);
            Assert.DoesNotContain("PATH", message);
        }

        /// <summary>
        /// The working directory is a real part of the diagnosis — a relative CLI path resolves
        /// against it — but it is omitted rather than printed empty when there is none to state.
        /// </summary>
        [Fact]
        public void AnAbsentWorkingDirectoryIsOmittedRatherThanShownEmpty()
        {
            var message = Describe(FileNotFound, "kiro-cli", string.Empty);

            Assert.Equal("no program named 'kiro-cli' was found on PATH", message);
        }

        /// <summary>
        /// End to end through the real ctor, because the pure helper above cannot show that the throw
        /// is actually CAUGHT — the bug was a live `Process.Start` exception escaping past the branch
        /// written for it. The OS's own exception is kept as the inner one: it carries the error code,
        /// and a log that has been given a friendlier sentence must not have lost the original.
        /// </summary>
        [Fact]
        public void TheLaunchItselfReportsTheProgramRatherThanABareOsMessage()
        {
            var missing = "cwkt-no-such-agent-" + System.Guid.NewGuid().ToString("N");

            var ex = Assert.Throws<System.InvalidOperationException>(
                () => new ProcessAcpConnection(
                    missing, new[] { "acp" }, null, Path.GetTempPath(), "kiro"));

            Assert.Contains(missing, ex.Message);
            Assert.Contains("was found on PATH", ex.Message);
            Assert.IsType<Win32Exception>(ex.InnerException);
        }

        private static string Describe(int nativeErrorCode, string cliPath, string workingDirectory) =>
            ProcessAcpConnection.DescribeLaunchFailure(
                new Win32Exception(nativeErrorCode), cliPath, workingDirectory);
    }
}
