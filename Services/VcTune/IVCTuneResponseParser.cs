namespace Yaesu_Web_Control.Services;

/// <summary>
/// Contract for the DI-injectable VT CAT response parser.
/// Implementations are stateless and safe to register as singletons.
/// </summary>
public interface IVCTuneResponseParser
{
    /// <summary>
    /// Parses a raw VT CAT response string into a structured <see cref="VCTuneResponse"/>.
    /// </summary>
    /// <param name="rawResponse">
    /// The full CAT response string as received from the radio, with or without
    /// the trailing semicolon (e.g. <c>"VT001+0125251;"</c> or <c>"VT001+0125251"</c>).
    /// </param>
    /// <returns>
    /// A fully validated, immutable <see cref="VCTuneResponse"/>. All fields are guaranteed
    /// to be in-range for any returned instance.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="rawResponse"/> is null or empty.
    /// </exception>
    /// <exception cref="FormatException">
    /// Thrown when <paramref name="rawResponse"/> does not match the VT response format:
    /// <c>VT{P1}{P2}{P3}{P4}{P5P5P5}{P6}[;]</c> (10 significant characters).
    /// The exception message identifies which field failed and why.
    /// </exception>
    VCTuneResponse ParseResponse(string rawResponse);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="rawResponse"/> can be
    /// parsed without throwing. Does not allocate a <see cref="VCTuneResponse"/>.
    /// Use this for cheap format pre-checks before committing to a full parse.
    /// </summary>
    bool CanParse(string? rawResponse);

    /// <summary>
    /// Attempts to parse the response without throwing.
    /// </summary>
    /// <param name="rawResponse">The CAT response string to parse.</param>
    /// <param name="response">
    /// On success, the parsed <see cref="VCTuneResponse"/>;
    /// <see langword="null"/> on any parse failure.
    /// </param>
    /// <returns><see langword="true"/> if parsing succeeded.</returns>
    bool TryParse(string? rawResponse, out VCTuneResponse? response);
}
