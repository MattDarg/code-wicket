using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// <see cref="ExtensionConfig.Load"/> must not throw, whatever the file says.
    /// <para>
    /// That is a contract rather than a nicety, because of who calls it and when. The chat window
    /// loads the config inside its async init with no catch of its own, so an exception here does not
    /// surface as an error — the pane simply never finishes starting, and sits on
    /// "Starting Code Wicket…" indefinitely. AGENTS.md attributes that exact symptom to a missing
    /// net472 dependency in the VSIX, so a config that could produce it would send the next
    /// investigation to the wrong place entirely. <c>Update</c> calls <c>Load</c> too, so a file that
    /// cannot be read also cannot be repaired by changing a setting.
    /// </para>
    /// <para>
    /// Hand-editing this file is the documented escape hatch for the config-only settings
    /// (<c>UseStubIde</c>), which is what makes a hand-written null an ordinary input rather than an
    /// exotic one.
    /// </para>
    /// </summary>
    [Collection(StoragePathCollection.Name)]
    public sealed class ExtensionConfigLoadTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-config-" + Guid.NewGuid().ToString("N"));

        public ExtensionConfigLoadTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            ExtensionConfig.RedirectTo(null!);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private ExtensionConfig LoadFrom(string json)
        {
            var path = Path.Combine(_dir, "config.json");
            File.WriteAllText(path, json);
            ExtensionConfig.RedirectTo(path);
            return ExtensionConfig.Load();
        }

        /// <summary>
        /// An explicit null for a collection reads as empty. A property initializer is no defence: it
        /// runs before deserialization, and STJ then OVERWRITES it with null when the JSON says null —
        /// only an ABSENT key keeps the initializer's value, which is why every one of these properties
        /// looks safe at its declaration.
        /// </summary>
        [Theory]
        [InlineData("allowedCommands")]
        [InlineData("alwaysPromptCommands")]
        [InlineData("allowedPaths")]
        [InlineData("allowedTools")]
        [InlineData("customAcpAgents")]
        [InlineData("engineEnvironment")]
        [InlineData("discoveredModels")]
        public void ANullCollectionReadsAsEmptyRatherThanThrowing(string key)
        {
            var config = LoadFrom("{\"" + key + "\": null}");

            Assert.NotNull(config.AllowedCommands);
            Assert.NotNull(config.AlwaysPromptCommands);
            Assert.NotNull(config.AllowedPaths);
            Assert.NotNull(config.AllowedTools);
            Assert.NotNull(config.CustomAcpAgents);
            Assert.NotNull(config.EngineEnvironment);
            Assert.NotNull(config.DiscoveredModels);
        }

        /// <summary>
        /// The one that actually threw, and the reason it is called out separately: the tool-rule
        /// migration runs OUTSIDE the try/catch guarding the file read, so its dereference of
        /// <c>AllowedCommands</c> escaped <c>Load</c> altogether — taking <c>LogConfigError</c> with it,
        /// so the failure recorded nothing anywhere.
        /// </summary>
        [Fact]
        public void ANullCommandListDoesNotEscapeTheMigration()
        {
            var config = LoadFrom("{\"allowedCommands\": null, \"allowedTools\": [\"code-wicket/build_solution\"]}");

            Assert.Empty(config.AllowedCommands);
            Assert.Equal(new[] { "code-wicket/build_solution" }, config.AllowedTools);
        }

        /// <summary>Ordinary values still load — the normalization must not be doing the work.</summary>
        [Fact]
        public void RealValuesAreUnaffected()
        {
            var config = LoadFrom("{\"allowedCommands\": [\"dotnet build\"], \"allowedPaths\": [\"C:\\\\repo\\\\*\"]}");

            Assert.Equal(new[] { "dotnet build" }, config.AllowedCommands);
            Assert.Equal(new[] { @"C:\repo\*" }, config.AllowedPaths);
        }

        // ---- the migration's write-back --------------------------------------------------------

        /// <summary>
        /// A namespaced rule leaves <c>allowedCommands</c> even when the canonical form is ALREADY in
        /// <c>allowedTools</c>.
        /// <para>
        /// The write-back was gated on the tools list alone, so this case rewrote nothing: the tool
        /// count does not change, and the dead command entry stayed in the user's settings for good.
        /// Reachable two ways — granting the tool again after a partial run, or two command entries that
        /// normalise to the same rule — and it is the "dead entry and no way to tell" outcome the
        /// migration exists to prevent, produced by the migration itself.
        /// </para>
        /// </summary>
        [Fact]
        public void ADeadCommandEntryIsRemovedEvenWhenTheToolRuleAlreadyExists()
        {
            var config = LoadFrom(
                "{\"allowedCommands\": [\"dotnet build\", \"@code-wicket/build_solution\"]," +
                " \"allowedTools\": [\"code-wicket/build_solution\"]}");

            Assert.Equal(new[] { "dotnet build" }, config.AllowedCommands);
            Assert.Single(config.AllowedTools);
        }

        /// <summary>The ordinary migration still works: the rule moves and both lists are rewritten.</summary>
        [Fact]
        public void ANamespacedCommandRuleMovesToTheToolList()
        {
            var config = LoadFrom("{\"allowedCommands\": [\"dotnet build\", \"@code-wicket/build_solution\"]}");

            Assert.Equal(new[] { "dotnet build" }, config.AllowedCommands);
            Assert.Contains(config.AllowedTools, t => t.EndsWith("build_solution", StringComparison.OrdinalIgnoreCase));
        }

        // ---- the policy the config feeds -------------------------------------------------------

        /// <summary>
        /// A null element in a path list is skipped, not fatal. <c>SetPathPolicy</c> mapped the slashes
        /// BEFORE the filter that exists to drop such an entry, so the transform met the null first —
        /// and its callers are the chat window's startup and its <c>ExtensionConfig.Changed</c> handler,
        /// neither of which catches. <c>SetCommandPolicy</c> was safe only because it happens not to map.
        /// </summary>
        [Fact]
        public async Task ANullPathRuleIsSkippedRatherThanThrowing()
        {
            var handler = new PolicyPermissionHandler(new NeverPrompt(), PermissionMode.Prompt);

            handler.SetPathPolicy(new string?[] { @"C:\repo\*", null, "  " }!);

            // The real rule still compiled, so the skip is a skip and not a bail-out.
            var decision = await handler.RequestAsync(new PermissionRequest(
                "t", @"Write C:\repo\a.cs", "edit", null, null,
                new[] { new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce) },
                Path: @"C:\repo\a.cs"));

            Assert.Equal("allow", decision.OptionId);
        }

        private sealed class NeverPrompt : IPermissionHandler
        {
            public System.Threading.Tasks.Task<PermissionDecision> RequestAsync(
                PermissionRequest request, System.Threading.CancellationToken cancellationToken = default)
                => System.Threading.Tasks.Task.FromResult(new PermissionDecision("prompted"));
        }
    }
}
