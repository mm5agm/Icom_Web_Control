using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// Off Windows no radio port opens until the operator has confirmed, and a connect
/// that finds anything wrong closes the port and stays closed. These run the check
/// on every system against a scripted radio. No real radio or port is involved.
/// </summary>
public class UsbLineCheckTests
{
    private const byte Mk2 = 0xB6;
    private const byte Original = 0x94;
    private const byte Ic7100 = 0x88;

    private sealed class ScriptedRadio : ICivClient
    {
        private readonly byte _address;
        public ScriptedRadio(byte address) => _address = address;

        public int Opens { get; private set; }
        public bool Off { get; init; }
        public bool Transmitting { get; set; }
        public bool AnswersTxStatus { get; init; } = true;
        public bool StartOpen { set => IsOpen = value; }
        public Dictionary<int, byte[]> Menu { get; } = new();
        public List<byte[]> Sent { get; } = new();
        public int RawSends { get; private set; }

        public bool IsOpen { get; private set; }
        public event EventHandler<CivFrame>? UnsolicitedFrame { add { } remove { } }

        public Task<bool> OpenAsync(string portName, int baudRate)
        {
            Opens++;
            IsOpen = true;
            return Task.FromResult(true);
        }

        public Task CloseAsync()
        {
            IsOpen = false;
            return Task.CompletedTask;
        }

        public Task SendRawAsync(byte[] bytes, CancellationToken cancellationToken = default)
        {
            RawSends++;
            return Task.CompletedTask;
        }

        public Task<CivFrame?> TransactAsync(byte[] frame, byte expectedCmd, int timeoutMs = 500, CancellationToken cancellationToken = default)
        {
            Sent.Add(frame);
            return Task.FromResult(Off ? null : Reply(frame[4], frame[5..^1]));
        }

        private CivFrame? Reply(byte cmd, byte[] data)
        {
            byte[]? body = cmd switch
            {
                CivProtocol.CmdReadId => new byte[] { cmd, 0x00, _address },
                0x03 => new byte[] { cmd, 0x00, 0x00, 0x10, 0x14, 0x00 },
                CivProtocol.CmdTransmit when data.Length == 1 && data[0] == CivProtocol.SubTxStatus && AnswersTxStatus
                    => new byte[] { cmd, data[0], (byte)(Transmitting ? 1 : 0) },
                CivProtocol.CmdMenu when data.Length == 3 && data[0] == CivProtocol.SubSetMenu
                    && Menu.TryGetValue(Number(data[1], data[2]), out var value)
                    => new byte[] { cmd, data[0], data[1], data[2] }.Concat(value).ToArray(),
                _ => null,
            };
            return body == null ? null : new CivFrame { To = CivProtocol.ControllerAddress, From = _address, Body = body };
        }

        private static int Number(byte hi, byte lo) =>
            (hi >> 4) * 1000 + (hi & 0x0F) * 100 + (lo >> 4) * 10 + (lo & 0x0F);

        public IEnumerable<byte[]> MenuFrames =>
            Sent.Where(f => f[4] == CivProtocol.CmdMenu && f[5] == CivProtocol.SubSetMenu);

        public IEnumerable<int> MenuItemsRead => MenuFrames.Select(f => Number(f[6], f[7]));
    }

    private static ScriptedRadio Radio(byte address, params int[] itemsOff)
    {
        var radio = new ScriptedRadio(address);
        foreach (var item in itemsOff) radio.Menu[item] = new byte[] { 0x00 };
        return radio;
    }

    private static (Icom_Web_Control.Services.CivRadioController Controller,
                    Icom_Web_Control.Services.RadioStateService State,
                    TestHost.MemorySettings Settings) Host(ScriptedRadio radio, bool confirmed = true, string model = "IC-7300MK2")
    {
        var (controller, state, settings, _) = TestHost.NewControllerWithHub(radio);
        controller.CheckUsbLines = true;
        settings.Settings.RadioModel = model;
        if (confirmed) settings.Settings.UsbLinesOffConfirmedFor = settings.Settings.UsbLinesKey();
        return (controller, state, settings);
    }

    [Fact]
    public async Task NothingOpensBeforeTheOperatorConfirms()
    {
        var radio = Radio(Mk2, 96, 97, 98);
        var (controller, state, _) = Host(radio, confirmed: false);

        Assert.False(await controller.ConnectAsync());

        Assert.Equal(0, radio.Opens);
        Assert.Empty(radio.Sent);
        Assert.Contains("confirm", state.ConnectionStatusText);
    }

    [Theory]
    [InlineData("COM9", "IC-7300MK2")]
    [InlineData("COM8", "IC-7100")]
    public async Task AConfirmationForAnotherPortOrModelCountsForNothing(string port, string model)
    {
        var radio = Radio(Mk2, 96, 97, 98);
        var (controller, _, settings) = Host(radio, confirmed: false);
        settings.Settings.UsbLinesOffConfirmedFor = $"{port}|{model}";

        Assert.False(await controller.ConnectAsync());

        Assert.Equal(0, radio.Opens);
    }

    [Fact]
    public async Task MovingToAnUnconfirmedPortLetsGoOfTheOldOne()
    {
        var radio = Radio(Mk2, 96, 97, 98);
        radio.StartOpen = true;
        var (controller, _, _) = Host(radio, confirmed: false);

        await controller.ConnectAsync();

        Assert.False(radio.IsOpen);
        Assert.Equal(0, radio.Opens);
    }

