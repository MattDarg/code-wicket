using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CodeWicket.Core;

namespace CodeWicket.Shell
{
    /// <summary>What the engine process left behind when it exited: its exit code and the tail of its
    /// stderr (issue #299).</summary>
    /// <remarks>
    /// The engine is framework-dependent, so on a machine without the .NET runtime it targets the
    /// program that runs is .NET's apphost, which refuses in tens of milliseconds and says exactly why on
    /// stderr. Before this type existed that account reached <c>engine.log</c> and nothing else: the pane
    /// showed StreamJsonRpc's "connection … lost" on every call and nothing on screen said ".NET".
    /// </remarks>
    public sealed class EngineExit
    {
        public EngineExit(int? exitCode, IReadOnlyList<string> stderrTail, int droppedStderrLines = 0)
        {
            ExitCode = exitCode;
            StderrTail = stderrTail ?? Array.Empty<string>();
            DroppedStderrLines = droppedStderrLines;
        }

        /// <summary>Null when the process would not report one. Never read as 0: a code that could not be
        /// read is not a clean exit.</summary>
        public int? ExitCode { get; }

        /// <summary>The last lines the process wrote to stderr, in order.</summary>
        public IReadOnlyList<string> StderrTail { get; }

        /// <summary>How many earlier stderr lines were not kept, so the tail never passes for the whole.</summary>
        public int DroppedStderrLines { get; }

        public EngineExitKind Kind => EngineExitDescription.Classify(ExitCode);
    }

    public enum EngineExitKind
    {
        /// <summary>Any exit whose code names no cause we can state. Reported as facts only.</summary>
        Other,

        /// <summary>The .NET host's <c>FrameworkMissingFailure</c>: .NET is present, but no version of
        /// the framework the engine targets that it can run on.</summary>
        FrameworkMissing,

        /// <summary>The .NET host's <c>CoreHostLibMissingFailure</c>: one of the hosting components is
        /// missing — what the apphost exits with when it finds no .NET installation at all.</summary>
        HostComponentMissing,
    }

    /// <summary>
    /// What to say about an engine exit, on screen and in <c>engine.log</c>. Pure, so the wording can be
    /// asserted directly: the wording IS the behaviour, the same reason <c>Core.Ide.FileWriteRefusal</c>
    /// and <c>ProcessAcpConnection.DescribeLaunchFailure</c> are written this way.
    /// <para><b>A cause is claimed only where the exit code names one.</b> The two .NET host codes do;
    /// every other exit gets what is known — that the engine exited, with which code, and where its log
    /// is — and no guess, because a reader handed a hedged cause repeats it more confidently than we
    /// held it.</para>
    /// <para><b>.NET's account is quoted, never re-derived.</b> The apphost already names the framework,
    /// the version it wanted, the versions it found and the download link, and it will keep saying the
    /// right thing for whatever .NET does next. A probe of our own would drift from it.</para>
    /// </summary>
    public static class EngineExitDescription
    {
        // Names and values from dotnet/runtime src/native/corehost/error_codes.h.
        internal const int FrameworkMissingFailure = unchecked((int)0x80008096);
        internal const int CoreHostLibMissingFailure = unchecked((int)0x80008083);

        /// <summary>The runtime the engine is built against. Hand-kept in step with the engine's TFM:
        /// this assembly also builds for net472, so it cannot read the engine's runtime version.</summary>
        internal const string RequiredRuntime = ".NET 10 runtime";

        public static EngineExitKind Classify(int? exitCode) => exitCode switch
        {
            FrameworkMissingFailure => EngineExitKind.FrameworkMissing,
            CoreHostLibMissingFailure => EngineExitKind.HostComponentMissing,
            _ => EngineExitKind.Other,
        };

