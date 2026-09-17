using System;
using System.Collections.Generic;

namespace CodeWicket.Core
{
    /// <summary>
    /// What a repository is allowed to say about itself (issue #59) - the honoured half of a
    /// checked-in project settings file, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This type shares nothing with <c>ExtensionConfig</c>, and that is the security model rather
    /// than tidiness.</b> It is not a subset view of it, not a partial, not that class with
    /// <c>[JsonIgnore]</c> on the dangerous members. A file that grants privilege and travels with a
    /// project is a supply-chain vector: open someone else's solution and its file has pre-approved
    /// <c>curl … | sh</c>. The permission model's whole claim is that WE own every "always" and the
    /// user reviews every sensitive command, and a project-controlled grant walks straight around it.
    /// </para>
    /// <para>
    /// <b>The threat is that the file ARRIVED WITH THE PROJECT, not that it is under version
    /// control.</b> A clone is the common vector and the easiest one to picture, but a zip, a shared
    /// folder or a template scaffold carry it just as well - and we cannot tell the difference from
    /// here, since nothing tells us whether the file is tracked, ignored, or in a repository at all.
    /// So the refusals say "project settings" rather than "a checked-in file": asserting the stronger
    /// thing would be claiming something we never checked, and would read as plainly wrong to anyone
    /// who has the file deliberately ignored.
    /// </para>
    /// <para>
    /// Were this a projection of the user's own config, adding a member there for an unrelated reason
    /// would silently make it repo-settable, with nothing in the build, the tests or a review diff
    /// saying that the repository's authority had just been widened. Keeping the types disjoint means
    /// widening it takes an edit to THIS file, whose doc block is the argument against making one.
    /// </para>
    /// <para>
    /// Immutable, with a public constructor: the reader lives in another assembly, and the security
    /// property is about which keys are HONOURED, never about who may construct the result.
    /// </para>
    /// </remarks>
    public sealed class ProjectSettings
    {
        /// <summary>A file that supplied nothing - also what a malformed or missing file yields.</summary>
        public static readonly ProjectSettings Empty = new ProjectSettings(null);

        public ProjectSettings(string? agentWorkspaceRoot)
        {
            AgentWorkspaceRoot = agentWorkspaceRoot;
        }

        /// <summary>
        /// A <b>relative</b> path redirecting the agent's working directory, or null when the file did
        /// not set one. Verbatim as written - validation is
        /// <c>ProjectWorkspaceRootOverride</c>'s, so an invalid value can be REPORTED rather than
        /// silently becoming absent.
        /// </summary>
        public string? AgentWorkspaceRoot { get; }
    }

    /// <summary>
    /// Which keys a project settings file may carry, and which are named refusals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="RefusedKeys"/> is REPORTING, not defence.</b> None of those keys can do anything,
    /// because none of them is read - that is the reader's shape (it asks for the keys it wants, one
    /// at a time, and never deserializes the document into a type). This list exists so that an
    /// ATTEMPT is visible: a user who clones a repository that tried to grant itself permissions is
    /// told, rather than the line sitting there looking effective.
    /// </para>
    /// <para>
    /// The distinction is load-bearing and is pinned by a test, because a deny-list slowly becomes
    /// "the mechanism" in everyone's head - and the day someone adds a privileged key and forgets to
    /// extend the list would then be the day the whole security story fails. Removing an entry here
    /// must change only the message.
    /// </para>
    /// </remarks>
    public static class ProjectSettingsSchema
    {
        /// <summary>The relative working-directory redirect.</summary>
        public const string AgentWorkspaceRootKey = "agentWorkspaceRoot";

        /// <summary>Recognised and deliberately inert - see <see cref="KnownKeys"/>.</summary>
        public const string SchemaVersionKey = "schemaVersion";

        /// <summary>
        /// Keys the reader recognises. <see cref="SchemaVersionKey"/> is here so that writing one earns
        /// no spurious "unknown key" warning; nothing branches on its value, because there is no second
        /// version and a version field nothing reads is honest forward-compatibility rather than a
        /// promise we would have to keep.
        /// </summary>
        public static readonly IReadOnlyList<string> KnownKeys = new[]
        {
            AgentWorkspaceRootKey,
            SchemaVersionKey,
        };

        /// <summary>
        /// Keys that are ignored AND reported. Everything here either grants privilege
        /// (<c>allowedCommands</c>, <c>allowedPaths</c>, <c>allowedTools</c>, <c>permissionMode</c>),
        /// removes a review that would otherwise happen (<c>alwaysPromptCommands</c>), turns on a tool
        /// the user chose to leave off (<c>runCommandEnabled</c>), redirects the network or the trust
        /// store out from under every agent process (<c>engineEnvironment</c>), or is outright code
        /// execution from a cloned repository (<c>customAcpAgents</c>: an arbitrary CLI path plus args).
        /// </summary>
        public static readonly IReadOnlyList<string> RefusedKeys = new[]
        {
            "allowedCommands",
            "allowedPaths",
            "alwaysPromptCommands",
            "allowedTools",
            "permissionMode",
            "runCommandEnabled",
            "engineEnvironment",
            "customAcpAgents",
        };

