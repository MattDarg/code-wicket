using System;
using System.IO;
using System.Security;

namespace CodeWicket.Core
{
    /// <summary>
    /// Where a project's checked-in Code Wicket settings folder is, if it has one (issue #59).
    /// </summary>
    /// <param name="FolderPath">The settings directory, or null when none was found.</param>
    /// <param name="SettingsFilePath">Its settings file, or null when the folder has none. A folder
    /// found through its steering directory alone is a real hit with nothing to read yet.</param>
    /// <param name="SteeringPath">Its steering directory, or null when it has none. Issue #59's
    /// deferred second part reads this; today it only decides whether the folder counts.</param>
    /// <param name="Reason">How the search ended, for <c>engine.log</c> - so "my project settings
    /// aren't loading" is answerable from a collected log rather than being a mystery.</param>
    /// <remarks>
    /// Deliberately asymmetric with <see cref="WorkspaceRootResult"/>, which never returns null
    /// because a process must have a working directory and the solution root is always a usable
    /// fallback. Here <b>not-found is the normal state and there is nothing to fall back to</b>:
    /// inventing a location would be inventing configuration.
    /// </remarks>
    public sealed record ProjectSettingsLocation(
        string? FolderPath, string? SettingsFilePath, string? SteeringPath, string Reason)
    {
        /// <summary>The miss, carrying why.</summary>
        public static ProjectSettingsLocation None(string reason) => new(null, null, null, reason);

        /// <summary>True when a folder was found. Its settings file may still be absent.</summary>
        public bool Found => FolderPath is not null;
    }

    /// <summary>
    /// The bounded upward search for a project's settings folder - a SECOND, independent search that
    /// must never be merged with <see cref="WorkspaceRootLocator"/>'s (issue #59).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why they cannot be one search</b>, in ascending order of force. They answer different
    /// questions: "where must the CLI's working directory be for this backend to find its own config"
    /// against "which checked-in file describes this project". The realistic layout then makes a
    /// merged answer actively wrong - per-project settings live with the project, so the settings
    /// folder sits at the SOLUTION while Kiro's marker sits at the REPOSITORY ROOT, and a
    /// nearest-marker-wins search over the union of both markers picks the solution, pins the working
    /// directory there, and loses the repo-root steering: <b>issue #54 reproduced exactly, by the
    /// feature built on top of its fix.</b> They also have opposite miss semantics (see
    /// <see cref="ProjectSettingsLocation"/>).
    /// </para>
    /// <para>
    /// And the decisive one: merging would make the folder's mere PRESENCE grant. An empty settings
    /// folder in a cloned repository would move the agent's working directory - whereas the root
    /// override is a <em>value in the file</em>, a deliberate statement by the repo author. Keeping
    /// the searches apart makes that structural rather than a rule someone has to remember.
    /// </para>
    /// <para>
    /// The ceilings are <see cref="WorkspaceRootLocator"/>'s, and the profile/drive refusal is
    /// literally its predicate (<see cref="WorkspaceRootLocator.IsRefusedAsWorkspace"/>) rather than a
    /// second copy. The repository ceiling means something DIFFERENT here, though, and the difference
    /// is worth stating so nobody "simplifies" one into the other: there it bounds how far the working
    /// directory may move, here it is a <b>trust boundary</b> - a settings folder sitting in a shared
    /// source root must not silently configure every clone below it.
    /// </para>
    /// </remarks>
    public static class ProjectSettingsLocator
    {
        // Matched as either a directory or a file - a submodule/worktree checkout stores its own as a
        // file rather than a directory.
        private static readonly string[] RepositoryMarkers = { ".git", ".hg", ".svn" };

