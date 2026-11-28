using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Icom_Web_Control.Services.Wsjtx;

namespace Icom_Web_Control.Services
{
    /// <summary>
    /// Listens for WSJT-X UDP status broadcasts and syncs frequency/TX state to the app.
    /// WSJT-X default UDP address: 224.0.0.1:2237 (multicast).
    /// Frequency changes from WSJT-X are also handled via the rigctld TCP connection (port 4532);
    /// this service provides real-time redundancy and exposes TX status to the web UI.
    /// </summary>
    public class WsjtxUdpService : BackgroundService
    {
        // Removed hardcoded port; will use settings
        private const uint MagicNumber = 0xADBCCBDA;
        private const uint MessageTypeHeartbeat = 0;
        private const uint MessageTypeStatus = 1;
        private const uint MessageTypeDecode = 2;
        private const uint MessageTypeClose = 6;

        private readonly IRadioController _radio;
        private readonly RadioStateService _radioStateService;
        private readonly ILogger<WsjtxUdpService> _logger;
        private readonly ISettingsService _settingsService;

        private readonly object _lock = new();
        private DateTime _lastSeen = DateTime.MinValue;
        private bool _isTransmitting;
        private string _wsjtxMode = "";
        private string _wsjtxId = "";
        private string _deCall = "";
        private string _deGrid = "";
        private long _dialFrequency;
        private IPEndPoint? _wsjtxEndPoint;
        private UdpClient? _udpClient;

        /// <summary>Every station WSJT-X decodes, for the Spot Map's instant "I heard".</summary>
        public WsjtxHeardList HeardList { get; } = new();

        /// <summary>WSJT-X's dial frequency from its last Status, in Hz. 0 until a Status arrives.</summary>
        public long DialFrequency { get { lock (_lock) return _dialFrequency; } }

        /// <summary>
        /// A decode's mode: WSJT-X marks FT8 "~" and FT4 "+"; anything else
        /// takes the mode from its last Status.
        /// </summary>
        public static string DecodeModeName(string symbol, string statusMode) => symbol switch
        {
            "~" => "FT8",
            "+" => "FT4",
            _ => string.IsNullOrWhiteSpace(statusMode) ? "WSJT-X" : statusMode.Trim(),
        };

        /// <summary>How old a decode the Spot Map's Call button will still answer.</summary>
        public static readonly TimeSpan HeardReplyMaxAge = TimeSpan.FromMinutes(30);

        /// <summary>
        /// The Spot Map's Call button: asks WSJT-X/WS to answer a station's
        /// latest decode, as a double-click would. WSJT-X ignores it if that
        /// decode is no longer in its Band Activity window, and needs
        /// "Accept UDP requests" ticked.
        /// </summary>
        public async Task<(bool Ok, string? Error)> SendHeardReplyAsync(string call)
        {
            if (!HeardList.TryGetLatest(call, out var decode, out var at) || decode is null)
                return (false, $"{call} hasn't been decoded since IWC started");
            if (DateTime.UtcNow - at > HeardReplyMaxAge)
                return (false, $"{call} was last decoded over {HeardReplyMaxAge.TotalMinutes:0} minutes ago");
            return await SendAsync(id => WsjtxProtocol.BuildReply(decode, id), $"Reply to {call} (Spot Map)");
        }

        private async Task<(bool Ok, string? Error)> SendAsync(Func<string, byte[]> build, string what)
        {
            UdpClient? udp;
            IPEndPoint? target;
            string id;
            lock (_lock)
            {
                udp = _udpClient;
                target = _wsjtxEndPoint;
                id = _wsjtxId;
            }
            if (udp is null) return (false, "IWC isn't listening for WSJT-X");
            if (target is null || !IsConnected) return (false, "WSJT-X is not sending to IWC");
            var bytes = build(id);
            await udp.SendAsync(bytes, bytes.Length, target);
            _logger.LogInformation("[WSJT-X UDP] Sent {What} to '{Id}' at {EndPoint}", what, id, target);
            return (true, null);
        }

        public bool IsConnected { get { lock (_lock) return (DateTime.UtcNow - _lastSeen).TotalSeconds < 30; } }
        public bool IsTransmitting { get { lock (_lock) return _isTransmitting; } }
        public string WsjtxMode { get { lock (_lock) return _wsjtxMode; } }
        public string WsjtxId { get { lock (_lock) return _wsjtxId; } }
        /// <summary>The operator's callsign from WSJT-X's last Status (Settings, My Call). Empty until a Status arrives.</summary>
        public string DeCall { get { lock (_lock) return _deCall; } }
        /// <summary>The operator's grid from WSJT-X's last Status (Settings, My Grid), for the Spot Map. Empty until a Status arrives.</summary>
        public string DeGrid { get { lock (_lock) return _deGrid; } }

