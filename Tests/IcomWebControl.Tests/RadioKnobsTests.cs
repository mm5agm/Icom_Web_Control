using System.Reflection;
using Icom_Web_Control.Services;
using Icom_Web_Control.Services.RadioMenu;

namespace IcomWebControl.Tests;

/// <summary>
/// The front panel settings saved with the radio menu (RF GAIN, NB, AGC,
/// PREAMP, MIC GAIN, RF POWER and so on): the [Front panel] section, reading
/// it back, reading the radio, per-mode and per-band settings, and the save
/// reminder and close check built on them. Values are the radio's own raw
/// numbers: 0–255 for a level, the code for a button or a choice.
/// </summary>
public class RadioKnobsTests
{
    private const string Model = "IC-7300MK2";
    private static readonly DateTime T0 = new(2026, 11, 27, 18, 0, 0);
    private static readonly RadioKnobPlace UsbOn20 = new("USB", "20m");
    private static IReadOnlyList<RadioKnob> Knobs => RadioKnobs.TableFor(Model)!;
    private static RadioKnob K(string key) => Knobs.Single(k => k.Key == key);

    private static string FileText(bool withKnobs = true, int mic = 128, bool comp = false)
    {
        var item = Ic7300Mk2Menu.Items[0];
        var menu = RadioMenuFile.ToIni(Model, T0, new[] { new RadioMenuReading(item, item.Codec.Values?[0] ?? new byte[] { 0 }) });
        if (!withKnobs) return menu;
        return menu + RadioKnobs.ToIni(new[]
        {
            new RadioKnobReading(K("MIC"), mic),
            new RadioKnobReading(K("COMP"), comp ? 1 : 0),
            new RadioKnobReading(K("POWER"), 64),
            new RadioKnobReading(K("VOX"), null),
            new RadioKnobReading(K("AGC"), 2, "USB"),
            new RadioKnobReading(K("PREAMP"), 1, "20m"),
            new RadioKnobReading(K("KEYSPEED"), 85),
        });
    }

    private static RadioMenuSaveInfo Saved(int daysAgo, bool withKnobs = true) =>
        new("file.ini", T0.AddDays(-daysAgo), FileText(withKnobs));

    private static Dictionary<string, int?> Live(params (string Address, int? Value)[] values) =>
        values.ToDictionary(v => v.Address, v => v.Value);

    [Fact]
    public void Only_the_IC7300MK2_has_levels()
    {
        Assert.NotNull(RadioKnobs.TableFor("IC-7300MK2"));
        Assert.Null(RadioKnobs.TableFor("IC-705"));
        Assert.Null(RadioKnobs.TableFor(null));
    }

    [Fact]
    public void The_table_has_every_front_panel_setting_once_and_marks_where_the_radio_keeps_it()
    {
        Assert.Equal(27, Knobs.Count);
        Assert.Equal(Knobs.Count, Knobs.Select(k => k.Key).Distinct().Count());
        Assert.Equal(Knobs.Count, Knobs.Select(k => k.Name).Distinct().Count());
        Assert.Equal(RadioKnobScope.Mode, K("AGC").Scope);
        Assert.Equal(new[] { "PREAMP", "ATT", "IPPLUS" }, Knobs.Where(k => k.Scope == RadioKnobScope.Band).Select(k => k.Key));
        Assert.Single(Knobs, k => k.Scope == RadioKnobScope.Mode);
        // Receive settings are ticked on Load; the transmit ones are not.
        Assert.False(K("RFGAIN").Tx);
        Assert.False(K("NB").Tx);
        Assert.True(K("POWER").Tx);
        Assert.True(K("MIC").Tx);
    }

    [Fact]
    public void Only_table_commands_may_be_sent_and_never_VOX_on_or_PTT()
    {
        Assert.All(Knobs, k => Assert.True(RadioKnobs.IsPanelCommand(k.Command, k.Sub), k.Name));
        Assert.False(RadioKnobs.IsPanelCommand(0x16, 0x46)); // VOX on/off
        Assert.False(RadioKnobs.IsPanelCommand(0x1C, 0x00)); // PTT
        Assert.False(RadioKnobs.IsPanelCommand(0x1C, 0x01)); // tuner
        Assert.False(RadioKnobs.IsPanelCommand(0x14, 0x01)); // AF gain (volume) is left out
        Assert.False(RadioKnobs.IsPanelCommand(0x14, 0x07)); // PBT IN is left out
        Assert.False(RadioKnobs.IsPanelCommand(0x17, 0x00)); // CW send
    }

