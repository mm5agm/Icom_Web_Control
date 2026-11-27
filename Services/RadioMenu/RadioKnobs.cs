using System.Globalization;
using System.Text.RegularExpressions;

namespace Icom_Web_Control.Services.RadioMenu;

/// <summary>
/// A front-panel level saved with the radio menu: a knob or button the SET
/// menu does not hold, such as MIC GAIN or the COMP level. On the IC-7300MK2
/// these are the same in every mode (checked on the radio 2026-11-27: a level
/// set in AM shows in USB), so each is read once, with no mode change.
/// </summary>
/// <param name="Key">Short name used in the address, e.g. "MIC".</param>
/// <param name="Name">The name the radio uses for it.</param>
/// <param name="Civ">The CI-V command, for the file's comment, e.g. "14 0B".</param>
/// <param name="OnOff">True for a button (OFF/ON), false for a 0–100% level.</param>
public sealed record RadioKnob(string Key, string Name, string Civ, bool OnOff)
{
    public string Show(int value) => OnOff ? (value != 0 ? "ON" : "OFF") : $"{value}%";
}

/// <summary>A knob and what the radio answered (null if it did not).</summary>
public sealed record RadioKnobReading(RadioKnob Knob, int? Value);

/// <summary>
/// The front-panel part of the radio menu save: a [Front panel] section after
/// the menu. Nothing here transmits; every command is a read or a level.
/// </summary>
public static class RadioKnobs
{
    public const string Section = "Front panel";

    /// <summary>Knob rows use "knob:MIC" as their address, so they can never clash with a 1A 05 item number.</summary>
    public const string AddressPrefix = "knob:";

    private static readonly IReadOnlyList<RadioKnob> Ic7300Mk2 =
    [
        new("MIC", "MIC GAIN", "14 0B", false),
        new("COMP", "COMP", "16 44", true),
        new("COMPLEVEL", "COMP LEVEL", "14 0E", false),
        new("POWER", "RF POWER", "14 0A", false),
        new("MONI", "MONITOR LEVEL", "14 15", false),
        new("VOX", "VOX GAIN", "14 16", false),
    ];

    /// <summary>The knobs for a model, or null if IWC saves none for it.</summary>
    public static IReadOnlyList<RadioKnob>? TableFor(string? radioModel) => radioModel switch
    {
        "IC-7300MK2" => Ic7300Mk2,
        _ => null,
    };

    public static string Address(RadioKnob knob) => AddressPrefix + knob.Key;

    public static bool IsKnobAddress(string? address) =>
        address is not null && address.StartsWith(AddressPrefix, StringComparison.Ordinal);

    public static bool IsKnobSection(string section) => section.Equals(Section, StringComparison.OrdinalIgnoreCase);

    /// <summary>The knob a "knob:MIC" address is for on this radio, or null.</summary>
    public static RadioKnob? ByAddress(string? address, string? radioModel) =>
        IsKnobAddress(address) ? TableFor(radioModel)?.FirstOrDefault(k => Address(k) == address) : null;

    /// <summary>The knob a [Front panel] line's name is for, e.g. "MIC GAIN", or null. Any knob IWC knows, any radio.</summary>
    public static RadioKnob? ByName(string name) =>
        Ic7300Mk2.FirstOrDefault(k => k.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static readonly Regex Percent = new(@"^\s*(\d{1,3})\s*%?\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The value for a level as the file shows it ("50%", "50", "ON"), or null
    /// with the reason if the radio would not take it.
    /// </summary>
    public static int? Encode(RadioKnob knob, string shown, out string? error)
    {
        error = null;
        var text = shown.Trim();
        if (knob.OnOff)
        {
            if (text.Equals("ON", StringComparison.OrdinalIgnoreCase)) return 1;
            if (text.Equals("OFF", StringComparison.OrdinalIgnoreCase)) return 0;
            error = $"\"{text}\" is not a {knob.Name} setting. Use ON or OFF.";
            return null;
        }
        var m = Percent.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= 100)
            return n;
        error = $"\"{text}\" is not a {knob.Name} level. Use 0% to 100%.";
        return null;
    }

    /// <summary>The knob part of the save file: one [Front panel] section.</summary>
    public static string ToIni(IEnumerable<RadioKnobReading> readings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine()
          .AppendLine("; Front panel: levels set with the knobs and buttons, not in the SET menu.")
          .AppendLine("; The radio keeps one of each for every mode.")
          .AppendLine("; Each line is: level = value as the radio shows it   ; CI-V command")
          .AppendLine()
          .AppendLine($"[{Section}]");
        foreach (var r in readings)
        {
            var line = $"{r.Knob.Name} = {(r.Value is int v ? r.Knob.Show(v) : RadioMenuLoad.NoAnswer)}";
            sb.Append(line.PadRight(40)).Append(" ; CI-V ").Append(r.Knob.Civ).AppendLine("  [TX]");
        }
        return sb.ToString();
    }

    /// <summary>Reads a knob through IWC's radio controller: percent, or 1/0 for a button.</summary>
    public static async Task<int?> ReadAsync(IRadioController radio, RadioKnob knob, CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                int value = knob.Key switch
                {
                    "MIC" => await radio.GetMicGainPercentAsync(ct),
                    "COMP" => await radio.GetSpeechCompressorAsync(ct) ? 1 : 0,
                    "COMPLEVEL" => await radio.GetCompressorLevelPercentAsync(ct),
                    "POWER" => await radio.GetRfPowerPercentAsync(ct),
                    "MONI" => await radio.GetMonitorLevelPercentAsync(ct),
                    "VOX" => await radio.GetVoxGainPercentAsync(ct),
                    _ => -1,
                };
                // The level reads give -1 when the radio does not answer.
                if (value >= 0) return value;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // No answer or a garbled one: try again.
            }
        }
        return null;
    }

    /// <summary>
    /// Reads every knob. The COMP button's read cannot tell "OFF" from no
    /// answer, so it only counts if the radio answered the levels.
    /// </summary>
    public static async Task<List<RadioKnobReading>> ReadAllAsync(IRadioController radio, IReadOnlyList<RadioKnob> knobs, CancellationToken ct)
    {
        var readings = new List<RadioKnobReading>();
        foreach (var knob in knobs)
            readings.Add(new(knob, await ReadAsync(radio, knob, ct)));
        if (readings.Where(r => !r.Knob.OnOff).All(r => r.Value is null))
            readings = readings.Select(r => r with { Value = null }).ToList();
        return readings;
    }

    /// <summary>Sets a knob through IWC's radio controller.</summary>
    public static Task WriteAsync(IRadioController radio, RadioKnob knob, int value, CancellationToken ct) => knob.Key switch
    {
        "MIC" => radio.SetMicGainPercentAsync(value, ct),
        "COMP" => radio.SetSpeechCompressorAsync(value != 0, ct),
        "COMPLEVEL" => radio.SetCompressorLevelPercentAsync(value, ct),
        "POWER" => radio.SetRfPowerPercentAsync(value, ct),
        "MONI" => radio.SetMonitorLevelPercentAsync(value, ct),
        "VOX" => radio.SetVoxGainPercentAsync(value, ct),
        _ => Task.CompletedTask,
    };
}
