using System;
using System.Globalization;
using System.Threading;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Coercion of the untyped column values that come back from a VS table snapshot — the half of the
    /// filter-independent Error List read (issue #93) that can be pinned without devenv.
    /// <para>
    /// It is pinned here because it fails SILENTLY. Reading the table's DATA rather than the Error List
    /// window's control means the SDK no longer converts column values for us: a snapshot hands back a
    /// boxed <c>object</c>. Every way of getting that wrong produces a plausible answer rather than an
    /// error — a mis-read <c>ErrorSource</c> excludes the row and the whole build's errors vanish, which
    /// is precisely the shape of the bug this replaces.
    /// </para>
    /// </summary>
    public sealed class TableValueTests
    {
        /// <summary>Shaped like the SDK's <c>ErrorSource</c>: Build is the DEFAULT member, value 0.</summary>
        private enum SourceLike { Build = 0, Other = 1 }

        /// <summary>An enum whose underlying type is not int — nothing guarantees a column's is.</summary>
        private enum ByteBacked : byte { Zero = 0, One = 1 }

        // --- The build-source filter: the read that decides whether a build's errors exist at all. ---

        [Fact]
        public void BoxedEnum_reads_as_its_numeric_value()
        {
            Assert.True(TableValue.TryAsInt(SourceLike.Build, out var build));
            Assert.Equal(0, build);
            Assert.True(TableValue.TryAsInt(SourceLike.Other, out var other));
            Assert.Equal(1, other);
        }

        [Fact]
        public void BoxedEnum_with_a_non_int_underlying_type_still_reads()
        {
            Assert.True(TableValue.TryAsInt(ByteBacked.One, out var value));
            Assert.Equal(1, value);
        }

        [Fact]
        public void Boxed_int_reads_as_itself()
        {
            Assert.True(TableValue.TryAsInt(2, out var value));
            Assert.Equal(2, value);
        }

        [Fact]
        public void A_missing_column_is_not_a_number()
        {
            // Must be false, not 0: 0 is ErrorSource.Build, so "absent" reading as a number would admit
            // every row with no ErrorSource column as a build row.
            Assert.False(TableValue.TryAsInt(null, out _));
        }

        [Fact]
        public void A_bool_is_not_a_number()
        {
            // Convert.ToInt32 would make this 0 == ErrorSource.Build. Nothing should route a bool through
            // here; if something does, "not a number" is the answer that cannot mislead.
            Assert.False(TableValue.TryAsInt(false, out _));
            Assert.False(TableValue.TryAsInt(true, out _));
        }

        [Fact]
        public void Text_that_is_not_a_number_is_rejected_and_text_that_is_reads()
        {
            Assert.False(TableValue.TryAsInt("build", out _));
            Assert.True(TableValue.TryAsInt("42", out var value));
            Assert.Equal(42, value);
        }

        [Fact]
        public void A_value_too_large_for_an_int_is_rejected_rather_than_wrapped()
        {
            Assert.False(TableValue.TryAsInt(long.MaxValue, out _));
            Assert.False(TableValue.TryAsInt(ulong.MaxValue, out _));
        }

        [Fact]
        public void An_unrelated_type_is_rejected()
        {
            Assert.False(TableValue.TryAsInt(new object(), out _));
            Assert.False(TableValue.TryAsInt(1.5d, out _));
        }

        // --- Positions: the table's Line/Column are 0-based, everything we report is 1-based. ---

        [Fact]
        public void A_zero_based_position_reports_one_based()
        {
            Assert.Equal(1, TableValue.AsOneBasedPosition(0));
            Assert.Equal(1477, TableValue.AsOneBasedPosition(1476));
        }

        [Fact]
        public void An_absent_or_nonsensical_position_reports_the_top_of_the_file()
        {
            // Line 1 rather than 0 or a dropped row: an odd position must still reach the agent.
            Assert.Equal(1, TableValue.AsOneBasedPosition(null));
            Assert.Equal(1, TableValue.AsOneBasedPosition("nowhere"));
            Assert.Equal(1, TableValue.AsOneBasedPosition(-1));
        }

        // --- Text columns. ---

        [Fact]
        public void Absent_and_empty_text_are_both_null()
        {
            Assert.Null(TableValue.AsString(null));
            Assert.Null(TableValue.AsString(string.Empty));
        }

        [Fact]
        public void Text_reads_through_unchanged()
        {
            Assert.Equal("CS1002", TableValue.AsString("CS1002"));
            Assert.Equal("; expected", TableValue.AsString("; expected"));
        }

        [Fact]
        public void A_non_string_column_renders_invariantly_not_in_the_ambient_culture()
        {
            // A German UI locale must not turn a numeric column into "1,5" in a payload the agent parses.
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("1.5", TableValue.AsString(1.5d));
                Assert.Equal("4", TableValue.AsString(4));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }
    }
}
