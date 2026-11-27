using System.Reflection;
using Icom_Web_Control.Services;
using Icom_Web_Control.Services.RadioMenu;

namespace IcomWebControl.Tests;

/// <summary>
/// The front panel levels saved with the radio menu (MIC GAIN, COMP, RF POWER
/// and so on): the [Front panel] section, reading it back, reading the radio,
/// and the save reminder and close check built on them.
/// </summary>
public class RadioKnobsTests
{
    private const string Model = "IC-7300MK2";
    private static readonly DateTime T0 = new(2026, 11, 27, 18, 0, 0);
    private static IReadOnlyList<RadioKnob> Knobs => RadioKnobs.TableFor(Model)!;
    private static RadioKnob K(string key) => Knobs.Single(k => k.Key == key);

    private static string FileText(bool withKnobs = true, int mic = 50, bool comp = false)
    {
        var item = Ic7300Mk2Menu.Items[0];
        var menu = RadioMenuFile.ToIni(Model, T0, new[] { new RadioMenuReading(item, item.Codec.Values?[0] ?? new byte[] { 0 }) });
        if (!withKnobs) return menu;
        return menu + RadioKnobs.ToIni(new[]
        {
            new RadioKnobReading(K("MIC"), mic),
            new RadioKnobReading(K("COMP"), comp ? 1 : 0),
            new RadioKnobReading(K("POWER"), 25),
            new RadioKnobReading(K("VOX"), null),
        });
    }

    private static RadioMenuSaveInfo Saved(int daysAgo, bool withKnobs = true) =>
        new("file.ini", T0.AddDays(-daysAgo), FileText(withKnobs));

