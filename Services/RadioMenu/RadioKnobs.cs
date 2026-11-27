using System.Globalization;
using System.Text.RegularExpressions;

namespace Icom_Web_Control.Services.RadioMenu;

/// <summary>How a front panel value is shown in the file.</summary>
public enum RadioKnobKind
{
    /// <summary>A 0–255 level shown as 0–100%.</summary>
    Percent,
    /// <summary>A button: OFF or ON.</summary>
    OnOff,
    /// <summary>One of a few named settings, e.g. AGC FAST/MID/SLOW.</summary>
    Choice,
    /// <summary>Keyer speed: 0–255 shown as 6–48 WPM.</summary>
    Wpm,
    /// <summary>CW pitch: 0–255 shown as 300–900 Hz.</summary>
    PitchHz,
    /// <summary>Break-in delay: 0–255 shown as 2.0–13.0 dots.</summary>
    Dots,
}

/// <summary>Whether the radio keeps one value, one per mode or one per band.</summary>
public enum RadioKnobScope { Global, Mode, Band }

/// <summary>
/// A front panel control saved with the radio menu: a knob or button the SET
/// menu does not hold, such as MIC GAIN or the NB. Checked on an IC-7300MK2
/// on 2026-11-27 by setting each one, changing mode (USB, LSB, CW, AM, FM)
/// and band (20 m, 40 m), and reading it back: most are the same everywhere,
/// AGC is per mode, and PREAMP, ATT and IP+ are per band.
/// </summary>
/// <param name="Key">Short name used in the address, e.g. "MIC".</param>
/// <param name="Name">The name the radio uses for it.</param>
/// <param name="Command">CI-V command: 14 (level), 16 (function) or 11 (attenuator).</param>
/// <param name="Sub">CI-V sub-command; 0 for the attenuator, which has none.</param>
/// <param name="Kind">How the value is shown.</param>
/// <param name="Tx">True if it changes what is transmitted; Load leaves these unticked.</param>
/// <param name="Scope">Global, per mode or per band.</param>
/// <param name="Choices">For <see cref="RadioKnobKind.Choice"/>: (radio code, name) pairs.</param>
/// <param name="Tolerance">
/// How far a read back may be from what was sent and still count, in 0–255
/// steps. NR LEVEL, COMP LEVEL and CW PITCH only take certain steps, so the
/// radio rounds what it is sent.
/// </param>
public sealed record RadioKnob(
    string Key, string Name, byte Command, byte Sub, RadioKnobKind Kind, bool Tx,
    RadioKnobScope Scope = RadioKnobScope.Global,
    (int Code, string Name)[]? Choices = null,
    int Tolerance = 1)
{
    public string Civ => Command == RadioKnobs.CmdAttenuator ? "11" : $"{Command:X2} {Sub:X2}";

    public bool OnOff => Kind == RadioKnobKind.OnOff;

    /// <summary>The radio's value as the file shows it: "50%", "ON", "MID", "20 WPM", "600 Hz", "7.5 dots".</summary>
    public string Show(int raw) => Kind switch
    {
        RadioKnobKind.OnOff => raw != 0 ? "ON" : "OFF",
        RadioKnobKind.Choice => Choices!.FirstOrDefault(c => c.Code == raw).Name ?? $"code {raw}",
        RadioKnobKind.Wpm => $"{RadioKnobs.ToUnits(raw, 6, 48):0} WPM",
        RadioKnobKind.PitchHz => $"{Math.Round(RadioKnobs.ToUnits(raw, 300, 900) / 5) * 5:0} Hz",
        RadioKnobKind.Dots => $"{RadioKnobs.ToUnits(raw, 2.0, 13.0).ToString("0.0", CultureInfo.InvariantCulture)} dots",
        _ => $"{RadioKnobs.ToUnits(raw, 0, 100):0}%",
    };

    /// <summary>True if two radio values are the same setting: they show the same, or are within <see cref="Tolerance"/>.</summary>
    public bool Same(int a, int b) =>
        a == b || Show(a) == Show(b)
        || (Kind is not (RadioKnobKind.OnOff or RadioKnobKind.Choice) && Math.Abs(a - b) <= Tolerance);
}

