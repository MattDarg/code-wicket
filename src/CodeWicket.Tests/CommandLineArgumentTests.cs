using System;
using System.Runtime.InteropServices;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// <see cref="CommandLineArgument.Quote"/> against the parser that will actually read it.
    /// <para>
    /// The claim is "one string in, exactly one argument out, byte-identical" - and the only honest
    /// witness for that is the splitter the child process uses, so every case here goes through
    /// <c>CommandLineToArgvW</c> rather than through an expectation of what the quoted form should
    /// look like. A test that restated the escaping rules would pass just as happily against a
    /// Quote that got them wrong the same way.
    /// </para>
    /// </summary>
    public sealed class CommandLineArgumentTests
    {
        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>Splits a command line exactly as a Windows child process would.</summary>
        internal static string[] Split(string commandLine)
        {
            var argv = CommandLineToArgvW(commandLine, out var count);
            if (argv == IntPtr.Zero)
                throw new InvalidOperationException("CommandLineToArgvW failed: " + Marshal.GetLastWin32Error());
            try
            {
                var result = new string[count];
                for (var i = 0; i < count; i++)
                    result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
                return result;
            }
            finally
            {
                LocalFree(argv);
            }
        }

        [Theory]
        [InlineData("plain")]
        [InlineData("")]
        [InlineData("two words")]
        [InlineData("FullyQualifiedName~Ns.Class&TestCategory=Fast")]
        [InlineData(@"FullyQualifiedName=Ns.C.M\(1\)")]                 // VSTest's own escapes: untouched
        [InlineData(@"C:\path\with\trailing\")]                        // trailing backslash before our closing quote
        [InlineData(@"C:\path\\double\\")]
        [InlineData("say \"hi\"")]                                     // a quote
        [InlineData("x\" --test-adapter-path \\\\attacker\\share \"")] // the injection payload
        [InlineData("back\\\"slash-quote")]                            // backslash immediately before a quote
        [InlineData("many\\\\\\\"backslashes")]
        [InlineData("tab\there")]
        [InlineData("unicode ✓ 日本")]
        public void QuoteRoundTripsAsExactlyOneArgument(string value)
        {
            var argv = Split("prog.exe " + CommandLineArgument.Quote(value) + " after");

            Assert.Equal(new[] { "prog.exe", value, "after" }, argv);
        }

        [Fact]
        public void TheQuotedFormIsAlwaysQuoted()
        {
            // The callers wrote "value" by hand before; an unquoted plain value would change every
            // existing command line for no reason.
            Assert.Equal("\"plain\"", CommandLineArgument.Quote("plain"));
            Assert.Equal("\"\"", CommandLineArgument.Quote(""));
        }

        /// <summary>The counter-example: the hand-written form the callers used to build.</summary>
        [Fact]
        public void InterpolatingIntoQuotesIsWhatQuoteReplaces()
        {
            const string payload = "x\" --test-adapter-path \\\\attacker\\share \"";

            var naive = Split("prog.exe --filter \"" + payload + "\"");
            var quoted = Split("prog.exe --filter " + CommandLineArgument.Quote(payload));

            Assert.Contains("--test-adapter-path", naive);   // the injection, as it was
            Assert.Equal(3, quoted.Length);                  // prog.exe, --filter, the value
            Assert.Equal(payload, quoted[2]);
        }
    }
}