    [Fact]
    public void The_file_section_reads_back_with_knob_addresses()
    {
        var file = RadioMenuLoad.Parse(FileText());
        var knobLines = file.Items.Where(i => RadioKnobs.IsKnobAddress(i.Address)).ToList();
        Assert.Equal(new[] { "knob:MIC", "knob:COMP", "knob:POWER", "knob:VOX", "knob:AGC@USB", "knob:PREAMP@20m", "knob:KEYSPEED" },
            knobLines.Select(l => l.Address));
        Assert.Equal(new[] { "MIC GAIN", "COMP", "RF POWER", "VOX GAIN", "AGC (USB)", "PREAMP (20m)", "KEY SPEED" },
            knobLines.Select(l => l.Name));
        Assert.Equal("50%", knobLines[0].Value);
        Assert.Equal("OFF", knobLines[1].Value);
        Assert.Equal("25%", knobLines[2].Value);
        Assert.Equal(RadioMenuLoad.NoAnswer, knobLines[3].Value);
        Assert.Equal("MID", knobLines[4].Value);
        Assert.Equal("P.AMP1", knobLines[5].Value);
        Assert.Equal("20 WPM", knobLines[6].Value);
        Assert.All(knobLines, l => Assert.Equal(RadioKnobs.Section, l.Section));
        // The menu line is still found in the table, not taken for a level.
        var menuLine = Assert.Single(file.Items, i => !RadioKnobs.IsKnobAddress(i.Address));
        Assert.NotNull(RadioMenuLoad.Find(Ic7300Mk2Menu.Items, menuLine));
    }

    [Fact]
    public void Only_the_transmit_lines_are_marked_TX_in_the_file()
    {
        var text = RadioKnobs.ToIni(new[] { new RadioKnobReading(K("RFGAIN"), 255), new RadioKnobReading(K("POWER"), 64) });
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.DoesNotContain("[TX]", lines.Single(l => l.StartsWith("RF GAIN")));
        Assert.Contains("[TX]", lines.Single(l => l.StartsWith("RF POWER")));
    }

    [Fact]
    public void A_file_saved_before_the_new_settings_still_loads()
    {
        // Saved by IWC v1.7.0-pre29: six levels, no per-mode or per-band lines.
        var file = RadioMenuLoad.Parse("[File]\nRadio = IC-7300MK2\n\n[Front panel]\nMIC GAIN = 50%   ; CI-V 14 0B  [TX]\nCOMP = ON\nRF POWER = 14%\n");
        Assert.Equal(new[] { "knob:MIC", "knob:COMP", "knob:POWER" }, file.Items.Select(i => i.Address));
        Assert.Equal(128, RadioKnobs.Encode(K("MIC"), file.Items[0].Value, out _));
    }

    [Fact]
    public void A_knob_line_never_matches_a_menu_item()
    {
        var file = RadioMenuLoad.Parse(FileText());
        foreach (var line in file.Items.Where(i => RadioKnobs.IsKnobAddress(i.Address)))
            Assert.Null(Ic7300Mk2Menu.Items.FirstOrDefault(i => i.Address == line.Address));
    }

    [Fact]
    public void An_unknown_front_panel_name_keeps_a_knob_address_but_no_knob()
    {
        var file = RadioMenuLoad.Parse("[File]\nRadio = IC-7300MK2\n\n[Front panel]\nBASS = 3\nTREBLE (USB) = 4\n");
        Assert.All(file.Items, line =>
        {
            Assert.True(RadioKnobs.IsKnobAddress(line.Address));
            Assert.Null(RadioKnobs.ByAddress(line.Address, Model));
        });
    }

    [Fact]
    public void Addresses_carry_the_mode_or_band()
    {
        Assert.Equal("knob:AGC@CW", RadioKnobs.AddressForName("agc (CW)"));
        Assert.Equal("knob:PREAMP@40m", RadioKnobs.AddressForName("PREAMP(40m)"));
        // A global setting ignores brackets' contents for its address.
        Assert.Equal("knob:MIC", RadioKnobs.AddressForName("MIC GAIN (USB)"));
        var knob = RadioKnobs.ByAddress("knob:ATT@15m", Model, out var where);
        Assert.Equal("ATT", knob!.Key);
        Assert.Equal("15m", where);
    }