/// <summary>A knob, what the radio answered (null if it did not), and the mode or band it is for.</summary>
/// <param name="Where">The mode ("USB") or band ("20m") for a per-mode or per-band knob; null for the rest.</param>
public sealed record RadioKnobReading(RadioKnob Knob, int? Value, string? Where = null)
{
    public string Address => RadioKnobs.Address(Knob, Where);
    public string Label => RadioKnobs.Label(Knob, Where);
}

/// <summary>The mode and band the radio is on, for the per-mode and per-band knobs.</summary>
public sealed record RadioKnobPlace(string Mode, string Band);

/// <summary>
/// The front-panel part of the radio menu save: a [Front panel] section after
/// the menu. Nothing here transmits; every command is a read or a setting,
/// and only the commands in the table can be sent (never VOX on, 16 46).
/// </summary>
public static class RadioKnobs
{
    public const string Section = "Front panel";

    /// <summary>Knob rows use "knob:MIC" as their address, so they can never clash with a 1A 05 item number.</summary>
    public const string AddressPrefix = "knob:";

    public const byte CmdAttenuator = 0x11;
    public const byte CmdLevel = 0x14;
    public const byte CmdFunction = 0x16;

    private static readonly (int, string)[] Agc = [(1, "FAST"), (2, "MID"), (3, "SLOW")];
    private static readonly (int, string)[] Preamp = [(0, "OFF"), (1, "P.AMP1"), (2, "P.AMP2")];
    private static readonly (int, string)[] NotchWidth = [(0, "WIDE"), (1, "MID"), (2, "NAR")];
    private static readonly (int, string)[] BreakIn = [(0, "OFF"), (1, "SEMI"), (2, "FULL")];
    private static readonly (int, string)[] Att = [(0x00, "OFF"), (0x20, "20dB")];

    private static RadioKnob Level(string key, string name, byte sub, bool tx, RadioKnobKind kind = RadioKnobKind.Percent, int tolerance = 1) =>
        new(key, name, CmdLevel, sub, kind, tx, Tolerance: tolerance);

    private static RadioKnob Button(string key, string name, byte sub, bool tx) =>
        new(key, name, CmdFunction, sub, RadioKnobKind.OnOff, tx);

    private static RadioKnob Pick(string key, string name, byte sub, bool tx, (int, string)[] choices, RadioKnobScope scope = RadioKnobScope.Global) =>
        new(key, name, CmdFunction, sub, RadioKnobKind.Choice, tx, scope, choices);

    private static readonly IReadOnlyList<RadioKnob> Ic7300Mk2 =
    [
        // Receive
        Level("RFGAIN", "RF GAIN", 0x02, false),
        Level("SQL", "SQL", 0x03, false),
        Pick("AGC", "AGC", 0x12, false, Agc, RadioKnobScope.Mode),
        Pick("PREAMP", "PREAMP", 0x02, false, Preamp, RadioKnobScope.Band),
        new("ATT", "ATT", CmdAttenuator, 0x00, RadioKnobKind.Choice, false, RadioKnobScope.Band, Att),
        new("IPPLUS", "IP+", CmdFunction, 0x65, RadioKnobKind.OnOff, false, RadioKnobScope.Band),
        Button("NB", "NB", 0x22, false),
        Level("NBLEVEL", "NB LEVEL", 0x12, false),
        Button("NR", "NR", 0x40, false),
        Level("NRLEVEL", "NR LEVEL", 0x06, false, tolerance: 10),
        Button("ANOTCH", "AUTO NOTCH", 0x41, false),
        Button("MNOTCH", "MANUAL NOTCH", 0x48, false),
        Level("NOTCHPOS", "NOTCH POSITION", 0x0D, false),
        Pick("NOTCHWIDTH", "NOTCH WIDTH", 0x57, false, NotchWidth),
        Button("TWINPEAK", "TWIN PEAK", 0x4F, false),
        // Transmit (none of these transmit; they change what goes out when the user does)
        Level("POWER", "RF POWER", 0x0A, true),
        Level("MIC", "MIC GAIN", 0x0B, true),
        Button("COMP", "COMP", 0x44, true),
        Level("COMPLEVEL", "COMP LEVEL", 0x0E, true, tolerance: 14),
        Button("MONITOR", "MONITOR", 0x45, true),
        Level("MONI", "MONITOR LEVEL", 0x15, true),
        Level("VOX", "VOX GAIN", 0x16, true),
        Level("ANTIVOX", "ANTI VOX", 0x17, true),
        // CW
        Level("KEYSPEED", "KEY SPEED", 0x0C, true, RadioKnobKind.Wpm, tolerance: 3),
        Level("PITCH", "CW PITCH", 0x09, true, RadioKnobKind.PitchHz, tolerance: 3),
        Pick("BKIN", "BK-IN", 0x47, true, BreakIn),
        Level("BKINDELAY", "BK-IN DELAY", 0x0F, true, RadioKnobKind.Dots, tolerance: 3),
    ];