        public WsjtxUdpService(
            IRadioController radio,
            RadioStateService radioStateService,
            ILogger<WsjtxUdpService> logger,
            ISettingsService settingsService)
        {
            _radio = radio;
            _radioStateService = radioStateService;
            _logger = logger;
            _settingsService = settingsService;
            _logger.LogInformation("WsjtxUdpService constructor called. Service is being constructed.");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogWarning("WsjtxUdpService ExecuteAsync started - listening for WSJT-X UDP packets");
            UdpClient? udpClient = null;
            try
            {
                _logger.LogWarning("Loading WSJT-X UDP settings...");
                var settings = await _settingsService.GetSettingsAsync();
                _logger.LogWarning("WSJT-X UDP Settings: Address={Address}, Port={Port}", settings.WsjtxUdpAddress, settings.WsjtxUdpPort);
                udpClient = CreateUdpListener(settings.WsjtxUdpAddress, settings.WsjtxUdpPort);
                lock (_lock) _udpClient = udpClient;
                _logger.LogWarning("WSJT-X UDP listener ACTIVE on port {Port} (address filter: {Address})", settings.WsjtxUdpPort, settings.WsjtxUdpAddress);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udpClient.ReceiveAsync(stoppingToken);
                        _logger.LogWarning("[WSJT-X UDP] Packet received: {Length} bytes from {RemoteEndPoint}", result.Buffer.Length, result.RemoteEndPoint);
                        await ProcessMessageAsync(result.Buffer, result.RemoteEndPoint, stoppingToken);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error processing WSJT-X UDP packet");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WSJT-X UDP service failed to start or crashed");
            }
            finally
            {
                lock (_lock) _udpClient = null;
                udpClient?.Close();
                _logger.LogInformation("WSJT-X UDP listener stopped");
                _logger.LogWarning("WsjtxUdpService ExecuteAsync has exited. Service should be stopped.");
            }

        }

        // --- Message parsing (all synchronous — spans are safe here) ---

