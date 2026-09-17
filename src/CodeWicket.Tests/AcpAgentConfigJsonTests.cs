using System.Collections.Generic;
using System.Linq;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Parsing user-authored custom ACP agents (config.json <c>customAcpAgents</c> -> CWKT_CUSTOM_AGENTS).
    /// The overriding contract is that this NEVER throws: bad JSON or a bad entry is warned and skipped so
    /// one broken config line can't take down every backend. Also pins defaulting (capabilities, models),
    /// trimming, and the DiscoverCapabilities flag that decides whether initialize can refresh capabilities.
    /// </summary>
    public sealed class AcpAgentConfigJsonTests
    {
        private static (IReadOnlyList<AcpAgentConfig> configs, List<string> warnings) Parse(string json)
        {
            var warnings = new List<string>();
            var configs = AcpAgentConfigJson.ParseList(json, warnings.Add);
            return (configs, warnings);
        }

        [Fact]
        public void ValidEntry_ParsesAllFields()
        {
            var (configs, warnings) = Parse(
                """
                [ {
                  "providerId": "my-agent",
                  "displayName": "My Agent",
                  "cliPath": "my-agent.cmd",
                  "args": ["acp", "--flag"],
                  "env": { "FOO": "bar" },
                  "capabilities": ["ToolCalls", "ResumeSession"],
                  "models": [ { "id": "m1", "name": "Model One" } ]
                } ]
                """);

            var config = Assert.Single(configs);
            Assert.Empty(warnings);
            Assert.Equal("my-agent", config.ProviderId);
            Assert.Equal("My Agent", config.DisplayName);
            Assert.Equal("my-agent.cmd", config.CliPath);
            Assert.Equal(new[] { "acp", "--flag" }, config.LaunchArgs);
            Assert.Equal("bar", config.Environment!["FOO"]);
            Assert.True(config.Capabilities.HasFlag(AgentCapabilities.ToolCalls));
            Assert.True(config.Capabilities.HasFlag(AgentCapabilities.ResumeSession));
            Assert.Equal("m1", config.Models[0].Id);
            Assert.Equal("Model One", config.Models[0].DisplayName);
            // Hand-declared capabilities are authoritative -> not refreshed from initialize.
            Assert.False(config.DiscoverCapabilities);
        }

        [Fact]
        public void InvalidJson_ReturnsEmptyAndWarns_DoesNotThrow()
        {
            var (configs, warnings) = Parse("not json {[");

            Assert.Empty(configs);
            Assert.Single(warnings);
            Assert.Contains("invalid", warnings[0]);
        }

        [Fact]
        public void EmptyArray_ReturnsEmptyNoWarnings()
        {
            var (configs, warnings) = Parse("[]");

            Assert.Empty(configs);
            Assert.Empty(warnings);
        }

        [Fact]
        public void MissingProviderId_SkipsEntryWithWarning()
        {
            var (configs, warnings) = Parse("""[ { "cliPath": "x.cmd" } ]""");

            Assert.Empty(configs);
            Assert.Contains(warnings, w => w.Contains("providerId"));
        }

        [Fact]
        public void MissingCliPath_SkipsEntryWithWarning()
        {
            var (configs, warnings) = Parse("""[ { "providerId": "a" } ]""");

            Assert.Empty(configs);
            Assert.Contains(warnings, w => w.Contains("cliPath"));
        }

        // One bad entry never sinks the good ones alongside it.
        [Fact]
        public void MixedGoodAndBadEntries_KeepsGoodSkipsBad()
        {
            var (configs, warnings) = Parse(
                """
                [
                  { "providerId": "good", "cliPath": "good.cmd" },
                  { "cliPath": "orphan.cmd" }
                ]
                """);

            var config = Assert.Single(configs);
            Assert.Equal("good", config.ProviderId);
            Assert.NotEmpty(warnings);
        }

        // No capabilities listed -> the spec baseline is defaulted AND the entry is left discoverable
        // so initialize can refresh it on session start.
        [Fact]
        public void NoCapabilities_DefaultsBaselineAndStaysDiscoverable()
        {
            var config = Assert.Single(Parse("""[ { "providerId": "a", "cliPath": "a.cmd" } ]""").configs);

            Assert.True(config.Capabilities.HasFlag(AgentCapabilities.ToolCalls));
            Assert.True(config.Capabilities.HasFlag(AgentCapabilities.Mcp));
            // ResumeSession/ModelSelection stay opt-in (not in the default baseline).
            Assert.False(config.Capabilities.HasFlag(AgentCapabilities.ResumeSession));
            Assert.True(config.DiscoverCapabilities);
        }

        [Fact]
        public void UnknownCapability_IsIgnoredWithWarning()
        {
            var (configs, warnings) = Parse(
                """[ { "providerId": "a", "cliPath": "a.cmd", "capabilities": ["ToolCalls", "Bogus"] } ]""");

            var config = Assert.Single(configs);
            Assert.True(config.Capabilities.HasFlag(AgentCapabilities.ToolCalls));
            Assert.Contains(warnings, w => w.Contains("Bogus"));
        }

        /// <summary>
        /// A capability is a NAME we know, or nothing — and the reason is that the alternative is silent.
        /// <c>Enum.TryParse</c> accepts numbers, so <c>"64"</c> granted <c>ModelSelection</c> to an agent
        /// with no <c>session/set_model</c>, and <c>"9999"</c> set bits no member defines; both returned
        /// true, taking the branch that skips the warning a bad entry is supposed to earn. The declaration
        /// here is a user's config claiming what a CLI can do, so it gets the same allowlist-by-
        /// construction treatment as the project settings file.
        /// </summary>
        [Theory]
        [InlineData("64")]           // ModelSelection's value
        [InlineData("9999")]         // bits no member defines
        [InlineData("-1")]           // every bit
        [InlineData("Mcp, Thinking")] // a list, granting two from one entry
        public void ANumericOrCompoundCapability_IsRejectedLikeAnyUnknownName(string entry)
        {
            var (configs, warnings) = Parse(
                $$"""[ { "providerId": "a", "cliPath": "a.cmd", "capabilities": ["ToolCalls", "{{entry}}"] } ]""");

            var config = Assert.Single(configs);
            Assert.True(config.Capabilities.HasFlag(AgentCapabilities.ToolCalls)); // the real name still lands
            Assert.False(config.Capabilities.HasFlag(AgentCapabilities.ModelSelection));
            Assert.False(config.Capabilities.HasFlag(AgentCapabilities.Thinking));
            Assert.Contains(warnings, w => w.Contains(entry));
        }

        // The other direction: names still work, whatever their casing, so this costs nothing real.
        [Theory]
        [InlineData("modelselection")]
        [InlineData("ModelSelection")]
        [InlineData("  ModelSelection  ")]
        public void ANamedCapability_IsStillAccepted(string entry)
        {
            var (configs, warnings) = Parse(
                $$"""[ { "providerId": "a", "cliPath": "a.cmd", "capabilities": ["{{entry}}"] } ]""");

            Assert.True(Assert.Single(configs).Capabilities.HasFlag(AgentCapabilities.ModelSelection));
            Assert.Empty(warnings);
        }

        // The picker binds Models[0] as the default selection, so the list must never be empty.
        [Fact]
        public void NoModels_GetsASyntheticDefaultModel()
        {
            var config = Assert.Single(Parse("""[ { "providerId": "a", "cliPath": "a.cmd" } ]""").configs);

            var model = Assert.Single(config.Models);
            Assert.Equal("default", model.Id);
        }

        [Fact]
        public void MissingDisplayName_FallsBackToProviderId()
        {
            var config = Assert.Single(Parse("""[ { "providerId": "raw-id", "cliPath": "a.cmd" } ]""").configs);

            Assert.Equal("raw-id", config.DisplayName);
        }

        // Whitespace around authored values is trimmed so ids/paths match cleanly downstream.
        [Fact]
        public void Fields_AreTrimmed()
        {
            var config = Assert.Single(Parse(
                """[ { "providerId": "  spaced  ", "cliPath": "  a.cmd  " } ]""").configs);

            Assert.Equal("spaced", config.ProviderId);
            Assert.Equal("a.cmd", config.CliPath);
        }

        // Capabilities present but the author asked for them to stay discoverable (probed) -> honoured.
        [Fact]
        public void CapabilitiesDiscoveredFlag_KeepsEntryRefreshable()
        {
            var config = Assert.Single(Parse(
                """[ { "providerId": "a", "cliPath": "a.cmd", "capabilities": ["ToolCalls"], "capabilitiesDiscovered": true } ]""").configs);

            Assert.True(config.DiscoverCapabilities);
        }
    }
}
