using System;
using System.Collections.Generic;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The record behind the Settings UI's change notifications (issue #271). Unified Settings pulls
    /// a value once per row and again only for a moniker the provider NAMES, so what this record
    /// names — and what it stays quiet about — is what the page shows after a banner writes a rule.
    /// </summary>
    public sealed class ServedSettingsTests
    {
        private const string Commands = "codeWicket.permissions.externalRegion.allowedCommands";
        private const string Paths = "codeWicket.permissions.externalRegion.allowedPaths";

        private static Func<string, object?> Store(params (string moniker, object? value)[] entries)
        {
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (moniker, value) in entries)
                map[moniker] = value;
            return m => map.TryGetValue(m, out var v) ? v : null;
        }

        [Fact]
        public void AServedValueThatMovedIsNamed()
        {
            // The issue's shape: the page read the list, then the banner appended a rule.
            var served = new ServedSettings();
            served.Served(Commands, "git status");

            var changed = served.Reconcile(Store((Commands, "git status\ngit push")));

            Assert.Equal(new[] { Commands }, changed);
        }

        [Fact]
        public void AMonikerNeverServedIsNeverNamed()
        {
            // No row has asked for it, so no row holds a stale copy; naming it would only make USX
            // pull a setting nobody is looking at.
            var served = new ServedSettings();
            served.Served(Commands, "git status");

            var changed = served.Reconcile(Store((Commands, "git status"), (Paths, "src/**")));

            Assert.Empty(changed);
        }

        [Fact]
        public void AChangeIsNamedOnceBecauseTheReconcileReRecords()
        {
            // Every config write reconciles, and most writes (zoom, the picker) touch nothing the page
            // serves — so a change must not be re-announced on every later write until the row happens
            // to re-pull. The reconcile that names it also records it.
            var served = new ServedSettings();
            served.Served(Commands, "git status");
            var store = Store((Commands, "git status\ngit push"));

            var first = served.Reconcile(store);
            var second = served.Reconcile(store);

            Assert.Equal(new[] { Commands }, first);
            Assert.Empty(second);
        }

        [Fact]
        public void OnlyTheMonikersThatMovedAreNamed()
        {
            var served = new ServedSettings();
            served.Served(Commands, "git status");
            served.Served(Paths, "src/**");

            var changed = served.Reconcile(Store((Commands, "git status"), (Paths, "src/**\ntests/**")));

            Assert.Equal(new[] { Paths }, changed);
        }

        [Fact]
        public void AValueTheStoreCannotSupplyLeavesTheRecordAlone()
        {
            // Null is "could not read it", not a new value: naming it would make the row re-pull into
            // a failure, and the record must still hold what the row last saw for the next reconcile.
            var served = new ServedSettings();
            served.Served(Commands, "git status");

            var changed = served.Reconcile(Store((Commands, null)));
            var later = served.Reconcile(Store((Commands, "git status\ngit push")));

            Assert.Empty(changed);
            Assert.Equal(new[] { Commands }, later);
        }

        [Fact]
        public void BoxedValuesCompareByValueNotByReference()
        {
            // Every serve boxes afresh (a bool, an int, a joined string), so reference equality would
            // name every served setting on every write — a full-page re-pull per zoom tick.
            var served = new ServedSettings();
            served.Served("codeWicket.tools.externalRegion.runCommand", true);
            served.Served("codeWicket.advanced.externalRegion.storage.attachmentLimitMb", 100);

            var changed = served.Reconcile(Store(
                ("codeWicket.tools.externalRegion.runCommand", true),
                ("codeWicket.advanced.externalRegion.storage.attachmentLimitMb", 100)));

            Assert.Empty(changed);
        }

        [Fact]
        public void MonikersMatchCaseInsensitivelyAsUsxDoes()
        {
            // USX's AppliesTo compares OrdinalIgnoreCase; the record must not hold two entries for one
            // row because a caller spelled the moniker differently.
            var served = new ServedSettings();
            served.Served(Commands, "git status");
            served.Served(Commands.ToUpperInvariant(), "git status");

            var changed = served.Reconcile(Store((Commands, "git push")));

            Assert.Single(changed);
        }
    }
}
