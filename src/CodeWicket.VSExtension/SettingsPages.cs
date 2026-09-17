using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using CodeWicket.Core;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// The classic Tools &gt; Options pages, which are the ONLY settings surface on Visual Studio 2022.
    /// <para>
    /// <b>Why they exist</b> (measured 2026-09-16, VS 2022 17.14 against VS 2026 18.9.1). Unified
    /// Settings renders our external region natively on 18.x. On 17.x the Options dialog is the legacy
    /// one, and it does NOT render an external region — it renders a classic page or nothing. That was
    /// established by experiment: registering a node and setting <c>IsInUnifiedSettings=1</c> produced
    /// the tree entries and four EMPTY pages, because the content always comes from the page itself.
    /// With no page of our own, VS 2022 had no settings UI at all.
    /// </para>
    /// <para>
    /// <b>One settings model, two renderers.</b> These pages read and write <see cref="ExtensionConfig"/>
    /// directly — the same config.json the unified region's provider uses, and the same one the chat
    /// header's pickers write. There is no second store and no synchronisation step; both surfaces are
    /// views onto one file, which is why a value changed in either is live in the other.
    /// </para>
    /// <para>
    /// <b>The UI is the PropertyGrid's, not ours.</b> Declaring typed properties with
    /// <see cref="CategoryAttribute"/>/<see cref="DisplayNameAttribute"/>/<see cref="DescriptionAttribute"/>
    /// gets checkboxes, dropdowns, text boxes and a collection editor for free, themed as VS themes its
    /// own pages. An enum property is what makes an invalid value unrepresentable: the grid offers the
    /// members and nothing else, which is the guarantee the provider gets by round-tripping through a
    /// parser.
    /// </para>
    /// <para>
    /// <b>On 18.x these pages still register</b>, because <c>ProvideOptionPage</c> is a static attribute.
    /// The duplicate that would otherwise appear in Unified Settings — a "these settings haven't been
    /// migrated yet" placeholder per category — is suppressed by
    /// <c>ShouldShowUnifiedSettingsPlaceholder</c> in CodeWicket.SettingsManifest.pkgdef. That is the
    /// arrangement NuGet ships on VS 2022: a real classic page, a unified region, and the placeholder
    /// turned off.
    /// </para>
    /// </summary>
    internal static class SettingsPageIds
    {
        public const string General = "da46080c-bfe9-44a7-9950-53c3d3c76356";
        public const string Backends = "c9fae043-018c-44bd-913a-91eb9bbda959";
        public const string Permissions = "f12b69b7-0631-4c7a-86d7-3001ba6ce134";
        public const string Advanced = "cfa74fef-497a-433c-8373-25d898a53deb";

        /// <summary>The Tools &gt; Options tree node these pages sit under.</summary>
        public const string Category = Branding.ProductName;
    }

    /// <summary>
    /// Base for the pages: load on open, save on OK, both against <see cref="ExtensionConfig"/>.
    /// <para>
    /// <see cref="DialogPage"/>'s own storage is VS's settings store, which is the wrong file — so both
    /// halves are overridden rather than extended. <see cref="ExtensionConfig.Save"/> raises the change
    /// event the rest of the extension already listens to, so an edit here reaches a live chat window
    /// exactly as one made in Unified Settings does.
    /// </para>
    /// </summary>
    public abstract class CodeWicketOptionPage : DialogPage
    {
        public override void LoadSettingsFromStorage() => ReadFrom(ExtensionConfig.Load());

        public override void SaveSettingsToStorage() => ExtensionConfig.Save(WriteTo);

        /// <summary>Copy config into the page's properties.</summary>
        protected abstract void ReadFrom(ExtensionConfig config);

        /// <summary>Copy the page's properties into config.</summary>
        protected abstract void WriteTo(ExtensionConfig config);

        // KEY=value lines <-> the config's map. Deliberately tolerant: a malformed line is DROPPED
        // rather than rejecting the save, which is no worse than hand-editing config.json (where a bad
        // entry is skipped with a warning) and avoids a modal refusal inside VS's own dialog. The
        // unified-settings provider does reject, because it can show the offending line in context.
        private protected static string[] ToLines(Dictionary<string, string> map) =>
            map is null ? Array.Empty<string>()
                        : map.Select(kv => kv.Key + "=" + kv.Value).ToArray();

        private protected static Dictionary<string, string> FromLines(string[] lines)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var i = line.IndexOf('=');
                if (i <= 0)
                    continue;
                map[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }

            return map;
        }

        private protected static string[] OrEmpty(string[] value) => value ?? Array.Empty<string>();
    }

    /// <summary>The backend the chat opens on. Stored as the id, shown as a name the grid can offer.</summary>
    public enum BackendChoice
    {
        Kiro,
        ClaudeCode,
    }

    /// <summary>
    /// Where a message typed mid-turn waits. A local enum rather than <c>PendingRelease</c>, whose
    /// members are named for the release POINT (TurnEnd/NextStep) while the stored and displayed words
    /// are Queue/Steer — and the two surfaces must offer the same words.
    /// </summary>
    public enum MessageReleaseChoice
    {
        Queue,
        Steer,
    }

    /// <summary>General: the backend, the model, mid-turn messages and the agent's working directory.</summary>
    [Guid(SettingsPageIds.General)]
    public sealed class GeneralOptionPage : CodeWicketOptionPage
    {
        [Category("General")]
        [DisplayName("Backend")]
        [Description("Default agent backend. Switch per session from the chat header.")]
        public BackendChoice Backend { get; set; } = BackendChoice.Kiro;

        [Category("General")]
        [DisplayName("Default model")]
        [Description("Model id passed to the backend; leave blank to let it choose.")]
        public string DefaultModel { get; set; } = string.Empty;

        [Category("General")]
        [DisplayName("Messages typed while the agent is working")]
        [Description("Where a message typed mid-turn waits before it is sent. 'Queue' holds it until the " +
                     "turn ends; 'Steer' sends it at the agent's next step — the next moment no tool call " +
                     "is running, which may be seconds away. Either way nothing is sent into a running " +
                     "tool call, and the tray above the message box can change it at any time.")]
        public MessageReleaseChoice MessagesTypedWhileWorking { get; set; } = MessageReleaseChoice.Queue;

        [Category("General")]
        [DisplayName("Agent workspace")]
        [Description("Where the agent runs. A backend such as Kiro reads its steering, agent configs and " +
                     "workspace MCP servers from its working directory only, so a solution nested inside a " +
                     "repository loses them when they live at the repository root. 'RepositoryRoot' looks " +
                     "upwards for the backend's workspace folder (for example .kiro); the search never " +
                     "leaves the repository and never uses your user profile folder. Applies when the chat " +
                     "window next opens.")]
        public AgentWorkspaceScope AgentWorkspace { get; set; } = AgentWorkspaceScope.RepositoryRoot;

        protected override void ReadFrom(ExtensionConfig c)
        {
            Backend = string.Equals(c.DefaultProvider, "claude-code", StringComparison.OrdinalIgnoreCase)
                ? BackendChoice.ClaudeCode
                : BackendChoice.Kiro;
            DefaultModel = c.DefaultModel ?? string.Empty;
            // Through the parser in BOTH directions, so a config written before this setting existed —
            // or hand-edited to a spelling the tray has no rung for — shows what the window will do.
            MessagesTypedWhileWorking =
                PendingReleaseModes.Parse(c.DefaultMessageRelease) == PendingRelease.NextStep
                    ? MessageReleaseChoice.Steer
                    : MessageReleaseChoice.Queue;
            AgentWorkspace = AgentWorkspaceScopeParser.Parse(c.AgentWorkspaceScope);
        }

        protected override void WriteTo(ExtensionConfig c)
        {
            c.DefaultProvider = Backend == BackendChoice.ClaudeCode ? "claude-code" : "kiro";
            c.DefaultModel = string.IsNullOrWhiteSpace(DefaultModel) ? null : DefaultModel.Trim();
            c.DefaultMessageRelease = PendingReleaseModes.ToName(
                MessagesTypedWhileWorking == MessageReleaseChoice.Steer
                    ? PendingRelease.NextStep
                    : PendingRelease.TurnEnd);
            c.AgentWorkspaceScope = AgentWorkspace.ToString();
        }
    }

    /// <summary>Backends: which are offered, where their CLIs are, and the engine environment.</summary>
    [Guid(SettingsPageIds.Backends)]
    public sealed class BackendsOptionPage : CodeWicketOptionPage
    {
        [Category("Kiro")]
        [DisplayName("Enable Kiro")]
        [Description("Make the built-in Kiro backend available in the chat's agent picker. Turn off for " +
                     "installs where Kiro can never work. Applies when the chat window next opens.")]
        public bool KiroEnabled { get; set; } = true;

        [Category("Kiro")]
        [DisplayName("Kiro CLI path")]
        [Description("Path to kiro-cli; leave blank to use 'kiro-cli' on PATH.")]
        public string KiroCliPath { get; set; } = string.Empty;

        [Category("Kiro")]
        [DisplayName("Kiro agent engine")]
        [Description("Kiro agent engine to launch with (kiro-cli acp --agent-engine), e.g. v1, v2, or v3. " +
                     "Blank uses Kiro's default engine. Applies when the chat window next opens.")]
        public string KiroAgentEngine { get; set; } = string.Empty;

        [Category("Claude Code")]
        [DisplayName("Enable Claude Code")]
        [Description("Make the built-in Claude Code backend available in the chat's agent picker. Turn off " +
                     "for installs where Claude Code can never work. Applies when the chat window next opens.")]
        public bool ClaudeCodeEnabled { get; set; } = true;

        [Category("Claude Code")]
        [DisplayName("Claude Code adapter path")]
        [Description("Path to the claude-agent-acp adapter (npm i -g @agentclientprotocol/claude-agent-acp); " +
                     "leave blank to resolve it on PATH.")]
        public string ClaudeAcpPath { get; set; } = string.Empty;

        [Category("Custom and shared")]
        [DisplayName("Engine environment variables")]
        [Description("Environment variables for the engine process and every agent CLI it spawns, one " +
                     "KEY=value per line — e.g. HTTPS_PROXY=http://proxy:8080 and NO_PROXY=localhost. For " +
                     "TLS-intercepting proxies add AWS_CA_BUNDLE=<pem> (Kiro) and NODE_EXTRA_CA_CERTS=<pem> " +
                     "(Claude Code). A line without '=' is ignored. Applies when the chat window next opens.")]
        public string[] EngineEnvironment { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Read-only on purpose. Saving a custom agent through Unified Settings runs a real ACP handshake
        /// and auto-discovers the agent's capabilities; a page writing the array straight to config would
        /// skip that silently and store an entry that looks right and may not work. Better to send the
        /// user to the file than to offer an editor that quietly does less.
        /// </summary>
        [Category("Custom and shared")]
        [DisplayName("Custom ACP agents")]
        [Description("Extra ACP-over-stdio backends. Edit these in config.json (Extensions > Code Wicket > " +
                     "Settings…), where each entry is validated with a real ACP handshake and its " +
                     "capabilities discovered — which this page cannot do.")]
        [ReadOnly(true)]
        public string CustomAcpAgents { get; private set; } = string.Empty;

        protected override void ReadFrom(ExtensionConfig c)
        {
            KiroEnabled = c.KiroEnabled;
            KiroCliPath = c.KiroCliPath ?? string.Empty;
            KiroAgentEngine = c.KiroAgentEngine ?? string.Empty;
            ClaudeCodeEnabled = c.ClaudeCodeEnabled;
            ClaudeAcpPath = c.ClaudeAcpPath ?? string.Empty;
            EngineEnvironment = ToLines(c.EngineEnvironment);
            CustomAcpAgents = c.CustomAcpAgents.Length == 0
                ? "(none — edit config.json to add one)"
                : string.Join(", ", c.CustomAcpAgents.Select(a => a.ProviderId));
        }

        protected override void WriteTo(ExtensionConfig c)
        {
            c.KiroEnabled = KiroEnabled;
            c.KiroCliPath = string.IsNullOrWhiteSpace(KiroCliPath) ? null : KiroCliPath.Trim();
            c.KiroAgentEngine = string.IsNullOrWhiteSpace(KiroAgentEngine) ? null : KiroAgentEngine.Trim();
            c.ClaudeCodeEnabled = ClaudeCodeEnabled;
            c.ClaudeAcpPath = string.IsNullOrWhiteSpace(ClaudeAcpPath) ? null : ClaudeAcpPath.Trim();
            c.EngineEnvironment = FromLines(EngineEnvironment);
            // CustomAcpAgents is deliberately not written back — see the property's remarks.
        }
    }

    /// <summary>Permissions: the mode, and the four rule lists.</summary>
    [Guid(SettingsPageIds.Permissions)]
    public sealed class PermissionsOptionPage : CodeWicketOptionPage
    {
        [Category("Permissions")]
        [DisplayName("Permission mode")]
        [Description("How the agent's actions are approved. A cumulative ladder: each level auto-approves " +
                     "everything below it plus one more risk tier.")]
        public PermissionMode PermissionMode { get; set; } = PermissionMode.Prompt;

        [Category("Permissions")]
        [DisplayName("Allowed commands")]
        [Description("Commands to auto-approve without prompting, one per line. A pattern must match the " +
                     "whole command: 'git status' allows exactly that; 'git *' allows any git command. " +
                     "* matches anything and ? one character; write [*] or [?] for the character itself. A " +
                     "rule with a wildcard never applies to a command line containing a shell operator " +
                     "(& | ; > < ( ` or a line break) — that always asks.")]
        public string[] AllowedCommands { get; set; } = Array.Empty<string>();

        [Category("Permissions")]
        [DisplayName("Always-prompt (sensitive) commands")]
        [Description("Patterns (e.g. 'rm -rf', 'git push --force') for sensitive commands that must ALWAYS " +
                     "be reviewed — even under an auto-approve mode. Matched anywhere in the command. A " +
                     "match always shows the permission banner, flagged, and needs an extra confirm. Not a " +
                     "silent block — every match is reviewable. Keep this list short and high-signal.")]
        public string[] AlwaysPromptCommands { get; set; } = Array.Empty<string>();

        [Category("Permissions")]
        [DisplayName("Allowed edit paths")]
        [Description(@"Absolute file paths whose edits are auto-approved without prompting, one per line; " +
                     @"* matches anything (e.g. C:\repo\src\*).")]
        public string[] AllowedPaths { get; set; } = Array.Empty<string>();

        [Category("Permissions")]
        [DisplayName("Allowed MCP tools")]
        [Description("MCP tools auto-approved without prompting, one server/tool per line — for example " +
                     "code-wicket/build_solution for this extension's own tools, or github/create_issue " +
                     "for another server's. A bare tool name is taken as this extension's own and gains the " +
                     "server when used. Exact names, not globs — a rule names one tool. A sensitive-command " +
                     "pattern still forces a prompt, whatever is listed here.")]
        public string[] AllowedTools { get; set; } = Array.Empty<string>();

        protected override void ReadFrom(ExtensionConfig c)
        {
            PermissionMode = PermissionModeParser.Parse(c.DefaultPermissionMode);
            AllowedCommands = OrEmpty(c.AllowedCommands);
            AlwaysPromptCommands = OrEmpty(c.AlwaysPromptCommands);
            AllowedPaths = OrEmpty(c.AllowedPaths);
            AllowedTools = OrEmpty(c.AllowedTools);
        }

        protected override void WriteTo(ExtensionConfig c)
        {
            c.DefaultPermissionMode = PermissionMode.ToString();
            c.AllowedCommands = OrEmpty(AllowedCommands);
            c.AlwaysPromptCommands = OrEmpty(AlwaysPromptCommands);
            c.AllowedPaths = OrEmpty(AllowedPaths);
            // Stored as typed. The policy canonicalises every entry as it builds its rule set
            // (PolicyPermissionHandler.SetToolPolicy -> IdeMcpServer.TryNormalizeToolRule), so a bare
            // name still gains our server and a foreign one keeps its own (issue #129).
            c.AllowedTools = OrEmpty(AllowedTools);
        }
    }

    /// <summary>Advanced: the tools, attachment storage, and the diagnostic tees.</summary>
    [Guid(SettingsPageIds.Advanced)]
    public sealed class AdvancedOptionPage : CodeWicketOptionPage
    {
        [Category("Tools")]
        [DisplayName("Enable the run_command tool (VS developer environment)")]
        [Description("Let the agent run command lines in the Visual Studio Developer environment (msbuild, " +
                     "vstest.console, signing tools on PATH). On by default: every permission mode except " +
                     "'AcceptAll' asks before each command, so nothing runs without you seeing it. Turn it " +
                     "off to hide the tool from the agent entirely. Applies when the chat window next opens.")]
        public bool RunCommandEnabled { get; set; } = true;

        [Category("Tools")]
        [DisplayName("Mirror commands to a terminal pane")]
        [Description("Show agent command output in a live 'Code Wicket Terminal' pane (as well as in the " +
                     "chat). Experimental — rides a Visual Studio internal service and switches itself off " +
                     "for the session if that service misbehaves. Applies immediately.")]
        public bool TerminalMirrorEnabled { get; set; }

        [Category("Storage")]
        [DisplayName("Attachment storage limit (MB)")]
        [Description("How much attached content to keep on disk before the oldest is discarded. A " +
                     "conversation reopened after that shows the attachment's name without its picture; " +
                     "exporting a conversation embeds its images permanently. Applies when the chat window " +
                     "next opens.")]
        public int AttachmentStorageLimitMb { get; set; } = AttachmentStore.DefaultLimitMb;

        [Category("Diagnostics")]
        [DisplayName("Log agent protocol frames (acp.log)")]
        [Description("Raw ACP JSON frames between the engine and the agent CLI. Use when a backend's reply " +
                     "is missing or malformed.")]
        public bool LogAcpFrames { get; set; }

        [Category("Diagnostics")]
        [DisplayName("Log engine channel bytes (engine-channel.log)")]
        [Description("Raw JSON-RPC bytes between the Visual Studio shell and the engine process, for " +
                     "diagnosing stream corruption. Contains your prompts, the agent's replies, file " +
                     "contents and attached images, unredacted — treat it as conversation content.")]
        public bool LogEngineChannel { get; set; }

        [Category("Diagnostics")]
        [DisplayName("Log chat rendering cost (render.log)")]
        [Description("What the chat pane spends drawing itself — currently one summary line per streamed " +
                     "reply, plus a header describing this machine. Turn on when the chat streams smoothly " +
                     "on one machine and stutters on another. Applies when the chat window next opens.")]
        public bool LogRendering { get; set; }

        protected override void ReadFrom(ExtensionConfig c)
        {
            RunCommandEnabled = c.RunCommandEnabled;
            TerminalMirrorEnabled = c.TerminalMirrorEnabled;
            // Clamped on the way OUT as well as in, so a hand-edited config.json shows the value that is
            // actually in force rather than the one it will not honour.
            AttachmentStorageLimitMb = Math.Max(AttachmentStore.MinimumLimitMb, c.AttachmentStorageLimitMb);
            LogAcpFrames = c.LogAcpFrames;
            LogEngineChannel = c.LogEngineChannel;
            LogRendering = c.LogRendering;
        }

        protected override void WriteTo(ExtensionConfig c)
        {
            c.RunCommandEnabled = RunCommandEnabled;
            c.TerminalMirrorEnabled = TerminalMirrorEnabled;
            c.AttachmentStorageLimitMb = Math.Max(AttachmentStore.MinimumLimitMb, AttachmentStorageLimitMb);
            c.LogAcpFrames = LogAcpFrames;
            c.LogEngineChannel = LogEngineChannel;
            c.LogRendering = LogRendering;
        }
    }
}
