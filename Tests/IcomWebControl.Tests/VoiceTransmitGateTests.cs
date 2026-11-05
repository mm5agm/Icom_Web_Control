// Voice control is only in the Windows build.
#if WINDOWS
using Icom_Web_Control.Services.Voice;

namespace IcomWebControl.Tests;

public class VoiceTransmitGateTests
{
    [Theory]
    [InlineData("TxOn")]
    [InlineData("AtuTune")]
    public void CommandsThatGoOnTheAirAreCovered(string intent)
    {
        Assert.True(VoiceTransmitGate.StartsTransmit(intent, null));
    }

    // Stopping has to stay easy, and the everyday commands keep the normal gate.
    [Theory]
    [InlineData("TxOff")]
    [InlineData("AtuOn")]
    [InlineData("AtuOff")]
    [InlineData("BandUp")]
    [InlineData("SetNoiseReduction")]
    [InlineData("SetTxPower")]
    public void OtherCommandsAreNotCovered(string intent)
    {
        Assert.False(VoiceTransmitGate.StartsTransmit(intent, null));
    }

    [Theory]
    [InlineData("1C 00 01;")]             // PTT on
    [InlineData("1C0001")]                // no spaces
    [InlineData("1c 00 02;")]             // any non-zero state, lower case
    [InlineData("1C 01 02;")]             // start a tuner cycle
    [InlineData("17 43 51;")]             // send CW
    [InlineData("28 00 01;")]             // play a voice memory
    [InlineData("16 46 01;")]             // VOX on
    [InlineData("16 40 01;1C 00 01;")]    // hidden behind a harmless command
    public void MacrosThatGoOnTheAirAreCovered(string cat)
    {
        Assert.True(VoiceTransmitGate.StartsTransmit("Macro", cat));
    }

    [Theory]
    [InlineData("16 40 01;")]             // NR on
    [InlineData("07 00;07 A0;")]          // shipped Copy A to B
    [InlineData("1C 00 00;")]             // back to receive
    [InlineData("1C 00;")]                // only reads the state
    [InlineData("1C 01 01;")]             // tuner in line
    [InlineData("1C 01 00;")]             // tuner bypassed
    [InlineData("17 FF;")]                // stop sending CW
    [InlineData("28 00 00;")]             // stop a voice memory
    [InlineData("16 46 00;")]             // VOX off
    [InlineData("not hex;")]              // never sent: the dispatcher rejects it
    [InlineData("")]
    [InlineData(null)]
    public void OtherMacrosAreNotCovered(string? cat)
    {
        Assert.False(VoiceTransmitGate.StartsTransmit("Macro", cat));
    }

    // Saying the tune phrase during a cycle stops it: that must stay easy.
    [Fact]
    public void StoppingATuneIsNotCovered()
    {
        Assert.False(VoiceTransmitGate.StartsTransmit("AtuTune", null, atuTuning: true));
    }

    [Fact]
    public void TheBarIsWellAboveTheNormalOne()
    {
        Assert.True(VoiceTransmitGate.MinConfidence >= 0.8f);
    }
}
#endif
