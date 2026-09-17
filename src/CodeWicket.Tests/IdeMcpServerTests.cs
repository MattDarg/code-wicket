using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The server-guarded parser that recovers a bare IDE tool name from a backend's namespaced form.
    /// This is the join between a permission request's reported tool name and the shell's authored risk
    /// map, so the server guard (only OUR tools resolve) and both backends' shapes are pinned here.
    /// </summary>
    public sealed class IdeMcpServerTests
    {
        [Theory]
        [InlineData("mcp__code-wicket__run_tests", "run_tests")]         // Claude / spec-ACP
        [InlineData("mcp__code-wicket__get_diagnostics", "get_diagnostics")]
        [InlineData("@code-wicket/run_tests", "run_tests")]              // Kiro v2
        [InlineData("@code-wicket/rename_symbol", "rename_symbol")]
        [InlineData("@code_wicket/run_tests", "run_tests")]              // Kiro v3 normalizes '-' to '_'
        [InlineData("@code_wicket/echo", "echo")]                        // (captured live 2026-07-20)
        [InlineData("mcp__code_wicket__run_tests", "run_tests")]         // underscore form, Claude shape
        public void ResolvesOurNamespacedTools(string toolName, string expected)
        {
            Assert.True(IdeMcpServer.TryResolveLocalTool(toolName, out var local));
            Assert.Equal(expected, local);
        }

        [Theory]
        [InlineData("mcp__some-other-server__run_tests")]  // different MCP server
        [InlineData("@other-server/run_tests")]
        [InlineData("@other_server/run_tests")]            // foreign stays foreign in v3's underscore form
        [InlineData("run_tests")]                          // not namespaced
        [InlineData("mcp__code-wicket__")]         // empty tool segment
        [InlineData("@code-wicket/")]              // empty tool segment
        [InlineData("@/run_tests")]                        // empty server segment
        [InlineData("")]
        [InlineData(null)]
        public void RejectsForeignOrMalformedNames(string? toolName)
        {
            Assert.False(IdeMcpServer.TryResolveLocalTool(toolName, out var local));
            Assert.Equal(string.Empty, local);
        }

        // The server name is a single source of truth shared with the engine's MCP registration.
        [Fact]
        public void ServerName_IsStable()
        {
            Assert.Equal("code-wicket", IdeMcpServer.Name);
        }

        /// <summary>
        /// The server compare is EXACT, never a prefix match. The bare name makes a third-party
        /// <c>code-wicket-…</c> server plausible in a way an <c>-ide</c>-qualified one was not
        /// (<c>Breakpoints</c> reasons about the same hypothetical helper), so a neighbour that merely
        /// starts with our name must not resolve to our authored risk map.
        /// </summary>
        [Theory]
        [InlineData("@code-wicket-helper/run_tests")]
        [InlineData("@code-wicket-ide/run_tests")]
        [InlineData("@not-code-wicket/run_tests")]
        [InlineData("mcp__code-wicket-extra__run_tests")]
        public void ANeighbouringServerIsNotClaimed(string named)
        {
            Assert.False(IdeMcpServer.TryResolveLocalTool(named, out _));
        }
    }
}
