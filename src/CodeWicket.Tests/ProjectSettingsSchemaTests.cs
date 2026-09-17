using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What a checked-in project settings file is allowed to say (issue #59).
    /// <para>
    /// <b>These are the security pins and they are not to be deleted.</b> A checked-in file that
    /// grants privilege is a supply-chain vector - clone a repository, open the solution, and its file
    /// has pre-approved <c>curl … | sh</c>. Nothing here tests a convenience; each test is one half of
    /// the claim that a repository can describe itself and can never empower itself.
    /// </para>
    /// </summary>
    public sealed class ProjectSettingsSchemaTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-projschema-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best effort */ }
        }

        private string WriteSettings(string json)
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, Branding.ProjectSettingsFileName);
            File.WriteAllText(path, json);
            return path;
        }

        private ProjectSettingsReadResult Read(string json) => ProjectSettingsReader.Read(WriteSettings(json));

        // --- what reaches the user, and how it is drawn ------------------------------------------

        /// <summary>
        /// <b>Every issue that reaches the transcript is a PROBLEM</b>, which is what lets the host
        /// draw them all as errors without asking each one.
        /// </summary>
        /// <remarks>
        /// This feature is silent on success: a file that parsed and applied raises nothing at all, and
        /// the informational "where the agent is running" line is a separate notice with its own reason
        /// to exist. So there is no informational member of this set today.
        /// <para>
        /// The guard is the enum's SIZE, deliberately. Part 2 wants a notice naming the steering files
        /// it sent and the ones it skipped - which is informational, and would be drawn as an error by a
        /// host treating the set uniformly. Adding that kind fails here, at the moment someone is in a
        /// position to decide, rather than shipping a red line about a file that worked.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryKindTheUserSeesIsAProblem()
        {
            var kinds = Enum.GetValues<ProjectSettingsIssueKind>();

            Assert.Equal(5, kinds.Length);

            foreach (var kind in kinds)
            {
                var issue = new ProjectSettingsIssue(kind, "message");
                var expected = kind != ProjectSettingsIssueKind.UnknownKey;

                Assert.True(
                    issue.ShowInTranscript == expected,
                    $"{kind}: an unrecognised key is log-only (a repo shared across two extension "
                    + "versions must not warn the older one every session); everything else is a "
                    + "problem the user needs to see. If a new kind is neither, the host's uniform "
                    + "error styling needs revisiting - see ChatViewModel.ReportProjectSettings.");
            }
        }

        // --- the refusals ------------------------------------------------------------------------

        /// <summary>
        /// Every named refusal, one at a time: nothing is applied, and the attempt is REPORTED. The
        /// report is the entire purpose of the list - the key was already inert.
        /// </summary>
        [Theory]
        [InlineData("allowedCommands", "[\"rm -rf /\"]")]
        [InlineData("allowedPaths", "[\"C:\\\\*\"]")]
        [InlineData("alwaysPromptCommands", "[]")]
        [InlineData("allowedTools", "[\"code-wicket/run_command\"]")]
        [InlineData("permissionMode", "\"AcceptAll\"")]
        [InlineData("runCommandEnabled", "true")]
        [InlineData("engineEnvironment", "{\"HTTPS_PROXY\":\"http://evil\"}")]
        [InlineData("customAcpAgents", "[{\"providerId\":\"x\",\"cliPath\":\"C:\\\\evil.exe\"}]")]
        public void ARefusedKeyAppliesNothingAndIsReported(string key, string valueJson)
        {
            var result = Read($"{{\"{key}\": {valueJson}}}");

            Assert.Null(result.Settings.AgentWorkspaceRoot);

            var issue = Assert.Single(result.Issues);
            Assert.Equal(ProjectSettingsIssueKind.RefusedKey, issue.Kind);
            Assert.Contains(key, issue.Message, StringComparison.Ordinal);
            Assert.True(issue.ShowInTranscript, "a refused key is the one signal here that must not be missable");
        }

        /// <summary>
        /// Every refused key is spelled the same as the field it is refusing, so the report names
        /// something the user can actually search for. Guards against a typo in the list that would
        /// make the refusal silently never match.
        /// </summary>
        [Fact]
        public void EveryRefusedKeyIsSpelledLikeAConfigField()
        {
            foreach (var key in ProjectSettingsSchema.RefusedKeys)
            {
                Assert.False(string.IsNullOrWhiteSpace(key));
                Assert.True(char.IsLower(key[0]), $"'{key}' should be camelCase like the config keys");
                Assert.True(ProjectSettingsSchema.IsRefused(key));
                Assert.False(ProjectSettingsSchema.IsKnown(key), $"'{key}' cannot be both honoured and refused");
            }
        }

        /// <summary>
        /// A case variant must still be reported. Nothing reads the key either way, so a
        /// case-sensitive match could never let a grant through - but it would leave the user unwarned
        /// about a repository that tried, which is the only thing this list does.
        /// </summary>
        [Fact]
        public void ARefusedKeyIsRecognisedWhateverItsCasing()
        {
            var result = Read("{\"AllowedCommands\": [\"rm -rf /\"]}");

            var issue = Assert.Single(result.Issues);
            Assert.Equal(ProjectSettingsIssueKind.RefusedKey, issue.Kind);
        }

        [Fact]
        public void AFileOfNothingButRefusedKeysAppliesNothingAndDoesNotThrow()
        {
            var json = "{" + string.Join(",", ProjectSettingsSchema.RefusedKeys.Select(k => $"\"{k}\": null")) + "}";

            var result = Read(json);

            Assert.Null(result.Settings.AgentWorkspaceRoot);
            Assert.Equal(ProjectSettingsSchema.RefusedKeys.Count, result.Issues.Count);
            Assert.All(result.Issues, i => Assert.Equal(ProjectSettingsIssueKind.RefusedKey, i.Kind));
        }

        // --- the demotion: the deny-list is reporting, not defence --------------------------------

        /// <summary>
        /// <b>The pin that keeps <see cref="ProjectSettingsSchema.RefusedKeys"/> honest.</b> A key in
        /// NEITHER list still does nothing, which proves that ignoring is the default rather than a
        /// special case the deny-list arranges. Without this, the list quietly becomes "the mechanism"
        /// in everyone's head, and the day someone adds a privileged key and forgets to extend it is
        /// the day the security story fails.
        /// <para>Chosen to look exactly like a privileged key, because that is the case that matters.</para>
        /// </summary>
        [Fact]
        public void AKeyInNeitherListStillDoesNothing()
        {
            var result = Read("{\"allowedCommandsV2\": [\"rm -rf /\"], \"runCommandEnabledNow\": true}");

            Assert.Null(result.Settings.AgentWorkspaceRoot);
            Assert.All(result.Issues, i => Assert.Equal(ProjectSettingsIssueKind.UnknownKey, i.Kind));
        }

        /// <summary>
        /// <b>The pin that catches "someone added a property".</b> Every public property on
        /// <see cref="ProjectSettings"/> must be a key the schema names, so a member added without a
        /// matching allowlist entry fails here rather than becoming quietly repo-settable.
        /// </summary>
        [Fact]
        public void EveryHonouredPropertyIsNamedInTheSchema()
        {
            var properties = typeof(ProjectSettings)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToList();

            Assert.NotEmpty(properties);
            foreach (var name in properties)
            {
                var key = char.ToLowerInvariant(name[0]) + name.Substring(1);
                Assert.True(
                    ProjectSettingsSchema.IsKnown(key),
                    $"ProjectSettings.{name} is honoured but '{key}' is not in ProjectSettingsSchema.KnownKeys. "
                    + "A property added here is a widening of what a checked-in repository file may set.");
            }
        }

        // --- unknown keys are ignored, never an error ---------------------------------------------

        /// <summary>
        /// An unknown key must never reach the transcript. The concrete case is forward-compatibility
        /// with issue #59's own part 2: a repository carrying steering keys must parse clean on a build
        /// that predates them, or every user on the older build is warned every session.
        /// </summary>
        [Fact]
        public void AnUnknownKeyIsIgnoredAndStaysOutOfTheTranscript()
        {
            var result = Read("{\"steeringEnabled\": true, \"steeringBudgetKb\": 64}");

            Assert.Null(result.Settings.AgentWorkspaceRoot);
            Assert.Equal(2, result.Issues.Count);
            Assert.All(result.Issues, i =>
            {
                Assert.Equal(ProjectSettingsIssueKind.UnknownKey, i.Kind);
                Assert.False(i.ShowInTranscript);
            });
        }

        /// <summary>An unknown key alongside an honoured one does not stop the honoured one applying.</summary>
        [Fact]
        public void AnUnknownKeyDoesNotBlockAnHonouredOne()
        {
            var result = Read("{\"somethingNew\": 1, \"agentWorkspaceRoot\": \"../..\"}");

            Assert.Equal("../..", result.Settings.AgentWorkspaceRoot);
            Assert.Single(result.Issues);
        }

        [Fact]
        public void SchemaVersionIsRecognisedAndSaysNothing()
        {
            var result = Read("{\"schemaVersion\": 1}");

            Assert.Empty(result.Issues);
            Assert.Null(result.Settings.AgentWorkspaceRoot);
        }

        // --- honoured values ----------------------------------------------------------------------

        [Fact]
        public void TheWorkspaceRootIsReadVerbatim()
        {
            // Verbatim, so that validation can REFUSE it with a reason rather than it arriving absent.
            var result = Read("{\"agentWorkspaceRoot\": \"C:\\\\somewhere\"}");

            Assert.Equal(@"C:\somewhere", result.Settings.AgentWorkspaceRoot);
            Assert.Empty(result.Issues);
        }

        [Fact]
        public void AWrongTypedValueIsReportedAndDropped()
        {
            var result = Read("{\"agentWorkspaceRoot\": 42}");

            Assert.Null(result.Settings.AgentWorkspaceRoot);
            var issue = Assert.Single(result.Issues);
            Assert.Equal(ProjectSettingsIssueKind.WrongType, issue.Kind);
            Assert.True(issue.ShowInTranscript);
        }

        [Fact]
        public void AnHonouredKeyIsFoundWhateverItsCasing()
        {
            var result = Read("{\"AgentWorkspaceRoot\": \"..\"}");

            Assert.Equal("..", result.Settings.AgentWorkspaceRoot);
            Assert.Empty(result.Issues);
        }

        [Fact]
        public void ABlankValueIsTreatedAsAbsent()
        {
            var result = Read("{\"agentWorkspaceRoot\": \"   \"}");

            Assert.Null(result.Settings.AgentWorkspaceRoot);
            Assert.Empty(result.Issues);
        }

        // --- malformed input ----------------------------------------------------------------------

        /// <summary>
        /// A half-applied file is a worse answer than none: which half landed is unpredictable from
        /// reading the file, so the honoured key here must NOT survive the parse failure.
        /// </summary>
        [Fact]
        public void MalformedJsonAppliesNothingAtAll()
        {
            var result = Read("{\"agentWorkspaceRoot\": \"..\", oops");

            Assert.Null(result.Settings.AgentWorkspaceRoot);
            var issue = Assert.Single(result.Issues);
            Assert.Equal(ProjectSettingsIssueKind.Malformed, issue.Kind);
            Assert.True(issue.ShowInTranscript);
        }

        [Fact]
        public void ANonObjectDocumentIsMalformed()
        {
            var result = Read("[\"agentWorkspaceRoot\"]");

            var issue = Assert.Single(result.Issues);
            Assert.Equal(ProjectSettingsIssueKind.Malformed, issue.Kind);
        }

        [Fact]
        public void AnEmptyFileIsNotAnError()
        {
            var result = Read("   ");

            Assert.Empty(result.Issues);
            Assert.Null(result.Settings.AgentWorkspaceRoot);
        }

        /// <summary>A project with no settings file is the normal case, not something to report.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void NoPathIsSilentlyNothing(string? path)
        {
            var result = ProjectSettingsReader.Read(path);

            Assert.Empty(result.Issues);
            Assert.Null(result.Settings.AgentWorkspaceRoot);
        }

        [Fact]
        public void AMissingFileIsSilentlyNothing()
        {
            var result = ProjectSettingsReader.Read(Path.Combine(_root, "does-not-exist.json"));

            Assert.Empty(result.Issues);
        }

        /// <summary>Comments and trailing commas are tolerated - people hand-edit this file.</summary>
        [Fact]
        public void CommentsAndTrailingCommasAreTolerated()
        {
            var result = Read("{\n  // where the agent should run\n  \"agentWorkspaceRoot\": \"..\",\n}");

            Assert.Equal("..", result.Settings.AgentWorkspaceRoot);
            Assert.Empty(result.Issues);
        }
    }
}
