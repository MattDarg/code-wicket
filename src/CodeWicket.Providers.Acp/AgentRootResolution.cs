using System;
using System.Collections.Generic;
using CodeWicket.Core;

namespace CodeWicket.Providers.Acp
{
    /// <summary>
    /// Where the agent should run, and everything the project's settings file had to do with it
    /// (issue #59).
    /// </summary>
    /// <param name="Root">The working directory, resolved exactly once for both the CLI process and
    /// the ACP <c>session/new.cwd</c>.</param>
    /// <param name="Location">Where the settings folder was found, or why it was not.</param>
    /// <param name="Issues">What the file got wrong, in file order. Empty for the common case.</param>
    /// <remarks>
    /// <b>This type exists so the session start and the session LISTING cannot disagree.</b> Both ask
    /// the same question and both used to call <c>WorkspaceRootLocator.Resolve</c> with identical
    /// arguments; once a checked-in file can redirect that answer, an override reaching only one of
    /// them makes the backend's session listing read a different store. That comes back as an empty
    /// list, which is indistinguishable from the user having no conversations - the failure measured
    /// at two hours forty-one minutes in issue #108. Sharing one method makes it structural rather
    /// than a rule someone has to remember when they add the next caller.
    /// </remarks>
    public sealed record AgentRootResolution(
        WorkspaceRootResult Root,
        ProjectSettingsLocation Location,
        IReadOnlyList<ProjectSettingsIssue> Issues)
    {
        /// <summary>The sentences the user should see - the subset of <see cref="Issues"/> worth
        /// interrupting them with. See <see cref="ProjectSettingsIssue.ShowInTranscript"/>.</summary>
        public IReadOnlyList<string> Notices()
        {
            List<string>? notices = null;
            foreach (var issue in Issues)
            {
                if (!issue.ShowInTranscript)
                    continue;
                notices ??= new List<string>();
                notices.Add(issue.Message);
            }

            return (IReadOnlyList<string>?)notices ?? Array.Empty<string>();
        }
    }
}