    private static Dictionary<string, int?> Live(params (string Key, int? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public void Only_the_IC7300MK2_has_levels()
    {
        Assert.NotNull(RadioKnobs.TableFor("IC-7300MK2"));
        Assert.Null(RadioKnobs.TableFor("IC-705"));
        Assert.Null(RadioKnobs.TableFor(null));
    }

    [Fact]
    public void The_file_section_reads_back_with_knob_addresses()
    {
        var file = RadioMenuLoad.Parse(FileText());
        var knobLines = file.Items.Where(i => RadioKnobs.IsKnobAddress(i.Address)).ToList();
        Assert.Equal(new[] { "knob:MIC", "knob:COMP", "knob:POWER", "knob:VOX" }, knobLines.Select(l => l.Address));
        Assert.Equal("50%", knobLines[0].Value);
        Assert.Equal("OFF", knobLines[1].Value);
        Assert.Equal(RadioMenuLoad.NoAnswer, knobLines[3].Value);
        Assert.All(knobLines, l => Assert.Equal(RadioKnobs.Section, l.Section));
        // The menu line is still found in the table, not taken for a level.
        var menuLine = Assert.Single(file.Items, i => !RadioKnobs.IsKnobAddress(i.Address));
        Assert.NotNull(RadioMenuLoad.Find(Ic7300Mk2Menu.Items, menuLine));
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
        var file = RadioMenuLoad.Parse("[File]\nRadio = IC-7300MK2\n\n[Front panel]\nBASS = 3\n");
        var line = Assert.Single(file.Items);
        Assert.True(RadioKnobs.IsKnobAddress(line.Address));
        Assert.Null(RadioKnobs.ByAddress(line.Address, Model));
    }

    [Theory]
    [InlineData("MIC", "50%", 50)]
    [InlineData("MIC", " 7 ", 7)]
    [InlineData("MIC", "100%", 100)]
    [InlineData("MIC", "0", 0)]
    [InlineData("COMP", "on", 1)]
    [InlineData("COMP", "OFF", 0)]
    public void Values_as_the_file_shows_them_encode(string key, string shown, int want)
    {
        Assert.Equal(want, RadioKnobs.Encode(K(key), shown, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("MIC", "101%")]
    [InlineData("MIC", "-1")]
    [InlineData("MIC", "loud")]
    [InlineData("COMP", "50%")]
    public void Bad_values_say_why(string key, string shown)
    {
        Assert.Null(RadioKnobs.Encode(K(key), shown, out var error));
        Assert.False(string.IsNullOrEmpty(error));
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
    public async Task COMP_off_does_not_count_when_the_radio_answered_nothing_else()
    {
        // Every level read gives -1; the COMP read gives false, which could be OFF or silence.
        var radio = FakeRadio.Create(new Queue<object>(), levels: -1, comp: false);
        var readings = await RadioKnobs.ReadAllAsync(radio, Knobs, default);
        Assert.All(readings, r => Assert.Null(r.Value));

        var answering = FakeRadio.Create(new Queue<object>(), levels: 30, comp: false);
        var good = await RadioKnobs.ReadAllAsync(answering, Knobs, default);
        Assert.Equal(0, good.Single(r => r.Knob.Key == "COMP").Value);
        Assert.Equal(30, good.Single(r => r.Knob.Key == "MIC").Value);
    }

    [Fact]
    public void Reminder_kinds()
    {
        Assert.Equal("none", RadioMenuReminder.Check(null, T0, Model, Live()).Kind);
        Assert.Equal("ok", RadioMenuReminder.Check(Saved(2), T0, Model, Live(("MIC", 50))).Kind);
        Assert.Equal("old", RadioMenuReminder.Check(Saved(RadioMenuReminder.OldAfterDays), T0, Model, Live(("MIC", 50))).Kind);
        Assert.Equal("noknobs", RadioMenuReminder.Check(Saved(2, withKnobs: false), T0, Model, Live(("MIC", 10))).Kind);
        var changed = RadioMenuReminder.Check(Saved(40), T0, Model, Live(("MIC", 10)));
        Assert.Equal("changed", changed.Kind);
        Assert.Contains("MIC GAIN changed from 50% to 10%", RadioMenuReminder.Message(changed));
    }

    [Fact]
    public void A_level_with_no_answer_or_no_line_is_not_a_change()
    {
        // VOX was "(no answer)" in the file, MONI has no line, MIC did not answer now.
        var r = RadioMenuReminder.Check(Saved(2), T0, Model, Live(("VOX", 80), ("MONI", 80), ("MIC", null)));
        Assert.Equal("ok", r.Kind);
    }

    [Fact]
    public void A_hand_edited_file_compares_by_what_it_shows()
    {
        var text = FileText().Replace("MIC GAIN = 50%", "MIC GAIN = 35");
        var r = RadioMenuReminder.Check(new("f.ini", T0, text), T0, Model, Live(("MIC", 35)));
        Assert.Equal("ok", r.Kind);
    }

    [Fact]
    public void Close_asks_only_when_a_level_differs()
    {
        Assert.False(RadioMenuReminder.ExitCheck(Saved(2), T0, Model, Live(("MIC", 50), ("COMP", 0))).Ask);
        var (ask, message, changes) = RadioMenuReminder.ExitCheck(Saved(2), T0, Model, Live(("MIC", 50), ("COMP", 1)));
        Assert.True(ask);
        Assert.Equal("COMP", Assert.Single(changes).Name);
        Assert.Contains("2 days ago", message);
        Assert.Contains("COMP changed from OFF to ON", message);
    }

    [Fact]
    public void Close_does_not_ask_with_no_file_no_readings_or_another_radio()
    {
        Assert.False(RadioMenuReminder.ExitCheck(null, T0, Model, Live(("MIC", 10))).Ask);
        Assert.False(RadioMenuReminder.ExitCheck(Saved(2), T0, Model, Live()).Ask);
        Assert.False(RadioMenuReminder.ExitCheck(Saved(2), T0, "IC-705", Live(("MIC", 10))).Ask);
    }

    [Fact]
    public void Saved_today_says_today()
    {
        var (_, message, _) = RadioMenuReminder.ExitCheck(Saved(0), T0, Model, Live(("MIC", 20)));
        Assert.Contains("(today)", message);
    }

    /// <summary>
    /// A stand-in radio: level reads take the next value from a queue, or the
    /// fixed <c>levels</c> value once it is empty; the COMP read gives <c>comp</c>.
    /// </summary>
    public class FakeRadio : DispatchProxy
    {
        private Queue<object> _answers = new();
        private int _levels = -1;
        private bool _comp;

        public static IRadioController Create(Queue<object> answers, int levels = -1, bool comp = false)
        {
            var proxy = Create<IRadioController, FakeRadio>();
            var fake = (FakeRadio)(object)proxy;
            fake._answers = answers;
            fake._levels = levels;
            fake._comp = comp;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var type = method!.ReturnType;
            if (type == typeof(Task<int>))
                return Task.FromResult(_answers.Count > 0 ? (int)_answers.Dequeue() : _levels);
            if (type == typeof(Task<bool>))
                return Task.FromResult(_comp);
            if (type == typeof(Task))
                return Task.CompletedTask;
            throw new NotSupportedException(method.Name);
        }
    }
}
