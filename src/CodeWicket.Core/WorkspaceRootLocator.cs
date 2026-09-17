using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

namespace CodeWicket.Core
{
    /// <summary>
    /// How far above the solution folder we may look for the backend's workspace marker. Binary by
    /// design: once the repository ceiling and the profile/drive refusal in
    /// <see cref="WorkspaceRootLocator"/> are in place, a numeric depth almost never binds (the
    /// realistic walk terminates at the repository root within one to three levels) — so the depth cap
    /// stays an internal constant and this is the only exposed dial.
    /// </summary>
    public enum AgentWorkspaceScope
    {
        /// <summary>Never look above the solution folder (the agent's cwd is always the solution root).</summary>
        SolutionOnly,

        /// <summary>Look upwards for the backend's workspace marker, stopping at the repository root.</summary>
        RepositoryRoot,
    }

    /// <summary>Parses an <see cref="AgentWorkspaceScope"/> name tolerantly; anything unknown (or
    /// absent) is <see cref="AgentWorkspaceScope.RepositoryRoot"/>, the default.</summary>
    public static class AgentWorkspaceScopeParser
    {
        public static AgentWorkspaceScope Parse(string? value) =>
            Enum.TryParse<AgentWorkspaceScope>(value, ignoreCase: true, out var scope)
                ? scope
                : AgentWorkspaceScope.RepositoryRoot;
    }

    /// <summary>
    /// A directory that marks a backend's workspace root — e.g. Kiro's <c>.kiro</c>. Declared as data
    /// by the provider config so the locator stays backend-agnostic.
    /// </summary>
    /// <param name="DirectoryName">The marker directory's name, e.g. <c>.kiro</c>.</param>
    /// <param name="RequiredAnyOf">Entries the marker must contain for it to count (e.g. Kiro's
    /// <c>steering</c>/<c>specs</c>/…). Guards against a stray empty marker directory capturing the
    /// agent's working directory. Empty = the marker only has to be non-empty.</param>
    public sealed record WorkspaceMarker(string DirectoryName, IReadOnlyList<string> RequiredAnyOf)
    {
        public WorkspaceMarker(string directoryName) : this(directoryName, Array.Empty<string>()) { }
    }

    /// <summary>The outcome of a <see cref="WorkspaceRootLocator.Resolve"/> call.</summary>
    /// <param name="Root">The directory the agent should run in. Always a real, usable path — the
    /// solution root when nothing was found, so callers never special-case a miss.</param>
    /// <param name="Widened">True when <paramref name="Root"/> is above the solution folder.</param>
    /// <param name="MarkerPath">The marker directory that decided it, or null when nothing was found.</param>
    /// <param name="Reason">Human-readable account of how the walk ended, for the transcript notice
    /// and engine.log — so "my steering isn't loading" is answerable from a collected log.</param>
    public sealed record WorkspaceRootResult(string Root, bool Widened, string? MarkerPath, string Reason);

    /// <summary>
    /// Resolves the directory an agent backend should treat as its workspace — the ACP
    /// <c>session/new.cwd</c> and the CLI process's working directory.
    /// <para>
    /// This exists because a backend can resolve its workspace <em>strictly</em> from cwd with no
    /// upward walk of its own. Probed against kiro-cli 2.13.0 (2026-07-25): with
    /// <c>repo/.kiro/steering</c> present, a run from <c>repo/</c> applies the steering and a run from
    /// <c>repo/src/solution/</c> does not. It is not only steering — <c>kiro-cli agent list</c> reports
    /// its workspace agent scope as <c>&lt;cwd&gt;\.kiro\agents</c> and workspace MCP is
    /// <c>&lt;cwd&gt;\.kiro\settings\mcp.json</c>, so cwd <em>is</em> Kiro's workspace root. A solution
    /// that lives below the folder holding <c>.kiro</c> therefore silently loses all of it (issue #54).
    /// </para>
    /// <para>
    /// The guardrails are the whole safety story, because the naive walk is actively dangerous:
    /// <c>~/.kiro</c> exists on <em>every</em> Kiro install (it is the global config directory), so an
    /// unbounded walk from <c>C:\Users\someone\source\…</c> terminates at <c>C:\Users\someone</c> and
    /// silently makes the user's profile folder the agent's workspace.
    /// </para>
    /// </summary>
    public static class WorkspaceRootLocator
    {
        /// <summary>
        /// Backstop for the case where no repository marker is ever found. Deliberately NOT a user
        /// setting: the repository ceiling and the profile/drive refusal below are what make the walk
        /// safe, and exposing a depth would invite "search harder" values that re-open the
        /// profile-capture hole. See <see cref="AgentWorkspaceScope"/>.
        /// </summary>
        public const int MaxSearchDepth = 6;

