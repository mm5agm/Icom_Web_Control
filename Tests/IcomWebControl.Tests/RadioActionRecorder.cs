using System.Reflection;
using System.Text;
using Icom_Web_Control.Services;

namespace IcomWebControl.Tests;

/// <summary>
/// Runs every action on <see cref="IRadioController"/> against a simulated radio
/// and keeps the exact frames each one sent. A method added to the interface is
/// covered without touching this. It all runs on a fake bus: nothing can reach a
/// radio, so the transmit actions are safe to include.
/// </summary>
internal static class RadioActionRecorder
{
    internal sealed record Action(string Label, IReadOnlyList<string> Frames);

    private static readonly HashSet<string> NotActions = new()
    {
        nameof(IRadioController.ConnectAsync), nameof(IRadioController.DisconnectAsync),
        nameof(IRadioController.FindRadioAsync), nameof(IRadioController.RequestScopeStatusAnnounce),
        nameof(IRadioController.GetScopeDiagnostics),
    };

    /// <summary>The actions that send nothing more unless the radio is in a plain voice mode.</summary>
    private static readonly HashSet<string> NeedPlainUsb = new()
    {
        nameof(IRadioController.GetRxFilterAsync), nameof(IRadioController.SetRxFilterAsync),
        nameof(IRadioController.GetRxToneAsync), nameof(IRadioController.SetRxToneAsync),
    };

    /// <summary>Connecting, then every action on its own fresh connection.</summary>
    public static async Task<List<Action>> RecordAll(byte address)
    {
        var first = new SimulatedRadioClient(address);
        var (connected, _) = TestHost.NewController(first);
        Assert.True(await connected.ConnectAsync());
        var results = new List<Action> { new("ConnectAsync()", first.SentHex.ToList()) };

        var methods = typeof(IRadioController).GetMethods()
            .Where(m => !m.IsSpecialName && !NotActions.Contains(m.Name))
            .OrderBy(m => m.Name, StringComparer.Ordinal);

        // Together, because each connect waits out the scope retries. Every action
        // has its own controller and bus, and the results keep this order.
        var actions = methods.SelectMany(m => ArgumentSets(m).SelectMany(args =>
            NeedPlainUsb.Contains(m.Name)
                ? new[] { RecordOne(address, m, args, false), RecordOne(address, m, args, true) }
                : new[] { RecordOne(address, m, args, false) }));
        results.AddRange(await Task.WhenAll(actions));
        return results;
    }

    private static async Task<Action> RecordOne(byte address, MethodInfo method, object?[] args, bool plainUsb)
    {
        var bus = new SimulatedRadioClient(address, plainUsb);
        var (controller, _) = TestHost.NewController(bus);
        await controller.ConnectAsync();
        bus.Sent.Clear();

        if (method.Invoke(controller, args) is Task task)
            await task;
        return new Action(Label(method, args) + (plainUsb ? " in USB" : ""), bus.SentHex.ToList());
    }

    public static string Render(IEnumerable<Action> actions)
    {
        var text = new StringBuilder();
        foreach (var action in actions)
        {
            text.Append(action.Label).Append('\n');
            foreach (var frame in action.Frames) text.Append("  ").Append(frame).Append('\n');
        }
        return text.ToString();
    }

    private static IEnumerable<object?[]> ArgumentSets(MethodInfo method)
    {
        var parameters = method.GetParameters();
        bool hasBool = parameters.Any(p => p.ParameterType == typeof(bool));
        foreach (var flag in hasBool ? new[] { true, false } : new[] { true })
            yield return parameters.Select(p => Sample(p, flag)).ToArray();
    }

    private static object? Sample(ParameterInfo p, bool flag)
    {
        var t = p.ParameterType;
        if (t == typeof(CancellationToken)) return CancellationToken.None;
        if (t == typeof(bool)) return flag;
        if (t == typeof(int)) return p.Name switch
        {
            "percent" => 50,
            "channel" or "number" => 5,
            "wpm" => 20,
            "ms" => 500,
            "tenthsHz" => 885,
            "markHz" => 2125,
            "shiftHz" => 170,
            "spanHz" or "halfHz" => 2500,
            "hz" or "hpfHz" => 500,
            "lpfHz" => 2500,
            _ => 1,  // levels, and the small codes: filter 1, preamp 1, tuner on (2 would start a tune)
        };
        if (t == typeof(long)) return 14_074_000L;
        if (t == typeof(double)) return 7.5;
        if (t == typeof(string)) return p.Name == "message" ? "CQ" : "USB";
        if (t == typeof(RadioVfo)) return RadioVfo.A;
        if (t == typeof(byte[])) return new byte[] { 0x01 };
        if (t == typeof(IReadOnlyList<byte>)) return new byte[] { 0x03 };
        if (t == typeof(RadioMemoryChannel))
            return new RadioMemoryChannel { Channel = 5, FrequencyHz = 14_074_000, TxFrequencyHz = 14_074_000, Name = "TEST" };
        throw new NotSupportedException($"No sample for {t} ({p.Name}); add one.");
    }

    private static string Label(MethodInfo method, object?[] args) =>
        $"{method.Name}({string.Join(", ", args.Where(a => a is not CancellationToken).Select(a => a switch
        {
            byte[] bytes => CivRecording.Hex(bytes),
            RadioMemoryChannel channel => $"memory {channel.Channel}",
            _ => a?.ToString(),
        }))})";
}
