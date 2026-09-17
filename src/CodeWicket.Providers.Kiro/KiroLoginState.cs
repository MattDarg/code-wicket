using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace CodeWicket.Providers.Kiro
{
    /// <summary>
    /// Whether kiro-cli currently has a usable login, so a signed-out CLI is never launched in a way
    /// that starts a browser login we would then break.
    /// <para><b>Why a gate at all.</b> kiro-cli answers a credential-less command by starting an OAuth
    /// login: it opens the browser and listens on 127.0.0.1:&lt;port&gt; for the redirect back. Every
    /// process we start it in is headless (<c>CreateNoWindow</c>, both pipes redirected) and bounded by a
    /// timeout, so the listener dies mid-flow and the user's browser lands on a refused port — a login
    /// that cannot complete.</para>
    /// <para><b>Scope: <c>kiro-cli acp</c> ONLY.</b> Everything else goes through the <c>chat</c>
    /// subcommand's <c>--no-interactive</c>, which suppresses the flow at source and needs no gate.
    /// <c>acp</c> rejects that flag outright (clap parse error), which is the entire reason this exists.
    /// If <c>acp</c> ever gains it, prefer it and delete this class.</para>
    /// <para><b>The probe is <c>chat --no-interactive --list-models</c>, deliberately NOT
    /// <c>whoami</c>.</b> `whoami` only reads the stored credentials, so it reports a DEAD token as a
    /// good login — measured 2026-07-29, where it said "Logged in with Builder ID" at the same moment the
    /// v3 auth callback was failing with "You are not logged in", and only flipped to "Not logged in"
    /// after that refresh attempt had rejected and cleared the store. Gating on it would therefore let
    /// exactly the expired-credential case through, which is the case most likely to strand a browser.
    /// The <c>chat</c> probe exercises more of the auth path and cannot itself open anything.</para>
    /// <para><b>Residual, stated honestly:</b> nothing short of an actual model call proves a token is
    /// live, so a probe that passes is not a guarantee. That is why this is a gate and not the whole
    /// story — the failure paths downstream (Kiro's own `authMethods` guidance, and
    /// <see cref="KiroKasTokenProvider"/> relaying "You are not logged in. Please log in with
    /// `kiro-cli login`.") have to stay good regardless.</para>
    /// </summary>
    internal static class KiroLoginState
    {
        // Matches the CLI's own wording: "Not logged in. Set the KIRO_API_KEY environment variable or
        // run `kiro-cli login` first." Only this phrasing counts as a definitive negative — see
        // IsSignedOut on why anything less certain must not block.
        private const string SignedOutMarker = "not logged in";

        // Generous enough to cover a proxied/TLS-intercepted round trip (the same reason
        // KiroModelCatalog uses 10s), since a timeout here is treated as "no answer" and lets the launch
        // proceed — being slow must not read as being signed out.
        private const int TimeoutMs = 10000;

        // A positive is cached for the engine's lifetime: it is a weak claim regardless (see the
        // residual note above), so re-paying the probe for it buys nothing. A negative is deliberately
        // NOT cached, so signing in from a terminal recovers the next session without restarting VS.
        private static volatile bool _knownLoggedIn;

        // Completes the moment a credentialed call succeeds — which happens on the FIRST of
        // KiroModelCatalog.TryList's two CLI calls, not at the end of the probe. That distinction is
        // worth a signal of its own: the session gate only needs to know we are signed in, and waiting
        // for the whole probe made it also wait out `settings chat.defaultModel` (~500 ms, timed), a
        // call that exists to order the picker and tells the gate nothing. See
        // KiroAgentProvider.WaitForPendingModelRefresh.
        private static TaskCompletionSource<bool> _signedIn =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when a credentialed kiro-cli call has succeeded. Never faults.</summary>
        internal static Task SignedIn => _signedIn.Task;

        /// <summary>Whether a credentialed call has already succeeded, so the gate would short-circuit.</summary>
        internal static bool IsKnownSignedIn => _knownLoggedIn;

        // Process-wide state, so a test that depends on it being unset has to be able to say so.
        internal static void ResetForTests()
        {
            _knownLoggedIn = false;
            _signedIn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Records that a kiro-cli call requiring credentials just succeeded. Called by
        /// <see cref="KiroModelCatalog"/>, which runs the same probe command for the model picker — so on
        /// the normal path (window opens, picker populates, user sends) the gate below costs nothing at
        /// all rather than spawning a second, identical process.
        /// </summary>
        public static void NoteSignedIn()
        {
            _knownLoggedIn = true;
            _signedIn.TrySetResult(true);
        }

        /// <summary>
        /// True only when kiro-cli <em>definitively</em> reports no login. Every other outcome — signed
        /// in, the CLI missing, a timeout, a crash, an unrecognised error — is false, because this gates
        /// work that would otherwise happen: turning "can't tell" into "you aren't signed in" both blocks
        /// a session that might have worked AND sends the user off to fix a login that may be perfectly
        /// fine. Those cases fall through to the normal path and fail with their own message.
        /// </summary>
        public static bool IsSignedOut(string cliPath)
        {
            if (_knownLoggedIn)
                return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = string.IsNullOrEmpty(cliPath) ? "kiro-cli" : cliPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var arg in KiroModelCatalog.ListModelsArgs)
                    psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process is null)
                    return false;

                // Drain both pipes before waiting, so a chatty CLI can't deadlock on a full buffer.
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(TimeoutMs))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    return false;
                }

                if (process.ExitCode == 0)
                {
                    NoteSignedIn(); // through the same door, so the signal can't diverge from the flag
                    return false;
                }

                // The CLI reports this failure on stderr, but check both: which stream carries it is not
                // a contract, and getting it wrong here fails open (a missed match just means no gate).
                var output = stdoutTask.GetAwaiter().GetResult() + stderrTask.GetAwaiter().GetResult();
                return SaysSignedOut(output);
            }
            catch
            {
                // Can't tell — say so by saying nothing.
                return false;
            }
        }

        /// <summary>
        /// Whether a failed probe's output is kiro-cli saying there is no login, rather than any of the
        /// other reasons the command can fail (network, proxy, a service error). Matching on the CLI's
        /// wording is a hostage to fortune, so it fails OPEN: an unrecognised message means no gate, and
        /// the launch proceeds exactly as it did before this existed.
        /// </summary>
        internal static bool SaysSignedOut(string output) =>
            output is not null
            && output.IndexOf(SignedOutMarker, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
