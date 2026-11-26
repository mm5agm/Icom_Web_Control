using System.Buffers.Binary;
using System.Text;
using Icom_Web_Control.Services;
using Xunit;

namespace IcomWebControl.Tests;

// WSJT-X Status (type 1) parsing: the early fields IWC has always read, and
// the operator's own call and grid that the Spot Map page uses.
public class WsjtxStatusParseTests
{
    private static void U32(List<byte> b, uint v) { var x = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(x, v); b.AddRange(x); }
    private static void U64(List<byte> b, ulong v) { var x = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(x, v); b.AddRange(x); }
    private static void Str(List<byte> b, string s) { var x = Encoding.UTF8.GetBytes(s); U32(b, (uint)x.Length); b.AddRange(x); }

    private static List<byte> StatusStart()
    {
        var b = new List<byte>();
        U32(b, 0xADBCCBDA); U32(b, 2); U32(b, 1);
        Str(b, "WSJT-X");
        U64(b, 7_074_000);
        Str(b, "FT8"); Str(b, "K1ABC"); Str(b, "-10"); Str(b, "FT8");
        b.Add(1);   // Tx enabled
        b.Add(1);   // Transmitting
        return b;
    }

    [Fact]
    public void Full_Status_ReadsTheOperatorsCallAndGrid()
    {
        var b = StatusStart();
        b.Add(0); U32(b, 1500); U32(b, 1500);
        Str(b, "MM5AGM"); Str(b, "IO85 "); Str(b, "FN42");
        b.Add(0); Str(b, "");

        var m = WsjtxUdpService.TryParseMessage(b.ToArray());

        Assert.NotNull(m);
        Assert.Equal(7_074_000, m!.DialFrequency);
        Assert.Equal("FT8", m.Mode);
        Assert.True(m.Transmitting);
        Assert.Equal("MM5AGM", m.DeCall);
        Assert.Equal("IO85", m.DeGrid);
    }

    [Fact]
    public void Short_Status_StillGivesTheEarlyFields()
    {
        var m = WsjtxUdpService.TryParseMessage(StatusStart().ToArray());

        Assert.NotNull(m);
        Assert.Equal(7_074_000, m!.DialFrequency);
        Assert.True(m.Transmitting);
        Assert.Equal("", m.DeCall);
        Assert.Equal("", m.DeGrid);
    }
}
