namespace CodeWicket.Core
{
    /// <summary>
    /// The product's display name, centralized so every user-visible surface (tool window caption,
    /// dialog titles, diff headers, standalone host title) reads from one place. Change it here to
    /// rebrand every displayed occurrence at once.
    /// </summary>
    /// <remarks>
    /// This covers strings rendered at runtime. A few build-time manifests carry the name literally
    /// and cannot reference this constant: the VSIX <c>source.extension.vsixmanifest</c> DisplayName,
    /// the VSCT <c>ButtonText</c>, and the unified-settings <c>CodeWicket.registration.json</c>
    /// title. Update those by hand when rebranding. On-disk paths are NOT among them: they resolve
    /// through <see cref="StorageFolderName"/> and <see cref="StoragePaths"/>, and renaming that
    /// constant orphans the user's config, saved conversations and attachments — so it travels with a
    /// migration, never on its own.
    /// </remarks>
    public static class Branding
    {
        /// <summary>The product's display name.</summary>
        public const string ProductName = "Code Wicket";

        /// <summary>
        /// The lockup tagline: the one messaging line written for a human reading the product's
        /// own chrome, rather than for search or for repetition.
        /// </summary>
        /// <remarks>
        /// One of four messaging lines in <c>docs/engineering/BRANDING.md</c>, each with a different job.
        /// This is the one whose job is to sit under the wordmark for a person — the others carry
        /// search terms, name the agents, or repeat as a drumbeat, and none of that belongs in a
        /// dialog someone opened to read a version number. Kept a <c>const</c> deliberately: the
        /// About dialog is answerable when our assemblies have failed to load, and only an inlined
        /// constant preserves that.
        /// </remarks>
        public const string Tagline = "The way in for agents.";
        /// <summary>
        /// Caption of the chat tool window. Equal to the product name: there is one tool window, and
        /// multi-tab (#150) puts tabs INSIDE it rather than adding a second surface, so a trailing
        /// noun disambiguates against nothing on a tab strip where width is the scarce thing.
        /// <para>
        /// The MENU label is deliberately not this. That command is placed both under View and inside
        /// our own Extensions menu, where the product name alone reads as "Code Wicket > Code Wicket",
        /// so the .vsct keeps "Code Wicket Chat" - one string serving two contexts, which is exactly
        /// why this constant is separate rather than shared with it.
        /// </para>
        /// </summary>
        public const string ChatWindowName = ProductName;

        /// <summary>
        /// Caption of the VS terminal pane the agent's command output is mirrored into. Lives here
        /// rather than beside the mirror because the unified-settings manifest quotes it back to the
        /// user verbatim (the "Mirror commands to a terminal pane" description) — two copies that
        /// must agree, and did drift.
        /// </summary>
        public const string TerminalPaneName = ProductName + " Terminal";

        /// <summary>
        /// The single folder name under <c>%APPDATA%</c> and <c>%LOCALAPPDATA%</c> holding everything
        /// this product keeps on disk. Resolve paths through <see cref="StoragePaths"/>; this bare
        /// constant is for the one caller that cannot afford to load a type.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT derived from <see cref="ProductName"/>. A display name is free to change
        /// and this is not: it names the user's config, saved conversations and attachments, so
        /// changing it orphans them. The two happen to be renamed together right now; tying them
        /// would make every future display tweak a silent data migration.
        /// </remarks>
        public const string StorageFolderName = "code-wicket";