    [Fact]
    public async Task AMk2WithAllThreeOffConnects()
    {
        var radio = Radio(Mk2, 96, 97, 98);
        var (controller, state, settings) = Host(radio);

        Assert.True(await controller.ConnectAsync());

        Assert.True(radio.IsOpen);
        Assert.Equal("", state.ConnectionStatusText);
        Assert.Equal(new[] { 96, 97, 98 }, radio.MenuItemsRead);
        Assert.Equal(settings.Settings.UsbLinesKey(), settings.Settings.UsbLinesOffConfirmedFor);
    }

    [Fact]
    public async Task AnOriginal7300IsReadAtItsOwnItemNumbers()
    {
        var radio = Radio(Original, 78, 79, 80);
        var (controller, _, _) = Host(radio, model: "IC-7300");

        Assert.True(await controller.ConnectAsync());

        Assert.Equal(new[] { 78, 79, 80 }, radio.MenuItemsRead);
    }

    [Theory]
    [InlineData(96, "USB SEND")]
    [InlineData(97, "USB Keying (CW)")]
    [InlineData(98, "USB Keying (RTTY)")]
    public async Task AnItemNotOffClosesThePortAndItStaysClosed(int item, string name)
    {
        var radio = Radio(Mk2, 96, 97, 98);
        radio.Menu[item] = new byte[] { 0x01 };
        var (controller, state, settings) = Host(radio);

        Assert.False(await controller.ConnectAsync());

        Assert.False(radio.IsOpen);
        Assert.Contains(name, state.ConnectionStatusText);
        Assert.Equal("", settings.Settings.UsbLinesOffConfirmedFor);

        // The poll loop retries every few seconds. Reopening would raise the lines each time.
        Assert.False(await controller.ConnectAsync());
        Assert.Equal(1, radio.Opens);
    }

    [Fact]
    public async Task AnItemThatCannotBeReadCountsAgainst()
    {
        var radio = Radio(Mk2, 96, 98);
        var (controller, state, _) = Host(radio);

        Assert.False(await controller.ConnectAsync());

        Assert.False(radio.IsOpen);
        Assert.Contains("USB Keying (CW)", state.ConnectionStatusText);
    }

    [Fact]
    public async Task ARadioTransmittingOnConnectIsClosedBeforeAnythingElseIsAsked()
    {
        var radio = Radio(Mk2, 96, 97, 98);
        radio.Transmitting = true;
        var (controller, state, settings) = Host(radio);

        Assert.False(await controller.ConnectAsync());

        Assert.False(radio.IsOpen);
        Assert.Contains("transmitting", state.ConnectionStatusText);
        Assert.Empty(radio.MenuItemsRead);
        Assert.Equal("", settings.Settings.UsbLinesOffConfirmedFor);
    }

    [Fact]
    public async Task NoAnswerAboutTransmittingCountsAgainst()
    {
        var radio = new ScriptedRadio(Mk2) { AnswersTxStatus = false };
        var (controller, _, _) = Host(radio);

        Assert.False(await controller.ConnectAsync());

        Assert.False(radio.IsOpen);
    }

    [Fact]
    public async Task AnIc7100HasNoItemsToReadButIsStillAskedAboutTransmitting()
    {
        var radio = Radio(Ic7100);
        var (controller, _, _) = Host(radio, model: "IC-7100");

        Assert.True(await controller.ConnectAsync());

        Assert.Empty(radio.MenuFrames);
        Assert.Contains(radio.Sent, f => f[4] == CivProtocol.CmdTransmit && f[5] == CivProtocol.SubTxStatus);
    }

    [Fact]
    public async Task AnIc7100TransmittingOnConnectIsClosed()
    {
        var radio = Radio(Ic7100);
        radio.Transmitting = true;
        var (controller, _, _) = Host(radio, model: "IC-7100");

        Assert.False(await controller.ConnectAsync());

        Assert.False(radio.IsOpen);
    }

    [Fact]
    public async Task ARadioIwcHasNoTableForIsClosed()
    {
        var radio = Radio(0x70);
        var (controller, state, _) = Host(radio);

        Assert.False(await controller.ConnectAsync());

        Assert.False(radio.IsOpen);
        Assert.Contains("doesn't know", state.ConnectionStatusText);
    }

    [Fact]
    public async Task ASilentRadioIsNotTurnedOn()
    {
        var radio = new ScriptedRadio(Mk2) { Off = true };
        var (controller, _, settings) = Host(radio);
        settings.Settings.PowerOnAtStart = true;

        Assert.False(await controller.ConnectAsync());

        Assert.Equal(0, radio.RawSends);
        Assert.False(radio.IsOpen);
    }

    [Fact]
    public async Task TheCheckOnlyEverReads()
    {
        var radio = Radio(Mk2, 96, 97, 98);
        var (controller, _, _) = Host(radio);

        await controller.ConnectAsync();

        // FE FE to from 1A 05 hi lo FD: nothing after the item number.
        Assert.All(radio.MenuFrames, f => Assert.Equal(9, f.Length));
        Assert.DoesNotContain(radio.Sent, f => f[4] == CivProtocol.CmdTransmit && f.Length > 7);
    }
}
