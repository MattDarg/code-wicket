namespace CodeWicket.Core
{
    /// <summary>
    /// What each <see cref="PermissionMode"/> is CALLED to the user — the words on the header dropdown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One source, because the mode is now named in two places that a reader sees together: the picker
    /// they set it in, and the sentence on a tool row saying the mode is why the call ran. Those saying
    /// different things is the defect this exists to prevent — the row used to read
    /// <c>mode=AcceptReads(risk=Read)</c>, which is a LOG LINE, while the picker two inches below it said
    /// "Allow reads".
    /// </para>
    /// <para>
    /// The rule it makes concrete is one <c>PolicyPermissionHandler.Log</c> already states in the other
    /// direction: the string it writes to the log is a log line, and meaning must not be re-derived from
    /// it. Showing that same string to the user is the same mistake wearing the other hat — the log wants
    /// the mode AND the resolved risk that let the call through, the row wants the four words the user
    /// chose from. So the log keeps its string and the outcome carries this one.
    /// </para>
    /// <para>
    /// In Core rather than in the view-model that owns the picker because the POLICY needs it too, and
    /// the policy (Shell) cannot reference the UI.
    /// </para>
    /// </remarks>
    public static class PermissionModeLabel
    {
        /// <summary>The dropdown's words for <paramref name="mode"/>.</summary>
        public static string For(PermissionMode mode) => mode switch
        {
            PermissionMode.Prompt => "Ask each time",
            PermissionMode.AcceptReads => "Allow reads",
            PermissionMode.AcceptEdits => "Allow edits",
            PermissionMode.AcceptAll => "Allow all",
            // An unrecognised mode is a mode we have no words for. Its own name beats inventing some:
            // wrong-but-technical is recoverable, confidently-wrong prose is not.
            _ => mode.ToString(),
        };
    }
}
