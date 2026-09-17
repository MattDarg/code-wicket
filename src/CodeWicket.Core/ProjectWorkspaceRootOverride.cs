using System;
using System.IO;
using System.Security;
using CodeWicket.Core.Ide;

namespace CodeWicket.Core
{
    /// <summary>The outcome of validating a repository's <c>agentWorkspaceRoot</c>.</summary>
    /// <param name="Root">The directory to run in, or null when the value was not applied.</param>
    /// <param name="Reason">Always set, and always written to <c>engine.log</c>.</param>
    /// <param name="Issue">The sentence the USER sees, or null when there is nothing worth saying to
    /// them. Separate from <paramref name="Reason"/> deliberately: a value refused because the user's
    /// own scope setting forbids it is a log line and nothing more, since from their seat nothing
    /// surprising happened.</param>
    public sealed record ProjectRootOverride(string? Root, string Reason, ProjectSettingsIssue? Issue)
    {
        /// <summary>No value was set. Not a problem, and not reported anywhere.</summary>
        public static readonly ProjectRootOverride NotSet = new(null, "no agentWorkspaceRoot set", null);

        public bool Applied => Root is not null;
    }

    /// <summary>
    /// Validates the one honoured key that can move where the agent runs (issue #59).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists because <see cref="WorkspaceRootLocator"/>'s upward walk is a GUESS - a good one, but
    /// bounded by a repository marker that is sometimes the wrong boundary (a submodule, a nested
    /// repository, a monorepo whose workspace is not its checkout root). When it guesses wrong every
    /// failure is silent: steering, workspace MCP and agent configs all quietly fail to load, and the
    /// backend's session listing reads a different store, which comes back as an empty list
    /// indistinguishable from "you have no conversations". There is no other escape hatch -
    /// <see cref="AgentWorkspaceScope"/> only narrows, and the user's <c>config.json</c> is per-user,
    /// so a single path there is wrong the moment a second solution is open.
    /// </para>
    /// <para>
    /// <b>The value is relative, and refused outright when it is not.</b> An absolute path in a
    /// checked-in file is broken on every machine but its author's, and honouring one is how a
    /// repository comes to name a drive root. Relative also survives the two things that actually
    /// happen to repositories: being cloned somewhere else, and having a parent folder renamed.
    /// </para>
    /// <para>
    /// <b>And it is CONTAINED, not merely bounded by a distance.</b> A relative value still describes
    /// a route, so what has to hold is that the route stays inside a directory the project itself sits
    /// in: every ancestor it is measured against must be one a workspace may live in (see
    /// <see cref="TryFindContainingAncestor"/>), and no step of it may be a link (see
    /// <see cref="LinkBelow"/>). A distance alone let a checked-in file name the user's own
    /// <c>.ssh</c> directory, three levels up from a checkout under the profile - which is a profile
    /// DESCENDANT, so the refusal that names the profile never saw it.
    /// </para>
    /// </remarks>
    public static class ProjectWorkspaceRootOverride
    {
        /// <summary>
        /// Resolves <paramref name="value"/> against <paramref name="anchorDirectory"/>.
        /// </summary>
        /// <param name="value">The value as written, or null/blank for "not set".</param>
        /// <param name="anchorDirectory">
        /// <b>The directory CONTAINING the settings folder</b> - not the solution root.
        /// <para>
        /// This is the only anchor a human editing the file assumes, and the only one stable under the
        /// things that happen to repositories. Measured from the solution root instead, <c>..</c> would
        /// mean a different directory depending on how deep the solution happens to sit below the
        /// settings folder - so moving a solution one level down would silently repoint the override,
        /// destroying the exact survivability the relativity exists for.
        /// </para>
        /// </param>
        /// <param name="scope">The user's own setting. <see cref="AgentWorkspaceScope.SolutionOnly"/>
        /// refuses any override - see the remarks on that branch below.</param>
        /// <param name="settingsFilePath">Named in the reason so a notice can point at the file.</param>
        public static ProjectRootOverride Resolve(
            string? value,
            string? anchorDirectory,
            AgentWorkspaceScope scope,
            string? settingsFilePath = null)
        {
            if (string.IsNullOrWhiteSpace(value))
                return ProjectRootOverride.NotSet;

            var raw = value!.Trim();

            // The user's setting wins, and it must: this is the flagship repo-honourable key, so if it
            // could overrule an explicit user choice then the claim that a checked-in file only ever
            // DESCRIBES would fail on its own showcase. No user-facing issue - from their seat nothing
            // surprising happened, the working directory is the solution folder exactly as they asked,
            // and a notice every session in a repository whose author set this would be pure noise.
            if (scope == AgentWorkspaceScope.SolutionOnly)
            {
                return new ProjectRootOverride(
                    null,
                    $"agentWorkspaceRoot '{raw}' refused: the workspace scope setting is Solution folder only",
                    null);
            }

            if (AgentPath.IsRooted(raw))
            {
                return Refused(raw, "it must be a relative path. This file travels with the project, "
                                    + "and an absolute path is only correct on the machine it was "
                                    + "written on.");
            }

            if (string.IsNullOrWhiteSpace(anchorDirectory))
                return Refused(raw, "there is no directory to measure it from.");

            string resolved;
            try
            {
                // Legal here, unlike the general case the #54/#62 rule is about: the anchor is a real
                // absolute directory, so this never falls back to the PROCESS working directory (which
                // in the VS host is devenv's install folder).
                resolved = Path.GetFullPath(Path.Combine(anchorDirectory!, raw))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                           or PathTooLongException or SecurityException)
            {
                return Refused(raw, "it is not a usable path.");
            }

            if (resolved.Length == 0 || (resolved.Length == 2 && resolved[1] == ':'))
                resolved += Path.DirectorySeparatorChar;

            bool exists;
            try { exists = Directory.Exists(resolved); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                exists = false;
            }

            // We do not create it. A directory that is not there cannot be a workspace, and inventing
            // one would manufacture an empty workspace the agent then reports nothing from.
            if (!exists)
                return Refused(raw, NotFound(raw, resolved, anchorDirectory!));

            if (WorkspaceRootLocator.IsRefusedAsWorkspace(resolved))
            {
                return Refused(raw, $"'{resolved}' is the user profile or a drive root, "
                                    + "which is never a workspace.");
            }

            if (!TryFindContainingAncestor(anchorDirectory!, resolved, out var ancestor, out var blocked))
            {
                // Two different refusals, deliberately not one message: "it is too far out" is an
                // author's mistake to correct, "it reaches out through the profile" is a repository
                // asking for something it may never have.
                return blocked is null
                    ? Refused(raw, $"'{resolved}' is more than {WorkspaceRootLocator.MaxSearchDepth} "
                                   + "levels away from the project.")
                    : Refused(raw, $"'{resolved}' is outside the project: reaching it from "
                                   + $"'{anchorDirectory}' means going up through '{blocked}', which "
                                   + "is the user profile or a drive root and is never a workspace.");
            }

            // The containment above is decided on the path as WRITTEN, so the route it names has to be
            // the route the operating system would take.
            if (LinkBelow(ancestor, resolved) is { } throughALink)
                return Refused(raw, throughALink);

            var from = string.IsNullOrEmpty(settingsFilePath)
                ? "this project's settings"
                : settingsFilePath!;

            return new ProjectRootOverride(
                resolved, $"agentWorkspaceRoot '{raw}' from {from}", null);
        }

