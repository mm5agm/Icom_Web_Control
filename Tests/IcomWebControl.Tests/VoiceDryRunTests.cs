// Voice control is only in the Windows build.
#if WINDOWS
using System.Reflection;
using Icom_Web_Control.Services;
using Icom_Web_Control.Services.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcomWebControl.Tests;

/// <summary>
/// "Test this pack" promises that a dry run sends nothing to the radio. These run
/// on a recording stand-in for the radio, so nothing can reach a real one.
/// </summary>
public class VoiceDryRunTests
{
    public static TheoryData<string, string?, string?> Commands => new()
    {
        { "TxOn", null, null },
        { "TxOff", null, null },
        { "AtuOn", null, null },
        { "AtuOff", null, null },
        { "AtuTune", null, null },
        { "SetTxPower", "50", null },
        { "SetNoiseReduction", "on", null },
        { "Macro", null, "1C 00 01;" },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task ADryRunSendsNothingToTheRadio(string intent, string? value, string? macroCat)
    {
        Assert.Empty(await RadioCallsFrom(intent, value, macroCat, dryRun: true));
    }

    // Without this the test above would pass on a recorder that sees nothing.
    [Theory]
    [MemberData(nameof(Commands))]
    public async Task TheSameCommandReallySendsOutsideADryRun(string intent, string? value, string? macroCat)
    {
        Assert.NotEmpty(await RadioCallsFrom(intent, value, macroCat, dryRun: false));
    }

    // "Confirm" is settled before the dispatcher, and is not a command of its own.
    [Fact]
    public async Task ConfirmItselfSendsNothingToTheRadio()
    {
        Assert.Empty(await RadioCallsFrom(VoiceTransmitConfirm.ConfirmIntent, null, null, dryRun: false));
    }

    // The recogniser can deliver a phrase after the button is released, so the end
    // of listening must not turn the dry run off.
    [Fact]
    public async Task ADryRunStaysSetAfterListeningStops()
    {
        var voice = NewVoiceService();

        await voice.StartListeningAsync(dryRun: true);
        await voice.StopListeningAsync();

        Assert.True(voice.CurrentStatus.DryRun);
    }

    [Fact]
    public void TheShippedPhrasesAskForConfirmAndDropKeyTransmitter()
    {
        var commands = VoicePhraseStore.BuildDefaults().SimpleCommands;

        Assert.Equal(new[] { "confirm" }, commands[VoiceTransmitConfirm.ConfirmIntent]);
        Assert.DoesNotContain("key transmitter", commands["TxOn"]);
    }

    [Fact]
    public void AnOlderPackLosesKeyTransmitterAndGainsConfirm()
    {
        var pack = VoicePhraseStore.BuildDefaults();
        pack.Version = 11;
        pack.SimpleCommands.Remove(VoiceTransmitConfirm.ConfirmIntent);
        pack.SimpleCommands["TxOn"] = new List<string> { "Key Transmitter", "my own phrase" };

        VoicePhraseStore.MigrateToV12(pack);

        Assert.Equal(12, pack.Version);
        Assert.Equal(new[] { "my own phrase" }, pack.SimpleCommands["TxOn"]);
        Assert.Equal(new[] { "confirm" }, pack.SimpleCommands[VoiceTransmitConfirm.ConfirmIntent]);
    }

    [Fact]
    public void APackWithOnlyKeyTransmitterGetsTheShippedPhrases()
    {
        var pack = VoicePhraseStore.BuildDefaults();
        pack.Version = 11;
        pack.SimpleCommands["TxOn"] = new List<string> { "key transmitter" };

        VoicePhraseStore.MigrateToV12(pack);

        Assert.Equal(VoicePhraseStore.BuildDefaults().SimpleCommands["TxOn"], pack.SimpleCommands["TxOn"]);
    }

    // Saying it would then run the macro, not confirm a transmit. Nothing can be
    // confirmed, which is the safe way round.
    [Fact]
    public void APackThatUsesConfirmForAMacroKeepsIt()
    {
        var pack = VoicePhraseStore.BuildDefaults();
        pack.Version = 11;
        pack.SimpleCommands.Remove(VoiceTransmitConfirm.ConfirmIntent);
        pack.Macros!.Add(new() { Name = "Mine", Phrases = ["Confirm"], Cat = "1C 00 00;" });

        VoicePhraseStore.MigrateToV12(pack);

        Assert.False(pack.SimpleCommands.ContainsKey(VoiceTransmitConfirm.ConfirmIntent));
    }

    [Fact]
    public void TheShippedGrammarFileMatchesThePhrases()
    {
        var srgs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Grammars", "Commands.en-GB.srgs"));

        Assert.DoesNotContain("key transmitter", srgs);
        Assert.Contains("out=\"ConfirmTransmit\";", srgs);
    }

    [Fact]
    public async Task TheNextLiveStartEndsTheDryRun()
    {
        var voice = NewVoiceService();

        await voice.StartListeningAsync(dryRun: true);
        await voice.StopListeningAsync();
        await voice.StartListeningAsync(dryRun: false);

        Assert.False(voice.CurrentStatus.DryRun);
    }

    private static async Task<IReadOnlyList<string>> RadioCallsFrom(string intent, string? value, string? macroCat, bool dryRun)
    {
        var radio = DispatchProxy.Create<IRadioController, RecordingRadio>();
        var (_, _, state) = TestHost.NewController();
        var dispatcher = new IntentDispatcher(
            NullLogger<IntentDispatcher>.Instance, state, new TestHost.MemorySettings(),
            new TestHost.SilentHubContext(), radio);

        var args = new Dictionary<string, object>();
        if (value != null) args["value"] = value;
        if (macroCat != null) args["macroCat"] = macroCat;

        await dispatcher.DispatchAsync(intent, args, dryRun: dryRun);
        return ((RecordingRadio)(object)radio).Calls;
    }

    // Nothing here starts the recogniser: its engine is only built when the app
    // starts as a hosted service, and the parts left null are only used by a
    // recognised phrase.
    private static VoiceControlService NewVoiceService() => new(
        NullLogger<VoiceControlService>.Instance, new TestHost.SilentHubContext(),
        null!, null!, new TestHost.MemorySettings(), null!, null!);

    /// <summary>Records every call that could change the radio, and answers the rest with defaults.</summary>
    public class RecordingRadio : DispatchProxy
    {
        public List<string> Calls { get; } = new();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method == null) return null;
            if (!method.Name.StartsWith("Get") && !method.Name.StartsWith("get_"))
                Calls.Add(method.Name);

            var type = method.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                // A raw command only "succeeds" if it reports that it did.
                object? answer = inner == typeof(bool) ? true
                    : inner.IsValueType ? Activator.CreateInstance(inner)
                    : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner)
                    .Invoke(null, new[] { answer });
            }
            if (type == typeof(string)) return "IC-7300MK2";
            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }
}
#endif
