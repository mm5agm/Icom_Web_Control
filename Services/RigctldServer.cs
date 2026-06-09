using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Implements a rigctld-compatible TCP server for WSJT-X and other Hamlib clients
    /// </summary>
    public class RigctldServer : BackgroundService
    {
        private readonly CatMultiplexerService _multiplexer;
        private readonly RadioStateService _radioStateService;
        private readonly ILogger<RigctldServer> _logger;
        private TcpListener? _listener;
        private readonly List<TcpClient> _clients = new();
        private readonly object _clientsLock = new();
        private const int RigctldPort = 4532;

        // State for advanced commands
        private bool _splitEnabled = false;
        private long _splitFrequency = 0;
        private int _ritOffset = 0;
        private int _xitOffset = 0;

        private static readonly HashSet<string> SupportedModes = new()
        {
            "LSB", "USB", "CW", "CW-R", "RTTY", "RTTY-R", "AM", "FM",
            // Hamlib "packet" mode names — WSJT-X uses these for digital modes
            // (PKTUSB = FT8/FT4/PSK on the USB-data side, = Yaesu DATA-U).
            // GetModeAsync already translates DATA-USB → PKTUSB outbound, so
            // accepting them on inbound completes the round-trip.
            "PKTUSB", "PKTLSB", "PKTFM"
        };

        // Hamlib → Yaesu mode-name mapping for the set-mode path.
        // Anything not in this map is sent verbatim to CatCommands.FormatMode.
        private static readonly Dictionary<string, string> HamlibToYaesuMode = new()
        {
            { "PKTUSB", "DATA-U"   },
            { "PKTLSB", "DATA-L"   },
            { "PKTFM",  "DATA-FM"  },
            { "CW",     "CW-U"     },
            { "CW-R",   "CW-L"     },
            { "RTTY",   "RTTY-L"   },
            { "RTTY-R", "RTTY-U"   },
        };

        private const long MinFrequency = 30000;
        private const long MaxFrequency = 75000000;

        // Band mapping for set_band support (using BSxx; command codes)
        private static readonly Dictionary<string, string> BandCodes = new()
        {
            { "160m", "00" },
            { "80m",  "01" },
            { "60m",  "02" },
            { "40m",  "03" },
            { "30m",  "04" },
            { "20m",  "05" },
            { "17m",  "06" },
            { "15m",  "07" },
            { "12m",  "08" },
            { "10m",  "09" },
            { "6m",   "10" },
            { "4m",   "11" } // Added 4m band
        };

        public RigctldServer(CatMultiplexerService multiplexer, RadioStateService radioStateService, ILogger<RigctldServer> logger)
        {
            _multiplexer = multiplexer;
            _radioStateService = radioStateService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, RigctldPort);
                _listener.Start();
                _logger.LogInformation("✓ rigctld server listening on port {Port}", RigctldPort);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                    var clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                    _logger.LogInformation("rigctld client connected: {Endpoint}", clientEndpoint);

                    lock (_clientsLock)
                    {
                        _clients.Add(client);
                    }

                    _ = Task.Run(() => HandleClientAsync(client, stoppingToken), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "rigctld server error");
            }
            finally
            {
                _listener?.Stop();
                _logger.LogInformation("rigctld server stopped");
                _logger.LogWarning("RigctldServer ExecuteAsync has exited. Service should be stopped.");
            }

        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var remoteEndPoint = client.Client.RemoteEndPoint;
            var clientEndpoint = remoteEndPoint != null ? remoteEndPoint.ToString() : "unknown";
            var clientId = remoteEndPoint != null
                ? $"rigctld-{remoteEndPoint}"
                : "rigctld-unknown";
            var stream = client.GetStream();
            var buffer = new byte[1024];

            try
            {
                while (!cancellationToken.IsCancellationRequested && client.Connected)
                {
                    var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                    if (bytesRead == 0) break;

                    // Split on newlines — Hamlib may pipeline multiple commands in one TCP write
                    var data = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                    var commands = data.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                    foreach (var rawCmd in commands)
                    {
                        var command = rawCmd.Trim();
                        if (string.IsNullOrEmpty(command)) continue;

                        _logger.LogDebug("[{ClientId}] Received: {Command}", clientId, command);

                        var response = await ProcessRigctldCommandAsync(command, clientId);

                        if (!string.IsNullOrEmpty(response))
                        {
                            var responseBytes = Encoding.ASCII.GetBytes(response + "\n");
                            await stream.WriteAsync(responseBytes, cancellationToken);
                            _logger.LogDebug("[{ClientId}] Sent: {Response}", clientId, response.TrimEnd());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ClientId}] Client handler error", clientId);
            }
            finally
            {
                lock (_clientsLock)
                {
                    _clients.Remove(client);
                }
                client.Close();
                _logger.LogInformation("[{ClientId}] Client disconnected", clientId);
            }
        }

        private async Task<string> ProcessRigctldCommandAsync(string command, string clientId)
        {
            // Strip leading backslash — Hamlib long-form commands are prefixed with \
            if (command.StartsWith("\\"))
                command = command[1..];

            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "RPRT 0";

            var cmd = parts[0];

            // Short-form commands (single character) are CASE-SENSITIVE:
            //   lowercase = get (f, m, t, …)   uppercase = set (F, M, T, …)
            // Long-form commands (get_freq, set_freq, …) are case-insensitive.
            if (cmd.Length == 1)
            {
                return cmd switch
                {
                    "f" => await GetFrequencyAsync(clientId),
                    "F" => parts.Length > 1 ? await SetFrequencyAsync(parts[1], clientId) : "RPRT -1",
                    "m" => await GetModeAsync(clientId),
                    "M" => parts.Length > 1 ? await SetModeAsync(parts[1], parts.Length > 2 ? parts[2] : null, clientId) : "RPRT -1",
                    "v" => "VFOA",
                    "V" => parts.Length > 1 ? SetVfo(parts[1]) : "RPRT -1",
                    "t" => await GetPttAsync(clientId),
                    "T" => parts.Length > 1 ? await SetPttAsync(parts[1], clientId) : "RPRT -1",
                    "l" => parts.Length > 1 ? await GetLevelAsync(parts[1], clientId) : "RPRT -1",
                    "L" => parts.Length > 2 ? SetLevel(parts[1], parts[2]) : "RPRT -1",
                    "x" => GetSplit(),
                    "X" => parts.Length > 1 ? SetSplit(parts[1]) : "RPRT -1",
                    "z" => GetSplitFrequency(),
                    "Z" => parts.Length > 1 ? SetSplitFrequency(parts[1]) : "RPRT -1",
                    "r" => GetRit(),
                    "R" => parts.Length > 1 ? SetRit(parts[1]) : "RPRT -1",
                    "c" => GetXit(),
                    "C" => parts.Length > 1 ? SetXit(parts[1]) : "RPRT -1",
                    "q" => "RPRT 0",
                    _ => "RPRT -1"
                };
            }

            // Long-form commands — case-insensitive
            return cmd.ToLowerInvariant() switch
            {
                "get_freq"      => await GetFrequencyAsync(clientId),
                "set_freq"      => parts.Length > 1 ? await SetFrequencyAsync(parts[1], clientId) : "RPRT -1",
                "get_mode"      => await GetModeAsync(clientId),
                "set_mode"      => parts.Length > 1 ? await SetModeAsync(parts[1], parts.Length > 2 ? parts[2] : null, clientId) : "RPRT -1",
                "get_vfo"       => "VFOA",
                "set_vfo"       => parts.Length > 1 ? SetVfo(parts[1]) : "RPRT -1",
                "get_ptt"       => await GetPttAsync(clientId),
                "set_ptt"       => parts.Length > 1 ? await SetPttAsync(parts[1], clientId) : "RPRT -1",
                "get_level"     => parts.Length > 1 ? await GetLevelAsync(parts[1], clientId) : "RPRT -1",
                "set_level"     => parts.Length > 2 ? SetLevel(parts[1], parts[2]) : "RPRT -1",
                "get_split_vfo" => GetSplit(),
                "set_split_vfo" => parts.Length > 1 ? SetSplit(parts[1]) : "RPRT -1",
                "get_split_freq"=> GetSplitFrequency(),
                "set_split_freq"=> parts.Length > 1 ? SetSplitFrequency(parts[1]) : "RPRT -1",
                "get_rit"       => GetRit(),
                "set_rit"       => parts.Length > 1 ? SetRit(parts[1]) : "RPRT -1",
                "get_xit"       => GetXit(),
                "set_xit"       => parts.Length > 1 ? SetXit(parts[1]) : "RPRT -1",
                "get_info"      => GetInfo(),
                "set_band"      => parts.Length > 1 ? await SetBandAsync(parts[1], clientId) : "RPRT -1",
                "get_powerstat" => "1",
                "chk_vfo"       => "CHKVFO 0",
                "dump_state"    => GetDumpState(),
                "get_mem"       => "RPRT -1",
                "set_mem"       => "RPRT -1",
                "get_band"      => "RPRT -1",
                "get_filter"    => "RPRT -1",
                "set_filter"    => "RPRT -1",
                "quit"          => "RPRT 0",
                _               => "RPRT -1"
            };
        }

        // --- Command Implementations and Stubs ---

        private async Task<string> GetFrequencyAsync(string clientId)
        {
            var response = await _multiplexer.SendCommandAsync("FA", clientId);
            var freq = CatCommands.ParseFrequency(response ?? string.Empty);
            if (freq > 0)
                return freq.ToString();
            // Fallback: use RadioStateService if multiplexer/CAT fails
            if (_radioStateService.FrequencyA > 0)
                return _radioStateService.FrequencyA.ToString();
            return "RPRT -1";
        }

        private async Task<string> SetFrequencyAsync(string freqStr, string clientId)
        {
            // Hamlib sends frequencies as decimals: e.g. "10136055.000000"
            if (!double.TryParse(freqStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var freqDouble))
                return "RPRT -1";

            var freq = (long)Math.Round(freqDouble);
            if (freq < MinFrequency || freq > MaxFrequency)
                return "RPRT -1";

            var command = CatCommands.FormatFrequencyA(freq);
            await _multiplexer.SendCommandAsync(command, clientId);

            // Update RadioStateService immediately so the UI updates via SignalR
            _radioStateService.FrequencyA = freq;
            _logger.LogInformation("Rigctld set_freq: {Freq} Hz (client: {ClientId})", freq, clientId);

            return "RPRT 0";
        }

        private async Task<string> GetModeAsync(string clientId)
        {
            var response = await _multiplexer.SendCommandAsync("MD0", clientId);
            var mode = CatCommands.ParseMode(response ?? string.Empty);
            var bandwidth = 0; // FT-dx101 manages bandwidth automatically

            // Convert to Hamlib mode names
            var hamlibMode = mode switch
            {
                "USB" => "USB",
                "LSB" => "LSB",
                "CW" => "CW",
                "CW-R" => "CW-R",
                "FM" => "FM",
                "AM" => "AM",
                "RTTY" => "RTTY",
                "RTTY-R" => "RTTY-R",
                "DATA-USB" or "DATA-LSB" => "PKTUSB",
                _ => "USB"
            };

            return $"{hamlibMode}\n{bandwidth}";
        }

        private async Task<string> SetModeAsync(string mode, string? passband, string clientId)
        {
            var hamlibMode = mode.ToUpper();
            if (!SupportedModes.Contains(hamlibMode))
                return "RPRT -1 // E_MODE: Unsupported mode for this rig.";

            // Translate Hamlib mode names to Yaesu names where they differ
            // (PKTUSB → DATA-U, CW → CW-U, etc.). Sending raw Hamlib names to
            // CatCommands.FormatMode would set an unintended mode on the radio.
            var yaesuMode = HamlibToYaesuMode.TryGetValue(hamlibMode, out var mapped)
                ? mapped
                : hamlibMode;
            var command = CatCommands.FormatMode(yaesuMode, false);
            await _multiplexer.SendCommandAsync(command, clientId);
            return "RPRT 0";
        }

        private async Task<string> GetPttAsync(string clientId)
        {
            var response = await _multiplexer.SendCommandAsync("TX", clientId);
            return response?.Contains("TX1") == true ? "1" : "0";
        }

        private async Task<string> SetPttAsync(string ptt, string clientId)
        {
            var command = ptt == "1" ? "TX1;" : "TX0;";
            await _multiplexer.SendCommandAsync(command, clientId);
            return "RPRT 0";
        }

        private string SetVfo(string vfo)
        {
            // Always ignore VFO selection and force VFOA
            _logger.LogInformation("WSJT-X requested VFO change to {Vfo}, but only VFOA is supported. Ignoring.", vfo);
            return "RPRT 0";
        }

        private string GetVfo()
        {
            return "VFOA";
        }

        private string GetSplit() => _splitEnabled ? "1" : "0";
        private string SetSplit(string enabled)
        {
            _splitEnabled = enabled == "1";
            return "RPRT 0";
        }

        private string GetSplitFrequency() => _splitFrequency.ToString();
        private string SetSplitFrequency(string freqStr)
        {
            if (long.TryParse(freqStr, out var freq) && freq >= MinFrequency && freq <= MaxFrequency)
            {
                _splitFrequency = freq;
                return "RPRT 0";
            }
            return "RPRT -1";
        }

        private string GetRit() => _ritOffset.ToString();
        private string SetRit(string offsetStr)
        {
            if (int.TryParse(offsetStr, out var offset) && offset >= -9990 && offset <= 9990)
            {
                _ritOffset = offset;
                return "RPRT 0";
            }
            return "RPRT -1";
        }

        private string GetXit() => _xitOffset.ToString();
        private string SetXit(string offsetStr)
        {
            if (int.TryParse(offsetStr, out var offset) && offset >= -9990 && offset <= 9990)
            {
                _xitOffset = offset;
                return "RPRT 0";
            }
            return "RPRT -1";
        }

        private async Task<string> GetLevelAsync(string level, string clientId)
        {
            if (level.ToUpper() == "STRENGTH")
                return await GetSignalStrengthAsync(clientId);
            // Add more levels as needed
            return "0";
        }

        private string SetLevel(string level, string value)
        {
            // Stub: accept but do nothing
            return "RPRT 0";
        }

        private async Task<string> GetSignalStrengthAsync(string clientId)
        {
            var response = await _multiplexer.SendCommandAsync("SM0", clientId);
            var sMeter = CatCommands.ParseSMeter(response ?? string.Empty);

            var dbm = (sMeter / 255.0) * 60 - 60;
            return $"{dbm:F0}";
        }

        private string GetInfo()
        {
            // Example info string for FTdx101MP (Hamlib expects: "mfg;model;version;serial;id")
            return "Yaesu;FTDX101MP;1.0.0;000000;3115";
        }

        private static string GetDumpState()
        {
            // Minimal Hamlib dump_state response. Same as v2.1.0 shipped —
            // user reports Log4OM frequency display *was* working in a
            // previous version with this response, so reverting any
            // attempted "fixes" until we figure out what actually regressed.
            return string.Join("\n",
                "0",            // protocol version
                "2",            // rig model (2 = NET rigctl)
                "1800000.000000 30000000.000000 0x1ff -1 -1 0x10000003 0x3",  // HF RX range
                "50000000.000000 54000000.000000 0x1ff -1 -1 0x10000003 0x3", // 6m RX range
                "0 0 0 0 0 0 0",  // end of RX ranges
                "1800000.000000 30000000.000000 0x1ff 5 200 0x10000003 0x3",  // HF TX range
                "50000000.000000 54000000.000000 0x1ff 5 100 0x10000003 0x3", // 6m TX range
                "0 0 0 0 0 0 0",  // end of TX ranges
                "0 0",          // end of tuning steps
                "0 0",          // end of filters
                "0",            // max RIT
                "0",            // max XIT
                "0",            // max IF-shift
                "0",            // announces
                "0",            // preamp list
                "0",            // attenuator list
                "0x00000003",   // has_get_func
                "0x00000003",   // has_set_func
                "0x000fffff",   // has_get_level
                "0x000fffff",   // has_set_level
                "0",            // has_get_parm
                "0"             // has_set_parm
            );
        }

        // --- Band change support using BSxx; command ---
        private async Task<string> SetBandAsync(string band, string clientId)
        {
            if (BandCodes.TryGetValue(band.ToLower(), out var code))
            {
                // Always send to VFO A
                await _multiplexer.SendCommandAsync($"BS{code};", clientId);
                return "RPRT 0";
            }
            // Try parsing as frequency in Hz
            if (long.TryParse(band, out var freqHz) && freqHz >= MinFrequency && freqHz <= MaxFrequency)
            {
                var command = CatCommands.FormatFrequencyA(freqHz);
                await _multiplexer.SendCommandAsync(command, clientId);
                return "RPRT 0";
            }
            return "RPRT -1";
        }
    }
}