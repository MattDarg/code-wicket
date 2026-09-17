using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CodeWicket.Core;

namespace CodeWicket.Providers.Acp
{
    /// <summary>
    /// Reads a project's checked-in settings file (issue #59). <b>The allowlist is this method's
    /// code</b> - it asks for the keys it honours, one at a time, and never hands the document to a
    /// deserializer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why that is construction rather than validation.</b> With
    /// <c>JsonSerializer.Deserialize&lt;ProjectSettings&gt;</c> the security property would be "the
    /// type happens to have no dangerous member" - true today, one refactor from false, and nothing
    /// fails when it turns. Someone reuses the type for a second purpose, adds a convenience member,
    /// and a cloned repository can now set it, with no build error, no failing test and nothing in the
    /// review diff saying the repository's authority was widened.
    /// </para>
    /// <para>
    /// Read key by key, the allowlist is enumerable by reading one method, and widening it takes two
    /// deliberate edits in two files. It is the same instinct already load-bearing in the permission
    /// model, where a foreign tool rule is matched by CONSTRUCTION - building both spellings from its
    /// own halves - rather than by parsing the incoming name.
    /// </para>
    /// <para>
    /// Lives here rather than in Core because Core carries no <c>System.Text.Json</c> dependency and
    /// that is worth keeping; this assembly is net10-only and already has STJ through StreamJsonRpc.
    /// The SCHEMA and the sentences are in Core, so both stay testable without this layer.
    /// </para>
    /// </remarks>
    public static class ProjectSettingsReader
    {
        /// <summary>
        /// Reads <paramref name="settingsFilePath"/>. Never throws: an unreadable or malformed file is
        /// an empty result carrying an issue that says so.
        /// </summary>
        /// <remarks>
        /// Pure - it logs nothing and touches no global state, so the caller decides what reaches
        /// <c>engine.log</c> and what reaches the transcript. A null or absent path is simply no
        /// settings, with no issue raised: a project without a settings file is the normal case, not a
        /// problem to report.
        /// </remarks>
        public static ProjectSettingsReadResult Read(string? settingsFilePath)
        {
            if (string.IsNullOrEmpty(settingsFilePath))
                return ProjectSettingsReadResult.None;

            string text;
            try
            {
                if (!File.Exists(settingsFilePath))
                    return ProjectSettingsReadResult.None;

                text = File.ReadAllText(settingsFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return Single(ProjectSettingsReport.Malformed(settingsFilePath!, ex.Message));
            }

            // An empty file is a file someone created and has not filled in yet, not a broken one.
            if (string.IsNullOrWhiteSpace(text))
                return ProjectSettingsReadResult.None;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            }
            catch (JsonException ex)
            {
                return Single(ProjectSettingsReport.Malformed(settingsFilePath!, ex.Message));
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Single(ProjectSettingsReport.Malformed(
                        settingsFilePath!, "the file must contain a JSON object."));
                }

                return ReadObject(document.RootElement);
            }
        }

        private static ProjectSettingsReadResult ReadObject(JsonElement root)
        {
            var issues = new List<ProjectSettingsIssue>();

            // Every property is classified, so a refusal or a typo is REPORTED rather than merely
            // having no effect. Done before the honoured keys are read, so the order of the report
            // follows the file rather than the order we happen to ask in.
            foreach (var property in root.EnumerateObject())
            {
                if (ProjectSettingsSchema.IsRefused(property.Name))
                    issues.Add(ProjectSettingsReport.RefusedKey(property.Name));
                else if (!ProjectSettingsSchema.IsKnown(property.Name))
                    issues.Add(ProjectSettingsReport.UnknownKey(property.Name));
            }

            var agentWorkspaceRoot = ReadString(
                root, ProjectSettingsSchema.AgentWorkspaceRootKey, issues);

            return new ProjectSettingsReadResult(new ProjectSettings(agentWorkspaceRoot), issues);
        }

        /// <summary>
        /// One honoured string key. A present-but-wrong-typed value is reported and dropped; the key
        /// being absent is silent, which is the common case.
        /// </summary>
        private static string? ReadString(JsonElement root, string key, List<ProjectSettingsIssue> issues)
        {
            if (!TryGetProperty(root, key, out var value))
                return null;

            if (value.ValueKind != JsonValueKind.String)
            {
                issues.Add(ProjectSettingsReport.WrongType(key, "a string"));
                return null;
            }

            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        /// <summary>
        /// Case-insensitive property lookup, matching <see cref="ProjectSettingsSchema.IsKnown"/>.
        /// </summary>
        /// <remarks>
        /// <c>JsonElement.TryGetProperty</c> is ordinal-only, so a file writing
        /// <c>AgentWorkspaceRoot</c> would be classified as known by the schema and then silently
        /// found to be absent here - the one shape where the two halves could disagree, and it would
        /// present as a key that is accepted without warning and does nothing.
        /// </remarks>
        private static bool TryGetProperty(JsonElement root, string key, out JsonElement value)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static ProjectSettingsReadResult Single(ProjectSettingsIssue issue) =>
            new ProjectSettingsReadResult(ProjectSettings.Empty, new[] { issue });
    }
}
