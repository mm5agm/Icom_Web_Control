using Icom_Web_Control.Services;
using Icom_Web_Control.Services.Civ;
using Icom_Web_Control.Services.RadioMenu;

namespace IcomWebControl.Tests;

/// <summary>
/// The CI-V controller against the bytes an IC-7300MK2 really sent back. The
/// bus answers each request with the reply recorded for it, so these check
/// both what the controller asks and what it makes of the answer.
/// </summary>
public class CivRadioControllerReplayTests
{
    private static readonly CivRecording Recording = CivRecording.Load();

    // Connect turns the scope on in Center mode; these are sets, never recorded.
    private static readonly string[] ScopeSets =
    {
        "FE FE B6 E0 27 10 01 FD",
        "FE FE B6 E0 27 11 01 FD",
        "FE FE B6 E0 27 14 00 00 FD",
    };

    private static async Task<(CivRadioController C, ReplayCivClient Bus, RadioStateService State)> Connected()
    {
        var host = TestHost.NewController(Recording);
        Assert.True(await host.Controller.ConnectAsync());
        return host;
    }

    private static CivFrame FromRadio(params byte[] body) => new() { To = 0xE0, From = 0xB6, Body = body };

    [Fact]
    public async Task ConnectIdentifiesTheRadio()
    {
        var (c, bus, state) = await Connected();
        Assert.True(c.IsConnected);
        Assert.Equal("IC-7300MK2", c.ModelId);
        Assert.Equal("IC-7300MK2", state.RadioModel);
        Assert.Equal("", state.ConnectionStatusText);
        // Identify goes to everyone; after that every frame is for B6.
        Assert.StartsWith("FE FE 00 E0 19 00", bus.SentHex.First());
        Assert.All(bus.SentHex.Skip(1), h => Assert.StartsWith("FE FE B6 E0", h));
    }

    [Fact]
    public async Task ConnectSendsOnlyReadsAndTheScopeOn()
    {
        var (_, bus, _) = await Connected();
        Assert.Equal(ScopeSets, bus.SentNotRecorded.Distinct().OrderBy(h => h));
    }

    [Fact]
    public async Task ConnectSeedsTheReceiveControls()
    {
        var (_, _, state) = await Connected();
        Assert.Equal(29, state.AfGainA);    // 14 01 00 29
        Assert.Equal(255, state.RfGainA);   // 14 02 02 55
        Assert.Equal(80, state.SquelchA);   // 14 03 00 80
        Assert.Equal(8, state.NrLevelA);    // 14 06 00 08
        Assert.Equal(100, state.NbLevelA);  // 14 12 01 00
        Assert.Equal(115, state.ManualNotchFreqA);
        Assert.Equal("0", state.IpoA);      // 16 02 00
        Assert.Equal("3", state.AgcA);      // 16 12 03
        Assert.Equal("0", state.NbA);
        Assert.Equal("0", state.NrA);
        Assert.Equal("00", state.AttA);     // 11 00
        // Receiver-wide, so B mirrors A.
        Assert.Equal(state.AfGainA, state.AfGainB);
        Assert.Equal(state.AgcA, state.AgcB);
    }

    [Fact]
    public async Task ASilentRadioIsNotConnected()
    {
        // Port opens but nothing answers: a radio switched off at the front panel.
        var empty = new CivRecording { Sent = new(), Chunks = new(), Received = new(), Replies = new() };
        var (c, bus, state) = TestHost.NewController(empty);
        Assert.False(await c.ConnectAsync());
        Assert.False(bus.IsOpen);
        Assert.Contains("isn't responding", state.ConnectionStatusText);
    }

    [Fact]
    public async Task FrequenciesAndModeRead()
    {
        var (c, _, _) = await Connected();
        Assert.Equal(14_074_000, await c.GetFrequencyHzAsync(RadioVfo.A));   // 03
        Assert.Equal(7_110_400, await c.GetFrequencyHzAsync(RadioVfo.B));    // 25 01
        Assert.Equal("DATA-U", await c.GetModeAsync(RadioVfo.A));            // 26 00 01 01 01
    }

