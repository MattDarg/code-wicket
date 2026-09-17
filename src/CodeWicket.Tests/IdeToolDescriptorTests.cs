using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CodeWicket.Core.Ide;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What the agent is told about each IDE tool, held to what actually reaches it.
    /// </summary>
    /// <remarks>
    /// Claude Code cuts an MCP tool description at 2KB and marks nothing (changelog 2.1.84). Three of ours
    /// were over it, and the text past the cap — the whole of <c>get_diagnostics</c>' passage on why its
    /// build and IntelliSense counts differ —
    /// read correctly in every review, because nothing a reviewer looks at is truncated. See
    /// <see cref="IdeToolDescriptors"/>.
    /// </remarks>
    public sealed class IdeToolDescriptorTests
    {
        private static IReadOnlyList<ToolDescriptor> All() =>
            typeof(IdeToolDescriptors).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(ToolDescriptor))
                .Select(f => (ToolDescriptor)f.GetValue(null)!)
                .ToList();

        [Fact]
        public void EveryToolIsSeenOnce()
        {
            // A count, so a reflection that finds nothing cannot pass every check below vacuously.
            var names = All().Select(t => t.Name).ToList();
            Assert.Equal(17, names.Count);
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void EveryDescriptionFitsWhatClaudeCodeSendsTheModel()
        {
            var over = All()
                .Select(t => (t.Name, Bytes: Encoding.UTF8.GetByteCount(t.Description)))
                .Where(t => t.Bytes > IdeToolDescriptors.MaxDescriptionBytes)
                .Select(t => $"{t.Name}: {t.Bytes} bytes")
                .ToList();

            Assert.True(over.Count == 0,
                "Past " + IdeToolDescriptors.MaxDescriptionBytes + " bytes the rest is cut silently: " + string.Join("; ", over));
        }

        /// <summary>
        /// Kiro v2 EXCLUDES a tool whose name breaks its pattern, whose name is too long beside the server's,
        /// or whose description is empty (kiro.dev/docs/mcp; each message is a string in the v2 binary). The
        /// failure is a tool that is simply not there, reported only in Kiro's own output.
        /// </summary>
        [Fact]
        public void EveryToolIsOneKiroV2WillRegister()
        {
            // "Combined with server name": v2's separator is not documented, so two characters are allowed
            // for it (calls are titled "@code-wicket/<tool>").
            const int MaxCombinedName = 64;
            var pattern = new System.Text.RegularExpressions.Regex("^[a-zA-Z][a-zA-Z0-9_]*$");

            var refused = All()
                .Where(t => !pattern.IsMatch(t.Name)
                            || IdeMcpServer.Name.Length + 2 + t.Name.Length > MaxCombinedName
                            || string.IsNullOrWhiteSpace(t.Description))
                .Select(t => t.Name)
                .ToList();

            Assert.True(refused.Count == 0, "Kiro v2 would exclude: " + string.Join(", ", refused));
        }

        /// <summary>
        /// Kiro v3 (<c>@kiro/agent</c>) excludes nothing — it RENAMES. Every MCP tool becomes
        /// <c>mcp_</c> + <c>server_tool</c> with whitespace and hyphens as <c>_</c>, anything else outside
        /// <c>[a-zA-Z0-9_]</c> dropped, lowercased, SLICED to 64 characters, and given <c>_1</c>, <c>_2</c>…
        /// on a collision (<c>fuc</c> / <c>hTr=64</c> in <c>acp-server.js</c>, 0.63.3). A slice or a suffix
        /// is silent, and the model then calls a name that is not the one this catalog wrote.
        /// </summary>
        [Fact]
        public void EveryToolKeepsItsNameOnKiroV3()
        {
            const int MaxToolId = 64;
            static string V3Id(string server, string tool)
            {
                var joined = System.Text.RegularExpressions.Regex.Replace(server + "_" + tool, @"[\s-]", "_");
                joined = System.Text.RegularExpressions.Regex.Replace(joined, "[^a-zA-Z0-9_]", "");
                return "mcp_" + joined.ToLowerInvariant();
            }

            var ids = All().Select(t => (t.Name, Id: V3Id(IdeMcpServer.Name, t.Name))).ToList();
            var renamed = ids
                .Where(t => t.Id.Length > MaxToolId || ids.Count(o => o.Id == t.Id) > 1)
                .Select(t => $"{t.Name} -> {t.Id} ({t.Id.Length} chars)")
                .ToList();

            Assert.True(renamed.Count == 0, "Kiro v3 would slice or suffix: " + string.Join("; ", renamed));
        }

        [Fact]
        public void EverySchemaIsAClosedJsonObject()
        {
            foreach (var tool in All())
            {
                using var doc = JsonDocument.Parse(tool.InputSchemaJson);
                Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
                Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean(), tool.Name);
            }
        }

        [Fact]
        public void EveryFirstSentenceIsAWholeHintOnItsOwn()
        {
            // Collected rather than asserted per tool, so one failure cannot hide the rest.
            var broken = All()
                .Select(t => (t.Name, Summary: AcpAgentSession.Summarize(t.Description)))
                .Where(t => !t.Summary.EndsWith(".", StringComparison.Ordinal) || t.Summary.EndsWith("e.g.", StringComparison.Ordinal))
                .Select(t => $"{t.Name}: \"{t.Summary}\"")
                .ToList();

            Assert.True(broken.Count == 0, "The <ide-tools> hint would read: " + string.Join("; ", broken));
        }

        [Fact]
        public void ASummaryDoesNotStopAtAnAbbreviation()
        {
            Assert.Equal(
                "Apply a fix — e.g. add a missing using.",
                AcpAgentSession.Summarize("Apply a fix — e.g. add a missing using. Prefer it over editing."));
        }

        /// <summary>
        /// The tier is the permission gate for every one of these, and it moved project with the text: a
        /// descriptor that lost its initializer would fall back to <see cref="ToolRisk.ReadOnly"/> silently.
        /// </summary>
        [Fact]
        public void TheRiskTiersAreThePermissionModelsTiers()
        {
            var expected = new Dictionary<string, ToolRisk>
            {
                ["build_solution"] = ToolRisk.ReadOnly,
                ["get_diagnostics"] = ToolRisk.ReadOnly,
                ["run_tests"] = ToolRisk.ReadOnly,
                ["open_file"] = ToolRisk.ReadOnly,
                [Breakpoints.PlainToolName] = ToolRisk.Edit,
                [Breakpoints.ExpressionToolName] = ToolRisk.Command,
                ["list_breakpoints"] = ToolRisk.ReadOnly,
                ["clear_breakpoints"] = ToolRisk.Edit,
                ["get_debug_output"] = ToolRisk.ReadOnly,
                ["read_expression"] = ToolRisk.ReadOnly,
                ["execute_expression"] = ToolRisk.Command,
                ["run_command"] = ToolRisk.Command,
                ["find_references"] = ToolRisk.ReadOnly,
                ["find_symbol"] = ToolRisk.ReadOnly,
                ["find_implementations"] = ToolRisk.ReadOnly,
                ["rename_symbol"] = ToolRisk.Edit,
                ["apply_code_fix"] = ToolRisk.Edit,
            };

            Assert.Equal(expected, All().ToDictionary(t => t.Name, t => t.Risk));
        }
    }
}