    [Theory]
    [InlineData("MIC", "50%", 128)]
    [InlineData("MIC", " 7 ", 18)]
    [InlineData("MIC", "100%", 255)]
    [InlineData("MIC", "0", 0)]
    [InlineData("COMP", "on", 1)]
    [InlineData("COMP", "OFF", 0)]
    [InlineData("AGC", "mid", 2)]
    [InlineData("AGC", "SLOW", 3)]
    [InlineData("PREAMP", "P.AMP2", 2)]
    [InlineData("ATT", "20dB", 0x20)]
    [InlineData("ATT", "20 dB", 0x20)]
    [InlineData("ATT", "OFF", 0)]
    [InlineData("BKIN", "full", 2)]
    [InlineData("NOTCHWIDTH", "NAR", 2)]
    [InlineData("KEYSPEED", "20 WPM", 85)]
    [InlineData("KEYSPEED", "6", 0)]
    [InlineData("PITCH", "600 Hz", 128)]
    [InlineData("BKINDELAY", "7.5 dots", 128)]
    [InlineData("BKINDELAY", "13", 255)]
    public void Values_as_the_file_shows_them_encode(string key, string shown, int want)
    {
        Assert.Equal(want, RadioKnobs.Encode(K(key), shown, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("MIC", 128, "50%")]
    [InlineData("AGC", 1, "FAST")]
    [InlineData("ATT", 0x20, "20dB")]
    [InlineData("KEYSPEED", 85, "20 WPM")]
    [InlineData("PITCH", 128, "600 Hz")]
    [InlineData("BKINDELAY", 128, "7.5 dots")]
    [InlineData("NR", 1, "ON")]
    public void Values_show_as_the_radio_does(string key, int raw, string shown) =>
        Assert.Equal(shown, K(key).Show(raw));

    [Theory]
    [InlineData("MIC", "101%")]
    [InlineData("MIC", "-1")]
    [InlineData("MIC", "loud")]
    [InlineData("COMP", "50%")]
    [InlineData("AGC", "slowest")]
    [InlineData("KEYSPEED", "60 WPM")]
    [InlineData("PITCH", "200 Hz")]
    [InlineData("BKINDELAY", "1.0 dots")]
    public void Bad_values_say_why(string key, string shown)
    {
        Assert.Null(RadioKnobs.Encode(K(key), shown, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Same_allows_for_rounding_and_the_radio_steps()
    {
        // 47% is sent as 120; a radio holding 119 shows 47% too.
        Assert.True(K("MIC").Same(120, 119));
        Assert.False(K("MIC").Same(120, 100));
        // NR LEVEL takes steps: sent 68, read back 72.
        Assert.True(K("NRLEVEL").Same(68, 72));
        Assert.False(K("AGC").Same(1, 2));
        Assert.False(K("NB").Same(0, 1));
    }

    [Fact]
    public void Only_values_the_radio_takes_are_sent()
    {
        Assert.True(RadioKnobs.IsValue(K("MIC"), 255));
        Assert.False(RadioKnobs.IsValue(K("MIC"), 256));
        Assert.False(RadioKnobs.IsValue(K("NB"), 2));
        Assert.True(RadioKnobs.IsValue(K("ATT"), 0x20));
        Assert.False(RadioKnobs.IsValue(K("ATT"), 0x06));
        Assert.False(RadioKnobs.IsValue(K("AGC"), 0));
    }

    [Theory]
    [InlineData(14_074_000L, "20m")]
    [InlineData(7_074_000L, "40m")]
    [InlineData(10_137_900L, "30m")]
    [InlineData(1_840_000L, "160m")]
    [InlineData(50_313_000L, "6m")]
    [InlineData(9_500_000L, "GEN")]
    public void Bands_follow_the_frequency(long hz, string band) => Assert.Equal(band, RadioKnobs.BandOf(hz));

    [Theory]
    [InlineData("CW-U", "CW")]
    [InlineData("CW-L", "CW-R")]
    [InlineData("DATA-U", "USB-D")]
    [InlineData("USB", "USB")]
    public void Modes_use_the_radio_names(string iwc, string radio) => Assert.Equal(radio, RadioKnobs.ModeLabel(iwc));

    [Fact]
    public void A_per_mode_or_per_band_line_loads_only_where_it_was_saved()
    {
        Assert.Null(RadioKnobs.NotHere(K("MIC"), null, null));
        Assert.Null(RadioKnobs.NotHere(K("AGC"), "USB", UsbOn20));
        Assert.Null(RadioKnobs.NotHere(K("PREAMP"), "20m", UsbOn20));
        Assert.Contains("Saved for CW; the radio is on USB now", RadioKnobs.NotHere(K("AGC"), "CW", UsbOn20));
        Assert.Contains("Saved for 40m", RadioKnobs.NotHere(K("ATT"), "40m", UsbOn20));
        Assert.Contains("did not say", RadioKnobs.NotHere(K("AGC"), "USB", null));
        Assert.Contains("does not say which", RadioKnobs.NotHere(K("AGC"), null, UsbOn20));
    }

    [Fact]
    public async Task Reading_treats_minus_one_as_no_answer_and_retries()
    {
        var radio = FakeRadio.Create(new Queue<object>(new object[] { -1, -1, 40 }));
        Assert.Equal(40, await RadioKnobs.ReadAsync(radio, K("MIC"), default));
        var silent = FakeRadio.Create(new Queue<object>(new object[] { -1, -1, -1 }));
        Assert.Null(await RadioKnobs.ReadAsync(silent, K("MIC"), default));
    }

    [Fact]
    public async Task Reading_all_labels_the_per_mode_and_per_band_settings()
    {
        var radio = FakeRadio.Create(new Queue<object>(), levels: 2);
        var readings = await RadioKnobs.ReadAllAsync(radio, Knobs, UsbOn20, default);
        Assert.Equal("knob:AGC@USB", readings.Single(r => r.Knob.Key == "AGC").Address);
        Assert.Equal("PREAMP (20m)", readings.Single(r => r.Knob.Key == "PREAMP").Label);
        Assert.Equal("knob:MIC", readings.Single(r => r.Knob.Key == "MIC").Address);
        Assert.All(readings, r => Assert.Equal(2, r.Value));

        // Mode and band unknown: those four are not read, the rest are.
        var blind = await RadioKnobs.ReadAllAsync(radio, Knobs, null, default);
        Assert.Equal(4, blind.Count(r => r.Value is null));
        Assert.Null(blind.Single(r => r.Knob.Key == "AGC").Value);
    }

    [Fact]
    public async Task The_place_comes_from_the_radio_mode_and_frequency()
    {
        var radio = FakeRadio.Create(new Queue<object>(), mode: "CW-U", hz: 7_030_000);
        Assert.Equal(new RadioKnobPlace("CW", "40m"), await RadioKnobs.ReadPlaceAsync(radio, RadioVfo.A, default));
        var silent = FakeRadio.Create(new Queue<object>(), mode: "", hz: 0);
        Assert.Null(await RadioKnobs.ReadPlaceAsync(silent, RadioVfo.A, default));
    }

    [Fact]
    public async Task Writing_sends_the_knob_command()
    {
        var radio = FakeRadio.Create(new Queue<object>());
        Assert.True(await RadioKnobs.WriteAsync(radio, K("ATT"), 0x20, default));
        Assert.Equal((0x11, 0x00, 0x20), ((FakeRadio)(object)radio).Writes.Single());
    }

    [Fact]
    public void Reminder_kinds()
    {
        Assert.Equal("none", RadioMenuReminder.Check(null, T0, Model, Live()).Kind);
        Assert.Equal("ok", RadioMenuReminder.Check(Saved(2), T0, Model, Live(("knob:MIC", 128))).Kind);
        Assert.Equal("old", RadioMenuReminder.Check(Saved(RadioMenuReminder.OldAfterDays), T0, Model, Live(("knob:MIC", 128))).Kind);
        Assert.Equal("noknobs", RadioMenuReminder.Check(Saved(2, withKnobs: false), T0, Model, Live(("knob:MIC", 26))).Kind);
        var changed = RadioMenuReminder.Check(Saved(40), T0, Model, Live(("knob:MIC", 26)));
        Assert.Equal("changed", changed.Kind);
        Assert.Contains("MIC GAIN changed from 50% to 10%", RadioMenuReminder.Message(changed));
    }

    [Fact]
    public void A_level_with_no_answer_or_no_line_is_not_a_change()
    {
        // VOX was "(no answer)" in the file, MONI has no line, MIC did not answer now.
        var r = RadioMenuReminder.Check(Saved(2), T0, Model, Live(("knob:VOX", 200), ("knob:MONI", 200), ("knob:MIC", null)));
        Assert.Equal("ok", r.Kind);
    }

    [Fact]
    public void A_per_mode_setting_is_only_compared_in_its_own_mode()
    {
        // Saved AGC (USB) = MID. Now in CW with AGC FAST: not a change.
        Assert.Equal("ok", RadioMenuReminder.Check(Saved(2), T0, Model, Live(("knob:AGC@CW", 1))).Kind);
        // Back in USB with AGC FAST: a change.
        var r = RadioMenuReminder.Check(Saved(2), T0, Model, Live(("knob:AGC@USB", 1)));
        Assert.Equal("changed", r.Kind);
        Assert.Contains("AGC (USB) changed from MID to FAST", RadioMenuReminder.Message(r));
        // PREAMP on another band: not a change.
        Assert.Equal("ok", RadioMenuReminder.Check(Saved(2), T0, Model, Live(("knob:PREAMP@40m", 0))).Kind);
    }

    [Fact]
    public void A_hand_edited_file_compares_by_what_it_shows()
    {
        var text = FileText().Replace("MIC GAIN = 50%", "MIC GAIN = 35");
        var r = RadioMenuReminder.Check(new("f.ini", T0, text), T0, Model, Live(("knob:MIC", 89)));
        Assert.Equal("ok", r.Kind);
    }

    [Fact]
    public void Rounding_to_a_percent_is_not_a_change()
    {
        // The file says 50% (128); the radio holds 127, which also shows 50%.
        Assert.Equal("ok", RadioMenuReminder.Check(Saved(2), T0, Model, Live(("knob:MIC", 127))).Kind);
    }

    [Fact]
    public void Close_asks_only_when_a_level_differs()
    {
        Assert.False(RadioMenuReminder.ExitCheck(Saved(2), T0, Model, Live(("knob:MIC", 128), ("knob:COMP", 0))).Ask);
        var (ask, message, changes) = RadioMenuReminder.ExitCheck(Saved(2), T0, Model, Live(("knob:MIC", 128), ("knob:COMP", 1)));
        Assert.True(ask);
        Assert.Equal("COMP", Assert.Single(changes).Name);
        Assert.Contains("2 days ago", message);
        Assert.Contains("COMP changed from OFF to ON", message);
    }

    [Fact]
    public void Close_does_not_ask_with_no_file_no_readings_or_another_radio()
    {
        Assert.False(RadioMenuReminder.ExitCheck(null, T0, Model, Live(("knob:MIC", 26))).Ask);
        Assert.False(RadioMenuReminder.ExitCheck(Saved(2), T0, Model, Live()).Ask);
        Assert.False(RadioMenuReminder.ExitCheck(Saved(2), T0, "IC-705", Live(("knob:MIC", 26))).Ask);
    }

    [Fact]
    public void Saved_today_says_today()
    {
        var (_, message, _) = RadioMenuReminder.ExitCheck(Saved(0), T0, Model, Live(("knob:MIC", 51)));
        Assert.Contains("(today)", message);
    }

    /// <summary>
    /// A stand-in radio: front panel reads take the next value from a queue,
    /// or the fixed <c>levels</c> value once it is empty; writes are recorded
    /// and acknowledged; mode and frequency are fixed.
    /// </summary>
    public class FakeRadio : DispatchProxy
    {
        private Queue<object> _answers = new();
        private int _levels = -1;
        private string _mode = "USB";
        private long _hz = 14_074_000;
        public List<(int Cmd, int Sub, int Value)> Writes { get; } = new();

        public static IRadioController Create(Queue<object> answers, int levels = -1, string mode = "USB", long hz = 14_074_000)
        {
            var proxy = Create<IRadioController, FakeRadio>();
            var fake = (FakeRadio)(object)proxy;
            fake._answers = answers;
            fake._levels = levels;
            fake._mode = mode;
            fake._hz = hz;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IRadioController.ReadPanelSettingAsync):
                    return Task.FromResult(_answers.Count > 0 ? (int)_answers.Dequeue() : _levels);
                case nameof(IRadioController.WritePanelSettingAsync):
                    Writes.Add(((byte)args![0]!, (byte)args[1]!, (int)args[2]!));
                    return Task.FromResult(true);
                case nameof(IRadioController.GetModeAsync):
                    return Task.FromResult(_mode);
                case nameof(IRadioController.GetFrequencyHzAsync):
                    return Task.FromResult(_hz);
            }
            throw new NotSupportedException(method.Name);
        }
    }
}
