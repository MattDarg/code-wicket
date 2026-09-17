using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Serialises every test class that constructs an <c>McpToolServer</c>, because the server reads
    /// <c>CWKT_MCP_LOG</c> — one process-wide environment variable. Same shape and same reason as
    /// <see cref="RenderSinkCollection"/>: xUnit runs test classes in parallel, so two classes
    /// set-and-restore one static and interleave.
    /// </summary>
    /// <remarks>
    /// The variable is read once, inside the constructor, so the window is narrow and the failure is
    /// intermittent rather than reproducible: a class that sets the path and builds a server can have
    /// the restore from the other class land between the two, and its server then tees nowhere. The
    /// test that reads the file back finds it absent or empty, and reports the tee as broken.
    /// <para>A collection rather than a fixture: nothing needs sharing, only ordering.</para>
    /// </remarks>
    [CollectionDefinition(Name)]
    public sealed class McpLogCollection
    {
        public const string Name = "mcp-log";
    }
}
