using System;
using System.Diagnostics;

namespace CodeWicket.Shell.Sessions
{
    /// <summary>
    /// Which conversation a send was started for, as the lifetime counts them (issue #217). Opaque: a
    /// send compares it through <see cref="SessionLifetime.IsCurrent"/> and never reads a number out of it.
    /// </summary>
    public readonly struct SessionToken
    {
        internal SessionToken(int epoch) => Epoch = epoch;

        internal int Epoch { get; }
    }

    /// <summary>
    /// The lease a send holds from <see cref="SessionLifetime.Commit"/> until its turn returns. While it
    /// is outstanding the lifetime starts no warm session, so a retired turn's return comes before the
    /// warm start that replaces it by the lifetime's own state.
    /// </summary>
    public sealed class PromptedTurn
    {
        private readonly SessionLifetime _lifetime;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TurnReturn? _returned;

        internal PromptedTurn(SessionLifetime lifetime, SessionToken token, string? title)
        {
            _lifetime = lifetime;
            Token = token;
            Title = title;
        }

        /// <summary>The conversation this send was started for.</summary>
        public SessionToken Token { get; }

        internal string? Title { get; }

        internal TimeSpan Age => _clock.Elapsed;

        /// <summary>
        /// The turn has returned. Idempotent: the first call realigns a retired turn's epochs and ends the
        /// lease, and every later call answers the same without doing either again.
        /// </summary>
        public TurnReturn Return() => _returned ??= _lifetime.ReturnTurn(this);
    }

    /// <summary>What a turn's return found.</summary>
    public sealed class TurnReturn
    {
        internal TurnReturn(bool wasRetired) => WasRetired = wasRetired;

        /// <summary>A workspace move retired the turn while it ran (issue #217).</summary>
        public bool WasRetired { get; }
    }
}
