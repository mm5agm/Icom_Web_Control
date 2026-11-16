using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// The MOD input, DATA OFF MOD and DATA MOD (IWC #144): the items each radio's
/// manual gives, the frames sent, and what a reply means.
/// </summary>
public class ModInputTests
{
    private static readonly CivRecording Recording = CivRecording.Load();

    private static CivFrame FromRadio(params byte[] body) => new() { To = 0xE0, From = 0xB6, Body = body };

    [Fact]
    public void EachRadioHasItsOwnItemsAndChoices()
    {
        var mk2 = RadioProfile.Ic7300Mk2.ModInput!;
        Assert.Equal((84, 85), (mk2.DataOffItem, mk2.DataItem));
        Assert.Equal(new[] { "MIC", "USB", "ACC", "MIC,USB", "MIC,ACC", "LAN" }, mk2.Choices);

        var old = RadioProfile.Ic7300.ModInput!;
        Assert.Equal((66, 67), (old.DataOffItem, old.DataItem));
        Assert.Equal(new[] { "MIC", "ACC", "MIC,ACC", "USB", "MIC,USB" }, old.Choices);

        var v = RadioProfile.Ic9700.ModInput!;
        Assert.Equal((115, 116), (v.DataOffItem, v.DataItem));
        Assert.Equal(new[] { "MIC", "ACC", "MIC,ACC", "USB", "MIC,USB", "LAN" }, v.Choices);

        Assert.Null(RadioProfile.Ic7100.ModInput);
    }

    [Theory]
    [InlineData("FE FE A2 E0 1A 05 01 15 FD", true)]      // DATA OFF MOD, read
    [InlineData("FE FE A2 E0 1A 05 01 16 03 FD", true)]   // DATA MOD to USB
    [InlineData("FE FE A2 E0 1A 05 01 17 FD", false)]     // the next item: not ours
    public void TheIc9700ListLetsTheItemsThrough(string hex, bool allowed) =>
        Assert.Equal(allowed, RadioProfile.Ic9700.Allows(CivRecording.Bytes(hex)));

    [Theory]
    [InlineData(new byte[] { 0x00 }, 0)]
    [InlineData(new byte[] { 0x05 }, 5)]
    [InlineData(new byte[] { 0x06 }, null)]       // past LAN
    [InlineData(new byte[] { 0x0A }, null)]       // not BCD
    [InlineData(new byte[] { 0x00, 0x01 }, null)] // two bytes
    [InlineData(new byte[0], null)]
    public void AReplyIsOneBcdByteNamingAChoice(byte[] data, int? expected) =>
        Assert.Equal(expected, ModInput.Decode(data, RadioProfile.Ic7300Mk2.ModInput!));

    [Fact]
    public async Task SettingDataModWritesItemEightyFiveAndReadsItBack()
    {
        var host = TestHost.NewController(Recording);
        Assert.True(await host.Controller.ConnectAsync());
        var items = RadioProfile.Ic7300Mk2.ModInput!;
        const string write = "FE FE B6 E0 1A 05 00 85 03 FD";
        host.Bus.Answer(write, FromRadio(CivProtocol.AckOk));
        host.Bus.Answer("FE FE B6 E0 1A 05 00 85 FD", FromRadio(0x1A, 0x05, 0x00, 0x85, 0x03));

        Assert.Equal(3, await ModInput.SetAsync(host.Controller, items, dataMode: true, choice: 3));
        Assert.Contains(write, host.Bus.SentHex);
    }

    [Fact]
    public async Task AChoiceThatIsNotThereIsNeverSent()
    {
        var host = TestHost.NewController(Recording);
        Assert.True(await host.Controller.ConnectAsync());
        int before = host.Bus.SentHex.Count();
        Assert.Null(await ModInput.SetAsync(host.Controller, RadioProfile.Ic7300.ModInput!, false, 5));
        Assert.Null(await ModInput.SetAsync(host.Controller, RadioProfile.Ic7300.ModInput!, false, -1));
        Assert.Equal(before, host.Bus.SentHex.Count());
    }

    [Fact]
    public async Task ARefusedWriteIsNotTakenAsDone()
    {
        var host = TestHost.NewController(Recording);
        Assert.True(await host.Controller.ConnectAsync());
        host.Bus.Answer("FE FE B6 E0 1A 05 00 84 01 FD", FromRadio(CivProtocol.AckNg));
        Assert.Null(await ModInput.SetAsync(host.Controller, RadioProfile.Ic7300Mk2.ModInput!, false, 1));
    }
}
