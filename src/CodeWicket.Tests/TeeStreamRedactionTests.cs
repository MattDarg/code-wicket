using System;
using System.IO;
using System.Text;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The CWKT_ACP_LOG send-direction tee must never write the user's bearer token to disk: the
    /// response to Kiro v3's <c>_kiro/auth/getAccessToken</c> host callback rides client→agent, so
    /// the tee redacts <c>accessToken</c> values while leaving every other frame byte-exact.
    /// </summary>
    public sealed class TeeStreamRedactionTests : IDisposable
    {
        private readonly string _logPath = Path.Combine(Path.GetTempPath(), "cwkt-tee-test-" + Guid.NewGuid().ToString("N") + ".log");

        public void Dispose()
        {
            try { File.Delete(_logPath); } catch { /* best-effort cleanup */ }
        }

        private string RunThroughTee(params string[] writes)
        {
            using (var sink = FrameLogSink.Open(_logPath))
            using (var tee = new FrameTeeWriteStream(Stream.Null, sink))
            {
                foreach (var chunk in writes)
                {
                    var bytes = Encoding.UTF8.GetBytes(chunk);
                    tee.Write(bytes, 0, bytes.Length);
                }
            }
            return File.ReadAllText(_logPath);
        }

        [Fact]
        public void AccessTokenResponse_IsRedacted_OtherFramesVerbatim()
        {
            const string authResponse =
                """{"jsonrpc":"2.0","id":4,"result":{"accessToken":"eyJSECRET.PAYLOAD.SIG","expiresAt":"2026-07-20T12:00:00Z","profileArn":"arn:aws:codewhisperer:us-east-1:x:profile/y"}}""" + "\n";
            const string prompt =
                """{"jsonrpc":"2.0","id":5,"method":"session/prompt","params":{"prompt":[{"type":"text","text":"hi"}]}}""" + "\n";

            var logged = RunThroughTee(authResponse, prompt);

            Assert.DoesNotContain("eyJSECRET", logged);
            Assert.Contains("\"accessToken\":\"<REDACTED>\"", logged);
            // The rest of the auth frame (expiry, profile) and the unrelated frame stay intact.
            Assert.Contains("2026-07-20T12:00:00Z", logged);
            Assert.Contains(prompt, logged);
        }

        // A frame split across multiple Write calls (StreamJsonRpc doesn't guarantee frame-aligned
        // writes) must still be redacted — the tee buffers to the newline boundary before scanning.
        [Fact]
        public void TokenSplitAcrossWrites_IsStillRedacted()
        {
            var logged = RunThroughTee(
                """{"jsonrpc":"2.0","id":1,"result":{"accessToken":"eyJPART-ONE""",
                "PART-TWO\",\"expiresAt\":\"2026-07-20T12:00:00Z\"}}\n");

            Assert.DoesNotContain("PART-ONE", logged);
            Assert.DoesNotContain("PART-TWO", logged);
            Assert.Contains("<REDACTED>", logged);
        }

        // A trailing partial frame (no newline before teardown) is flushed on dispose, redacted.
        [Fact]
        public void TrailingPartialFrame_FlushedRedactedOnDispose()
        {
            var logged = RunThroughTee(
                """{"jsonrpc":"2.0","id":2,"result":{"accessToken":"eyJTRAILING","expiresAt":"x"}}""");

            Assert.DoesNotContain("eyJTRAILING", logged);
            Assert.Contains("<REDACTED>", logged);
        }
    }
}
