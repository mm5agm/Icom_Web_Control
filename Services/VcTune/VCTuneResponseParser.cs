namespace Yaesu_Web_Control.Services;

/// <summary>
/// Parses VT CAT responses from the FTdx101 into <see cref="VcTuneReadResult"/> records.
/// <para>
/// Actual wire format from the radio: <c>VT{P1}{P2}{P5P5P5}{P6};</c>
/// (8 characters before the semicolon — confirmed by live radio diagnostic 2026-07-01).
/// The P3 and P4 fields documented in early design notes are NOT present in the actual response.
/// </para>
/// <list type="table">
///   <item><term>Index 0–1</term><description>"VT" command prefix</description></item>
///   <item><term>Index 2</term><description>P1: receiver — '0'=MAIN, '1'=SUB</description></item>
///   <item><term>Index 3</term><description>P2: state — '0'=Off, '1'=On, '2'=Default</description></item>
///   <item><term>Index 4–6</term><description>P5: meter value, three-digit decimal 000–255</description></item>
///   <item><term>Index 7</term><description>P6: availability — '0'=NotInstalled, '1'=Available, '2'=UnavailableFrequency</description></item>
/// </list>
/// Total: 8 characters before semicolon (9 including semicolon).
/// </summary>
internal static class VcTuneResponseParser
{
    private const int ExpectedLengthWithoutSemicolon = 8;

    /// <summary>
    /// Parses a raw VT response string.
    /// </summary>
    /// <param name="raw">The full CAT response, with or without trailing semicolon.</param>
    /// <returns>
    /// A <see cref="VcTuneReadResult"/> with <c>IsValid = true</c> on success,
    /// or <c>IsValid = false</c> with a non-null <c>RawResponse</c> on any failure.
    /// </returns>
    public static VcTuneReadResult Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return Invalid(raw ?? string.Empty, "null or empty");

        // Strip trailing semicolon for position-based parsing
        string s = raw.EndsWith(';') ? raw[..^1] : raw;

        if (s.Length != ExpectedLengthWithoutSemicolon)
            return Invalid(raw, $"unexpected length {s.Length}, expected {ExpectedLengthWithoutSemicolon}");

        if (!s.StartsWith("VT", StringComparison.Ordinal))
            return Invalid(raw, "does not start with VT");

        // P1 — receiver
        if (s[2] != '0' && s[2] != '1')
            return Invalid(raw, $"P1 '{s[2]}' is not 0 or 1");
        var receiver = s[2] == '1' ? VcTuneReceiver.Sub : VcTuneReceiver.Main;

        // P2 — on/off/default
        if (s[3] != '0' && s[3] != '1' && s[3] != '2')
            return Invalid(raw, $"P2 '{s[3]}' is not 0, 1, or 2");
        bool isOn = s[3] == '1';
        bool isDefault = s[3] == '2';

        // P5 — meter value (three-digit decimal 000–255) at indices 4–6
        if (!int.TryParse(s.AsSpan(4, 3), out int meter) || meter < 0 || meter > 255)
            return Invalid(raw, $"P5 '{s[4..7]}' is not a valid meter value (0-255)");

        // P6 — availability at index 7
        if (s[7] != '0' && s[7] != '1' && s[7] != '2')
            return Invalid(raw, $"P6 '{s[7]}' is not 0, 1, or 2");
        var availability = (VcTuneAvailability)(s[7] - '0');

        return new VcTuneReadResult(
            receiver, isOn, isDefault, '+', 0,
            meter, availability, raw, IsValid: true);
    }

    private static VcTuneReadResult Invalid(string raw, string reason) =>
        new(VcTuneReceiver.Main, false, false, '+', 0, -1,
            VcTuneAvailability.NotInstalled, raw, IsValid: false);
}

