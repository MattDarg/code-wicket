using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Providers.Kiro;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The Kiro model-list cache (<see cref="KiroAgentProvider.Models"/>): a successful catalog is cached
    /// for the engine's lifetime; a failed probe caches the "Kiro default" fallback only until the next
    /// read past the retry cooldown, so a transient launch-time timeout (slow/proxied network) self-heals
    /// instead of stranding the picker on the fallback until VS restarts. Exercised through the internal
    /// fetch+clock seam so nothing shells out to kiro-cli or waits real time.
    /// </summary>
    public sealed class KiroModelCacheTests
    {
        // Must match KiroAgentProvider.RetryCooldown.
        private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

        private static IReadOnlyList<ModelInfo> Live(params string[] ids) =>
            ids.Select(id => new ModelInfo(id, id)).ToList();

        private static IReadOnlyList<ModelInfo> Empty() => Array.Empty<ModelInfo>();

        // A path Process.Start cannot launch, so KiroLoginState's own probe fails fast instead of
        // really shelling out to kiro-cli (same trick as AcpPreflightTests).
        private const string UnlaunchableCli = "cwkt-not-a-real-kiro-cli.exe";

        // Drains whatever background probe is in flight, so an assertion never races one. Loops because
        // a read can start a fresh probe (the cooldown path), and a single await would miss it.
        private static async Task Settle(KiroAgentProvider provider)
        {
            for (var i = 0; i < 10 && provider.PendingModelRefresh is { } probe; i++)
                await probe;
        }

        // A fetch the test can hold open. Without it "the read didn't wait for the shell-out" is a race
        // rather than an assertion — the point is to check what Models returns WHILE the probe is stuck.
        private sealed class GatedFetch
        {
            private readonly ManualResetEventSlim _release = new(false);
            private readonly IReadOnlyList<ModelInfo> _result;

            public GatedFetch(IReadOnlyList<ModelInfo> result) => _result = result;

            public IReadOnlyList<ModelInfo> Fetch(string _)
            {
                _release.Wait(TimeSpan.FromSeconds(10));
                return _result;
            }

            public void Release() => _release.Set();
        }

        // A controllable clock the provider reads through its _utcNow seam.
        private sealed class FakeClock
        {
            public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            public void Advance(TimeSpan by) => Now += by;
        }

        // Wires a provider over a fetch that counts its calls and the fake clock; `fetchCount` reads the
        // running call count.
        private static KiroAgentProvider Provider(
            Func<string, IReadOnlyList<ModelInfo>> fetch, FakeClock clock, out Func<int> fetchCount)
        {
            var count = 0;
            fetchCount = () => count;
            Func<string, IReadOnlyList<ModelInfo>> counting = cli => { count++; return fetch(cli); };
            return new KiroAgentProvider(new KiroProviderOptions(), counting, () => clock.Now);
        }

        [Fact]
        public void SuccessfulCatalog_IsCachedForLifetime_NeverReprobes()
        {
            var clock = new FakeClock();
            var p = Provider(_ => Live("auto", "sonnet"), clock, out var fetches);

            var first = p.Models;
            Assert.Equal(new[] { "auto", "sonnet" }, first.Select(m => m.Id));
            Assert.Equal(1, fetches());

            // Even long past the cooldown, a real list is authoritative — no re-probe.
            clock.Advance(Cooldown + TimeSpan.FromMinutes(5));
            var second = p.Models;
            Assert.Same(first, second);
            Assert.Equal(1, fetches());
        }

        [Fact]
        public void FailedProbe_ReturnsFallback_AndDoesNotReprobeWithinCooldown()
        {
            var clock = new FakeClock();
            var p = Provider(_ => Empty(), clock, out var fetches);

            var first = p.Models;
            Assert.Equal(new[] { "default" }, first.Select(m => m.Id)); // the synthetic fallback
            Assert.Equal(1, fetches());

            // Repeated reads inside the cooldown reuse the cached fallback — no shell-out storm.
            clock.Advance(Cooldown - TimeSpan.FromSeconds(1));
            _ = p.Models;
            _ = p.Models;
            Assert.Equal(1, fetches());
        }

        [Fact]
        public void FailedProbe_ReprobesOnFirstReadPastCooldown()
        {
            var clock = new FakeClock();
            var p = Provider(_ => Empty(), clock, out var fetches);

            _ = p.Models;
            Assert.Equal(1, fetches());

            clock.Advance(Cooldown + TimeSpan.FromSeconds(1));
            _ = p.Models;
            Assert.Equal(2, fetches());
        }

        [Fact]
        public void FailThenSucceed_SelfHeals_ThenCachesForLifetime()
        {
            var clock = new FakeClock();
            IReadOnlyList<ModelInfo> result = Empty();
            var p = Provider(_ => result, clock, out var fetches);

            // First probe fails -> fallback.
            Assert.Equal(new[] { "default" }, p.Models.Select(m => m.Id));
            Assert.Equal(1, fetches());

            // Network recovers; the first read past the cooldown picks up the real list.
            result = Live("auto", "haiku");
            clock.Advance(Cooldown + TimeSpan.FromSeconds(1));
            Assert.Equal(new[] { "auto", "haiku" }, p.Models.Select(m => m.Id));
            Assert.Equal(2, fetches());

            // Now live, it's authoritative — a later CLI failure never re-probes or reverts to fallback.
            result = Empty();
            clock.Advance(Cooldown + TimeSpan.FromMinutes(10));
            Assert.Equal(new[] { "auto", "haiku" }, p.Models.Select(m => m.Id));
            Assert.Equal(2, fetches());
        }

        [Fact]
        public async Task SeededProvider_ShowsSeed_NotBareFallback_WhenProbeFails_ThenLiveSupersedes()
        {
            var clock = new FakeClock();
            IReadOnlyList<ModelInfo> result = Empty();
            var p = Provider(_ => result, clock, out var fetches);

            // A prior run's persisted list (via CWKT_MODEL_CACHE -> SeedModels).
            p.SeedModels(Live("auto", "sonnet", "haiku"));

            // Probe fails -> the picker shows the seed, NOT the single "Kiro default" fallback.
            p.BeginModelRefresh();
            await Settle(p);
            Assert.Equal(new[] { "auto", "sonnet", "haiku" }, p.Models.Select(m => m.Id));
            Assert.Equal(1, fetches());

            // The seed isn't treated as live: past the cooldown a read re-probes and a recovered network
            // wins. The read still answers from the seed — it starts the probe, it doesn't wait for it —
            // so the live list lands once that probe completes.
            result = Live("auto", "glm-5");
            clock.Advance(Cooldown + TimeSpan.FromSeconds(1));
            Assert.Equal(new[] { "auto", "sonnet", "haiku" }, p.Models.Select(m => m.Id));
            await Settle(p);
            Assert.Equal(new[] { "auto", "glm-5" }, p.Models.Select(m => m.Id));
            Assert.Equal(2, fetches());
        }

        [Fact]
        public async Task SeededProvider_AnswersFromTheSeedWithoutWaitingForTheProbe()
        {
            // The behaviour the whole change exists for. Previously a seeded provider still shelled out
            // on first read, and first read is engine/listProviders — so opening the chat window waited
            // ~1.5 s for kiro-cli (`--list-models` 967-1484 ms plus `settings chat.defaultModel` ~500 ms,
            // timed) to re-derive a list we had persisted ourselves and just handed back in.
            var clock = new FakeClock();
            var gate = new GatedFetch(Live("auto", "glm-5"));
            var p = new KiroAgentProvider(new KiroProviderOptions(), gate.Fetch, () => clock.Now);
            p.SeedModels(Live("auto", "sonnet", "haiku"));

            var pushed = new TaskCompletionSource<IReadOnlyList<ModelInfo>>();
            p.BeginModelRefresh(models => pushed.TrySetResult(models));
            var probe = p.PendingModelRefresh;
            Assert.NotNull(probe); // the probe is running, and HELD by the gate

            // Asserted while the fetch is still blocked: if the read waited on the probe, this line
            // could not return at all. That is the load-bearing part — not which list comes back.
            Assert.Equal(new[] { "auto", "sonnet", "haiku" }, p.Models.Select(m => m.Id));

            gate.Release();
            await probe!;

            // ...and the live list still supersedes the seed, only asynchronously now. It reaches the
            // picker as a push (ProviderModelsDto) because Kiro's session/new carries no models.
            Assert.Equal(new[] { "auto", "glm-5" }, (await pushed.Task).Select(m => m.Id));
            Assert.Equal(new[] { "auto", "glm-5" }, p.Models.Select(m => m.Id));
        }

        [Fact]
        public void SessionStart_WaitsForTheProbe_RatherThanSpawningASecondOne()
        {
            // Found in Visual Studio, not by reasoning. The login gate (KiroLoginState.IsSignedOut) runs the SAME
            // --list-models command as the model probe and short-circuits on the flag TryList sets on
            // success. While the probe ran inline inside listProviders it always finished before any
            // session started, so the gate spawned nothing. Backgrounding it broke that silently: the
            // session now starts 243-1243 ms in against a ~1.5 s probe, so the gate spawned a SECOND
            // identical CLI process alongside the first. Measured across eight sessions, the session's
            // own spawn went from ~500 ms to 1479-1802 ms and Kiro's readiness did not improve at all -
            // the whole point of the change, cancelled out, with nothing failing.
            KiroLoginState.ResetForTests(); // process-wide; another test signalling it would void this
            var clock = new FakeClock();
            var gate = new GatedFetch(Live("auto", "sonnet"));
            // An unlaunchable CLI so the login gate itself returns immediately: the only thing this can
            // be measuring is our wait. Driven through Preflight rather than the wait method, because
            // the WIRING is what regressed - a version of this test that called the wait directly
            // passed with the override gutted.
            var p = new KiroAgentProvider(
                new KiroProviderOptions { CliPath = UnlaunchableCli }, gate.Fetch, () => clock.Now);
            p.SeedModels(Live("auto", "sonnet"));
            p.BeginModelRefresh();

            var returned = new ManualResetEventSlim(false);
            var waiter = new Thread(() => { p.PreflightForTests(); returned.Set(); })
            {
                IsBackground = true,
            };
            waiter.Start();

            // The probe is held. If session start raced it instead of waiting, this would return now -
            // and go on to spawn the duplicate.
            Assert.False(returned.Wait(TimeSpan.FromMilliseconds(300)));

            gate.Release();
            Assert.True(returned.Wait(TimeSpan.FromSeconds(10)));
        }

        [Fact]
        public void SessionStart_DoesNotWaitWhenNoProbeIsRunning()
        {
            // The gate keeps its own probe for the case the wait exists to skip: nothing in flight (a
            // first-ever launch, or a probe that already finished) must not block at all.
            var clock = new FakeClock();
            var p = new KiroAgentProvider(
                new KiroProviderOptions { CliPath = UnlaunchableCli }, _ => Live("auto"), () => clock.Now);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            p.PreflightForTests();
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 500, $"waited {sw.ElapsedMilliseconds} ms with no probe running");
        }

        [Fact]
        public void SessionStart_StopsWaitingWhenLoginIsKNOWN_NotWhenTheProbeEnds()
        {
            // The gate needs one fact - are we signed in - and KiroModelCatalog.TryList establishes it
            // on the FIRST of its two CLI calls, then spends ~500 ms more on `settings chat.defaultModel`
            // purely to order the picker. Waiting for the whole probe waited out a call the gate has no
            // interest in, on the path to the user's first prompt.
            KiroLoginState.ResetForTests();
            var clock = new FakeClock();
            var release = new ManualResetEventSlim(false);

            // Stands in for TryList: signal login the moment the first call succeeds, then keep working.
            Func<string, IReadOnlyList<ModelInfo>> fetch = _ =>
            {
                KiroLoginState.NoteSignedIn();
                release.Wait(TimeSpan.FromSeconds(10)); // the second CLI call
                return Live("auto", "sonnet");
            };

            var p = new KiroAgentProvider(
                new KiroProviderOptions { CliPath = UnlaunchableCli }, fetch, () => clock.Now);
            p.SeedModels(Live("auto", "sonnet"));
            p.BeginModelRefresh();

            var returned = new ManualResetEventSlim(false);
            new Thread(() => { p.PreflightForTests(); returned.Set(); }) { IsBackground = true }.Start();

            // Returns while the probe is STILL RUNNING. Waiting for the probe instead would sit here
            // until the release below, which is the behaviour this replaced.
            Assert.True(returned.Wait(TimeSpan.FromSeconds(5)), "Preflight waited for the whole probe");
            Assert.NotNull(p.PendingModelRefresh); // ...and it really had not finished

            release.Set();
        }

        [Fact]
        public async Task SessionStart_DoesNotWaitAtAllOnceLoginIsAlreadyKnown()
        {
            // The second session in a window, and every one after it: nothing to establish, so the wait
            // must collapse to nothing even with a probe in flight.
            KiroLoginState.ResetForTests();
            KiroLoginState.NoteSignedIn();
            var clock = new FakeClock();
            var gate = new GatedFetch(Live("auto"));
            var p = new KiroAgentProvider(
                new KiroProviderOptions { CliPath = UnlaunchableCli }, gate.Fetch, () => clock.Now);
            p.SeedModels(Live("auto"));
            p.BeginModelRefresh();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            p.PreflightForTests();
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 500, $"waited {sw.ElapsedMilliseconds} ms with login already known");

            gate.Release();
            var probe = p.PendingModelRefresh;
            if (probe is not null)
                await probe;
        }

        [Fact]
        public async Task ARefreshMatchingTheSeedIsNotPushed()
        {
            // The steady state: the cache was right. A push here would rebuild the model list under a
            // user who may have the dropdown open, to replace it with the identical thing.
            var clock = new FakeClock();
            var gate = new GatedFetch(Live("auto", "sonnet"));
            var p = new KiroAgentProvider(new KiroProviderOptions(), gate.Fetch, () => clock.Now);
            p.SeedModels(Live("auto", "sonnet"));

            var pushes = 0;
            p.BeginModelRefresh(_ => Interlocked.Increment(ref pushes));
            var probe = p.PendingModelRefresh;
            gate.Release();
            if (probe is not null)
                await probe;

            Assert.Equal(0, pushes);
            Assert.Equal(new[] { "auto", "sonnet" }, p.Models.Select(m => m.Id));
        }

        [Fact]
        public async Task AReorderedListIsPushed_BecauseModelsZeroIsTheDefault()
        {
            // Same membership, different order. The catalog puts the user's configured default first and
            // the picker selects Models[0], so treating this as "no change" would leave the picker
            // defaulting to a model Kiro no longer starts on.
            var clock = new FakeClock();
            var gate = new GatedFetch(Live("sonnet", "auto"));
            var p = new KiroAgentProvider(new KiroProviderOptions(), gate.Fetch, () => clock.Now);
            p.SeedModels(Live("auto", "sonnet"));

            var pushed = new TaskCompletionSource<IReadOnlyList<ModelInfo>>();
            p.BeginModelRefresh(models => pushed.TrySetResult(models));
            var probe = p.PendingModelRefresh;
            gate.Release();
            if (probe is not null)
                await probe;

            Assert.Equal(new[] { "sonnet", "auto" }, (await pushed.Task).Select(m => m.Id));
        }

        [Fact]
        public async Task ConcurrentFirstAccess_ProbesExactlyOnce()
        {
            var clock = new FakeClock();
            var p = Provider(_ => Live("auto"), clock, out var fetches);

            // Many readers hitting the empty cache at once must collapse to a single probe: the gate is
            // held across the fetch and the first success sets the live flag the rest short-circuit on.
            var readers = Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => p.Models))
                .ToArray();
            await Task.WhenAll(readers);

            Assert.Equal(1, fetches());
            Assert.All(readers, t => Assert.Equal(new[] { "auto" }, t.Result.Select(m => m.Id)));
        }
    }
}
