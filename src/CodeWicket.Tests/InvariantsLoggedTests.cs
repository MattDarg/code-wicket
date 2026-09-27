using System.Collections.Generic;
using CodeWicket.Shell.Sessions;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The sink ChatViewModel reports through. Both halves are asserted: the <c>[invariant]</c> line is
    /// the product's only record of a breach, and the forward to the ambient scope is the only way a
    /// view-model breach fails its test.
    /// </summary>
    public sealed class InvariantsLoggedTests
    {
        [Fact]
        public void ABreachIsLoggedAndReachesTheOpenScope()
        {
            var lines = new List<string>();
            var recorder = new BreachRecorder();

            using (Invariants.OpenScope(recorder))
                Invariants.Logged(lines.Add).Breach("a turn never returned");

            Assert.Equal(new[] { "a turn never returned" }, lines);
            Assert.Equal(new[] { "a turn never returned" }, recorder.Drain());
        }

        /// <summary>The overload a plain test uses: the same one line, forwarded to the recorder it names.</summary>
        [Fact]
        public void ABreachIsLoggedAndReachesTheNamedSink()
        {
            var lines = new List<string>();
            var recorder = new BreachRecorder();

            Invariants.Logged(lines.Add, recorder).Breach("[lifetime] a turn never returned");

            Assert.Equal(new[] { "[lifetime] a turn never returned" }, lines);
            Assert.Equal(new[] { "[lifetime] a turn never returned" }, recorder.Drain());
        }

        /// <summary>The product opens no scope, and a host with no log passes none: neither may throw.</summary>
        [Fact]
        public void WithNoScopeAndNoLogABreachIsHarmless()
        {
            Invariants.Logged(null).Breach("nobody is listening");
        }
    }
}