        /// <summary>
        /// Finds the nearest project settings folder at or above <paramref name="solutionRoot"/>.
        /// </summary>
        /// <param name="solutionRoot">The open solution/folder directory.</param>
        /// <remarks>
        /// <b>There is deliberately no <see cref="AgentWorkspaceScope"/> parameter.</b> That setting
        /// says how far above the solution the agent may RUN; reading a checked-in file is not reach.
        /// The scope gates the <em>value</em> a settings file supplies (a workspace root override is
        /// refused under <see cref="AgentWorkspaceScope.SolutionOnly"/>), never the search for it -
        /// otherwise a user who pinned the working directory would also silently lose every unrelated
        /// key the file carries.
        /// </remarks>
        public static ProjectSettingsLocation Find(string? solutionRoot)
        {
            if (string.IsNullOrWhiteSpace(solutionRoot))
                return ProjectSettingsLocation.None("no workspace root");

            string current;
            try
            {
                current = Path.GetFullPath(solutionRoot!)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return ProjectSettingsLocation.None("workspace root is not a usable path");
            }

            // A drive root has no trailing-separator-free form ("C:" is not "C:\"), so restore it.
            if (current.Length == 0 || (current.Length == 2 && current[1] == ':'))
                current = solutionRoot!;

            // The solution folder itself, before any ancestor guard applies: a solution opened directly
            // in the profile folder may still carry its own settings, since nothing is being widened.
            if (TryRead(current) is { } atSolution)
                return atSolution with { Reason = $"found at '{current}'" };

            if (HasRepositoryMarker(current))
                return ProjectSettingsLocation.None($"the solution folder is the repository root '{current}'");

            var userProfile = SafeUserProfile();

            for (var depth = 1; depth <= WorkspaceRootLocator.MaxSearchDepth; depth++)
            {
                DirectoryInfo? parent;
                try { parent = Directory.GetParent(current); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    return ProjectSettingsLocation.None("the folder above is unreadable");
                }

                if (parent is null)
                    return ProjectSettingsLocation.None("reached the drive root");

                current = parent.FullName;

                if (WorkspaceRootLocator.IsRefusedAsWorkspace(current, userProfile))
                    return ProjectSettingsLocation.None(
                        $"stopped below '{current}' (user profile or drive root never holds project settings)");

                if (TryRead(current) is { } found)
                    return found with { Reason = $"found at '{current}'" };

                // Inclusive ceiling: this directory was checked above, and the walk stops here.
                if (HasRepositoryMarker(current))
                    return ProjectSettingsLocation.None($"stopped at the repository root '{current}'");
            }

            return ProjectSettingsLocation.None(
                $"search depth ({WorkspaceRootLocator.MaxSearchDepth}) reached");
        }

        /// <summary>
        /// The settings folder in <paramref name="dir"/> when it holds real content, else null.
        /// </summary>
        /// <remarks>
        /// <b>The content contract, and it is fixed now rather than grown later.</b> A folder counts
        /// when it holds the settings file OR a non-empty steering directory - even though nothing
        /// reads steering yet (issue #59 part 2). Defining it later instead would move this search's
        /// answer under existing users: with a steering-only folder beside the solution and a settings
        /// file at the repository root, nearest-wins picks the repository root today and the solution
        /// after the upgrade, silently changing which file governs with nothing failing.
        /// <para>A stray EMPTY folder is not a hit, mirroring
        /// <see cref="WorkspaceMarker.RequiredAnyOf"/> - it must never shadow a real one above it.</para>
        /// </remarks>
        private static ProjectSettingsLocation? TryRead(string dir)
        {
            string folder;
            try { folder = Path.Combine(dir, Branding.ProjectSettingsFolderName); }
            catch (ArgumentException) { return null; }

            try
            {
                if (!Directory.Exists(folder))
                    return null;

                var settings = Path.Combine(folder, Branding.ProjectSettingsFileName);
                var hasSettings = File.Exists(settings);

                var steering = Path.Combine(folder, Branding.ProjectSteeringFolderName);
                var hasSteering = Directory.Exists(steering) && HasAnyEntry(steering);

                if (!hasSettings && !hasSteering)
                    return null;

                return new ProjectSettingsLocation(
                    folder,
                    hasSettings ? settings : null,
                    hasSteering ? steering : null,
                    string.Empty);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable candidate is simply not a match; the walk continues upwards.
                return null;
            }
        }

        private static bool HasAnyEntry(string dir)
        {
            using var entries = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
            return entries.MoveNext();
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
                    // Treat an unreadable marker as absent - the walk's other ceilings still bound it.
                }
            }

            return false;
        }

        private static string? SafeUserProfile()
        {
            try
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrWhiteSpace(profile) ? null : Path.GetFullPath(profile);
            }
            catch
            {
                // No profile resolvable: the repository ceiling and depth cap still bound the walk.
                return null;
            }
        }
    }
}
