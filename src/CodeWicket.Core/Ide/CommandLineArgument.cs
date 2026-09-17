using System.Text;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// Spells a string as exactly ONE argument of a Windows command line, whatever it contains.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A process launched with <c>ProcessStartInfo.Arguments</c> is handed one string, and it is the
    /// CHILD that splits it - by the C runtime's rules (the same ones <c>CommandLineToArgvW</c>
    /// implements), which are: a quote toggles whether whitespace splits; a backslash is literal unless
    /// it precedes a quote, where a run of <i>2n</i> backslashes becomes <i>n</i> and a run of
    /// <i>2n+1</i> becomes <i>n</i> plus a literal quote. So a value dropped into <c>"…"</c> by
    /// interpolation is one argument only until it contains a quote of its own, at which point the
    /// rest of it becomes further arguments - and the argument after a filter value is a runner
    /// option (pre-release security review: <c>x" --test-adapter-path \\evil\share "</c>).
    /// </para>
    /// <para>
    /// This is the algorithm .NET's own <c>ProcessStartInfo.ArgumentList</c> applies before joining,
    /// written here because that API does not exist on net472, where the tool catalog lives. It
    /// always quotes - a value with nothing special in it comes back as <c>"value"</c>, which is what
    /// every caller here already wrote by hand - and it escapes only what the parser reads as
    /// special: a backslash NOT followed by a quote passes through untouched, which is what keeps
    /// VSTest's documented <c>\(</c> / <c>\)</c> / <c>\&amp;</c> escapes working. A blanket ban on
    /// backslashes would refuse the only way to filter a
    /// parameterised test by its full name.
    /// </para>
    /// <para>
    /// <b>What quoting does not do:</b> an argument that is exactly <c>--some-option</c> is still that
    /// option once the quotes are stripped, so a value that must be a VALUE and never an option is
    /// refused by its caller when it begins with an option prefix (<see cref="TestRunCommands.RefuseFilterValue"/>).
    /// </para>
    /// </remarks>
    public static class CommandLineArgument
    {
        public static string Quote(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');

            var i = 0;
            while (i < value.Length)
            {
                var backslashes = 0;
                while (i < value.Length && value[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i == value.Length)
                {
                    // Trailing backslashes precede the closing quote we add, so they must be doubled or
                    // the last one escapes it.
                    sb.Append('\\', backslashes * 2);
                    break;
                }

                if (value[i] == '"')
                {
                    // 2n+1 backslashes then the quote: n literal backslashes and a literal quote.
                    sb.Append('\\', (backslashes * 2) + 1);
                    sb.Append('"');
                }
                else
                {
                    // Not before a quote: every backslash is literal and passes through as it came.
                    sb.Append('\\', backslashes);
                    sb.Append(value[i]);
                }

                i++;
            }

            sb.Append('"');
            return sb.ToString();
        }
    }
}
