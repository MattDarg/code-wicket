using System;
using System.Collections.Generic;

namespace CodeWicket.Shell
{
    /// <summary>
    /// What the Settings UI has been TOLD, per setting, so that a change to config.json can be
    /// reported as the monikers whose value it actually moved — and nothing else.
    /// <para>
    /// Unified Settings pulls an external setting's value once, when its row connects to the
    /// provider, and again only when the provider raises <c>SettingValuesChanged</c> naming it (issue
    /// #271: nothing re-asks when the page is shown, so a rule the banner wrote to config.json was
    /// invisible until devenv restarted). The provider therefore needs to know, on every config write,
    /// which of the values it has served are now stale. That is this record: <see cref="Served"/> is
    /// called with each value handed out, and <see cref="Reconcile"/> compares the lot against the
    /// store and names the differences.
    /// </para>
    /// <para>
    /// Only served monikers are ever named. A setting no row has asked for has no stale copy anywhere,
    /// and USX's <c>AppliesTo</c> is an exact (case-insensitive) match on the moniker the row itself
    /// uses — the same string it passed to <c>GetValueAsync</c> — so recording that string is what
    /// makes the notification land by construction, whatever shape the manifest gives a moniker.
    /// </para>
    /// <para>
    /// Every reconcile also RE-RECORDS the store's current values, so a change is named once: the
    /// next reconcile after a banner write sees the rule already recorded and stays quiet. And a value
    /// the store cannot currently supply (null) leaves the record alone — there is no change to name
    /// in "could not read it", and naming it would make USX re-pull a setting into a failure.
    /// </para>
    /// </summary>
    public sealed class ServedSettings
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, object?> _values =
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Records that <paramref name="value"/> was handed out for <paramref name="moniker"/>.</summary>
        public void Served(string moniker, object? value)
        {
            if (string.IsNullOrEmpty(moniker))
                return;
            lock (_gate)
                _values[moniker] = value;
        }

        /// <summary>
        /// Compares every served moniker against <paramref name="currentValueOf"/>, re-records the
        /// current values, and returns the monikers whose value differs from what was last served or
        /// recorded. Never names a moniker that was never served, and never names one whose current
        /// value is null.
        /// </summary>
        public IReadOnlyList<string> Reconcile(Func<string, object?> currentValueOf)
        {
            if (currentValueOf is null)
                throw new ArgumentNullException(nameof(currentValueOf));

            var changed = new List<string>();
            lock (_gate)
            {
                foreach (var moniker in new List<string>(_values.Keys))
                {
                    var now = currentValueOf(moniker);
                    if (now is null || Equals(now, _values[moniker]))
                        continue;

                    _values[moniker] = now;
                    changed.Add(moniker);
                }
            }

            return changed;
        }
    }
}
