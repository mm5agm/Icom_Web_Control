using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// Owns the CI-V serial port and turns it into a request/reply channel.
    /// Icom replacement for the Yaesu <c>CatMultiplexerService</c> — but far
    /// smaller in Phase 2: one transaction at a time, matched by command byte.
    ///
    /// Two CI-V realities this handles that the Yaesu code never had to:
    ///  * <b>Binary, not ASCII.</b> Bytes are read with <c>SerialPort.Read</c>,
    ///    never <c>ReadExisting</c> (which would decode the 0x00–0xFF payload as
    ///    text and corrupt it).
    ///  * <b>Bus echo.</b> Icom's CI-V (and its USB emulation of it) echoes the
    ///    controller's own transmission back before the radio replies. Echo
    ///    frames carry To == the radio's address; genuine replies carry
    ///    To == our controller address, so we simply drop the former.
    ///
    /// Serial parameters are the verified IC-7300 MkII values: 8 data bits, no
    /// parity, 1 stop bit (8N1). DTR and RTS are left de-asserted — on Icom's
    /// USB "Serial A" port those lines can be assigned to PTT/keying, and we
    /// must never risk keying the transmitter just by opening the port.
    /// </summary>
    public sealed class CivBusService : ICivClient, IDisposable
    {
        private readonly ILogger<CivBusService> _logger;
        private readonly CivFrameBuffer _buffer = new();
        private readonly object _portLock = new();
        private readonly SemaphoreSlim _txLock = new(1, 1);

        private SerialPort? _port;
        private Pending? _pending;

        public CivBusService(ILogger<CivBusService> logger)
        {
            _logger = logger;
            _buffer.FrameReceived += OnFrameReceived;
        }

        public bool IsOpen
        {
            get { lock (_portLock) { return _port?.IsOpen ?? false; } }
        }

        public event EventHandler<CivFrame>? UnsolicitedFrame;

        public Task<bool> OpenAsync(string portName, int baudRate)
        {
            lock (_portLock)
            {
                try
                {
                    if (_port?.IsOpen == true)
                    {
                        if (string.Equals(_port.PortName, portName, StringComparison.OrdinalIgnoreCase)
                            && _port.BaudRate == baudRate)
                        {
                            return Task.FromResult(true);
                        }
                        CloseInternal();
                    }

                    var available = SerialPort.GetPortNames();
                    if (Array.IndexOf(available, portName) < 0)
                    {
                        _logger.LogWarning("CI-V port {Port} not present. Available: {Ports}",
                            portName, string.Join(", ", available));
                        return Task.FromResult(false);
                    }

                    _port = new SerialPort
                    {
                        PortName = portName,
                        BaudRate = baudRate,
                        DataBits = 8,
                        Parity = Parity.None,
                        StopBits = StopBits.One,
                        Handshake = Handshake.None,
                        ReadTimeout = 500,
                        WriteTimeout = 500,
                        DtrEnable = false,
                        RtsEnable = false,
                    };

                    _buffer.Clear();
                    _port.DataReceived += OnDataReceived;
                    _port.Open();

                    _logger.LogInformation("✓ CI-V port {Port} open at {Baud} 8N1", portName, baudRate);
                    return Task.FromResult(true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to open CI-V port {Port}", portName);
                    CloseInternal();
                    return Task.FromResult(false);
                }
            }
        }

        public Task CloseAsync()
        {
            lock (_portLock)
            {
                CloseInternal();
            }
            return Task.CompletedTask;
        }

        private void CloseInternal()
        {
            if (_port == null) return;
            try
            {
                _port.DataReceived -= OnDataReceived;
                if (_port.IsOpen)
                    _port.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while closing CI-V port");
            }
            finally
            {
                _port.Dispose();
                _port = null;
                _pending?.Tcs.TrySetResult(null);
                _pending = null;
            }
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                byte[] chunk;
                lock (_portLock)
                {
                    if (_port?.IsOpen != true) return;
                    int n = _port.BytesToRead;
                    if (n <= 0) return;
                    chunk = new byte[n];
                    int read = _port.Read(chunk, 0, n);
                    if (read < n)
                        chunk = chunk[..read];
                }
                _buffer.Append(chunk);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error reading CI-V bytes");
            }
        }

        private void OnFrameReceived(object? sender, CivFrame frame)
        {
            // Drop our own bus echo: those frames are addressed TO the radio.
            if (frame.To != CivProtocol.ControllerAddress && frame.To != 0x00)
                return;

            var pending = _pending;
            if (pending != null && (frame.Cmd == pending.ExpectedCmd || frame.Cmd == CivProtocol.AckNg))
            {
                pending.Tcs.TrySetResult(frame);
                return;
            }

            UnsolicitedFrame?.Invoke(this, frame);
        }

        public async Task<CivFrame?> TransactAsync(byte[] frame, byte expectedCmd, int timeoutMs = 500, CancellationToken cancellationToken = default)
        {
            if (!IsOpen) return null;

            await _txLock.WaitAsync(cancellationToken);
            try
            {
                var tcs = new TaskCompletionSource<CivFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending = new Pending { ExpectedCmd = expectedCmd, Tcs = tcs };

                lock (_portLock)
                {
                    if (_port?.IsOpen != true) return null;
                    _port.Write(frame, 0, frame.Length);
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, timeoutCts.Token));
                timeoutCts.Cancel(); // stop the delay task if the reply won

                if (completed == tcs.Task)
                    return await tcs.Task;

                return null; // timed out
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                _pending = null;
                _txLock.Release();
            }
        }

        public void Dispose()
        {
            lock (_portLock)
            {
                CloseInternal();
            }
            _txLock.Dispose();
        }

        private sealed class Pending
        {
            public byte ExpectedCmd { get; init; }
            public TaskCompletionSource<CivFrame?> Tcs { get; init; } = null!;
        }
    }
}