        // A directory holding one of these is the repository root: the ceiling of the walk. Matched as
        // either a directory or a file — a submodule/worktree checkout has `.git` as a *file*.
        private static readonly string[] RepositoryMarkers = { ".git", ".hg", ".svn" };

        /// <summary>
        /// Finds the workspace root for a backend, starting at <paramref name="solutionRoot"/> and
        /// walking up. The first marker found wins, so a marker beside the solution beats a parent's —
        /// matching the backends' own nearest-wins precedence.
        /// </summary>
        /// <param name="solutionRoot">The open solution/folder directory. Returned unchanged whenever
        /// nothing is found, so the caller's fallback is always this same path.</param>
        /// <param name="markers">The backend's workspace markers. Empty (Claude Code, custom agents)
        /// short-circuits to <paramref name="solutionRoot"/> with no I/O — Claude Code already walks up
        /// for <c>CLAUDE.md</c>/<c>.claude</c> itself, so moving its cwd would change behaviour it gets
        /// right today.</param>
        /// <param name="scope">The user setting; <see cref="AgentWorkspaceScope.SolutionOnly"/>
        /// short-circuits before any I/O.</param>
        public static WorkspaceRootResult Resolve(
            string? solutionRoot,
            IReadOnlyList<WorkspaceMarker>? markers,
            AgentWorkspaceScope scope = AgentWorkspaceScope.RepositoryRoot)
        {
            if (string.IsNullOrWhiteSpace(solutionRoot))
                return new WorkspaceRootResult(solutionRoot ?? string.Empty, false, null, "no workspace root");

            if (scope == AgentWorkspaceScope.SolutionOnly)
                return new WorkspaceRootResult(solutionRoot!, false, null, "solution folder only (by setting)");

            if (markers is null || markers.Count == 0)
                return new WorkspaceRootResult(solutionRoot!, false, null, "backend declares no workspace marker");

            string current;
            try
            {
                current = Path.GetFullPath(solutionRoot!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new WorkspaceRootResult(solutionRoot!, false, null, "workspace root is not a usable path");
            }

            // A drive root has no trailing-separator-free form ("C:" is not "C:\"), so restore it.
            if (current.Length == 0 || (current.Length == 2 && current[1] == ':'))
                current = solutionRoot!;

            // The solution folder itself: no widening involved, so none of the ancestor guards apply
            // (a solution opened directly in the profile folder would run there anyway).
            if (FindMarker(current, markers) is { } atSolution)
                return new WorkspaceRootResult(
                    current, false, atSolution, "workspace marker in the solution folder");

            if (HasRepositoryMarker(current))
                return new WorkspaceRootResult(
                    current, false, null, "the solution folder is the repository root");

            var userProfile = TryGetUserProfile();

            for (var depth = 1; depth <= MaxSearchDepth; depth++)
            {
                DirectoryInfo? parent;
                try { parent = Directory.GetParent(current); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    return new WorkspaceRootResult(solutionRoot!, false, null, "the folder above is unreadable");
                }

                if (parent is null)
                    return new WorkspaceRootResult(solutionRoot!, false, null, "reached the drive root");

                current = parent.FullName;

                // Refuse the user profile (and anything above it) outright: ~/.kiro is the backend's
                // GLOBAL config, not a workspace, and the backend loads it regardless of cwd. Also
                // refuses a drive/UNC root, which has no parent.
                if (IsProtected(current, userProfile))
                    return new WorkspaceRootResult(
                        solutionRoot!, false, null,
                        $"stopped below '{current}' (user profile or drive root is never a workspace)");

                if (FindMarker(current, markers) is { } found)
                    return new WorkspaceRootResult(current, true, found, $"workspace marker found at '{current}'");

                // Inclusive ceiling: this directory was checked for a marker above, and we stop here.
                if (HasRepositoryMarker(current))
                    return new WorkspaceRootResult(
                        solutionRoot!, false, null, $"stopped at the repository root '{current}'");
            }

            return new WorkspaceRootResult(
                solutionRoot!, false, null, $"search depth ({MaxSearchDepth}) reached");
        }

        /// <summary>
        /// Whether two resolved roots name the same directory. Case-insensitive and separator-agnostic,
        /// because a root reaches us from three places that spell it differently — a session captured
        /// one at start, the host holds the solution's, and <see cref="Resolve"/> produces a third — and
        /// the question every caller is really asking is "would a backend keyed by cwd read the same
        /// store?".
        /// <para>A null or empty root matches nothing, INCLUDING another null: "the root is unknown" is
        /// not evidence that two roots agree, and the one caller here acts on agreement.</para>
        /// </summary>
        public static bool SameRoot(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;

            static string Normalize(string path)
            {
                try { path = Path.GetFullPath(path); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                               or PathTooLongException or SecurityException)
                {
                    // Unresolvable, so compare what we were given. A malformed root is a real state
                    // (a UNC path on a disconnected share); it must not throw out of a comparison.
                }

                return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return string.Equals(Normalize(a!), Normalize(b!), StringComparison.OrdinalIgnoreCase);
        }

        // The marker's full path when <dir> holds a real one, else null. "Real" means the directory
        // exists AND carries workspace content — a stray empty .kiro must never capture the cwd.
        private static string? FindMarker(string dir, IReadOnlyList<WorkspaceMarker> markers)
        {
            foreach (var marker in markers)
            {
                if (string.IsNullOrWhiteSpace(marker?.DirectoryName))
                    continue;

                string path;
                try { path = Path.Combine(dir, marker!.DirectoryName); }
                catch (ArgumentException) { continue; }

                try
                {
                    if (!Directory.Exists(path))
                        continue;

                    if (marker!.RequiredAnyOf.Count == 0)
                    {
                        // No content contract declared: any entry at all counts.
                        using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                        if (entries.MoveNext())
                            return path;
                        continue;
                    }

                    foreach (var required in marker.RequiredAnyOf)
                    {
                        if (string.IsNullOrWhiteSpace(required))
                            continue;
                        var candidate = Path.Combine(path, required);
                        if (Directory.Exists(candidate) || File.Exists(candidate))
                            return path;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable candidate is simply not a match; the walk continues upwards.
                }
            }

            return null;
        }

        private static bool HasRepositoryMarker(string dir)
        {
            foreach (var marker in RepositoryMarkers)
            {
                try
                {
                    var path = Path.Combine(dir, marker);
                    if (Directory.Exists(path) || File.Exists(path))
                        return true;
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    // Treat an unreadable marker as absent — the walk's other ceilings still bound it.
                }
            }

            return false;
        }

        /// <summary>
        /// Whether <paramref name="dir"/> may NEVER be an agent workspace: the user profile, any
        /// ancestor of it, or a drive/UNC root.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Public because it is now the ONE answer to that question, shared with
        /// <c>ProjectSettingsLocator</c>'s independent search (issue #59) and with the validation of a
        /// repo-supplied <c>agentWorkspaceRoot</c>. It was private while there was one caller; a second
        /// copy of this predicate would drift, and the drift is both silent and catastrophic - the
        /// disaster it prevents is identical for every caller, because <c>~/.kiro</c> exists on every
        /// Kiro install and a <c>.code-wicket</c> accidentally created once in the profile folder is
        /// there forever.
        /// </para>
        /// <para>
        /// <paramref name="userProfile"/> is resolved here when not supplied. <see cref="Resolve"/>
        /// passes its own pre-resolved value instead, because it asks per level of the walk and the
        /// answer cannot change between them.
        /// </para>
        /// </remarks>
        public static bool IsRefusedAsWorkspace(string dir, string? userProfile = null) =>
            IsProtected(dir, userProfile ?? TryGetUserProfile());

        // True when <dir> is the user profile, an ancestor of it, or a drive/UNC root.
        private static bool IsProtected(string dir, string? userProfile)
        {
            try
            {
                if (Directory.GetParent(dir) is null)
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return true;
            }

            if (string.IsNullOrEmpty(userProfile))
                return false;

            // Ancestor-or-self: the profile path starts with this directory.
            return IsSameOrAncestorOf(dir, userProfile!);
        }

        private static bool IsSameOrAncestorOf(string candidate, string descendant)
        {
            var a = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var b = descendant.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;

            return b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string? TryGetUserProfile()
        {
            try
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrWhiteSpace(profile) ? null : Path.GetFullPath(profile);
            }
            catch
            {
                return null; // no profile resolvable: the repository ceiling and depth cap still bound the walk
            }
        }
    }
}
