using CodeWicket.Providers.Kiro;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// How <c>kiro-cli chat _ get-kas-token</c> reports failure — the host-side auth callback behind
    /// Kiro's v3 agent engine. Captured from the real CLI (2.13.0): it writes a JSON envelope to
    /// STDOUT and exits 1 with an EMPTY stderr, so reading the exit code first produced
    /// "get-kas-token exited 1: " and dropped the one line the user could act on. That string is
    /// exactly what reached a live transcript, via Kiro's "Auth refresh callback failed: …".
    /// </summary>
    public class KiroKasTokenTests
    {
        // Verbatim from kiro-cli 2.13.0 with no KAS credentials.
        private const string NotLoggedIn =
            "{\"kind\":\"error\",\"data\":{\"message\":\"You are not logged in. "
            + "Please log in with `kiro-cli login`.\"}}";

        [Fact]
        public void ReportedErrorCarriesTheClisOwnMessage()
        {
            Assert.True(KiroKasTokenProvider.TryGetReportedError(NotLoggedIn, out var message));
            Assert.Equal("You are not logged in. Please log in with `kiro-cli login`.", message);
        }

        [Fact]
        public void ErrorEnvelopeIsFoundAmongLogLinesOnStdout()
        {
            // The CLI prefixes its own chatter; the envelope is one line among several.
            var stdout = "loading profile\n" + NotLoggedIn + "\n";

            Assert.True(KiroKasTokenProvider.TryGetReportedError(stdout, out var message));
            Assert.Contains("kiro-cli login", message);
        }

        [Fact]
        public void AnErrorEnvelopeWithNoMessageStillReportsSomething()
        {
            // Never surface an empty reason: "" would render as "Auth refresh callback failed: ",
            // which is the very failure mode this whole path exists to remove.
            Assert.True(KiroKasTokenProvider.TryGetReportedError("{\"kind\":\"error\",\"data\":{}}", out var message));
            Assert.False(string.IsNullOrWhiteSpace(message));
        }

        [Fact]
        public void ASuccessfulPayloadIsNotAnError()
        {
            // The success envelope carries the token under data; it must fall through to the parser
            // rather than being mistaken for a failure.
            var stdout = "{\"kind\":\"success\",\"data\":{\"accessToken\":\"t\",\"expiresAt\":\"2026-01-01T00:00:00Z\"}}";

            Assert.False(KiroKasTokenProvider.TryGetReportedError(stdout, out _));
        }

        [Fact]
        public void NonJsonOutputIsNotAnError()
        {
            Assert.False(KiroKasTokenProvider.TryGetReportedError("not json at all\n", out _));
        }
    }
}