    /// <summary>The knobs for a model, or null if IWC saves none for it.</summary>
    public static IReadOnlyList<RadioKnob>? TableFor(string? radioModel) => radioModel switch
    {
        "IC-7300MK2" => Ic7300Mk2,
        _ => null,
    };

    /// <summary>
    /// True if a CI-V command is one the front panel save may send: a knob in
    /// a table. Anything else, such as VOX on (16 46) or PTT (1C 00), is refused.
    /// </summary>
    public static bool IsPanelCommand(byte command, byte sub) =>
        Ic7300Mk2.Any(k => k.Command == command && (command == CmdAttenuator || k.Sub == sub));

    /// <summary>"knob:MIC", or "knob:AGC@USB" / "knob:PREAMP@20m" for a per-mode or per-band knob.</summary>
    public static string Address(RadioKnob knob, string? where = null) =>
        AddressPrefix + knob.Key + (knob.Scope != RadioKnobScope.Global && !string.IsNullOrEmpty(where) ? "@" + where : "");

    /// <summary>The name in the file: "MIC GAIN", "AGC (USB)", "PREAMP (20m)".</summary>
    public static string Label(RadioKnob knob, string? where = null) =>
        knob.Scope != RadioKnobScope.Global && !string.IsNullOrEmpty(where) ? $"{knob.Name} ({where})" : knob.Name;

    /// <summary>The mode or band this knob's value is for, at a place.</summary>
    public static string? WhereFor(RadioKnob knob, RadioKnobPlace? place) => knob.Scope switch
    {
        RadioKnobScope.Mode => place?.Mode,
        RadioKnobScope.Band => place?.Band,
        _ => null,
    };

    public static bool IsKnobAddress(string? address) =>
        address is not null && address.StartsWith(AddressPrefix, StringComparison.Ordinal);

    public static bool IsKnobSection(string section) => section.Equals(Section, StringComparison.OrdinalIgnoreCase);

    /// <summary>The knob a "knob:MIC" address is for on this radio, or null.</summary>
    public static RadioKnob? ByAddress(string? address, string? radioModel) => ByAddress(address, radioModel, out _);

    /// <summary>The knob a "knob:AGC@USB" address is for on this radio, or null; <paramref name="where"/> is "USB".</summary>
    public static RadioKnob? ByAddress(string? address, string? radioModel, out string? where)
    {
        where = null;
        if (!IsKnobAddress(address)) return null;
        var rest = address![AddressPrefix.Length..];
        int at = rest.IndexOf('@');
        if (at >= 0) { where = rest[(at + 1)..]; rest = rest[..at]; }
        return TableFor(radioModel)?.FirstOrDefault(k => k.Key == rest);
    }

