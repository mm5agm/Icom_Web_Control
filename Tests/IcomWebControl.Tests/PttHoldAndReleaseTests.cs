using Icom_Web_Control.Services.Civ;
using Xunit;

namespace IcomWebControl.Tests;

/// <summary>
/// Software PTT (1C 00) after IWC Discussion #51. JS8Call's Tune sent set_ptt 3
/// and then set_ptt 0 about 150 ms later, because a poll reading taken just
/// before the key-up said RX and went straight into the cache get_ptt answers
/// from. And when the radio stopped answering right after a key-up, IWC sent
/// "PTT off" once and gave up. No serial port: a fake radio answers 1C 00 and
/// passes everything else to the simulated IC-7300.
/// </summary>
public class PttHoldAndReleaseTests
{
    private const byte Ic7300 = 0x94;

    /// <summary>
    /// Answers 1C 00 itself. <see cref="StaleRead"/> makes reads give the given
    /// state whatever the radio is doing (the reading queued before a command);
    /// <see cref="RxDead"/> makes it ignore "PTT off".
    /// </summary>
    private sealed class PttRadio : ICivClient
    {
        private readonly ICivClient _inner = new SimulatedRadioClient(Ic7300);
        public bool Transmitting;
        public bool? StaleRead;
        public bool RxDead;
        public int RxSent;
        public List<string> Sent { get; } = new();

        public bool IsOpen => _inner.IsOpen;
        public string? PortName => _inner.PortName;
        public void SetDtr(bool on) => _inner.SetDtr(on);
        public Task<bool> OpenAsync(string portName, int baudRate) => _inner.OpenAsync(portName, baudRate);
        public Task CloseAsync() => _inner.CloseAsync();
        public Task SendRawAsync(byte[] bytes, CancellationToken cancellationToken = default) => _inner.SendRawAsync(bytes, cancellationToken);
        public event EventHandler<CivFrame>? UnsolicitedFrame
        {
            add => _inner.UnsolicitedFrame += value;
            remove => _inner.UnsolicitedFrame -= value;
        }

        public Task<CivFrame?> TransactAsync(byte[] frame, byte expectedCmd, int timeoutMs = 500, CancellationToken cancellationToken = default)
        {
            var body = frame[4..^1];
            lock (Sent) Sent.Add(CivRecording.Hex(body));
            if (body.Length >= 2 && body[0] == 0x1C && body[1] == 0x00)
            {
                if (body.Length == 2)
                    return Reply(0x1C, 0x00, (StaleRead ?? Transmitting) ? (byte)1 : (byte)0);
                if (body[2] == 0x00)
                {
                    RxSent++;
                    if (RxDead) return Task.FromResult<CivFrame?>(null);
                    Transmitting = false;
                }
                else
                {
                    Transmitting = true;
                }
                return Reply(CivProtocol.AckOk);
            }
            return _inner.TransactAsync(frame, expectedCmd, timeoutMs, cancellationToken);
        }

        private static Task<CivFrame?> Reply(params byte[] body)
            => Task.FromResult<CivFrame?>(new CivFrame { To = CivProtocol.ControllerAddress, From = Ic7300, Body = body });
    }

    private static (Icom_Web_Control.Services.CivRadioController Controller, Icom_Web_Control.Services.RadioStateService State, PttRadio Radio, Func<long> Advance) Setup()
    {
        var radio = new PttRadio();
        var (controller, state) = TestHost.NewController(radio);
        long now = 1_000_000;
        controller.PttClock = () => now;
        return (controller, state, radio, () => now += Icom_Web_Control.Services.CivRadioController.PttHoldMs);
    }

    [Fact]
    public async Task A_stale_RX_reading_just_after_key_up_does_not_turn_PTT_off()
    {
        var (controller, state, radio, advance) = Setup();
        await radio.OpenAsync("COM8", 19200);

        await controller.SetTransmitAsync(true);
        radio.StaleRead = false; // the reading queued before the key-up
        Assert.True(await controller.PollTransmitAsync(default));
        Assert.True(state.IsTransmitting);

        // After the hold, a reading that still says RX is believed.
        advance();
        Assert.False(await controller.PollTransmitAsync(default));
        Assert.False(state.IsTransmitting);
    }

