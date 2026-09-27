namespace CodeWicket.Shell.Sessions
{
    /// <summary>Where a live frame goes, as <see cref="SessionLifetime.Route"/> decides it.</summary>
    public enum FrameRoute
    {
        /// <summary>Onto the transcript on screen, recorded to its conversation.</summary>
        Live,

        /// <summary>Recorded to the live session's owner, which is not on screen (issue #256); never drawn.</summary>
        OffScreen,

        /// <summary>Nowhere: a retired turn's, an abandoned warm session's, or a deleted owner's.</summary>
        Drop,
    }
}
