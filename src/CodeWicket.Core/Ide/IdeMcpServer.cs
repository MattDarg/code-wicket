using System;
using System.Collections.Generic;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// The identity of our IDE tool-catalog MCP server, and the parser that recovers a bare local tool
    /// name from the namespaced form a backend reports in a permission request. Backends prefix MCP tool
    /// names with the server name so their origin is unambiguous, but each does it differently:
    /// <list type="bullet">
    /// <item>Claude Code / spec-ACP: <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> (e.g. <c>mcp__code-wicket__run_tests</c>).</item>
    /// <item>Kiro: <c>@&lt;server&gt;/&lt;tool&gt;</c> (e.g. <c>@code-wicket/run_tests</c>).</item>
    /// </list>
    /// The parse is <em>guarded on the server segment</em>: only names that belong to <see cref="Name"/>
    /// resolve, so an unrelated MCP server's tool never picks up our authored risk classification.
    /// </summary>
    public static class IdeMcpServer
    {
        /// <summary>The MCP server name the engine registers the IDE tool catalog under. Kept in one
        /// place so the engine's registration and the shell's name-parsing can't drift apart.</summary>
        /// <remarks>
        /// The bare product name, with no <c>-ide</c> qualifier: this catalog is the whole of what the
        /// extension exposes to an agent, so the suffix restated the product's purpose in every tool
        /// name the model reads and every rule the user stores. Keeping it to leave room for
        /// a second server buys nothing — <see cref="IsOurServer"/> would have to
        /// be edited for one whatever this is called.
        /// </remarks>
        public const string Name = "code-wicket";

        /// <summary>
        /// True for our server in either spelling, hyphenated or v3's underscored. One predicate so the
        /// resolve, the rule-normalizer and the MCP roster panel cannot come to different answers about
        /// whose server something is.
        /// <para>The panel needs it because <b>our bridge appears in the backend's own roster</b> and has
        /// to be pulled out of the user's section — it is ours, not their configuration, so counting it
        /// inflates every total and would put a server in their list they never configured. Measured on a
        /// Kiro v3 capture, which lists our bridge beside the user's own servers.</para>
        /// </summary>
        public static bool IsOurServer(string server) =>
            string.Equals(server.Replace('_', '-'), Name, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// If <paramref name="toolName"/> is one of <em>our</em> IDE tools in either backend's namespaced
        /// form, unwraps it to the bare local name (e.g. <c>run_tests</c>) and returns true. Returns false
        /// for null/blank input, an un-namespaced name, or a name scoped to a different MCP server.
        /// </summary>
        public static bool TryResolveLocalTool(string? toolName, out string localName)
        {
            localName = string.Empty;
            if (string.IsNullOrWhiteSpace(toolName))
                return false;

            var name = toolName!.Trim();

            // Claude Code / spec-ACP: mcp__<server>__<tool>. The server name itself contains hyphens
            // (not "__"), so splitting on the LAST "__" cleanly separates the tool from the server.
            const string mcpPrefix = "mcp__";
            if (name.StartsWith(mcpPrefix, StringComparison.Ordinal))
                return TrySplit(name.Substring(mcpPrefix.Length), "__", out localName);

            // Kiro: @<server>/<tool>.
            if (name.StartsWith("@", StringComparison.Ordinal))
                return TrySplit(name.Substring(1), "/", out localName);

            return false;
        }

        /// <summary>
        /// Decomposes ANY backend's namespaced tool name into <c>server/tool</c> — the canonical form
        /// every trust rule is stored in (see <see cref="TryNormalizeToolRule"/>). False for a bare or
        /// unrecognised name.
        /// <para>
        /// For an unknown server the split is genuinely ambiguous in Claude's spelling —
        /// <c>mcp__a__b__c</c> could be server <c>a</c> or server <c>a__b</c> — and it is resolved by
        /// taking the LAST separator. A wrong guess cannot cause a false match: the rule is compared by
        /// RE-CONSTRUCTING both spellings from its own halves (<see cref="NamesSameTool"/>), so a
        /// mis-split still reproduces the name it came from exactly. It costs only cross-backend
        /// portability for that one rule, which is the direction a trust rule may safely fail in.
        /// </para>
        /// </summary>
        public static bool TryParseNamespacedTool(string? toolName, out string server, out string tool)
        {
            server = string.Empty;
            tool = string.Empty;
            var name = toolName?.Trim();
            if (string.IsNullOrEmpty(name))
                return false;

            const string mcpPrefix = "mcp__";
            var rest = name!.StartsWith(mcpPrefix, StringComparison.Ordinal) ? name.Substring(mcpPrefix.Length)
                : name.StartsWith("@", StringComparison.Ordinal) ? name.Substring(1)
                : null;
            var separator = name.StartsWith(mcpPrefix, StringComparison.Ordinal) ? "__" : "/";
            if (rest is null)
                return false;

            var at = rest.LastIndexOf(separator, StringComparison.Ordinal);
            if (at <= 0 || at + separator.Length >= rest.Length)
                return false;

            server = rest.Substring(0, at);
            tool = rest.Substring(at + separator.Length);
            return true;
        }

        /// <summary>
        /// Normalizes a trust rule to the ONE shape the allow-list stores and the policy matches:
        /// canonical <c>server/tool</c>, whoever's tool it is and whichever backend spelling it arrived
        /// in. A bare name is taken as ours and gains <see cref="Name"/> — the short form stays typeable
        /// in settings, it just doesn't stay ambiguous once saved.
        /// <para>Keeping the server on OUR tools is not a safety requirement — a bare rule could only
        /// ever be reached through the server-anchored <see cref="TryResolveLocalTool"/> — it is so the
        /// stored list explains its own format to anyone editing it by hand, and so no reader has to
        /// know that "bare means ours". Nothing is given up: a canonical rule is matched by construction
        /// (<see cref="NamesSameTool"/>), so it holds across both spellings and v3's underscored server
        /// exactly as a bare one did.</para>
        /// <para>False for a namespaced name that won't parse — a rule that could never fire is worse
        /// stored than refused, because the user can see it and believe it.</para>
        /// </summary>
        public static bool TryNormalizeToolRule(string? toolName, out string rule)
        {
            rule = string.Empty;
            var name = toolName?.Trim();
            if (string.IsNullOrEmpty(name))
                return false;

            // A backend's namespaced name: exactly the name the row and the banner SHOW for it, so the
            // rule a user saves is the text they were looking at when they saved it.
            if (TryDisplayName(name, out var named))
            {
                rule = named;
                return true;
            }

            // ALREADY CANONICAL, and this branch is not cosmetic: the list is re-normalized on every
            // config load and every SetToolPolicy, so a rule that could not survive its own output
            // shape would be dropped the moment it was read back — every stored rule silently forgotten
            // one restart after the user made it. Normalization must be IDEMPOTENT, and it is pinned so.
            var slash = name!.IndexOf('/');
            if (slash > 0 && slash + 1 < name.Length && name.IndexOf('/', slash + 1) < 0)
            {
                var head = name.Substring(0, slash);
                // Fold our own server onto its canonical hyphenated spelling; leave anyone else's as
                // written, since only they know which form is theirs.
                rule = (IsOurServer(head) ? Name : head) + "/" + name.Substring(slash + 1);
                return true;
            }

            // A bare name is something a PERSON typed — every backend namespaces what it sends — so it
            // can only mean one of ours. A leftover separator is a namespaced name we couldn't parse.
            if (name.IndexOf('/') >= 0 || name.IndexOf("__", StringComparison.Ordinal) >= 0 ||
                name.StartsWith("@", StringComparison.Ordinal))
                return false;

            rule = Name + "/" + name;
            return true;
        }

        /// <summary>
        /// The name a person is SHOWN for a backend's namespaced MCP tool name: canonical
        /// <c>server/tool</c>, which is also the rule <see cref="TryNormalizeToolRule"/> stores for it. Our
        /// server is folded onto <see cref="Name"/> from any spelling; anyone else's is kept as written,
        /// which is also the collision guard — <c>github/create_issue</c> can never read as OUR
        /// <c>create_issue</c>, nor the reverse (issue #129). False for anything that is not a namespaced
        /// name (a bare name, a prose title).
        /// </summary>
        /// <remarks>
        /// One definition for the tool row, the permission banner and the saved rule, because the
        /// backends name one call three ways — Claude <c>mcp__code-wicket__build_solution</c>, Kiro v2
        /// <c>Running: @code-wicket/build_solution</c>, Kiro v3 <c>@code_wicket/build_solution</c> — and the
        /// settings list names it a fourth. Deciding WHETHER a call may be shown by this name is the
        /// host's, not this method's: a shell command whose title spells one of these must not be.
        /// </remarks>
        public static bool TryDisplayName(string? toolName, out string displayName)
        {
            displayName = string.Empty;

            // Ours: through the anchored resolve, so the server is its canonical hyphenated spelling
            // however it arrived (v3 sends "@code_wicket/…").
            if (TryResolveLocalTool(toolName, out var local))
            {
                displayName = Name + "/" + local;
                return true;
            }

            if (TryParseNamespacedTool(toolName, out var server, out var tool))
            {
                displayName = server + "/" + tool;
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when a canonical <c>server/tool</c> rule names the tool in <paramref name="toolName"/>,
        /// whichever backend spelling it arrived in. The rule's own halves are used to BUILD both
        /// spellings and compare — the incoming name is never parsed, so the ambiguity in
        /// <see cref="TryParseNamespacedTool"/> cannot widen a rule onto a tool it doesn't name.
        /// The SERVER half is offered both hyphenated and underscored (Kiro v3 underscores server names);
        /// the TOOL half is never folded, because <c>run_tests</c> and <c>run-tests</c> are different
        /// tools and a trust rule may not conflate them.
        /// </summary>
        public static bool NamesSameTool(string? canonicalRule, string? toolName)
        {
            var incoming = toolName?.Trim();
            if (string.IsNullOrEmpty(canonicalRule) || string.IsNullOrEmpty(incoming))
                return false;

            var slash = canonicalRule!.IndexOf('/');
            if (slash <= 0 || slash + 1 >= canonicalRule.Length)
                return false;

            var tool = canonicalRule.Substring(slash + 1);
            foreach (var server in new[]
                     {
                         canonicalRule.Substring(0, slash),
                         canonicalRule.Substring(0, slash).Replace('-', '_'),
                     })
            {
                if (string.Equals("@" + server + "/" + tool, incoming, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals("mcp__" + server + "__" + tool, incoming, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // Splits "<server><sep><tool>" on the LAST separator, requiring the server segment to be ours.
        // The server compare folds '_' to '-': Kiro v3 normalizes server names to underscores
        // (captured live 2026-07-20 — the server segment came back with every '-' replaced by '_'),
        // and without the fold none of our
        // tools would resolve under v3. The tool segment is returned verbatim — real tool names
        // (run_tests) are underscored and must stay that way to match the authored risk map.
        private static bool TrySplit(string rest, string separator, out string localName)
        {
            localName = string.Empty;
            var at = rest.LastIndexOf(separator, StringComparison.Ordinal);
            if (at <= 0)
                return false;

            var server = rest.Substring(0, at);
            var tool = rest.Substring(at + separator.Length);
            if (tool.Length == 0 || !IsOurServer(server))
                return false;

            localName = tool;
            return true;
        }
    }
}
