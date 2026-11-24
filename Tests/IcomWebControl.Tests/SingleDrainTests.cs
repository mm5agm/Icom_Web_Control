using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

// D#51 (Paul VK2AHB): DataReceived handlers ran at once on a busy laptop,
// waited in port.Read for bytes another handler had taken, and starved the
// thread pool. Only one handler drains now, in order, and none waits.
public sealed class SingleDrainTests
{
    private sealed class FakePort
    {
        private readonly Queue<byte> _bytes = new();
        public void Arrive(params byte[] b) { lock (_bytes) foreach (var x in b) _bytes.Enqueue(x); }
        public int Available() { lock (_bytes) return _bytes.Count; }
        public byte[]? Read(int n)
        {
            lock (_bytes)
            {
                if (_bytes.Count < n) throw new InvalidOperationException("read asked for bytes that weren't there");
                var chunk = new byte[n];
                for (int i = 0; i < n; i++) chunk[i] = _bytes.Dequeue();
                return chunk;
            }
        }
    }

    [Fact]
    public void One_caller_drains_everything_in_order()
    {
        var port = new FakePort();
        port.Arrive(1, 2, 3);
        var got = new List<byte>();
        Assert.True(new SingleDrain().Run(port.Available, port.Read, got.AddRange));
        Assert.Equal(new byte[] { 1, 2, 3 }, got);
    }

    [Fact]
    public void A_second_caller_returns_at_once_while_one_is_draining()
    {
        var port = new FakePort();
        var drain = new SingleDrain();
        var got = new List<byte>();
        bool inner = true;
        port.Arrive(1);
        drain.Run(port.Available, port.Read, chunk =>
        {
            got.AddRange(chunk);
            if (got.Count > 1) return;
            // Another DataReceived event fires mid-drain, with one more byte.
            port.Arrive(2);
            inner = drain.Run(port.Available, port.Read, got.AddRange);
        });
        Assert.False(inner);
        Assert.Equal(1, drain.Skipped);
        // The drainer picked up the byte the skipped event was raised for.
        Assert.Equal(new byte[] { 1, 2 }, got);
    }

    [Fact]
    public async Task Many_callers_at_once_never_read_bytes_that_are_not_there()
    {
        var port = new FakePort();
        var drain = new SingleDrain();
        var got = new List<byte>();
        var tasks = new List<Task>();
        for (int i = 0; i < 200; i++)
        {
            byte b = (byte)i;
            port.Arrive(b);
            tasks.Add(Task.Run(() => drain.Run(port.Available, port.Read, chunk => { lock (got) got.AddRange(chunk); })));
        }
        await Task.WhenAll(tasks);
        drain.Run(port.Available, port.Read, chunk => { lock (got) got.AddRange(chunk); });
        Assert.Equal(Enumerable.Range(0, 200).Select(i => (byte)i), got);
    }

    [Fact]
    public void Nothing_to_read_is_a_quick_no_op()
    {
        var port = new FakePort();
        int reads = 0;
        Assert.True(new SingleDrain().Run(port.Available, n => { reads++; return port.Read(n); }, _ => { }));
        Assert.Equal(0, reads);
    }
}