        /// <summary>
        /// The per-project settings folder a repository may check in, alongside <c>.kiro</c> and
        /// <c>.claude</c> - configuration that applies whichever backend is active (issue #59). Found
        /// by <c>ProjectSettingsLocator</c>, which is a SEPARATE search from
        /// <c>WorkspaceRootLocator</c>'s: this folder is never a workspace marker.
        /// </summary>
        /// <remarks>
        /// <b>Renaming this is worse than renaming <see cref="StorageFolderName"/>, not better.</b>
        /// That constant names files under the user's own profile, which we could migrate. This one
        /// names files in THEIR repositories, under version control: a rename lands as a diff in every
        /// consumer's working tree, on a branch they did not ask to touch, and there is no migration we
        /// can run on a machine we have never seen. Treat it as permanent.
        /// <para>
        /// Hyphenated to match <see cref="StorageFolderName"/> and the MCP server name, which are the
        /// other two places this product's name appears ON DISK in front of a user. The unhyphenated
        /// spelling elsewhere (telemetry event names, icon assets, the VSIX identity, the .NET
        /// namespaces) is for identifiers nobody reads.
        /// </para>
        /// <para>
        /// Deliberately NOT derived as <c>"." + StorageFolderName</c>, though it now looks like it
        /// could be. Not for appearance's sake: the two have different permanence, and this is the more
        /// permanent of them, so coupling it to the migratable one points the dependency the wrong way.
        /// </para>
        /// </remarks>
        public const string ProjectSettingsFolderName = ".code-wicket";

        /// <summary>The settings file inside <see cref="ProjectSettingsFolderName"/>.</summary>
        /// <remarks>
        /// Deliberately NOT <c>config.json</c>, which is the user's own global file
        /// (<c>%APPDATA%\code-wicket\config.json</c>). The whole security story of the project file is
        /// that the two are DIFFERENT schemas - the global one may grant permissions and the repo one
        /// may never - so they must not share a name that invites copying one over the other.
        /// </remarks>
        public const string ProjectSettingsFileName = "settings.json";

        /// <summary>The steering folder inside <see cref="ProjectSettingsFolderName"/>.</summary>
        /// <remarks>
        /// <b>Nothing reads its CONTENTS yet</b> - cross-agent steering is issue #59's deferred second
        /// part. It is named here anyway because the locator's content contract depends on it: a
        /// <c>.code-wicket</c> counts as real when it holds <see cref="ProjectSettingsFileName"/> OR a
        /// non-empty steering folder, and that rule has to be fixed BEFORE either half ships.
        /// <para>
        /// Defined later instead, the same tree would resolve to a different folder across two releases
        /// under nearest-wins: a user with a steering-only folder beside the solution and a settings
        /// file at the repository root would silently change which file governs, on upgrade, with
        /// nothing failing. The resolver's answer must not move under people.
        /// </para>
        /// </remarks>
        public const string ProjectSteeringFolderName = "steering";

        /// <summary>
        /// What we call ourselves to the agent CLI: ACP's <c>clientInfo.name</c>, sent on
        /// <c>initialize</c> by both the live session and the settings-page probe.
        /// </summary>
        /// <remarks>
        /// Here because those two build the handshake separately - one through the typed record, one
        /// as hand-written JSON so it works on net472 without the StreamJsonRpc plumbing - and a
        /// comment asking the second to match the first is not a mechanism. It crosses a wire to a
        /// program we did not write, so it is deliberately NOT derived from
        /// <see cref="StorageFolderName"/> or <see cref="ProductName"/>: the three answer different
        /// questions and only this one is read by a backend.
        /// </remarks>
        public const string AcpClientName = "code-wicket";

        /// <summary>The version reported beside <see cref="AcpClientName"/>. Same two callers.</summary>
        public const string AcpClientVersion = "0.1";

        /// <summary>
        /// File name of the out-of-process engine executable, spelled once for the five callers
        /// that resolve it: the VSIX (bundled beside its own assembly, and again in the About
        /// dialog), the Desktop host's dev-only probe, and two Console proofs.
        /// </summary>
        /// <remarks>
        /// A <c>const</c>, so it inlines at compile time and the About dialog can name the engine
        /// without loading anything - the same property that lets it read <see cref="ProductName"/>
        /// while reporting a missing-dependency startup failure.
        /// </remarks>
        public const string EngineExeName = "CodeWicket.Engine.exe";
    }
}
