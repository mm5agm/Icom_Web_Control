namespace Yaesu_Web_Control.Services;

/// <summary>
/// Categorises error conditions that can occur during VC Tune preselector
/// operations. Used by <see cref="VCTuneDiagnostics"/> to classify logged
/// error and fallback events.
/// </summary>
public enum VCTuneErrorType
{
    /// <summary>
    /// The operation was attempted on a receiver whose VC Tune board is not
    /// installed. P6 = 0 was observed for that band.
    /// </summary>
    NotInstalled,

    /// <summary>
    /// The preselector is installed but unavailable at the current operating
    /// frequency. P6 = 2 was observed for that band.
    /// </summary>
    UnavailableFrequency,

    /// <summary>
    /// One or more command parameters were outside the valid range (e.g.
    /// step amount not in 0–9, unrecognised direction character).
    /// </summary>
    InvalidParameters,

    /// <summary>
    /// The radio returned a NAK, an empty response, or a response that
    /// indicates the command was not accepted.
    /// </summary>
    CommandRejected,

    /// <summary>
    /// A VT READ command was sent but the response could not be parsed as a
    /// valid VT response (wrong length, non-numeric fields, etc.).
    /// </summary>
    ReadFailure,

    /// <summary>
    /// A VT command or READ did not receive a response within the expected
    /// time window.
    /// </summary>
    Timeout,

    /// <summary>
    /// A response was received but its content was structurally valid yet
    /// semantically inconsistent with the command that was sent (e.g. band
    /// mismatch between command and response).
    /// </summary>
    UnexpectedResponse,
}