        /// <summary>The notice's one-line text. <paramref name="engineLogFile"/> is supplied by the host
        /// (log paths are injected, never read here) and may be null.</summary>
        public static string Text(EngineExit exit, string? engineLogFile)
        {
            var product = Branding.ProductName;
            switch (exit.Kind)
            {
                case EngineExitKind.FrameworkMissing:
                    return $"{product}'s engine needs the {RequiredRuntime}, and could not start: .NET reported "
                        + $"that no compatible version of it is installed. Install the {RequiredRuntime}, then "
                        + "restart Visual Studio. .NET's own account, with its download link, is in the details.";
                case EngineExitKind.HostComponentMissing:
                    return $"{product}'s engine needs the {RequiredRuntime}, and could not start: .NET reported "
                        + $"that one of its hosting components is missing. Install the {RequiredRuntime}, then "
                        + "restart Visual Studio. .NET's own account, with its download link, is in the details.";
                default:
                    var sb = new StringBuilder($"{product}'s engine is not running: ");
                    sb.Append(exit.ExitCode is { } code
                        ? $"it exited with code {FormatCode(code)}."
                        : "it exited, and its exit code could not be read.");
                    if (!string.IsNullOrEmpty(engineLogFile))
                        sb.Append(" Its log is ").Append(engineLogFile).Append('.');
                    return sb.ToString();
            }
        }

        /// <summary>The notice's detail panel: plain text, shown verbatim (issue #82's rule). Null when
        /// there is nothing beyond the text.</summary>
        public static string? Details(EngineExit exit, string? engineLogFile)
        {
            var sb = new StringBuilder();
            var known = exit.Kind != EngineExitKind.Other;

            if (exit.StderrTail.Count > 0)
            {
                // Labelled for what it is. On a known code the stderr IS .NET's account — the apphost is
                // the only thing that ran. On any other exit it is the engine's own log tail, which is
                // context beside the exit rather than its cause.
                sb.AppendLine(known
                    ? "What .NET wrote when it refused to start the engine:"
                    : "The engine's last lines on stderr (not necessarily the cause):");
                if (exit.DroppedStderrLines > 0)
                    sb.AppendLine($"({exit.DroppedStderrLines.ToString(CultureInfo.InvariantCulture)} earlier lines not shown)");
                sb.AppendLine();
                foreach (var line in exit.StderrTail)
                    sb.AppendLine(line);
                sb.AppendLine();
            }

            if (known)
            {
                // The Other text already carries these; a known code's text is about .NET, so the
                // evidence goes here where a bug report will pick it up.
                sb.AppendLine($"Exit code: {FormatCode(exit.ExitCode!.Value)} ({CodeName(exit.Kind)})");
                if (!string.IsNullOrEmpty(engineLogFile))
                    sb.AppendLine($"Engine log: {engineLogFile}");
            }

            var text = sb.ToString().Trim();
            return text.Length == 0 ? null : text;
        }

        /// <summary>The unconditional <c>engine.log</c> line naming the classification, written beside
        /// the apphost's own lines so a collected log answers the question without decoding exit codes.</summary>
        public static string LogLine(EngineExit exit)
        {
            var code = exit.ExitCode is { } c ? FormatCode(c) : "unreadable";
            return exit.Kind switch
            {
                EngineExitKind.FrameworkMissing =>
                    $"exit code {code} ({CodeName(exit.Kind)}): .NET found no compatible version of the framework the engine targets; the engine needs the {RequiredRuntime}",
                EngineExitKind.HostComponentMissing =>
                    $"exit code {code} ({CodeName(exit.Kind)}): a .NET hosting component is missing; the engine needs the {RequiredRuntime}",
                _ => $"exit code {code}; not a .NET host failure code, no cause claimed",
            };
        }

        private static string CodeName(EngineExitKind kind) => kind switch
        {
            EngineExitKind.FrameworkMissing => nameof(FrameworkMissingFailure),
            EngineExitKind.HostComponentMissing => nameof(CoreHostLibMissingFailure),
            _ => string.Empty,
        };

        // A host failure code is an HRESULT and is only recognisable in hex; an ordinary exit reads as
        // the number a user would see anywhere else.
        internal static string FormatCode(int code) => code < 0
            ? $"0x{unchecked((uint)code).ToString("X8", CultureInfo.InvariantCulture)} ({code.ToString(CultureInfo.InvariantCulture)})"
            : code.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Thrown by every <see cref="EngineClient"/> call that fails because the engine process has exited,
    /// in place of StreamJsonRpc's "connection … lost" (issue #299). <see cref="Exception.Message"/> is
    /// the notice text with no log path, so a caller that only prints a message still says something
    /// true; the pane's own sites use <see cref="Exit"/> to add the path and the details.
    /// </summary>
    public sealed class EngineExitedException : InvalidOperationException
    {
        public EngineExitedException(EngineExit exit, Exception? inner = null)
            : base(EngineExitDescription.Text(exit, engineLogFile: null), inner)
        {
            Exit = exit;
        }

        public EngineExit Exit { get; }
    }
}
