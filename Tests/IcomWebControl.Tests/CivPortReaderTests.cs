using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

// D#51 (Paul VK2AHB): reading in SerialPort.DataReceived parked a pool thread
// on the port's own lock for every burst of bytes, until 1,282 pool threads were
// blocked and the poll loop froze. CI-V is read on a thread of its own now.
public sealed class CivPortReaderTests
{
    private sealed class FakePort
    {
        private readonly Queue<object> _script = new();
        public bool Current = true;
        public int Reads;
        public FakePort Bytes(params byte[] b) { _script.Enqueue(b); return this; }
        public FakePort Throws(Exception ex) { _script.Enqueue(ex); return this; }

        public int Read(byte[] buf, int offset, int count)
        {
            Reads++;
            if (_script.Count == 0) { Current = false; throw new TimeoutException(); }
            switch (_script.Dequeue())
            {
                case Exception ex: throw ex;
                case byte[] b: b.CopyTo(buf, offset); return b.Length;
                default: return 0;
            }
        }
    }

    private static (List<byte> Got, List<Exception> DeliverFaults, List<Exception> ReadFaults) Pump(
        FakePort port, Action<byte[]>? deliver = null)
    {
        var got = new List<byte>();
        var deliverFaults = new List<Exception>();
        var readFaults = new List<Exception>();
        CivPortReader.Run(port.Read, () => port.Current, deliver ?? got.AddRange, deliverFaults.Add, readFaults.Add);
        return (got, deliverFaults, readFaults);
    }

    [Fact]
    public void Chunks_are_delivered_in_order()
    {
        var (got, _, readFaults) = Pump(new FakePort().Bytes(1, 2).Bytes(3).Bytes(4, 5, 6));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, got);
        Assert.Empty(readFaults);
    }

    [Fact]
    public void A_read_that_times_out_just_reads_again()
    {
        var (got, _, readFaults) = Pump(new FakePort().Throws(new TimeoutException()).Bytes(7).Throws(new TimeoutException()).Bytes(8));
        Assert.Equal(new byte[] { 7, 8 }, got);
        Assert.Empty(readFaults);
    }

    [Fact]
    public void A_failed_read_ends_the_loop_and_is_reported()
    {
        var port = new FakePort().Bytes(1).Throws(new IOException("unplugged")).Bytes(2);
        var (got, _, readFaults) = Pump(port);
        Assert.Equal(new byte[] { 1 }, got);
        Assert.IsType<IOException>(Assert.Single(readFaults));
        Assert.Equal(2, port.Reads);
    }

    [Fact]
    public void A_port_let_go_of_stops_reading_quietly()
    {
        var port = new FakePort().Bytes(1).Bytes(2);
        var got = new List<byte>();
        var readFaults = new List<Exception>();
        CivPortReader.Run(port.Read, () => port.Current,
            b => { got.AddRange(b); port.Current = false; }, _ => { }, readFaults.Add);
        Assert.Equal(new byte[] { 1 }, got);
        Assert.Equal(1, port.Reads);
        Assert.Empty(readFaults);
    }

    [Fact]
    public void A_read_failing_after_the_port_was_let_go_of_is_not_reported()
    {
        var port = new FakePort();
        port.Throws(new ObjectDisposedException("port"));
        var readFaults = new List<Exception>();
        CivPortReader.Run((b, o, c) => { port.Current = false; return port.Read(b, o, c); },
            () => port.Current, _ => { }, _ => { }, readFaults.Add);
        Assert.Empty(readFaults);
    }

    [Fact]
    public void A_fault_in_delivery_is_reported_and_reading_carries_on()
    {
        var (_, deliverFaults, readFaults) = Pump(new FakePort().Bytes(1).Bytes(2),
            b => { if (b[0] == 1) throw new InvalidOperationException("bad frame"); });
        Assert.Single(deliverFaults);
        Assert.Empty(readFaults);
    }

    [Fact]
    public void Each_chunk_is_its_own_copy()
    {
        var chunks = new List<byte[]>();
        Pump(new FakePort().Bytes(1, 2).Bytes(3), chunks.Add);
        Assert.Equal(new byte[] { 1, 2 }, chunks[0]);
        Assert.Equal(new byte[] { 3 }, chunks[1]);
    }
}
