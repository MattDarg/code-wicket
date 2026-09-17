using System;
using System.Globalization;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// Coercion for a single column value read out of a VS table snapshot — the untyped half of reading
    /// the Error List by DATA rather than through the window's control (issue #93).
    /// </summary>
    /// <remarks>
    /// <b>Why this exists at all.</b> The two ways into a table row are not equivalent. A row reached
    /// through the control (<c>ITableEntryHandle</c>) or as an <c>ITableEntry</c> has a generic
    /// <c>TryGetValue&lt;T&gt;</c> that converts for you; a row reached through
    /// <c>ITableEntriesSnapshot</c> — the filter-independent path — has only
    /// <c>TryGetValue(int, string, out object)</c> and hands back a boxed <c>object</c> whose runtime type
    /// is whatever the publishing source chose to store. So the conversion the SDK used to do became ours.
    /// <para>
    /// It lives in Core, away from the VS-bound project, for the reason <see cref="FilePathMatch"/> does:
    /// the decision is pure and unit-testable without devenv, while enumerating the table's sources is the
    /// VS-coupled part and stays in <c>CodeWicket.Ide</c>. That split matters more here than usual —
    /// this code is load-bearing in a way that fails SILENTLY. A coercion that gives up returns "no value",
    /// and every consumer of a missing value has a plausible-looking default: a missing line becomes line
    /// 1, a missing <c>ErrorSource</c> excludes the row, and a whole build's worth of errors disappears
    /// with nothing reporting a problem. That is the exact shape of the bug this replaces.
    /// </para>
    /// </remarks>
    public static class TableValue
    {
        /// <summary>
        /// The value as text, or null when it is absent or empty. A non-string is rendered with
        /// <see cref="IFormattable"/> invariantly where it can be — a table column is displayed text, so a
        /// source storing, say, a number for the error code should still read as that code and not as
        /// whatever the current culture makes of it.
        /// </summary>
        public static string? AsString(object? value)
        {
            if (value is null)
                return null;
            if (value is string s)
                return s.Length == 0 ? null : s;
            var text = value is IFormattable f
                ? f.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>
        /// The value as an integer, accepting the shapes a table source realistically stores: a boxed
        /// integral, a boxed enum (the SDK's own <c>ErrorSource</c> / <c>__VSERRORCATEGORY</c> columns),
        /// or an invariant numeric string. Anything else — null, a bool, a non-numeric string, a
        /// non-integral number — is NOT a number and returns false rather than a made-up zero.
        /// </summary>
        /// <remarks>
        /// <b>bool is excluded deliberately.</b> <see cref="Convert.ToInt32(object)"/> converts it happily
        /// to 0/1, which would let a boolean column masquerade as <c>ErrorSource.Build</c> (0) and admit
        /// rows that are not build rows at all. Nothing should ever route a bool through here; if
        /// something does, saying "not a number" is the answer that cannot mislead.
        /// </remarks>
        public static bool TryAsInt(object? value, out int result)
        {
            result = 0;
            switch (value)
            {
                case null:
                case bool:
                    return false;
                case int i:
                    result = i;
                    return true;
                case Enum e:
                    // An enum of any underlying integral type; ToInt32 reads its numeric value.
                    try { result = Convert.ToInt32(e, CultureInfo.InvariantCulture); return true; }
                    catch (OverflowException) { return false; }
                case short or byte or sbyte or ushort:
                    result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return true;
                case long or uint or ulong:
                    try { result = Convert.ToInt32(value, CultureInfo.InvariantCulture); return true; }
                    catch (OverflowException) { return false; }
                case string s:
                    return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
                default:
                    return false;
            }
        }

        /// <summary>
        /// A table's 0-based Line/Column column as the 1-based number the tools report. An absent or
        /// unreadable value is reported as line/column 1 — the same "top of the file" the old typed read
        /// produced for a missing value, so a row with an odd position still reaches the agent rather than
        /// being dropped.
        /// </summary>
        public static int AsOneBasedPosition(object? value)
            => TryAsInt(value, out var zeroBased) && zeroBased > 0 ? zeroBased + 1 : 1;
    }
}