        /// <summary>
        /// True when <paramref name="key"/> is one this reader honours.
        /// </summary>
        /// <remarks>
        /// Case-insensitive, deliberately, and the reason is sharper for <see cref="IsRefused"/>: a
        /// file writing <c>AllowedCommands</c> must still be REPORTED. Matching case-sensitively would
        /// make the report trivially evadable - which achieves nothing on its own, since nothing reads
        /// the key either way, but it would leave the user unwarned about a repository that had tried.
        /// </remarks>
        public static bool IsKnown(string? key) => Contains(KnownKeys, key);

        /// <summary>True when <paramref name="key"/> is a named refusal. See <see cref="IsKnown"/>.</summary>
        public static bool IsRefused(string? key) => Contains(RefusedKeys, key);

        private static bool Contains(IReadOnlyList<string> keys, string? key)
        {
            if (string.IsNullOrEmpty(key))
                return false;

            foreach (var candidate in keys)
                if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }
    }

    /// <summary>Why a project settings file did not fully apply.</summary>
    public enum ProjectSettingsIssueKind
    {
        /// <summary>A key nothing recognises - a typo, or one a newer build writes.</summary>
        UnknownKey,

        /// <summary>A key this file may never carry (<see cref="ProjectSettingsSchema.RefusedKeys"/>).</summary>
        RefusedKey,

        /// <summary>A recognised key whose value was the wrong JSON type.</summary>
        WrongType,

        /// <summary>The file could not be parsed at all, so none of it was applied.</summary>
        Malformed,

        /// <summary>A recognised key whose value was rejected by its own validation.</summary>
        ValueRefused,
    }

    /// <summary>
    /// One thing worth saying about a project settings file, and whether the USER needs to see it.
    /// </summary>
    /// <remarks>
    /// <b><see cref="ShowInTranscript"/> belongs to the KIND, not to the call site.</b> An unknown key
    /// must never reach the pane: a repository shared between two versions of this extension would
    /// otherwise warn its users on the older one every session, which is exactly the pressure that
    /// makes people stop reading notices. A refused key is the opposite - it is an attempt to grant
    /// privilege from a checked-in file, and it is the one signal here that must not be missable.
    /// Deciding that per call site would have the two drift apart the first time a caller was added.
    /// </remarks>
    public sealed class ProjectSettingsIssue
    {
        public ProjectSettingsIssue(ProjectSettingsIssueKind kind, string message)
        {
            Kind = kind;
            Message = message;
        }

        public ProjectSettingsIssueKind Kind { get; }

        /// <summary>A complete sentence, usable in the transcript and in <c>engine.log</c> alike.</summary>
        public string Message { get; }

        /// <summary>Whether this is worth interrupting the user with. See the type's own remarks.</summary>
        public bool ShowInTranscript => Kind != ProjectSettingsIssueKind.UnknownKey;

        public override string ToString() => Message;
    }

    /// <summary>What a project settings file supplied, and everything worth saying about it.</summary>
    public sealed class ProjectSettingsReadResult
    {
        public static readonly ProjectSettingsReadResult None =
            new ProjectSettingsReadResult(ProjectSettings.Empty, Array.Empty<ProjectSettingsIssue>());

        public ProjectSettingsReadResult(ProjectSettings settings, IReadOnlyList<ProjectSettingsIssue> issues)
        {
            Settings = settings;
            Issues = issues;
        }

        public ProjectSettings Settings { get; }

        public IReadOnlyList<ProjectSettingsIssue> Issues { get; }
    }

    /// <summary>
    /// The sentences said about a project settings file, in one place so they are unit-testable rather
    /// than string literals scattered across the engine and the view-model.
    /// </summary>
    public static class ProjectSettingsReport
    {
        /// <summary>
        /// A key that may never come from a repository. The sentence names the key, says plainly that
        /// nothing happened, and points at where the setting CAN be made - a user reading this has
        /// usually just cloned someone else's repository and needs to know both halves.
        /// </summary>
        public static ProjectSettingsIssue RefusedKey(string key) => new ProjectSettingsIssue(
            ProjectSettingsIssueKind.RefusedKey,
            $"Ignored '{key}' from this project's settings file: project settings can never grant "
            + "permissions, change the permission mode, enable tools, or set the engine environment. "
            + "Change it in Code Wicket's own settings if you meant to.");

        /// <summary>Log-only: a typo, or a key a newer build understands. Never an error.</summary>
        public static ProjectSettingsIssue UnknownKey(string key) => new ProjectSettingsIssue(
            ProjectSettingsIssueKind.UnknownKey,
            $"Ignored unrecognised key '{key}' in this project's settings file.");

        public static ProjectSettingsIssue WrongType(string key, string expected) => new ProjectSettingsIssue(
            ProjectSettingsIssueKind.WrongType,
            $"Ignored '{key}' in this project's settings file: expected {expected}.");

        /// <summary>
        /// The file could not be parsed. <b>Nothing is applied</b> - a half-applied file is a worse
        /// answer than none, because the half that landed is unpredictable from reading the file.
        /// </summary>
        public static ProjectSettingsIssue Malformed(string path, string detail) => new ProjectSettingsIssue(
            ProjectSettingsIssueKind.Malformed,
            $"This project's settings file could not be read, so none of it was applied ({path}): {detail}");

        public static ProjectSettingsIssue ValueRefused(string key, string reason) => new ProjectSettingsIssue(
            ProjectSettingsIssueKind.ValueRefused,
            $"Ignored '{key}' in this project's settings file: {reason}");
    }
}
