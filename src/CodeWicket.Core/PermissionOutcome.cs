namespace CodeWicket.Core
{
    /// <summary>
    /// How a tool call came to be permitted — the fact a transcript row reports so the reader can tell
    /// the three silences apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this existed a row that ran without a prompt could mean any of: our policy auto-allowed it
    /// by a rule, the user approved it minutes ago, or the BACKEND never asked us at all. They rendered
    /// identically, so "why is this being auto-approved?" was unanswerable from the transcript — and in
    /// a measured session the answer was the third one: of three tool calls
    /// exactly one reached our policy, and it prompted. Nothing was being auto-approved; we were simply
    /// never consulted, which is the one possibility the UI gave no way to see.
    /// </para>
    /// <para>
    /// <see cref="PermissionOutcomeKind.NotRequested"/> is therefore recorded EXPLICITLY rather than
    /// inferred from a missing value, because absence already means something else — see the kind's own
    /// note. This is the <c>UsageReport</c> rule applied to a different field: a null means "not
    /// reported", never a value.
    /// </para>
    /// </remarks>
    public sealed record PermissionOutcome(
        PermissionOutcomeKind Kind,
        string? Rule = null,
        bool RulePersisted = false,
        bool CautionPrompted = false,
        string? CautionReason = null);

    /// <summary>
    /// The permission states a tool row can be in. Note there is no member for "we don't know": that is
    /// the ABSENCE of a <see cref="PermissionOutcome"/>, and keeping the two apart is the point — see
    /// <see cref="NotRequested"/>.
    /// </summary>
    public enum PermissionOutcomeKind
    {
        /// <summary>
        /// No permission request ever reached the host: the backend decided by its own rules and never
        /// consulted us. We cannot say what it decided or why — only that we were not asked.
        /// <para>
        /// Deliberately NOT the same as a null <see cref="PermissionOutcome"/>, which means "this
        /// conversation has no record" — an older log, or history replayed from a resumed CLI session,
        /// which never carried our decisions and never will. Conflating them would put the original
        /// complaint back one layer down, and the resume case means it would never age out.
        /// </para>
        /// </summary>
        NotRequested,

        /// <summary>The user answered the banner and allowed it.</summary>
        UserAllowed,

        /// <summary>The user answered the banner and refused it.</summary>
        UserDenied,

        /// <summary>A policy rule allowed it with no prompt. <see cref="PermissionOutcome.Rule"/> names
        /// which, and <see cref="PermissionOutcome.RulePersisted"/> says whether it outlives the session.</summary>
        RuleAllowed,

        /// <summary>A policy rule refused it with no prompt (the user's own in-session "Deny always").</summary>
        RuleDenied,

        /// <summary>The active permission MODE allowed it — its resolved risk sat at or below the ceiling.</summary>
        ModeAllowed,
    }
}