        /// <summary>
        /// Why the resolved directory was not there, said so the reader can correct it - which for
        /// this key means saying what the path was measured FROM.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The anchor is the thing people get wrong, and reasonably so.</b> Nearly every config
        /// format measures relative paths from the file they are written in - tsconfig, .editorconfig,
        /// .gitmodules - and this one measures from the folder CONTAINING the settings folder instead,
        /// so that a user's paths stay independent of our own layout. That is a defensible choice and
        /// an unguessable one, so the refusal has to teach it rather than merely report a missing
        /// directory.
        /// </para>
        /// <para>
        /// When the file-relative reading WOULD have resolved, that is said outright and the correct
        /// value offered verbatim: it is the one case where we know exactly what the author meant.
        /// The dangerous version of this confusion is the silent one - a value that resolves under
        /// BOTH readings applies the wrong directory with nothing reported - which is precisely why
        /// the message is worth its length in the case we can see.
        /// </para>
        /// </remarks>
        private static string NotFound(string value, string resolved, string anchor)
        {
            var message = $"'{resolved}' does not exist. A relative agentWorkspaceRoot is measured from "
                          + $"'{anchor}' - the folder containing {Branding.ProjectSettingsFolderName} - "
                          + "and not from the settings file itself.";

            // The file-relative reading: what the value would have meant measured from the settings
            // folder. Offered only when it actually resolves, or this becomes a guess dressed as help.
            try
            {
                var settingsFolder = Path.Combine(anchor, Branding.ProjectSettingsFolderName);
                var fileRelative = Path.GetFullPath(Path.Combine(settingsFolder, value));
                if (Directory.Exists(fileRelative))
                {
                    var suggestion = Branding.ProjectSettingsFolderName + "/" + value.Replace('\\', '/');
                    message += $" '{fileRelative}' does exist - if that is what you meant, write it as "
                               + $"\"{suggestion}\".";
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                           or PathTooLongException or SecurityException)
            {
                // The suggestion is a courtesy; never let building it change the refusal.
            }

            return message;
        }

        /// <summary>
        /// The nearest directory at or above <paramref name="anchor"/> that <paramref name="resolved"/>
        /// sits inside, searched up to <see cref="WorkspaceRootLocator.MaxSearchDepth"/> levels.
        /// </summary>
        /// <param name="anchor">The directory containing the settings folder.</param>
        /// <param name="resolved">The directory the value named, already made absolute.</param>
        /// <param name="ancestor">The directory holding both, when this returns true.</param>
        /// <param name="blocked">The ancestor the walk refused to stand in, or null when it simply ran
        /// out of levels.</param>
        /// <remarks>
        /// <para>
        /// Measuring to the common ancestor rather than counting <c>..</c> segments is what makes a
        /// sideways value work: <c>../../elsewhere</c> goes up two and back down one, and what should
        /// bound it is how far OUT of the project it reached, which is two - not the three segments it
        /// happens to be written with.
        /// </para>
        /// <para>
        /// <b>Every ancestor it passes must be one a workspace may live in, and THAT is what contains
        /// the value</b> - the distance never did. From a checkout at
        /// <c>&lt;profile&gt;\source\repos\App</c>, <c>../../../.ssh</c> is three levels out, the
        /// directory exists, and it is neither the user profile itself nor a drive root, so every
        /// guard this key had passed it and the agent's working directory became the user's private
        /// key directory. What is wrong with the value is the ROUTE: it has to stand in the profile to
        /// get there. This walk is the same shape as <see cref="WorkspaceRootLocator.Resolve"/>'s and
        /// <see cref="ProjectSettingsLocator"/>'s, refusing at the same place on the same predicate,
        /// and the refusal is asked BEFORE the containment test at each level so a value can never be
        /// measured against a directory the walk was not allowed to reach.
        /// </para>
        /// <para>
        /// The anchor itself (level 0) carries no such guard, deliberately: a value pointing further IN
        /// moves the agent nowhere the project is not already, which is why a solution opened inside
        /// the profile still runs there - the same exception <see cref="ProjectSettingsLocator"/> makes
        /// for the solution folder before its own walk starts.
        /// </para>
        /// </remarks>
        private static bool TryFindContainingAncestor(
            string anchor, string resolved, out string ancestor, out string? blocked)
        {
            ancestor = anchor;
            blocked = null;

            if (Contains(anchor, resolved))
                return true;

            var current = anchor;
            for (var up = 1; up <= WorkspaceRootLocator.MaxSearchDepth; up++)
            {
                DirectoryInfo? parent;
                try { parent = Directory.GetParent(current); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    return false;
                }

                if (parent is null)
                    return false;

                current = parent.FullName;

                if (WorkspaceRootLocator.IsRefusedAsWorkspace(current))
                {
                    blocked = current;
                    return false;
                }

                if (Contains(current, resolved))
                {
                    ancestor = current;
                    return true;
                }
            }

            return false;
        }

        // "At or under", in the one spelling both halves of the walk use.
        private static bool Contains(string directory, string candidate) =>
            WorkspaceRootLocator.SameRoot(candidate, directory) ||
            WorkspacePath.IsUnderRoot(candidate, directory);

        /// <summary>
        /// Why the route from <paramref name="ancestor"/> down to <paramref name="resolved"/> cannot be
        /// shown to stay inside the project, or null when every step of it is an ordinary directory.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The containment test is LEXICAL, and a link is what makes a lexical answer wrong.</b>
        /// <see cref="Path.GetFullPath(string)"/> resolves <c>..</c> textually and never touches the
        /// filesystem, so a junction that arrived with the project - <c>workspace</c> pointing at
        /// <c>~/.ssh</c> - reads as a plain child of it and satisfies every check above this one. It is
        /// also the cheapest thing for an archive to carry, which is why no distance ever bound it.
        /// </para>
        /// <para>
        /// <b>Only the steps BELOW the containing ancestor are examined, and that is the whole
        /// distinction.</b> A link above it - the <c>C:\src</c> to <c>D:\src</c> junction on a
        /// developer's own box - carries the project and this value together and moves nothing out of
        /// reach. A link below it is the value naming somewhere the path it is written as does not.
        /// </para>
        /// <para>
        /// Refused rather than followed. Reading where a link points needs an API the net472 half of
        /// this assembly does not have, and this file must mean one thing in both; refusing costs a
        /// repository a route it can write as a path instead, while following one silently would cost
        /// the guard above its meaning.
        /// </para>
        /// </remarks>
        private static string? LinkBelow(string ancestor, string resolved)
        {
            // Bounded rather than "walk until it reaches the ancestor": <resolved> is inside
            // <ancestor> by construction, and a loop that trusts a construction is a loop that spins
            // the day it stops holding. Far deeper than any real workspace route.
            const int MaxSteps = 64;

            var current = resolved;
            for (var step = 0; step < MaxSteps; step++)
            {
                if (WorkspaceRootLocator.SameRoot(current, ancestor))
                    return null;

                bool isLink;
                try
                {
#if NET
                    // Set for a junction or a symbolic link and for nothing else. The coarser
                    // ReparsePoint attribute answers a different question - a OneDrive placeholder
                    // directory carries one and is not a link - and refusing those would refuse
                    // ordinary projects.
                    isLink = new DirectoryInfo(current).LinkTarget is not null;
#else
                    // net472 has no link API, so it asks the coarsest question that is still safe:
                    // wrong in the direction that refuses more, never in the direction that follows a
                    // link. Nothing on this framework resolves an override today - the engine that
                    // does is net10.0 - so the coarseness costs nothing in the product.
                    isLink = (new DirectoryInfo(current).Attributes & FileAttributes.ReparsePoint) != 0;
#endif
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or SecurityException or ArgumentException)
                {
                    return $"'{current}' could not be read, so '{resolved}' cannot be shown to be "
                           + "inside the project.";
                }

                if (isLink)
                {
                    return $"'{resolved}' is reached through '{current}', which is a link. A link can "
                           + "point anywhere on the machine, so the path as written does not say where "
                           + "the agent would run.";
                }

                DirectoryInfo? parent;
                try { parent = Directory.GetParent(current); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    return $"'{current}' could not be read, so '{resolved}' cannot be shown to be "
                           + "inside the project.";
                }

                if (parent is null)
                    break;

                current = parent.FullName;
            }

            return $"'{resolved}' could not be traced back to '{ancestor}', so it cannot be shown to "
                   + "be inside the project.";
        }

        private static ProjectRootOverride Refused(string value, string why) => new(
            null,
            $"agentWorkspaceRoot '{value}' refused: {why}",
            ProjectSettingsReport.ValueRefused(ProjectSettingsSchema.AgentWorkspaceRootKey, why));
    }
}
