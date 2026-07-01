namespace Yaesu_Web_Control.Services;

/// <summary>
/// Represents the operational state of one VC Tune preselector receiver
/// as maintained by <see cref="IVCTuneStateMachine"/>.
/// </summary>
public enum VCTuneState
{
    /// <summary>
    /// The VC Tune preselector is disengaged. The radio passes RF directly to the
    /// front-end without the hardware variable-capacitor in the signal path.
    /// Corresponds to P2 = 0 in a VT response.
    /// </summary>
    Off,

    /// <summary>
    /// The VC Tune preselector is engaged and the capacitor is at a previously
    /// tuned or manually stepped position.
    /// Corresponds to P2 = 1 in a VT response.
    /// </summary>
    On,

    /// <summary>
    /// The VC Tune auto-tune (DEFAULT) routine was the last command received by
    /// the radio. The radio sweeps the capacitor to peak coupling and will
    /// subsequently report <see cref="On"/> or <see cref="Off"/> on the next read.
    /// Corresponds to P2 = 2 in a VT response.
    /// </summary>
    Default,

    /// <summary>
    /// A manual capacitor step command (<c>VT{P1}{dir}{amount};</c>) was the last
    /// SET operation sent. This is a transient state: the next READ response will
    /// resolve it to <see cref="On"/>, <see cref="Off"/>, or <see cref="Default"/>
    /// depending on P2.
    /// </summary>
    Stepping,

    /// <summary>
    /// A CENTER command (<c>VT{P1}+0;</c>) was the last SET operation sent,
    /// driving the capacitor to its mechanical centre position. This is a
    /// transient state; the next READ response resolves it.
    /// </summary>
    Centering,

    /// <summary>
    /// The VC Tune option board is fitted but the current VFO frequency is outside
    /// the board's operating range (P6 = 2). No commands should be sent while in
    /// this state.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The VC Tune option board is not fitted on this receiver (P6 = 0).
    /// This is the safe default state before the first READ response is received.
    /// </summary>
    NotInstalled,
}