    private static readonly Regex WithWhere = new(@"^(.*?)\s*\(\s*([^()]+?)\s*\)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The address for a [Front panel] line's name: "MIC GAIN" gives
    /// "knob:MIC", "AGC (USB)" gives "knob:AGC@USB". An unknown name keeps a
    /// knob address that matches no knob.
    /// </summary>
    public static string AddressForName(string name)
    {
        var text = name.Trim();
        string? where = null;
        var m = WithWhere.Match(text);
        if (m.Success) { text = m.Groups[1].Value; where = m.Groups[2].Value; }
        var knob = Ic7300Mk2.FirstOrDefault(k => k.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
        return knob is null ? AddressPrefix + name.Trim() : Address(knob, where);
    }

    /// <summary>The knob a [Front panel] line's name is for, e.g. "MIC GAIN" or "AGC (USB)", or null. Any knob IWC knows, any radio.</summary>
    public static RadioKnob? ByName(string name) => ByAddress(AddressForName(name), "IC-7300MK2");

    internal static double ToUnits(int raw, double lo, double hi) => lo + Math.Clamp(raw, 0, 255) / 255.0 * (hi - lo);

    private static int FromUnits(double value, double lo, double hi) =>
        (int)Math.Round((Math.Clamp(value, lo, hi) - lo) / (hi - lo) * 255.0);

    private static readonly Regex Number = new(@"^\s*(\d{1,3}(?:\.\d)?)\s*(%|wpm|hz|dots?|d)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The radio's value for a setting as the file shows it ("50%", "50",
    /// "ON", "MID", "20 WPM", "600 Hz", "7.5 dots"), or null with the reason
    /// if the radio would not take it.
    /// </summary>
    public static int? Encode(RadioKnob knob, string shown, out string? error)
    {
        error = null;
        var text = shown.Trim();
        switch (knob.Kind)
        {
            case RadioKnobKind.OnOff:
                if (text.Equals("ON", StringComparison.OrdinalIgnoreCase)) return 1;
                if (text.Equals("OFF", StringComparison.OrdinalIgnoreCase)) return 0;
                error = $"\"{text}\" is not a {knob.Name} setting. Use ON or OFF.";
                return null;
            case RadioKnobKind.Choice:
                foreach (var c in knob.Choices!)
                    if (c.Name.Equals(text, StringComparison.OrdinalIgnoreCase)
                        || c.Name.Replace(" ", "").Equals(text.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                        return c.Code;
                error = $"\"{text}\" is not a {knob.Name} setting. Use {string.Join(", ", knob.Choices.Select(c => c.Name))}.";
                return null;
        }
        var (lo, hi, unit) = knob.Kind switch
        {
            RadioKnobKind.Wpm => (6.0, 48.0, "6 WPM to 48 WPM"),
            RadioKnobKind.PitchHz => (300.0, 900.0, "300 Hz to 900 Hz"),
            RadioKnobKind.Dots => (2.0, 13.0, "2.0 dots to 13.0 dots"),
            _ => (0.0, 100.0, "0% to 100%"),
        };
        var m = Number.Match(text);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)
            && n >= lo && n <= hi)
            return FromUnits(n, lo, hi);
        error = $"\"{text}\" is not a {knob.Name} setting. Use {unit}.";
        return null;
    }

    /// <summary>The knob part of the save file: one [Front panel] section.</summary>
    public static string ToIni(IEnumerable<RadioKnobReading> readings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine()
          .AppendLine("; Front panel: settings made with the knobs and buttons, not in the SET menu.")
          .AppendLine("; Most are one setting for every mode and band. AGC is kept for each mode and")
          .AppendLine("; PREAMP, ATT and IP+ for each band, so those are saved for the mode or band")
          .AppendLine("; the radio was on, shown in brackets, and load only when it is on it again.")
          .AppendLine("; Each line is: setting = value as the radio shows it   ; CI-V command")
          .AppendLine()
          .AppendLine($"[{Section}]");
        foreach (var r in readings)
        {
            var line = $"{r.Label} = {(r.Value is int v ? r.Knob.Show(v) : RadioMenuLoad.NoAnswer)}";
            sb.Append(line.PadRight(40)).Append(" ; CI-V ").Append(r.Knob.Civ).AppendLine(r.Knob.Tx ? "  [TX]" : "");
        }
        return sb.ToString();
    }

    /// <summary>The band name a frequency is in, as the radio's band keys split them; "GEN" outside the amateur bands.</summary>
    public static string BandOf(long hz) => (hz / 1000) switch
    {
        >= 1_800 and <= 2_000 => "160m",
        >= 3_400 and <= 4_100 => "80m",
        >= 5_250 and <= 5_450 => "60m",
        >= 6_900 and <= 7_400 => "40m",
        >= 10_000 and <= 10_200 => "30m",
        >= 13_900 and <= 14_400 => "20m",
        >= 18_000 and <= 18_200 => "17m",
        >= 20_900 and <= 21_500 => "15m",
        >= 24_800 and <= 25_000 => "12m",
        >= 27_900 and <= 29_800 => "10m",
        >= 49_900 and <= 54_100 => "6m",
        >= 69_900 and <= 71_600 => "4m",
        _ => "GEN",
    };

    /// <summary>The radio's own name for an IWC mode: "CW-U" is the radio's CW, "DATA-U" its USB-D.</summary>
    public static string ModeLabel(string mode) => mode switch
    {
        "CW-U" => "CW",
        "CW-L" => "CW-R",
        "RTTY-L" => "RTTY",
        "RTTY-U" => "RTTY-R",
        "DATA-U" => "USB-D",
        "DATA-L" => "LSB-D",
        "DATA-FM" => "FM-D",
        _ => mode,
    };

    /// <summary>The mode and band of the VFO in use, or null if the radio did not say.</summary>
    public static async Task<RadioKnobPlace?> ReadPlaceAsync(IRadioController radio, RadioVfo vfo, CancellationToken ct)
    {
        try
        {
            var mode = await radio.GetModeAsync(vfo, ct);
            var hz = await radio.GetFrequencyHzAsync(vfo, ct);
            if (string.IsNullOrEmpty(mode) || mode.StartsWith('?') || hz <= 0) return null;
            return new(ModeLabel(mode), BandOf(hz));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads a knob: the radio's raw value, or null if it did not answer after three tries.</summary>
    public static async Task<int?> ReadAsync(IRadioController radio, RadioKnob knob, CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                int value = await radio.ReadPanelSettingAsync(knob.Command, knob.Sub, ct);
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
    /// Reads every knob. A per-mode or per-band knob is read for the mode or
    /// band the radio is on now; if that could not be read, it counts as no answer.
    /// </summary>
    public static async Task<List<RadioKnobReading>> ReadAllAsync(IRadioController radio, IReadOnlyList<RadioKnob> knobs,
        RadioKnobPlace? place, CancellationToken ct)
    {
        var readings = new List<RadioKnobReading>();
        foreach (var knob in knobs)
        {
            var where = WhereFor(knob, place);
            if (knob.Scope != RadioKnobScope.Global && where is null)
            {
                readings.Add(new(knob, null));
                continue;
            }
            readings.Add(new(knob, await ReadAsync(radio, knob, ct), where));
        }
        return readings;
    }

    /// <summary>True if a raw value is one the radio takes for this knob: 0–255, OFF/ON, or one of its settings.</summary>
    public static bool IsValue(RadioKnob knob, int value) => knob.Kind switch
    {
        RadioKnobKind.OnOff => value is 0 or 1,
        RadioKnobKind.Choice => knob.Choices!.Any(c => c.Code == value),
        _ => value is >= 0 and <= 255,
    };

    /// <summary>Sets a knob. True if the radio acknowledged it.</summary>
    public static Task<bool> WriteAsync(IRadioController radio, RadioKnob knob, int value, CancellationToken ct) =>
        radio.WritePanelSettingAsync(knob.Command, knob.Sub, value, ct);

    /// <summary>
    /// Why a per-mode or per-band line cannot load now, or null if it can:
    /// it is for another mode or band than the radio is on.
    /// </summary>
    public static string? NotHere(RadioKnob knob, string? where, RadioKnobPlace? place)
    {
        if (knob.Scope == RadioKnobScope.Global) return null;
        string what = knob.Scope == RadioKnobScope.Mode ? "mode" : "band";
        if (string.IsNullOrEmpty(where))
            return $"The radio keeps {knob.Name} for each {what}, and this line does not say which. Save a new file.";
        var now = WhereFor(knob, place);
        if (now is null) return $"The radio did not say which {what} it is on, so this cannot be checked.";
        return string.Equals(now, where, StringComparison.OrdinalIgnoreCase) ? null
            : $"Saved for {where}; the radio is on {now} now. Go to {where} and load again to set it.";
    }
}
