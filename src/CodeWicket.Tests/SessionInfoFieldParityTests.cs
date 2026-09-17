using System;
using System.Linq;
using System.Reflection;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A session fact crosses the engine-to-shell wire in three hand-copied places: the session's
    /// <see cref="SessionNegotiation"/>, the IPC <see cref="SessionInfoResponse"/>, and the panel's
    /// <see cref="SessionInfoResponseView"/>. A field added to two of them and not the third is
    /// dropped in transit in silence — the <c>WorkspaceSnapshot</c> lesson, where the formatter and
    /// the snapshot both carried a field the DTO did not, and the panel drew nothing with every
    /// offline check green. <see cref="SessionInfoWireTests"/> proves the VALUES survive the wire;
    /// this walks the three types by NAME so a field cannot be left out of one of them. The one
    /// field spelled differently on purpose is listed; anything else that differs fails.
    /// </summary>
    public sealed class SessionInfoFieldParityTests
    {
        // OpenedAt crosses as an ISO string (OpenedAtUtc) because the DTOs are flat text.
        private static readonly (string Core, string Dto, string View)[] Renamed =
        {
            ("OpenedAt", "OpenedAtUtc", "OpenedAt"),
        };

        // Known to the ENGINE rather than settled by the handshake — which backend the live session
        // belongs to — so they have a DTO and a view field and no Core source. Listed by name so a
        // third such field has to be added here on purpose rather than slip through.
        private static readonly string[] EngineKnown = { "ProviderId", "ProviderDisplayName" };

        private static string[] Names(Type t) =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .Where(n => n != "EqualityContract")
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

        [Fact]
        public void EveryNegotiationFieldReachesTheDtoAndThePanelView()
        {
            var core = Names(typeof(SessionNegotiation));
            var dto = Names(typeof(SessionInfoResponse));
            var view = Names(typeof(SessionInfoResponseView));

            foreach (var name in core)
            {
                var rename = Renamed.FirstOrDefault(r => r.Core == name);
                var onDto = rename.Core is null ? name : rename.Dto;
                var onView = rename.Core is null ? name : rename.View;
                Assert.True(dto.Contains(onDto), $"{name} is on SessionNegotiation but not on the IPC SessionInfoResponse: it would be dropped in transit.");
                Assert.True(view.Contains(onView), $"{name} is on SessionNegotiation but not on the panel's SessionInfoResponseView: it would be read and thrown away.");
            }

            // And nothing invented on the way: a DTO or view field with no source can never be filled,
            // and reads as "not reported" forever.
            foreach (var name in dto.Except(EngineKnown))
            {
                var rename = Renamed.FirstOrDefault(r => r.Dto == name);
                Assert.True(core.Contains(rename.Core ?? name), $"{name} is on the DTO but has no source on SessionNegotiation.");
            }
            foreach (var name in view.Except(EngineKnown))
            {
                var rename = Renamed.FirstOrDefault(r => r.View == name);
                Assert.True(core.Contains(rename.Core ?? name), $"{name} is on the panel view but has no source on SessionNegotiation.");
            }

            // And the engine-known pair reaches the view, or the panel could never say which backend
            // the connected session is.
            foreach (var name in EngineKnown)
            {
                Assert.Contains(name, dto);
                Assert.Contains(name, view);
            }
        }
    }
}
