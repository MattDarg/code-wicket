using System;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Issue #299: on a machine without the .NET 10 runtime the engine's apphost refuses to start in
    /// tens of milliseconds, and the pane said only "The JSON-RPC connection with the remote party was
    /// lost" — on load and on every send — while .NET's own precise account sat in engine.log. These
    /// pin the wording, because the wording is the fix: a cause is claimed only for the two .NET host
    /// codes that name one, .NET's account is quoted rather than retold, and every other exit gets
    /// facts and no guess.
    /// </summary>
    public sealed class EngineExitDescriptionTests
    {
        private const string LogFile = @"C:\logs\engine.log";

        private static readonly string[] FrameworkMissingStderr =
        {
            "You must install or update .NET to run this application.",
            "",
            "Framework: 'Microsoft.NETCore.App', version '10.0.0' (x64)",
            "To install missing framework, download:",
            "https://aka.ms/dotnet-core-applaunch?framework=Microsoft.NETCore.App&framework_version=10.0.0&arch=x64&rid=win-x64&os=win10",
        };

        [Fact]
        public void TheHostCodesAreTheOnesDotnetNames()
        {
            // dotnet/runtime src/native/corehost/error_codes.h. The measured exits (issue #299) were
            // -2147450730 with a framework forced to 99.0.0 and -2147450749 with no .NET root.
            Assert.Equal(-2147450730, EngineExitDescription.FrameworkMissingFailure);
            Assert.Equal(-2147450749, EngineExitDescription.CoreHostLibMissingFailure);
            Assert.Equal(EngineExitKind.FrameworkMissing, EngineExitDescription.Classify(-2147450730));
            Assert.Equal(EngineExitKind.HostComponentMissing, EngineExitDescription.Classify(-2147450749));
        }

        [Fact]
        public void AMissingFrameworkLeadsWithTheRuntimeAndQuotesDotnetWhole()
        {
            var exit = new EngineExit(EngineExitDescription.FrameworkMissingFailure, FrameworkMissingStderr);

            var text = EngineExitDescription.Text(exit, LogFile);
            Assert.StartsWith("Code Wicket's engine needs the .NET 10 runtime, and could not start:", text);
            Assert.Contains("no compatible version of it is installed", text);
            Assert.Contains("Install the .NET 10 runtime, then restart Visual Studio.", text);
            Assert.DoesNotContain("JSON-RPC", text);

            var details = EngineExitDescription.Details(exit, LogFile)!;
            Assert.Contains("What .NET wrote when it refused to start the engine:", details);
            // Verbatim, link included: that line is the one a user acts on.
            foreach (var line in FrameworkMissingStderr)
                Assert.Contains(line, details);
            Assert.Contains("Exit code: 0x80008096 (-2147450730) (FrameworkMissingFailure)", details);
            Assert.Contains("Engine log: " + LogFile, details);
        }

        /// <summary>The hostfxr-missing code means "one of the hosting components is missing". It is what
        /// the apphost exits with when no .NET is installed, but the code itself says only that — so the
        /// text says only that, and the "Not found" is left to .NET's own account in the details.</summary>
        [Fact]
        public void AMissingHostComponentSaysWhatTheCodeSaysAndNoMore()
        {
            var exit = new EngineExit(EngineExitDescription.CoreHostLibMissingFailure,
                new[] { "You must install .NET to run this application.", ".NET location: Not found" });

            var text = EngineExitDescription.Text(exit, LogFile);
            Assert.StartsWith("Code Wicket's engine needs the .NET 10 runtime, and could not start:", text);
            Assert.Contains("one of its hosting components is missing", text);
            Assert.DoesNotContain("not installed", text);

            var details = EngineExitDescription.Details(exit, LogFile)!;
            Assert.Contains(".NET location: Not found", details);
            Assert.Contains("(CoreHostLibMissingFailure)", details);
        }

        /// <summary>
        /// Every other exit is facts only. <c>FrameworkCompatFailure</c> is a .NET host code too, and the
        /// nearest one to the two above — which is exactly why it is here: an "any 0x8000808x means .NET is
        /// missing" shortcut would tell that user to install a runtime they already have.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(unchecked((int)0x8000809c))] // FrameworkCompatFailure
        public void AnyOtherExitNamesNoCause(int code)
        {
            var exit = new EngineExit(code, new[] { "[engine] something the engine logged" });

            var text = EngineExitDescription.Text(exit, LogFile);
            Assert.StartsWith("Code Wicket's engine is not running: it exited with code ", text);
            Assert.DoesNotContain(".NET", text);
            Assert.DoesNotContain("install", text, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("Its log is " + LogFile + ".", text);

            var details = EngineExitDescription.Details(exit, LogFile)!;
            Assert.Contains("(not necessarily the cause)", details);
            Assert.DoesNotContain("What .NET wrote", details);
        }

        [Fact]
        public void AnUnreadableCodeIsNeverReportedAsZero()
        {
            var text = EngineExitDescription.Text(new EngineExit(null, Array.Empty<string>()), engineLogFile: null);

            Assert.Equal("Code Wicket's engine is not running: it exited, and its exit code could not be read.", text);
            Assert.Null(EngineExitDescription.Details(new EngineExit(null, Array.Empty<string>()), null));
            Assert.Contains("unreadable", EngineExitDescription.LogLine(new EngineExit(null, Array.Empty<string>())));
        }

        [Fact]
        public void ATrimmedTailSaysHowMuchItDropped()
        {
            var details = EngineExitDescription.Details(new EngineExit(3, new[] { "last" }, droppedStderrLines: 812), null)!;

            Assert.Contains("(812 earlier lines not shown)", details);
        }

        [Fact]
        public void TheLogLineNamesTheClassification()
        {
            Assert.Equal(
                "exit code 0x80008096 (-2147450730) (FrameworkMissingFailure): .NET found no compatible version of the framework the engine targets; the engine needs the .NET 10 runtime",
                EngineExitDescription.LogLine(new EngineExit(EngineExitDescription.FrameworkMissingFailure, Array.Empty<string>())));
            Assert.Equal(
                "exit code 2; not a .NET host failure code, no cause claimed",
                EngineExitDescription.LogLine(new EngineExit(2, Array.Empty<string>())));
        }

        [Fact]
        public void TheExceptionMessageIsTheNoticeTextWithoutAPath()
        {
            var exit = new EngineExit(EngineExitDescription.FrameworkMissingFailure, FrameworkMissingStderr);

            Assert.Equal(EngineExitDescription.Text(exit, null), new EngineExitedException(exit).Message);
        }
    }
}