/// <summary>
/// DI-injectable implementation of <see cref="IVCTuneResponseParser"/>.
/// Parses VT CAT responses from the FTdx101 into <see cref="VCTuneResponse"/> records.
/// <para>
/// Unlike <see cref="VcTuneResponseParser"/> (the internal static class used directly by
/// <see cref="VcTuneService"/>), this class throws descriptive exceptions for invalid
/// input rather than returning an IsValid=false sentinel. This makes it the preferred
/// choice for unit-testable layers and endpoint code that propagates errors explicitly.
/// </para>
/// <para>
/// Register as a singleton in DI:
/// <c>services.AddSingleton&lt;IVCTuneResponseParser, VCTuneResponseParser&gt;();</c>
/// </para>
/// </summary>
public sealed class VCTuneResponseParser : IVCTuneResponseParser
{
    private const int ExpectedLengthWithoutSemicolon = 8;

    /// <inheritdoc/>
    public VCTuneResponse ParseResponse(string rawResponse)
    {
        if (string.IsNullOrEmpty(rawResponse))
            throw new ArgumentNullException(
                nameof(rawResponse), "VT response string must not be null or empty.");

        // Strip trailing semicolon for position-based parsing.
        string s = rawResponse.EndsWith(';') ? rawResponse[..^1] : rawResponse;

        if (s.Length != ExpectedLengthWithoutSemicolon)
            throw new FormatException(
                $"VT response has unexpected length {s.Length}; " +
                $"expected {ExpectedLengthWithoutSemicolon} characters (excluding semicolon). " +
                $"Raw: '{rawResponse}'");

        if (!s.StartsWith("VT", StringComparison.Ordinal))
            throw new FormatException(
                $"VT response does not start with 'VT'. Raw: '{rawResponse}'");

        // P1 — receiver (index 2): '0'=Main, '1'=Sub
        VCTuneBand band = s[2] switch
        {
            '0' => VCTuneBand.Main,
            '1' => VCTuneBand.Sub,
            _ => throw new FormatException(
                $"VT response P1 (index 2) is '{s[2]}'; expected '0' (Main) or '1' (Sub). Raw: '{rawResponse}'")
        };

        // P2 — on/off/default state (index 3)
        VCTuneCommandType? commandType = s[3] switch
        {
            '0' => VCTuneCommandType.Off,
            '1' => VCTuneCommandType.On,
            '2' => VCTuneCommandType.Default,
            _ => throw new FormatException(
                $"VT response P2 (index 3) is '{s[3]}'; expected '0' (Off), '1' (On), or '2' (Default). Raw: '{rawResponse}'")
        };

        // P5 — preselector meter (indices 4–6): three-digit decimal 000–255
        if (!int.TryParse(s.AsSpan(4, 3), out int meter) || meter is < 0 or > 255)
            throw new FormatException(
                $"VT response P5 (indices 4–6) is '{s[4..7]}'; " +
                $"expected three-digit decimal in range 000–255. Raw: '{rawResponse}'");

        // P6 — hardware availability (index 7): '0'=NotInstalled, '1'=Available, '2'=UnavailableFrequency
        if (s[7] != '0' && s[7] != '1' && s[7] != '2')
            throw new FormatException(
                $"VT response P6 (index 7) is '{s[7]}'; " +
                $"expected '0' (NotInstalled), '1' (Available), or '2' (UnavailableFrequency). Raw: '{rawResponse}'");
        int availability = s[7] - '0';

        return new VCTuneResponse(
            band,
            commandType,
            '+',
            0,
            meter,
            availability,
            rawResponse);
    }

    /// <inheritdoc/>
    public bool CanParse(string? rawResponse)
    {
        if (string.IsNullOrEmpty(rawResponse))
            return false;

        string s = rawResponse.EndsWith(';') ? rawResponse[..^1] : rawResponse;

        if (s.Length != ExpectedLengthWithoutSemicolon)
            return false;

        if (!s.StartsWith("VT", StringComparison.Ordinal))
            return false;

        if (s[2] != '0' && s[2] != '1') return false;
        if (s[3] != '0' && s[3] != '1' && s[3] != '2') return false;
        if (!int.TryParse(s.AsSpan(4, 3), out int m) || m is < 0 or > 255) return false;
        if (s[7] != '0' && s[7] != '1' && s[7] != '2') return false;

        return true;
    }

    /// <inheritdoc/>
    public bool TryParse(string? rawResponse, out VCTuneResponse? response)
    {
        response = null;
        if (!CanParse(rawResponse))
            return false;

        try
        {
            response = ParseResponse(rawResponse!);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
