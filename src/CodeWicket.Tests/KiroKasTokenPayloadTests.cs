using System;
using System.Text.Json;
using CodeWicket.Providers.Kiro;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Reading the token out of <c>kiro-cli chat _ get-kas-token</c>'s stdout
    /// (<see cref="KiroKasTokenProvider.ParsePayload"/>), which is what the v3 auth callback depends on.
    /// <para>
    /// The stakes are asymmetric: a payload we fail to find is not a degraded answer but a dead
    /// session — every model call after it fails with <c>TokenExpiredError</c>, and the message the user
    /// sees names the frame that was not the token rather than the one that was.
    /// </para>
    /// <para>
    /// No values are asserted against a real token, and none should be: the file's own rule is that
    /// error text names shapes and never property VALUES, so the token cannot reach a log.
    /// </para>
    /// </summary>
    public sealed class KiroKasTokenPayloadTests
    {
        private const string Token = "not-a-real-token";

        private static string TokenOf(JsonElement payload) => payload.GetProperty("accessToken").GetString()!;

        /// <summary>The bare payload: accessToken at the root.</summary>
        [Fact]
        public void ABarePayloadIsRead()
        {
            Assert.Equal(Token, TokenOf(KiroKasTokenProvider.ParsePayload(
                "{\"accessToken\":\"" + Token + "\"}")));
        }

        /// <summary>The envelope Kiro's TUI unwraps as <c>output.data</c>.</summary>
        [Fact]
        public void AnEnvelopeIsUnwrapped()
        {
            Assert.Equal(Token, TokenOf(KiroKasTokenProvider.ParsePayload(
                "{\"kind\":\"success\",\"data\":{\"accessToken\":\"" + Token + "\"}}")));
        }

        /// <summary>
        /// The bug: a JSON frame that is not the token must be SKIPPED, not fatal.
        /// </summary>
        /// <remarks>
        /// Every other rejection in the loop continues to the next line — a blank line, a non-`{` line,
        /// an unparseable one. A parseable object without an accessToken threw instead, so any preamble,
        /// warning or telemetry envelope ahead of the token frame failed the whole read while the real
        /// payload sat on the very next line. The asymmetry with its neighbours is what marks it as an
        /// oversight rather than a rule.
        /// </remarks>
        [Fact]
        public void APreambleFrameIsSkippedRatherThanFatal()
        {
            var stdout =
                "{\"kind\":\"info\",\"data\":{\"message\":\"refreshing credentials\"}}\n" +
                "{\"kind\":\"success\",\"data\":{\"accessToken\":\"" + Token + "\"}}\n";

            Assert.Equal(Token, TokenOf(KiroKasTokenProvider.ParsePayload(stdout)));
        }

        /// <summary>...and noise of every other shape around it, which already worked.</summary>
        [Fact]
        public void NoiseAroundThePayloadIsIgnored()
        {
            var stdout =
                "warming up\n" +
                "\n" +
                "{ not json at all\n" +
                "{\"kind\":\"info\",\"data\":{}}\n" +
                "{\"accessToken\":\"" + Token + "\"}\n" +
                "done\n";

            Assert.Equal(Token, TokenOf(KiroKasTokenProvider.ParsePayload(stdout)));
        }

        /// <summary>
        /// With no token anywhere, the failure names the last frame that could plausibly have been one —
        /// not the first thing that wasn't. That is the difference between a message that points at the
        /// problem and one that points at the preamble.
        /// </summary>
        [Fact]
        public void TheFailureNamesTheLastCandidateShape()
        {
            var stdout =
                "{\"kind\":\"info\",\"data\":{}}\n" +
                "{\"kind\":\"error\",\"message\":\"you are not logged in\"}\n";

            var ex = Assert.Throws<InvalidOperationException>(
                () => KiroKasTokenProvider.ParsePayload(stdout));

            Assert.Contains("kind,message", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        }

        /// <summary>No JSON at all is still its own message, distinct from "JSON without a token".</summary>
        [Fact]
        public void NoJsonAtAllSaysSo()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => KiroKasTokenProvider.ParsePayload("command not found\n"));

            Assert.Contains("no JSON payload", ex.Message, StringComparison.Ordinal);
        }
    }
}
