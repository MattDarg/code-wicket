using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CodeWicket.Core;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The one honoured key that can move where the agent runs (issue #59).
    /// <para>
    /// It exists because <see cref="WorkspaceRootLocator"/>'s walk is a guess bounded by a repository
    /// marker that is sometimes the wrong boundary, and when it guesses wrong every failure is silent.
    /// These tests are mostly about what it REFUSES, because a checked-in file supplying this is the
    /// one place where a repository gets to change something rather than merely describe itself.
    /// </para>
    /// </summary>
    public sealed class ProjectWorkspaceRootOverrideTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-projroot-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best effort */ }
        }

        private string Dir(params string[] parts)
        {
            var path = Path.Combine(_root, Path.Combine(parts));
            Directory.CreateDirectory(path);
            return path;
        }

        private static ProjectRootOverride Resolve(
            string? value, string anchor, AgentWorkspaceScope scope = AgentWorkspaceScope.RepositoryRoot) =>
            ProjectWorkspaceRootOverride.Resolve(value, anchor, scope);

        // --- the anchor ---------------------------------------------------------------------------

        /// <summary>
        /// <b>The anchor is the folder containing the settings folder, not the solution root.</b>
        /// Measured from the solution instead, the same "<c>..</c>" would name a different directory
        /// depending on how deep the solution sits - so moving a solution one level down would silently
        /// repoint the override, which is the survivability the relativity exists for.
        /// </summary>
        [Fact]
        public void TheValueIsMeasuredFromTheFolderHoldingTheSettings()
        {
            var repo = Dir("repo");
            var anchor = Dir("repo", "src", "solution");
            Dir("repo", "src");

            var result = Resolve("../..", anchor);

            Assert.True(result.Applied);
            Assert.Equal(repo, result.Root);
        }

        [Fact]
        public void ASidewaysValueIsMeasuredToTheCommonAncestor()
        {
            // Up two and back down one: what bounds it is how far OUT of the project it reached (two),
            // not the three segments it is written with.
            Dir("repo");
            var elsewhere = Dir("repo", "elsewhere");
            var anchor = Dir("repo", "src", "solution");

            var result = Resolve("../../elsewhere", anchor);

            Assert.True(result.Applied);
            Assert.Equal(elsewhere, result.Root);
        }

        [Fact]
        public void ADescendantIsAllowed()
        {
            var anchor = Dir("repo");
            var inner = Dir("repo", "workspace");

            var result = Resolve("workspace", anchor);

            Assert.True(result.Applied);
            Assert.Equal(inner, result.Root);
        }

        /// <summary>
        /// Crossing a repository boundary is the key's stated PURPOSE - a submodule or nested repo is
        /// exactly the case the marker walk cannot serve - so unlike that walk, this is not stopped by
        /// a repository marker. The walk stops there because it is guessing; this does not, because it
        /// is a deliberate statement by the repository author.
        /// </summary>
        [Fact]
        public void ItMayCrossARepositoryBoundaryUnlikeTheMarkerWalk()
        {
            var outer = Dir("outer");
            Dir("outer", "sub", ".git");
            var anchor = Dir("outer", "sub", "src");

            // The marker walk would stop at the nested repository root and never reach 'outer'.
            var walk = WorkspaceRootLocator.Resolve(
                anchor, new[] { new WorkspaceMarker(".kiro", new[] { "steering" }) });
            Assert.False(walk.Widened);

            var result = Resolve("../..", anchor);

            Assert.True(result.Applied);
            Assert.Equal(outer, result.Root);
        }

        // --- the refusals -------------------------------------------------------------------------

        /// <summary>
        /// An absolute path is only ever correct on the machine it was written on, and honouring one is
        /// how a checked-in file comes to name a drive root. Refused rather than warned-and-honoured.
        /// </summary>
        [Theory]
        [InlineData(@"C:\somewhere")]
        [InlineData(@"\\server\share")]
        [InlineData("/etc")]
        public void AnAbsoluteValueIsRefused(string value)
        {
            var anchor = Dir("repo");

            var result = Resolve(value, anchor);

            Assert.False(result.Applied);
            Assert.NotNull(result.Issue);
            Assert.Contains("relative", result.Issue!.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(result.Issue.ShowInTranscript);
        }

        /// <summary>
        /// A missing directory is refused, and the refusal says what the path was measured FROM.
        /// </summary>
        /// <remarks>
        /// The anchor is the thing people get wrong, and reasonably: nearly every config format
        /// measures relative paths from the file they are written in, and this one measures from the
        /// folder CONTAINING the settings folder. Reporting only "does not exist" leaves the author
        /// staring at a directory that is plainly there from where they are sitting.
        /// </remarks>
        [Fact]
        public void ANonexistentDirectoryIsRefusedAndTheRefusalNamesTheAnchor()
        {
            var anchor = Dir("repo");

            var result = Resolve("does-not-exist", anchor);

            Assert.False(result.Applied);
            Assert.NotNull(result.Issue);
            Assert.Contains("does not exist", result.Issue!.Message, StringComparison.Ordinal);
            Assert.Contains(anchor, result.Issue.Message, StringComparison.Ordinal);
            Assert.Contains(Branding.ProjectSettingsFolderName, result.Issue.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// <b>The confusion this feature will actually produce, caught and named.</b> An author who
        /// assumes the value is relative to <c>settings.json</c> writes a path that resolves under the
        /// settings folder. When that folder really is there, we know exactly what they meant - so the
        /// refusal says so and offers the corrected value verbatim rather than leaving them to work out
        /// that the anchor is one level up.
        /// </summary>
        [Fact]
        public void TheFileRelativeAssumptionIsRecognisedAndCorrected()
        {
            var anchor = Dir("repo");
            // What the author meant: <anchor>/<settings folder>/inner
            Dir("repo", Branding.ProjectSettingsFolderName, "inner");

            var result = Resolve("inner", anchor);

            Assert.False(result.Applied);
            Assert.NotNull(result.Issue);
            Assert.Contains("does exist", result.Issue!.Message, StringComparison.Ordinal);
            // The exact string to write, not a description of it.
            Assert.Contains(
                Branding.ProjectSettingsFolderName + "/inner", result.Issue.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The suggestion is offered only when it resolves. Otherwise it is a guess dressed as help,
        /// and would send an author chasing a directory that does not exist either.
        /// </summary>
        [Fact]
        public void NoSuggestionIsOfferedWhenTheFileRelativeReadingAlsoMisses()
        {
            var anchor = Dir("repo");

            var result = Resolve("nowhere", anchor);

            Assert.False(result.Applied);
            Assert.DoesNotContain("does exist", result.Issue!.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The same disaster the marker walk's guardrails exist for, reached by a different route: a
        /// repository must not be able to point the agent at the user's profile directory.
        /// </summary>
        [Fact]
        public void TheUserProfileIsRefused()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile) || !Directory.Exists(profile))
                return;

            // An anchor inside the profile, so a plain ".." reaches it.
            var anchor = Directory.CreateDirectory(
                Path.Combine(profile, "cwkt-projroot-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                var result = ProjectWorkspaceRootOverride.Resolve(
                    "..", anchor, AgentWorkspaceScope.RepositoryRoot);

                Assert.False(result.Applied);
                Assert.NotNull(result.Issue);
            }
            finally
            {
                try { Directory.Delete(anchor, recursive: true); }
                catch { /* best effort */ }
            }
        }

        /// <summary>
        /// <b>The profile's DESCENDANTS, which the refusal above cannot see</b> - and the reason a
        /// distance was never containment. A checkout at <c>&lt;profile&gt;\source\repos\App</c>
        /// sits four levels below the profile, so a sibling of the checkout (<c>~/.ssh</c> in the real
        /// case) exists, is not the profile itself, is not a drive root, and is well inside the depth
        /// cap: every guard this key had passed it, and the agent's working directory became the
        /// user's private key directory. What is wrong with the value is that reaching it means
        /// standing IN the profile.
        /// </summary>
        [Fact]
        public void AValueThatReachesOutThroughTheUserProfileIsRefused()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile) || !Directory.Exists(profile))
                return;

            var id = Guid.NewGuid().ToString("N");
            var secrets = Path.Combine(profile, "cwkt-secrets-" + id);
            var checkout = Path.Combine(profile, "cwkt-projroot-" + id);
            try
            {
                Directory.CreateDirectory(secrets);
                var anchor = Directory.CreateDirectory(
                    Path.Combine(checkout, "source", "repos", "App")).FullName;

                var result = ProjectWorkspaceRootOverride.Resolve(
                    "../../../../cwkt-secrets-" + id, anchor, AgentWorkspaceScope.RepositoryRoot);

                Assert.False(result.Applied);
                Assert.NotNull(result.Issue);
                Assert.True(result.Issue!.ShowInTranscript);

                // The containment named, not merely a refusal: another guard firing would leave this
                // test passing over an open hole.
                Assert.Contains("outside the project", result.Issue.Message, StringComparison.Ordinal);
            }
            finally
            {
                try { Directory.Delete(secrets, recursive: true); } catch { /* best effort */ }
                try { Directory.Delete(checkout, recursive: true); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// <b>A project living under the user profile is the normal case, not the attack.</b> Most
        /// checkouts sit somewhere below it, so refusing everything under the profile would refuse this
        /// key its stated purpose - pointing a nested solution at the repository root that holds it.
        /// What is refused is passing THROUGH the profile, never sitting under it.
        /// </summary>
        [Fact]
        public void AProjectUnderTheUserProfileMayStillPointAtItsOwnRepositoryRoot()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile) || !Directory.Exists(profile))
                return;

            var checkout = Path.Combine(profile, "cwkt-projroot-" + Guid.NewGuid().ToString("N"));
            try
            {
                var repo = Directory.CreateDirectory(
                    Path.Combine(checkout, "source", "repos", "App")).FullName;
                var anchor = Directory.CreateDirectory(Path.Combine(repo, "src", "solution")).FullName;

                var result = ProjectWorkspaceRootOverride.Resolve(
                    "../..", anchor, AgentWorkspaceScope.RepositoryRoot);

                Assert.True(result.Applied);
                Assert.Equal(repo, result.Root);
            }
            finally
            {
                try { Directory.Delete(checkout, recursive: true); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// <b>A link is how a value that IS contained names somewhere that is not.</b>
        /// <see cref="Path.GetFullPath(string)"/> resolves <c>..</c> textually and never touches the
        /// disk, so a junction that arrived with the project reads as a plain child of it and passes
        /// every lexical check above the route test. Refused rather than followed - reading where one
        /// points needs an API the net472 half of Core does not have, and this file has to mean the
        /// same thing in both.
        /// </summary>
        [Fact]
        public void AValueThatPointsThroughALinkIsRefused()
        {
            var anchor = Dir("repo");
            var elsewhere = Dir("elsewhere");
            var link = Path.Combine(anchor, "workspace");

            if (!TryCreateDirectoryLink(link, elsewhere))
                return;

            var result = Resolve("workspace", anchor);

            Assert.False(result.Applied);
            Assert.NotNull(result.Issue);
            Assert.Contains("is a link", result.Issue!.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A JUNCTION first, because it is the link a repository can realistically carry: creating one
        /// needs no privilege, while a symbolic link needs Developer Mode or elevation - so a check
        /// written on <see cref="Directory.CreateSymbolicLink"/> alone skips on an ordinary box and
        /// pins nothing there. False when the machine allows neither, and the caller then asserts
        /// nothing rather than failing for a reason that is not about the product.
        /// </summary>
        private static bool TryCreateDirectoryLink(string link, string target)
        {
            try
            {
                using var mklink = Process.Start(new ProcessStartInfo(
                    "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                mklink?.WaitForExit(20_000);

                if (Directory.Exists(link))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                // Fall through to the symbolic link.
            }

            try
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or PlatformNotSupportedException)
            {
                return false;
            }
        }

        [Fact]
        public void AValueMoreThanTheSearchDepthAwayIsRefused()
        {
            var parts = new List<string> { "top" };
            for (var i = 0; i <= WorkspaceRootLocator.MaxSearchDepth; i++)
                parts.Add("d" + i);
            var anchor = Dir(parts.ToArray());

            var up = string.Join("/", Repeat("..", WorkspaceRootLocator.MaxSearchDepth + 1));

            var result = Resolve(up, anchor);

            Assert.False(result.Applied);
            Assert.NotNull(result.Issue);
            Assert.Contains("levels away", result.Issue!.Message, StringComparison.Ordinal);
        }

        private static string[] Repeat(string value, int count)
        {
            var items = new string[count];
            for (var i = 0; i < count; i++)
                items[i] = value;
            return items;
        }

        /// <summary>
        /// <b>The user's own setting wins.</b> This is the flagship repo-honourable key, so a file that
        /// could overrule an explicit user choice would break the claim that a checked-in file only
        /// describes - on the very key the feature is named for.
        /// <para>
        /// And it is reported to the log ONLY. From the user's seat nothing surprising happened: the
        /// working directory is the solution folder, exactly as they asked for.
        /// </para>
        /// </summary>
        [Fact]
        public void TheUserScopeSettingRefusesTheOverrideAndSaysNothingToTheUser()
        {
            Dir("repo");
            var anchor = Dir("repo", "src");

            var result = Resolve("..", anchor, AgentWorkspaceScope.SolutionOnly);

            Assert.False(result.Applied);
            Assert.Null(result.Issue);
            Assert.Contains("Solution folder only", result.Reason, StringComparison.Ordinal);
        }

        // --- absence, and the reason line ---------------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AnAbsentValueIsNotAProblem(string? value)
        {
            var result = Resolve(value, Dir("repo"));

            Assert.False(result.Applied);
            Assert.Null(result.Issue);
        }

        [Fact]
        public void NoAnchorIsRefusedRatherThanResolvedAgainstTheProcessDirectory()
        {
            // The #54/#62 rule: a relative path with no root must never reach Path.GetFullPath, which
            // resolves against the process working directory - devenv's install folder in the VS host.
            var result = ProjectWorkspaceRootOverride.Resolve(
                "..", null, AgentWorkspaceScope.RepositoryRoot);

            Assert.False(result.Applied);
            Assert.NotNull(result.Issue);
        }

        /// <summary>Every outcome carries a reason, so a collected log can answer "why did it not apply".</summary>
        [Fact]
        public void EveryOutcomeCarriesAReason()
        {
            var anchor = Dir("repo");
            Dir("repo", "workspace");

            Assert.False(string.IsNullOrWhiteSpace(Resolve(null, anchor).Reason));
            Assert.False(string.IsNullOrWhiteSpace(Resolve("workspace", anchor).Reason));
            Assert.False(string.IsNullOrWhiteSpace(Resolve("nope", anchor).Reason));
            Assert.False(string.IsNullOrWhiteSpace(Resolve(@"C:\x", anchor).Reason));
        }

        [Fact]
        public void AnAppliedReasonNamesTheSettingsFileSoANoticeCanPointAtIt()
        {
            var anchor = Dir("repo");
            Dir("repo", "workspace");
            var settingsFile = Path.Combine(anchor, Branding.ProjectSettingsFolderName, Branding.ProjectSettingsFileName);

            var result = ProjectWorkspaceRootOverride.Resolve(
                "workspace", anchor, AgentWorkspaceScope.RepositoryRoot, settingsFile);

            Assert.True(result.Applied);
            Assert.Contains(settingsFile, result.Reason, StringComparison.Ordinal);
        }
    }
}