    [Fact]
    public async Task A_stale_TX_reading_just_after_release_does_not_turn_PTT_back_on()
    {
        var (controller, state, radio, _) = Setup();
        await radio.OpenAsync("COM8", 19200);

        await controller.SetTransmitAsync(true);
        await controller.SetTransmitAsync(false);
        radio.StaleRead = true;
        Assert.False(await controller.PollTransmitAsync(default));
        Assert.False(state.IsTransmitting);
    }

    [Fact]
    public async Task A_reading_that_agrees_with_the_command_goes_straight_through()
    {
        var (controller, state, radio, _) = Setup();
        await radio.OpenAsync("COM8", 19200);

        await controller.SetTransmitAsync(true);
        Assert.True(await controller.PollTransmitAsync(default));
        Assert.True(state.IsTransmitting);
    }

    [Fact]
    public async Task An_unacknowledged_release_is_retried_then_owed_to_the_poll_loop()
    {
        var (controller, state, radio, _) = Setup();
        await radio.OpenAsync("COM8", 19200);
        await controller.SetTransmitAsync(true);

        radio.RxDead = true;
        await controller.SetTransmitAsync(false);
        Assert.Equal(Icom_Web_Control.Services.CivRadioController.RxImmediateTries, radio.RxSent);
        Assert.True(controller.RxReleasePending);
        Assert.True(state.IsTransmitting); // get_ptt tells the truth: still keyed

        // Still dead: the poll loop's try fails and the release stays owed.
        await controller.SendPendingRxAsync(default);
        Assert.True(controller.RxReleasePending);

        // The radio answers again: the next poll loop releases it.
        radio.RxDead = false;
        await controller.SendPendingRxAsync(default);
        Assert.False(controller.RxReleasePending);
        Assert.False(radio.Transmitting);
        Assert.False(state.IsTransmitting);
    }

    [Fact]
    public async Task Nothing_is_sent_when_no_release_is_owed()
    {
        var (controller, _, radio, _) = Setup();
        await radio.OpenAsync("COM8", 19200);
        await controller.SendPendingRxAsync(default);
        Assert.Empty(radio.Sent);
    }

    [Fact]
    public async Task The_radio_reporting_RX_settles_an_owed_release()
    {
        var (controller, state, radio, advance) = Setup();
        await radio.OpenAsync("COM8", 19200);
        await controller.SetTransmitAsync(true);
        radio.RxDead = true;
        await controller.SetTransmitAsync(false);

        radio.Transmitting = false; // e.g. the operator pressed TX/RX on the radio
        advance();
        Assert.False(await controller.PollTransmitAsync(default));
        Assert.False(controller.RxReleasePending);
        Assert.False(state.IsTransmitting);
    }

    [Fact]
    public async Task A_new_key_up_replaces_an_owed_release()
    {
        var (controller, _, radio, _) = Setup();
        await radio.OpenAsync("COM8", 19200);
        await controller.SetTransmitAsync(true);
        radio.RxDead = true;
        await controller.SetTransmitAsync(false);

        await controller.SetTransmitAsync(true);
        Assert.False(controller.RxReleasePending);
    }

    [Fact]
    public async Task The_poll_loop_sends_the_owed_release_after_connecting()
    {
        var (controller, state, radio, _) = Setup();
        await radio.OpenAsync("COM8", 19200);
        await controller.SetTransmitAsync(true);
        radio.RxDead = true;
        await controller.SetTransmitAsync(false);
        Assert.True(controller.RxReleasePending);

        // The link drops and comes back with the radio answering.
        await radio.CloseAsync();
        radio.RxDead = false;
        lock (radio.Sent) radio.Sent.Clear();

        await controller.StartAsync(CancellationToken.None);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && controller.RxReleasePending)
                await Task.Delay(50);
        }
        finally
        {
            await controller.StopAsync(CancellationToken.None);
        }

        Assert.False(controller.RxReleasePending);
        Assert.False(radio.Transmitting);
        Assert.False(state.IsTransmitting);

        // Sent before the loop's first reading of anything else on the radio.
        List<string> sent;
        lock (radio.Sent) sent = radio.Sent.ToList();
        int rx = sent.IndexOf("1C 00 00");
        int firstTxRead = sent.IndexOf("1C 00");
        Assert.True(rx >= 0);
        Assert.True(firstTxRead < 0 || rx < firstTxRead);
    }
}
