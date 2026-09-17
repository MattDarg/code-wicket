using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// IDE-native tools made available to the agent (e.g. go-to-symbol, find-references, build,
    /// run tests, read Error List). The Kiro provider surfaces this catalog to the backend as an
    /// MCP server, which Kiro supports natively. The shape here is intentionally MCP-like.
    /// </summary>
    public interface IToolCatalog
    {
        /// <summary>The tools available to the agent.</summary>
        IReadOnlyList<ToolDescriptor> Tools { get; }

        /// <summary>Invokes a tool by name. <paramref name="argumentsJson"/> matches the tool's input schema.</summary>
        Task<ToolResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default);
    }

    /// <summary>Metadata for one tool. <see cref="InputSchemaJson"/> is a JSON Schema describing the arguments.</summary>
    /// <remarks>
    /// <see cref="Risk"/> classifies how sensitive invoking the tool is, so the permission ladder can
    /// auto-allow it at the right mode. It is authored per-tool by the catalog, read by the shell's
    /// policy to resolve the risk of a permission request by tool name. It does NOT cross the
    /// engine/shell wire (risk is resolved shell-side, where permissions terminate), so
    /// <c>DtoMapping</c> deliberately omits it.
    /// </remarks>
    public sealed record ToolDescriptor(string Name, string Description, string InputSchemaJson)
    {
        /// <summary>How sensitive this tool is, for the permission-mode ladder. Defaults to
        /// <see cref="ToolRisk.ReadOnly"/> — the safest classification.</summary>
        public ToolRisk Risk { get; init; } = ToolRisk.ReadOnly;

    }

    /// <summary>
    /// How sensitive invoking a tool is, driving the permission ladder (Prompt → AcceptReads →
    /// AcceptEdits → AcceptAll). A tool is auto-allowed once the active mode's ceiling reaches its risk.
    /// </summary>
    public enum ToolRisk
    {
        /// <summary>Reads/queries only; no workspace mutation (e.g. find_references, get_diagnostics, build_solution).</summary>
        ReadOnly,

        /// <summary>Mutates the workspace (e.g. rename_symbol, apply_code_fix).</summary>
        Edit,

        /// <summary>Executes arbitrary commands (e.g. run_command) — the top tier: only AcceptAll
        /// auto-allows; every other mode prompts.</summary>
        Command,
    }

    /// <summary>The result of invoking a tool. <see cref="ContentJson"/> is the JSON-encoded result content.</summary>
    public sealed record ToolResult(bool IsError, string ContentJson);
}
