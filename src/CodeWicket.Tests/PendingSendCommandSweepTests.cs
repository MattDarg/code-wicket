using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Every command a user can reach while a send is pending leaves no send behind: none parked with no banner
    /// to answer it, no pane busy with nothing running, and no transcript cleared around a pending send.
    /// </summary>
    /// <remarks>
    /// <para><b>Route-agnostic, which is the point.</b> <see cref="ConversationChangeOrderingTests"/> lists the
    /// routes that change the conversation; this runs every <see cref="ICommand"/> found by reflection on the
    /// pane, both history rows and the resume banner, in every phase, so a command added later is swept
    /// without anyone adding it to a list.</para>
    /// <para><b>A command is run only where a user could run it:</b> <see cref="ICommand.CanExecute"/> is
    /// honoured, because <c>RelayCommand.Execute</c> does not check it and a test that bypasses the gate
    /// reaches states no click can.</para>
    /// <para><b>What catches a send left behind.</b> Two assertions after the pane settles, plus the STA run's
    /// breach check, which fails the case when <c>ResetTranscriptItems</c> finds a send still pending as the
    /// transcript is cleared.</para>
    /// <para>The composer is empty in every phase (the send took it), so the send gestures are not offered and
    /// are not run.</para>
    /// </remarks>
    public sealed class PendingSendCommandSweepTests
    {
        /// <summary>Commands the sweep does not run. Every entry says why; an unexplained skip is a hole.</summary>
        private static readonly Dictionary<string, string> Denied = new(StringComparer.Ordinal)
        {
            ["pane:CopyTranscriptCommand"] =
                "writes the Windows clipboard, which is per user session and read by other STA tests (see StaTest)",
            ["pane:ContinueInTerminalCommand"] =
                "writes the Windows clipboard, for the same reason",
        };

        public static IEnumerable<object[]> Cases() =>
            from phase in Enum.GetValues(typeof(PendingPhase)).Cast<PendingPhase>()
            from command in CommandNames()
            where !Denied.ContainsKey(command)
            select new object[] { phase, command };

        [Theory]
        [MemberData(nameof(Cases))]
        public void EveryCommandWhileASendIsPendingLeavesNoSendBehind(PendingPhase phase, string command) =>
            StaTest.Run(() =>
            {
                using var s = PendingSendScenario.Reach(phase);
                var target = Resolve(s, command);
                if (target is null || !target.CanExecute(null))
                    return; // not offered in this phase: nothing a user can do with it

                target.Execute(null);
                s.Settle();

                Assert.False(s.Vm.HasPendingSendForDiagnostics && s.Vm.PendingResume is null,
                    $"{command} on {phase} left a send parked with no banner to answer it");
                Assert.False(s.Vm.IsBusy && s.Vm.PendingResume is null,
                    $"{command} on {phase} left the pane busy with nothing running and nothing to answer");
            }, withDispatcherContext: true);

        /// <summary>The denylist names real commands, gives each a reason, and leaves the sweep something to run.</summary>
        [Fact]
        public void EveryDeniedCommandExistsAndSaysWhy()
        {
            var names = CommandNames().ToList();
            foreach (var denied in Denied)
            {
                Assert.Contains(denied.Key, names);
                Assert.False(string.IsNullOrWhiteSpace(denied.Value), denied.Key + " is denied with no reason");
            }

            Assert.True(names.Count - Denied.Count >= 15, $"the sweep found only {names.Count} commands");
        }

        private static IEnumerable<string> CommandNames() =>
            Names(typeof(ChatViewModel), "pane")
                .Concat(Names(typeof(SessionSummaryViewModel), "current"))
                .Concat(Names(typeof(SessionSummaryViewModel), "other"))
                .Concat(Names(typeof(ResumeChoiceViewModel), "banner"));

        private static IEnumerable<string> Names(Type type, string owner) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => owner + ":" + p.Name);

        private static ICommand? Resolve(PendingSendScenario s, string command)
        {
            var colon = command.IndexOf(':');
            var owner = command.Substring(0, colon);
            var name = command.Substring(colon + 1);

            object? source;
            switch (owner)
            {
                case "pane":
                    source = s.Vm;
                    break;
                case "current":
                    s.Vm.RefreshHistory();
                    source = s.Vm.History.SingleOrDefault(h => h.IsCurrent);
                    break;
                case "other":
                    s.Vm.RefreshHistory();
                    source = s.Vm.History.SingleOrDefault(h => h.Id == s.OtherId);
                    break;
                case "banner":
                    source = s.Vm.PendingResume;
                    break;
                default:
                    throw new ArgumentException("unknown owner " + owner, nameof(command));
            }

            return source?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source) as ICommand;
        }
    }
}