        private UdpClient CreateUdpListener(string udpAddress, int udpPort)
        {
            var udp = new UdpClient();
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));

            _logger.LogWarning("UDP socket bound to port {Port} on all interfaces", udpPort);

            // If multicast address, join group on default interface and also on loopback.
            // WSJT-X may be configured with "Outgoing Interfaces: loopback_0", in which case
            // packets only arrive on the loopback adapter and won't be seen if we only join
            // on the default (LAN) interface.
            if (IPAddress.TryParse(udpAddress, out var ip) &&
                ip.AddressFamily == AddressFamily.InterNetwork &&
                ip.IsMulticast())
            {
                try
                {
                    udp.JoinMulticastGroup(ip);
                    _logger.LogWarning("✓ Joined WSJT-X multicast group {Address}:{Port} on default interface", udpAddress, udpPort);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "✗ FAILED to join multicast group {Address} on default interface - check firewall settings", udpAddress);
                }

                try
                {
                    udp.JoinMulticastGroup(ip, IPAddress.Loopback);
                    _logger.LogWarning("✓ Joined WSJT-X multicast group {Address}:{Port} on loopback interface", udpAddress, udpPort);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not join multicast {Address} on loopback (non-fatal): {Message}", udpAddress, ex.Message);
                }
            }
            else
            {
                _logger.LogWarning("Listening for unicast UDP on port {Port} (address {Address} is not multicast)", udpPort, udpAddress);
            }
            return udp;
        }

        private async Task ProcessMessageAsync(byte[] data, IPEndPoint sender, CancellationToken ct)
        {
            // Parse synchronously (no spans across await boundaries)

            var msg = TryParseMessage(data);
            if (msg == null)
            {
                // Log first 16 bytes of the packet for debugging
                var hex = BitConverter.ToString(data.Take(16).ToArray());
                _logger.LogWarning("[WSJT-X UDP] Failed to parse message. First 16 bytes: {Hex}", hex);
                return;
            }

            // Our own requests (Reply 4, HaltTx 8, Configure 15) come back to us when WSJT-X uses multicast.
            if (msg.Type is 4 or 8 or 15) return;

            lock (_lock)
            {
                _lastSeen = DateTime.UtcNow;
                _wsjtxId = msg.Id;
                _wsjtxEndPoint = sender;
            }

            switch (msg.Type)
            {
                case MessageTypeHeartbeat:
                    _logger.LogDebug("WSJT-X Heartbeat from '{Id}'", msg.Id);
                    break;

                case MessageTypeDecode:
                    var decode = WsjtxProtocol.TryParseDecode(data);
                    if (decode is not null)
                        HeardList.Add(decode, DialFrequency, WsjtxMode, DeCall, DateTime.UtcNow);
                    break;

                case MessageTypeStatus:
                    lock (_lock)
                    {
                        if (msg.DialFrequency > 0) _dialFrequency = msg.DialFrequency;
                        _isTransmitting = msg.Transmitting;
                        _wsjtxMode = msg.Mode;
                        // A short Status (older versions) has no DE fields: keep what we had.
                        if (msg.DeCall.Length > 0) _deCall = msg.DeCall;
                        if (msg.DeGrid.Length > 0) _deGrid = msg.DeGrid;
                    }

                    // Log every status message with frequency info for debugging
                    _logger.LogDebug("WSJT-X Status: Id={Id}, DialFreq={Freq}, Mode={Mode}, TX={TX}",
                        msg.Id, msg.DialFrequency, msg.Mode, msg.Transmitting);

                    // Sync frequency to radio if meaningfully different (>100 Hz).
                    // WSJT-X sends dial frequency changes when:
                    // - Band is changed
                    // - User clicks outside current passband on wide graph
                    // - Split mode frequency changes
                    if (msg.DialFrequency > 0)
                    {
                        var currentFreq = _radioStateService.FrequencyA;
                        var diff = Math.Abs(msg.DialFrequency - currentFreq);

                        if (diff > 100)
                        {
                            _logger.LogInformation("[WSJT-X UDP] Frequency change detected: {OldFreq} Hz → {NewFreq} Hz (diff={Diff} Hz)",
                                currentFreq, msg.DialFrequency, diff);

                            // Send to radio via the IRadioController seam (CI-V 05).
                            // Guard on IsConnected so a WSJT-X packet arriving while the
                            // radio is absent/soft-off is a no-op rather than a throw.
                            if (_radio.IsConnected)
                            {
                                await _radio.SetFrequencyHzAsync(RadioVfo.A, msg.DialFrequency, ct);

                                // Update RadioStateService immediately so the UI updates via SignalR
                                _radioStateService.FrequencyA = msg.DialFrequency;
                            }
                        }
                    }
                    break;

                case MessageTypeClose:
                    _logger.LogInformation("WSJT-X '{Id}' closed", msg.Id);
                    lock (_lock)
                    {
                        _isTransmitting = false;
                        _lastSeen = DateTime.MinValue;
                    }
                    break;
            }
        }

        // --- Message parsing (all synchronous — spans are safe here) ---

        internal record ParsedMessage(uint Type, string Id, long DialFrequency, bool Transmitting, string Mode,
            string DeCall = "", string DeGrid = "");

        internal static ParsedMessage? TryParseMessage(byte[] data)
        {
            if (data.Length < 8) return null;
            try
            {
                var span = data.AsSpan();
                int offset = 0;

                uint magic = ReadUInt32(span, ref offset);
                if (magic != MagicNumber) return null;

                ReadUInt32(span, ref offset); // schema version
                uint type = ReadUInt32(span, ref offset);
                string id = ReadQString(span, ref offset) ?? "";

                long dialFrequency = 0;
                bool transmitting = false;
                string mode = "";
                string deCall = "";
                string deGrid = "";

                if (type == MessageTypeStatus)
                {
                    dialFrequency = (long)ReadUInt64(span, ref offset);
                    mode = ReadQString(span, ref offset) ?? "";
                    ReadQString(span, ref offset); // DX Call
                    ReadQString(span, ref offset); // Report
                    ReadQString(span, ref offset); // TX Mode
                    ReadBool(span, ref offset);    // TX Enabled
                    transmitting = ReadBool(span, ref offset);

                    // The rest is optional: a short Status still gives the fields above.
                    try
                    {
                        ReadBool(span, ref offset);    // Decoding
                        ReadUInt32(span, ref offset);  // Rx DF
                        ReadUInt32(span, ref offset);  // Tx DF
                        deCall = (ReadQString(span, ref offset) ?? "").Trim();   // DE call: the operator's own (Spot Map)
                        deGrid = (ReadQString(span, ref offset) ?? "").Trim();   // DE grid: the operator's own (Spot Map)
                    }
                    catch (ArgumentOutOfRangeException) { }
                    catch (IndexOutOfRangeException) { }
                }

                return new ParsedMessage(type, id, dialFrequency, transmitting, mode, deCall, deGrid);
            }
            catch
            {
                return null;
            }
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int offset)
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            offset += 4;
            return value;
        }

        private static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int offset)
        {
            var value = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, 8));
            offset += 8;
            return value;
        }

        private static bool ReadBool(ReadOnlySpan<byte> data, ref int offset)
        {
            return data[offset++] != 0;
        }

        private static string? ReadQString(ReadOnlySpan<byte> data, ref int offset)
        {
            if (offset + 4 > data.Length) return null;
            uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            offset += 4;
            int length = (int)rawLength;
            if (length == -1) return null;       // null Qt string
            if (length == 0) return string.Empty;
            if (offset + length > data.Length) return null;
            var str = Encoding.UTF8.GetString(data.Slice(offset, length));
            offset += length;
            return str;
        }
    }
    // Extension for multicast detection
    public static class IPAddressExtensions
    {
        public static bool IsMulticast(this IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] >= 224 && bytes[0] <= 239;
        }
    }
}
