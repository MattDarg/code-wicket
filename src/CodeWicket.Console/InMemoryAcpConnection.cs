using System.IO;
using CodeWicket.Providers.Acp;

namespace CodeWicket.ConsoleHost
{
    /// <summary>
    /// An <see cref="IAcpConnection"/> over a single in-memory full-duplex stream, used to wire
    /// the session to an in-process fake ACP agent for offline testing.
    /// </summary>
    internal sealed class InMemoryAcpConnection : IAcpConnection
    {
        private readonly Stream _stream;

        public InMemoryAcpConnection(Stream duplexStream) => _stream = duplexStream;

        public Stream Sending => _stream;

        public Stream Receiving => _stream;

        public void Dispose() => _stream.Dispose();
    }
}
