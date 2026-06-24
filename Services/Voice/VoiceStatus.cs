namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Lifecycle state of the voice recogniser. Broadcast over SignalR as
    /// part of <see cref="VoiceStatusUpdate"/> so the on-screen mic button
    /// can render the right colour.
    /// </summary>
    public enum VoiceState
    {
        /// <summary>Mic button up; recogniser is constructed but not listening.</summary>
        Idle,
        /// <summary>PTT button held; recogniser is capturing audio.</summary>
        Listening,
        /// <summary>An utterance was recognised and matched a grammar rule.</summary>
        Heard,
        /// <summary>An utterance was heard but didn't match any rule (rejected).</summary>
        Unrecognised,
        /// <summary>The matched intent is being dispatched to the CAT layer.</summary>
        Executing,
        /// <summary>Something failed (no mic, no SAPI engine, grammar load failure).</summary>
        Error,
    }

    /// <summary>
    /// SignalR payload broadcast on every voice state change. <c>LastHeard</c>
    /// is the raw recognised phrase (for sanity-checking what SAPI thought it
    /// heard); <c>LastIntent</c> is the parsed semantic intent name (e.g.
    /// "SetFrequency"); <c>ErrorMessage</c> is set only when <c>State</c> is
    /// <see cref="VoiceState.Error"/>.
    /// </summary>
    public sealed record VoiceStatusUpdate(
        VoiceState State,
        string? LastHeard,
        string? LastIntent,
        string? ErrorMessage
    );
}