    [Fact]
    public async Task LevelsAndMetersRead()
    {
        var (c, _, _) = await Connected();
        Assert.Equal(15, await c.GetRfPowerPercentAsync());   // 14 0A 00 38: 38 of 255
        Assert.Equal(29, await c.GetAfGainAsync());
        Assert.Equal(0, await c.ReadSMeterAsync(RadioVfo.A)); // 15 02 00 00 on a quiet band
        Assert.False(await c.GetSplitAsync());                 // 0F 00
    }

    [Fact]
    public async Task MenuItemsRead()
    {
        var (c, _, _) = await Connected();
        Assert.Equal("MM5AGM    ", System.Text.Encoding.ASCII.GetString((await c.ReadMenuItemAsync(128))!));
        Assert.Equal("01 28", RadioMenuFile.Hex((await c.ReadMenuItemAsync(22))!));
        Assert.Equal("13 25", RadioMenuFile.Hex((await c.ReadMenuItemAsync(1))!));
        // Display Language and an item that does not exist both answered FA.
        Assert.Null(await c.ReadMenuItemAsync(130));
        Assert.Null(await c.ReadMenuItemAsync(999));
        Assert.Null(await c.ReadMenuItemAsync(0));
        Assert.Null(await c.ReadMenuItemAsync(10000));
    }

    [Fact]
    public async Task AMenuWriteSendsTheItemThenTheData()
    {
        var (c, bus, _) = await Connected();
        const string write = "FE FE B6 E0 1A 05 00 22 01 28 FD";
        // No write was recorded, so the FB here is made up; the frame sent is what is checked.
        bus.Answer(write, FromRadio(CivProtocol.AckOk));
        Assert.True(await c.WriteMenuItemAsync(22, new byte[] { 0x01, 0x28 }));
        Assert.Equal(write, bus.SentHex.Last());

        bus.Answer(write, FromRadio(CivProtocol.AckNg));
        Assert.False(await c.WriteMenuItemAsync(22, new byte[] { 0x01, 0x28 }));

        Assert.Null(await c.WriteMenuItemAsync(23, new byte[] { 0x01, 0x28 }));   // no answer
    }

    [Fact]
    public async Task AMenuWriteWithNoDataIsNotSent()
    {
        var (c, bus, _) = await Connected();
        int before = bus.Sent.Count;
        Assert.Null(await c.WriteMenuItemAsync(22, Array.Empty<byte>()));
        Assert.Equal(before, bus.Sent.Count);
    }

    [Fact]
    public async Task MemoryChannelsRead()
    {
        var (c, _, _) = await Connected();
        var one = (await c.ReadMemoryChannelAsync(1))!;
        Assert.False(one.IsEmpty);
        Assert.Equal(1_840_000, one.FrequencyHz);
        Assert.Equal("DATA-U", one.Mode);
        Assert.Equal(1, one.Filter);
        Assert.False(one.SplitOn);
        Assert.Equal("160m FT8", one.Name);

        var last = (await c.ReadMemoryChannelAsync(99))!;   // 1A 00 00 99 FF
        Assert.True(last.IsEmpty);
    }

    [Fact]
    public async Task RecordedSweepsReachTheScope()
    {
        var (c, bus, _) = await Connected();
        var scope = Recording.Received.Where(CivRecording.IsScope).ToList();
        bus.Push(scope);
        var d = c.GetScopeDiagnostics();
        Assert.True(d.Enabled);
        Assert.True(d.SweepsCompleted >= scope.Count(f => f.Data[2] == 0x01) - 2, $"{d.SweepsCompleted} sweeps");
        Assert.Equal(0, d.SweepsDiscarded);
        Assert.NotNull(d.SecondsSinceLastSweep);
    }

    [Fact]
    public async Task NothingSentEverTransmits()
    {
        // Connect and the reads: no PTT or tune (1C with data), no CW (17), no power (18).
        var (c, bus, _) = await Connected();
        await c.GetFrequencyHzAsync(RadioVfo.A);
        await c.ReadMenuItemAsync(128);
        await c.ReadMemoryChannelAsync(1);
        Assert.All(bus.Sent, f =>
        {
            var body = f[4..^1];
            Assert.NotEqual(CivProtocol.CmdCwSend, body[0]);
            Assert.NotEqual(CivProtocol.CmdPower, body[0]);
            Assert.False(body[0] == 0x1C && body.Length > 2, CivRecording.Hex(f));
        });
    }
}
