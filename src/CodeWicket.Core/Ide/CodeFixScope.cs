namespace CodeWicket.Core.Ide
{
    /// <summary>How far one <c>apply_code_fix</c> call reaches (issue #91).</summary>
    /// <remarks>
    /// Deliberately only two values, though Roslyn's own <c>FixAllScope</c> also offers Project and
    /// Solution. Tool risk resolves by tool NAME — <c>apply_code_fix</c> is authored <c>Edit</c> — so a
    /// solution-wide fix-all would auto-approve under AcceptEdits exactly like a one-line fix. Capping at
    /// one document keeps the blast radius of a single approval to a single file without needing either a
    /// second tool name or argument-derived risk, both of which were considered and rejected. Widening it
    /// is a permission decision first and an API change second.
    /// </remarks>
    public enum CodeFixScope
    {
        /// <summary>One occurrence, at a given line (and optionally column). The default and the original behaviour.</summary>
        Line,

        /// <summary>Every occurrence of one diagnostic id in the file, via Roslyn's fix-all.</summary>
        Document,
    }

    /// <summary>
    /// Parsing and argument validation for <c>apply_code_fix</c>'s <c>scope</c>, kept out of the VS-bound
    /// tool catalog so it can be tested without devenv — the same reason
    /// <see cref="WorkspaceDiagnostics"/> and <see cref="TableValue"/> live here.
    /// </summary>
    public static class CodeFixScopes
    {
        /// <summary>
        /// Parses the <c>scope</c> argument. Absent or empty is <see cref="CodeFixScope.Line"/>, which is
        /// what every caller written before the argument existed sends. Returns false with a message the
        /// agent can act on.
        /// </summary>
        /// <remarks>
        /// "file" is accepted as a synonym for "document" because it is the word a model reaches for when
        /// talking about a file, and refusing a synonym costs a whole round trip to learn nothing.
        /// "project" and "solution" get their OWN refusal rather than the generic one: they are real
        /// <c>FixAllScope</c> values a model may reasonably try, so the useful answer is what to do instead
        /// (call once per file), not a list of the two strings we happen to accept.
        /// </remarks>
        public static bool TryParse(string? argument, out CodeFixScope scope, out string? error)
        {
            scope = CodeFixScope.Line;
            error = null;
            // Explicit null check, not IsNullOrWhiteSpace: net472's overload carries no [NotNullWhen], so
            // the shared slice would warn on every use of 'argument' below.
            var normalized = argument?.Trim();
            if (normalized is null || normalized.Length == 0)
                return true;

            switch (normalized.ToLowerInvariant())
            {
                case "line":
                    return true;
                case "document":
                case "file":
                    scope = CodeFixScope.Document;
                    return true;
                case "project":
                case "solution":
                    error = $"scope '{normalized}' is not supported — a fix-all is capped at one document. " +
                            "Call apply_code_fix once per file with scope 'document'.";
                    return false;
                default:
                    error = $"unknown scope '{argument}' — use 'line' (default, one occurrence) or 'document' " +
                            "(every occurrence of one diagnosticId in the file).";
                    return false;
            }
        }

        /// <summary>
        /// The argument each scope requires, or null when the request is well-formed. A line scope is a
        /// POSITION and a document scope is a RULE, so each needs precisely what the other treats as
        /// optional — checked separately so the message names the one thing missing rather than restating
        /// the schema.
        /// </summary>
        public static string? Validate(CodeFixScope scope, int line, string? diagnosticId)
        {
            if (scope == CodeFixScope.Line && line < 1)
                return "the 'line' argument (1-based) is required";

            if (scope == CodeFixScope.Document && string.IsNullOrWhiteSpace(diagnosticId))
                return "scope 'document' fixes every occurrence of ONE diagnostic id, so 'diagnosticId' is required " +
                       "(e.g. 'IDE0055'). Run get_diagnostics on the file to see which ids it reports.";

            return null;
        }
    }
}
