using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Invariants of the Unified Settings manifest. These matter because the settings provider resolves
    /// a moniker by its LAST dotted segment (<c>CodeWicketSettingsProvider.Leaf</c>) — which is what
    /// let the settings be split across pages without touching the provider, and is also what makes a
    /// duplicated leaf name silently serve the wrong setting.
    /// </summary>
    public sealed class SettingsRegistrationTests
    {
        static JsonElement Manifest()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(
                    dir.FullName, "src", "CodeWicket.VSExtension", "CodeWicket.registration.json");
                if (File.Exists(candidate))
                    return JsonDocument.Parse(File.ReadAllText(candidate)).RootElement;
                dir = dir.Parent;
            }

            throw new FileNotFoundException("Could not locate CodeWicket.registration.json above " + AppContext.BaseDirectory);
        }

        static List<string> LeafNames() =>
            Manifest().GetProperty("properties").EnumerateObject()
                .SelectMany(region => region.Value.GetProperty("properties").EnumerateObject())
                .Select(setting => setting.Name.Split('.').Last())
                .ToList();

        [Fact]
        public void Every_setting_leaf_name_is_unique_across_regions()
        {
            // The provider dispatches on the leaf alone, so two regions sharing one — say a
            // "backends.enabled" and a "tools.enabled" — would read and write the same config field
            // while presenting as two settings. Nothing would throw; the pages would just lie.
            var duplicates = LeafNames().GroupBy(n => n, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();

            Assert.Empty(duplicates);
        }

        [Fact]
        public void Every_region_declares_the_callback_the_provider_is_registered_for()
        {
            // A region pointing at a different service silently renders with no values at all.
            foreach (var region in Manifest().GetProperty("properties").EnumerateObject())
            {
                Assert.Equal("external", region.Value.GetProperty("type").GetString());
                Assert.Equal(
                    "8E5B2F71-3C4D-4A6E-9B1F-2D7A6C8E4F30",
                    region.Value.GetProperty("callback").GetProperty("serviceId").GetString());
            }
        }

        [Fact]
        public void Every_page_has_a_category_and_every_category_a_page()
        {
            // A region whose moniker has no matching category gets an auto-titled node derived from the
            // path segment (which is how we ended up with a stray "Settings" node); a category with no
            // region is an empty page.
            var pages = Manifest().GetProperty("properties").EnumerateObject()
                .Select(r => r.Name.Substring(0, r.Name.LastIndexOf('.'))).ToList();
            var categories = Manifest().GetProperty("categories").EnumerateObject()
                .Select(c => c.Name).Where(n => n.Contains('.')).ToList();

            Assert.Equal(categories.OrderBy(x => x, StringComparer.Ordinal),
                         pages.OrderBy(x => x, StringComparer.Ordinal));
        }

        /// <summary>
        /// A rename must stop at the TITLE. The leaf is the config binding — the provider dispatches on
        /// it and <c>ExtensionConfig.LogAcpFrames</c>/<c>LogEngineChannel</c> are bound through it — so
        /// renaming one alongside its title would silently orphan the setting on every machine that had
        /// already set it: the checkbox reads as default-off while the old value sits in config.json
        /// doing nothing. These two were retitled for issue #82, which is exactly the edit that invites
        /// it, and nothing else in the manifest would have complained.
        /// </summary>
        [Fact]
        public void Retitling_a_diagnostic_setting_does_not_rename_its_config_leaf()
        {
            var leaves = LeafNames().Where(n => n.StartsWith("log", StringComparison.Ordinal)).ToList();

            Assert.Contains("logAcpFrames", leaves);
            Assert.Contains("logEngineChannel", leaves);
            Assert.Contains("logRendering", leaves);
        }

        /// <summary>
        /// The mid-turn release setting offers exactly the names the tray can act on (issue #190).
        /// <para>
        /// The failure this closes is a silent one, and it is the whole reason a setting whose values
        /// are words needs pinning: <c>PendingReleaseModes.Parse</c> answers Queue for anything it does
        /// not recognise, which is right for a config written before the setting existed and wrong for
        /// a manifest that offers a spelling the parser has never heard of. Picking "Steer" in the
        /// Settings UI would then save, redisplay correctly, and open the window on Queue — a setting
        /// that appears to work and does nothing. The declared default is checked against
        /// <c>ExtensionConfig</c>'s for the same reason the tees are: it is written down twice.
        /// </para>
        /// </summary>
        [Fact]
        public void The_message_release_setting_offers_only_modes_the_tray_has()
        {
            var setting = Manifest().GetProperty("properties").EnumerateObject()
                .SelectMany(region => region.Value.GetProperty("properties").EnumerateObject())
                .Single(s => s.Name.Split('.').Last() == "messageRelease").Value;

            var offered = setting.GetProperty("enum").EnumerateArray().Select(v => v.GetString()!).ToList();
            Assert.Equal(new[] { "Queue", "Steer" }, offered);

            // Every offered value round-trips: parsed to a rung, and named back as the value offered.
            foreach (var value in offered)
                Assert.Equal(value, PendingReleaseModes.ToName(PendingReleaseModes.Parse(value)));

            // ...and the two round-trip to DIFFERENT rungs, or the check above holds over a parser
            // that answers Queue for both — which is precisely the bug it is written against.
            Assert.NotEqual(PendingReleaseModes.Parse(offered[0]), PendingReleaseModes.Parse(offered[1]));

            Assert.Equal(setting.GetProperty("default").GetString(), new ExtensionConfig().DefaultMessageRelease);
        }

        /// <summary>
        /// Every diagnostic tee is off until asked for, and each one's default is declared TWICE — in
        /// this manifest and on <c>ExtensionConfig</c>. They have to agree: a manifest default of true
        /// over a config default of false shows a ticked box for a trace that is not running, and USX
        /// writes that true through on the first save of anything else on the page, switching on a
        /// diagnostic the user never asked for. The render one is the case to watch, because it was
        /// built default-on and turned over (see ExtensionConfig.LogRendering).
        /// </summary>
        [Fact]
        public void Every_diagnostic_tee_is_offered_as_off()
        {
            var defaults = Manifest().GetProperty("properties").EnumerateObject()
                .SelectMany(region => region.Value.GetProperty("properties").EnumerateObject())
                .Where(s => s.Name.StartsWith("diagnostics.", StringComparison.Ordinal))
                .ToDictionary(s => s.Name, s => s.Value.GetProperty("default").GetBoolean());

            Assert.Contains("diagnostics.logRendering", defaults.Keys);
            Assert.All(defaults, d => Assert.False(d.Value, d.Key + " should default off"));

            var config = new ExtensionConfig();
            Assert.False(config.LogRendering);
            Assert.False(config.LogAcpFrames);
            Assert.False(config.LogEngineChannel);
        }

        /// <summary>
        /// The Diagnostics page says where the logs are and which one to read first. It carries that on
        /// the CATEGORY rather than repeating it in both settings, so deleting it loses the pointer
        /// entirely with nothing else mentioning engine.log — which is the state issue #82 was reported
        /// from: the log holding the answer was always being written, and no text anywhere said so.
        /// </summary>
        [Fact]
        public void The_diagnostics_page_says_which_log_to_read_first()
        {
            var diagnostics = Manifest().GetProperty("properties").EnumerateObject()
                .Select(r => r.Value)
                .Where(r => r.TryGetProperty("categories", out _))
                .SelectMany(r => r.GetProperty("categories").EnumerateObject())
                .Single(c => c.Name == "diagnostics");

            var description = diagnostics.Value.GetProperty("description").GetString();

            Assert.Contains("engine.log", description);
            Assert.Contains("always written", description);
        }

        /// <summary>
        /// Every region keeps its Refresh button (issue #271). In USX <c>realtimeNotifications</c>
        /// decides exactly one thing — whether <c>ExternalSettingsRegionViewModel</c> offers a Refresh
        /// command, which calls the provider's <c>RefreshCacheAsync</c> — and NOT whether the page
        /// listens for <c>SettingValuesChanged</c>: every row subscribes to that regardless. The
        /// provider now pushes every in-process config.json change through that event, so the flag
        /// looks redundant; it is kept false because the event cannot see a config.json edited outside
        /// the process (the documented offline escape hatch for custom agents), and Refresh is the one
        /// route by which such an edit reaches an open page. Flipping it to true would remove that
        /// route silently — the page would just stop having a button.
        /// </summary>
        [Fact]
        public void Every_region_keeps_its_refresh_button()
        {
            foreach (var region in Manifest().GetProperty("properties").EnumerateObject())
            {
                Assert.True(region.Value.TryGetProperty("realtimeNotifications", out var flag),
                    region.Name + " must declare realtimeNotifications");
                Assert.False(flag.GetBoolean(), region.Name + " must keep realtimeNotifications false");
            }
        }

        /// <summary>
        /// Every setting the manifest offers is also on a classic Options page, under the SAME name.
        /// <para>
        /// This guards a failure that is invisible on the machine most of this is developed on. Visual
        /// Studio 2026 renders the unified region natively; Visual Studio 2022's Options dialog renders a
        /// classic page or NOTHING — never an external region (measured 2026-09-16 on 17.14, by
        /// registering a node and watching four empty pages appear). So the two are renderers over one
        /// config, and a setting present in the manifest but missing from the pages is simply unreachable
        /// on 17.x while every offline check and every look on 18.x stays green.
        /// </para>
        /// <para>
        /// Matched on the DISPLAYED NAME rather than the property name, because the two legitimately
        /// differ (<c>provider</c> is shown as "Backend") and because the name is the thing a user
        /// compares between the two surfaces. A title that drifts on one side is the same defect as a
        /// setting dropped from it.
        /// </para>
        /// <para>
        /// <b>What this does NOT pin, and it is worth knowing before a green run is read as more than it
        /// is:</b> a page that shows the right title while binding the wrong <see cref="ExtensionConfig"/>
        /// property. That is a text match away from this check and stays green — on BOTH versions, with
        /// nothing to see, because such a setting saves, redisplays correctly, and merely writes somewhere
        /// else. Reflection over the page types would catch it and is not available here: the pages are
        /// net472 and this suite is net10, which is the same reason the comparison is between two FILES.
        /// So a pass means the two surfaces NAME the same settings, not that they bind the same fields.
        /// </para>
        /// </summary>
        [Fact]
        public void Every_setting_is_also_on_a_classic_options_page()
        {
            var page = SettingsPagesSource();

            var missing = Manifest().GetProperty("properties").EnumerateObject()
                .SelectMany(region => region.Value.GetProperty("properties").EnumerateObject())
                .Select(s => s.Value.GetProperty("title").GetString()!)
                .Where(title => !page.Contains("[DisplayName(\"" + title + "\")]", StringComparison.Ordinal))
                .ToList();

            Assert.True(missing.Count == 0,
                "No classic Options page shows: " + string.Join(", ", missing) +
                ". Those settings are unreachable on Visual Studio 2022 — see SettingsPages.cs.");
        }

        /// <summary>
        /// Every page category names a classic page that exists. The id is what puts the category in
        /// 17.x's Options tree; one naming no page leaves the category with no node at all, and on 18.x
        /// the same field decides whether a "not migrated yet" placeholder is drawn — so a wrong id is
        /// visible in both directions and provable in neither by a unit test that only reads one file.
        /// </summary>
        [Fact]
        public void Every_page_category_names_a_page_that_exists()
        {
            var page = SettingsPagesSource();

            foreach (var category in Manifest().GetProperty("categories").EnumerateObject()
                         .Where(c => c.Name.Contains('.')))
            {
                Assert.True(category.Value.TryGetProperty("legacyOptionPageId", out var id),
                    $"Category '{category.Name}' declares no legacyOptionPageId, so it has no node in the " +
                    "Visual Studio 2022 Options tree.");

                var value = id.GetString()!;
                Assert.True(Guid.TryParse(value, out _),
                    $"Category '{category.Name}' has a legacyOptionPageId that is not a GUID: '{value}'.");
                Assert.True(page.Contains(value, StringComparison.OrdinalIgnoreCase),
                    $"Category '{category.Name}' names page '{value}', which SettingsPages.cs does not declare.");
            }
        }

        /// <summary>
        /// The pages' source, read as TEXT: they live in a net472 project this net10 suite cannot
        /// reference, and comparing the two FILES is the point — the drift being guarded is between a
        /// manifest and a page, not within either.
        /// </summary>
        static string SettingsPagesSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(
                    dir.FullName, "src", "CodeWicket.VSExtension", "SettingsPages.cs");
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);
                dir = dir.Parent;
            }

            throw new FileNotFoundException("Could not locate SettingsPages.cs above " + AppContext.BaseDirectory);
        }
    }
}
