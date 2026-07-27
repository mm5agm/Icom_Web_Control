using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Icom_Web_Control.Services
{
    /// <summary>
    /// A rigctld-compatible TCP server for WSJT-X and other Hamlib clients.
    ///
    /// Phase 4 carve: this used to speak Yaesu ASCII CAT through
    /// <c>CatMultiplexerService</c>. It now speaks radio concepts through the
    /// <see cref="IRadioController"/> seam — exactly like CatController and the
    /// voice dispatcher — so WSJT-X drives the IC-7300 over CI-V and coexists
    /// with IWC on the one COM port (the seam's CI-V bus serialises every
    /// transaction, so rigctld GET/SET and the poll loop never collide).
    ///
    /// GETs are answered from the cached <see cref="RadioStateService"/> where
    /// possible (the poll loop keeps it fresh within ~100 ms) so WSJT-X's
    /// get-immediately-after-set never races the radio's apply delay or the bus.
    /// SETs go through the seam.
    /// </summary>
    public class RigctldServer : BackgroundService
    {
        private readonly IRadioController _radio;
        private readonly RadioStateService _radioStateService;
        private readonly ILogger<RigctldServer> _logger;
        private TcpListener? _listener;
        private readonly List<TcpClient> _clients = new();
        private readonly object _clientsLock = new();
        private const int RigctldPort = 4532;

        // State for split commands. The IC-7300's split always transmits on the
        // other VFO (B), so the TX-VFO argument from Hamlib is accepted but the
        // radio side is fixed: RX on A, TX on B.
        private bool _splitEnabled = false;
        private long _splitFrequency = 0;
        private int _ritOffset = 0;
        private int _xitOffset = 0;

        // TX safety watchdog. A rigctld client can key the radio and then fail to
        // send the matching release — WSJT-X's Tune-stop not sending PTT-off, a
        // client crash, or a dropped connection all leave the radio keyed forever.
        // We track PTT keyed via rigctld and force RX; if it is held past
        // TxSafetyTimeoutSeconds, or if the client that keyed it disconnects.
        private const int TxSafetyTimeoutSeconds = 180;
        private readonly object _pttLock = new();
        private bool _rigctldPttActive = false;
        private string? _pttClientId = null;
        private CancellationTokenSource? _pttWatchdogCts;

        // Hamlib mode name → IWC display string (the seam's vocabulary, matching
        // CivRadioController.ModeNameToIcom). WSJT-X uses the PKT* names for
        // digital modes: PKTUSB = FT8/FT4 on the USB-data side = IC-7300 USB-D =
        // "DATA-U". CW/RTTY normal-vs-reverse map onto the app's -U/-L sideband
        // strings.
        private static readonly Dictionary<string, string> HamlibToDisplayMode =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "USB",    "USB"     },
                { "LSB",    "LSB"     },
                { "AM",     "AM"      },
                { "FM",     "FM"      },
                { "CW",     "CW-U"    },
                { "CW-R",   "CW-L"    },
                { "RTTY",   "RTTY-L"  },
                { "RTTY-R", "RTTY-U"  },
                { "PKTUSB", "DATA-U"  },
                { "PKTLSB", "DATA-L"  },
                { "PKTFM",  "DATA-FM" },
            };

        // IWC display string → Hamlib mode name (the get_mode direction).
        private static readonly Dictionary<string, string> DisplayToHamlibMode =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "USB",     "USB"    },
                { "LSB",     "LSB"    },
                { "AM",      "AM"     },
                { "FM",      "FM"     },
                { "CW-U",    "CW"     },
                { "CW-L",    "CW-R"   },
                { "RTTY-L",  "RTTY"   },
                { "RTTY-U",  "RTTY-R" },
                { "DATA-U",  "PKTUSB" },
                { "DATA-L",  "PKTLSB" },
                { "DATA-FM", "PKTFM"  },
            };

        // Nominal RX passband (Hz) reported by get_mode. WSJT-X only displays
        // this; the real DSP filter is set on the radio, not here.
        private static int NominalPassband(string hamlibMode) => hamlibMode switch
        {
            "CW" or "CW-R"     => 500,
            "RTTY" or "RTTY-R" => 500,
            "AM"               => 6000,
            "FM" or "PKTFM"    => 12000,
            _                  => 3000, // USB/LSB/PKTUSB/PKTLSB
        };

        private const long MinFrequency = 30000;
        private const long MaxFrequency = 75000000;

        public RigctldServer(IRadioController radio, RadioStateService radioStateService, ILogger<RigctldServer> logger)
        {
            _radio = radio;
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
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var remoteEndPoint = client.Client.RemoteEndPoint;
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

                // Safety net: never leave the radio keyed because a client that
                // held PTT went away without releasing it.
                await ReleasePttIfClientHeldAsync(clientId);

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
                    "m" => await GetModeAsync(),
                    "M" => parts.Length > 1 ? await SetModeAsync(parts[1], clientId) : "RPRT -1",
                    "v" => "VFOA",
                    "V" => parts.Length > 1 ? SetVfo(parts[1]) : "RPRT -1",
                    "t" => await GetPttAsync(),
                    "T" => parts.Length > 1 ? await SetPttAsync(parts[1], clientId) : "RPRT -1",
                    "l" => parts.Length > 1 ? await GetLevelAsync(parts[1]) : "RPRT -1",
                    "L" => parts.Length > 2 ? await SetLevelAsync(parts[1], parts[2], clientId) : "RPRT -1",
                    "x" => GetSplit(),
                    "X" => parts.Length > 1 ? await SetSplitAsync(parts[1], clientId) : "RPRT -1",
                    "z" => GetSplitFrequency(),
                    "Z" => parts.Length > 1 ? await SetSplitFrequencyAsync(parts[1], clientId) : "RPRT -1",
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
                "get_mode"      => await GetModeAsync(),
                "set_mode"      => parts.Length > 1 ? await SetModeAsync(parts[1], clientId) : "RPRT -1",
                "get_vfo"       => "VFOA",
                "set_vfo"       => parts.Length > 1 ? SetVfo(parts[1]) : "RPRT -1",
                "get_ptt"       => await GetPttAsync(),
                "set_ptt"       => parts.Length > 1 ? await SetPttAsync(parts[1], clientId) : "RPRT -1",
                "get_level"     => parts.Length > 1 ? await GetLevelAsync(parts[1]) : "RPRT -1",
                "set_level"     => parts.Length > 2 ? await SetLevelAsync(parts[1], parts[2], clientId) : "RPRT -1",
                "get_split_vfo" => GetSplit(),
                "set_split_vfo" => parts.Length > 1 ? await SetSplitAsync(parts[1], clientId) : "RPRT -1",
                "get_split_freq"=> GetSplitFrequency(),
                "set_split_freq"=> parts.Length > 1 ? await SetSplitFrequencyAsync(parts[1], clientId) : "RPRT -1",
                "get_rit"       => GetRit(),
                "set_rit"       => parts.Length > 1 ? SetRit(parts[1]) : "RPRT -1",
                "get_xit"       => GetXit(),
                "set_xit"       => parts.Length > 1 ? SetXit(parts[1]) : "RPRT -1",
                "get_info"      => GetInfo(),
                "set_band"      => parts.Length > 1 ? await SetBandAsync(parts[1], clientId) : "RPRT -1",
                "get_powerstat" => _radioStateService.RadioPowerOn ? "1" : "0",
                "chk_vfo"       => "CHKVFO 0",
                "dump_state"    => GetDumpState(),
                "get_func"      => "RPRT -1", // ATU (CI-V 1C 01) not yet on the seam
                "set_func"      => "RPRT -1",
                "get_mem"       => "RPRT -1",
                "set_mem"       => "RPRT -1",
                "get_band"      => "RPRT -1",
                "get_filter"    => "RPRT -1",
                "set_filter"    => "RPRT -1",
                "quit"          => "RPRT 0",
                _               => "RPRT -1"
            };
        }

        // --- Command Implementations ---

        private Task<string> GetFrequencyAsync(string clientId)
        {
            // Answer from cache. WSJT-X polls get_freq immediately after every
            // set_freq to confirm the change; the cache is updated the instant
            // SetFrequencyAsync sends the CI-V set (below), and manual knob-turns
            // reach the cache via the poll loop within ~100 ms — faster than
            // WSJT-X's own poll — so the "knob → WSJT-X follows" path stays live.
            // Reading cache also keeps get_freq off the CI-V bus entirely.
            if (_radioStateService.FrequencyA > 0)
                return Task.FromResult(_radioStateService.FrequencyA.ToString());
            return GetFrequencyFromRadioAsync();
        }

        private async Task<string> GetFrequencyFromRadioAsync()
        {
            // Bootstrap only: cache not yet populated at startup.
            var freq = await _radio.GetFrequencyHzAsync(RadioVfo.A);
            return freq > 0 ? freq.ToString() : "RPRT -1";
        }

        private async Task<string> SetFrequencyAsync(string freqStr, string clientId)
        {
            // Hamlib sends frequencies as decimals: e.g. "10136055.000000"
            if (!double.TryParse(freqStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var freqDouble))
                return "RPRT -1";

            var freq = (long)Math.Round(freqDouble);
            if (freq < MinFrequency || freq > MaxFrequency)
                return "RPRT -1";

            await _radio.SetFrequencyHzAsync(RadioVfo.A, freq, CancellationToken.None);

            // Update the cache immediately so the very next get_freq WSJT-X sends
            // returns what we just set rather than racing the radio's apply delay.
            _radioStateService.FrequencyA = freq;
            _logger.LogInformation("Rigctld set_freq: {Freq} Hz (client: {ClientId})", freq, clientId);

            return "RPRT 0";
        }

        private Task<string> GetModeAsync()
        {
            // Answer from cache (kept fresh by the poll loop's command-04 read).
            var display = _radioStateService.ModeA;
            var hamlibMode = !string.IsNullOrEmpty(display)
                && DisplayToHamlibMode.TryGetValue(display, out var mapped)
                    ? mapped
                    : "USB";
            return Task.FromResult($"{hamlibMode}\n{NominalPassband(hamlibMode)}");
        }

        private async Task<string> SetModeAsync(string mode, string clientId)
        {
            if (!HamlibToDisplayMode.TryGetValue(mode, out var displayMode))
                return "RPRT -1"; // E_MODE: mode this rig does not have

            await _radio.SetModeAsync(RadioVfo.A, displayMode, CancellationToken.None);
            _logger.LogInformation("Rigctld set_mode: {Hamlib} → {Display} (client: {ClientId})", mode, displayMode, clientId);
            return "RPRT 0";
        }

        private async Task<string> GetPttAsync()
        {
            // Cache reflects the poll loop's 1C 00 read; good enough for WSJT-X's
            // PTT confirmation and keeps get_ptt off the bus.
            var tx = _radioStateService.IsTransmitting;
            await Task.CompletedTask;
            return tx ? "1" : "0";
        }

        private async Task<string> SetPttAsync(string ptt, string clientId)
        {
            // Hamlib set_ptt values: 0=off, 1=on, 2=on (mic), 3=on (data) — WSJT-X
            // in Data/Pkt PTT mode sends 3, not 1, so any nonzero value keys up.
            var keyed = ptt != "0";
            _logger.LogInformation("Rigctld set_ptt: {Ptt} (client: {ClientId})", ptt, clientId);
            await _radio.SetTransmitAsync(keyed, CancellationToken.None);

            if (keyed)
                ArmTxWatchdog(clientId);
            else
                DisarmTxWatchdog();

            return "RPRT 0";
        }

        // Start (or restart) the TX safety timeout for a rigctld-initiated key-up.
        private void ArmTxWatchdog(string clientId)
        {
            CancellationToken token;
            lock (_pttLock)
            {
                _pttWatchdogCts?.Cancel();
                _pttWatchdogCts?.Dispose();
                _pttWatchdogCts = new CancellationTokenSource();
                _rigctldPttActive = true;
                _pttClientId = clientId;
                token = _pttWatchdogCts.Token;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(TxSafetyTimeoutSeconds), token);
                }
                catch (OperationCanceledException)
                {
                    return; // Released normally, or re-armed by a newer key-up.
                }

                bool stillActive;
                lock (_pttLock)
                {
                    stillActive = _rigctldPttActive;
                    _rigctldPttActive = false;
                    _pttClientId = null;
                }

                if (stillActive)
                {
                    _logger.LogWarning(
                        "TX safety watchdog: PTT held via rigctld for over {Seconds}s with no release (client: {ClientId}). Forcing RX.",
                        TxSafetyTimeoutSeconds, clientId);
                    try { await _radio.SetTransmitAsync(false, CancellationToken.None); }
                    catch (Exception ex) { _logger.LogError(ex, "TX safety watchdog: failed to force RX."); }
                }
            });
        }

        // Cancel the TX safety timeout after a normal release.
        private void DisarmTxWatchdog()
        {
            lock (_pttLock)
            {
                _pttWatchdogCts?.Cancel();
                _pttWatchdogCts?.Dispose();
                _pttWatchdogCts = null;
                _rigctldPttActive = false;
                _pttClientId = null;
            }
        }

        // On client disconnect: if that client keyed the radio and never released
        // it, force RX so a dropped connection can't leave the radio keyed.
        private async Task ReleasePttIfClientHeldAsync(string clientId)
        {
            bool shouldRelease = false;
            lock (_pttLock)
            {
                if (_rigctldPttActive && _pttClientId == clientId)
                {
                    shouldRelease = true;
                    _pttWatchdogCts?.Cancel();
                    _pttWatchdogCts?.Dispose();
                    _pttWatchdogCts = null;
                    _rigctldPttActive = false;
                    _pttClientId = null;
                }
            }

            if (shouldRelease)
            {
                _logger.LogWarning(
                    "rigctld client {ClientId} disconnected while holding PTT. Forcing RX for safety.", clientId);
                try { await _radio.SetTransmitAsync(false, CancellationToken.None); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to force RX after client disconnect."); }
            }
        }

        private string SetVfo(string vfo)
        {
            // Only VFOA is exposed to Hamlib (single-receiver rig model).
            _logger.LogInformation("WSJT-X requested VFO change to {Vfo}; only VFOA is exposed. Ignoring.", vfo);
            return "RPRT 0";
        }

        // get_split_vfo returns two lines: split state, then the TX VFO name.
        private string GetSplit() => $"{(_splitEnabled ? 1 : 0)}\nVFOB";

        private async Task<string> SetSplitAsync(string enabled, string clientId)
        {
            _splitEnabled = enabled == "1";
            await _radio.SetSplitAsync(_splitEnabled, CancellationToken.None);
            _radioStateService.SplitMode = _splitEnabled ? 1 : 0;
            _logger.LogInformation("Rigctld set_split_vfo: {On} (client: {ClientId})", _splitEnabled, clientId);
            return "RPRT 0";
        }

        private string GetSplitFrequency()
        {
            // The TX (split) frequency lives on VFO B.
            var b = _radioStateService.FrequencyB;
            return (b > 0 ? b : _splitFrequency).ToString();
        }

        private async Task<string> SetSplitFrequencyAsync(string freqStr, string clientId)
        {
            if (!double.TryParse(freqStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var freqDouble))
                return "RPRT -1";

            var freq = (long)Math.Round(freqDouble);
            if (freq < MinFrequency || freq > MaxFrequency)
                return "RPRT -1";

            // Split TX is VFO B on the IC-7300.
            await _radio.SetFrequencyHzAsync(RadioVfo.B, freq, CancellationToken.None);
            _radioStateService.FrequencyB = freq;
            _splitFrequency = freq;
            _logger.LogInformation("Rigctld set_split_freq: {Freq} Hz → VFO B (client: {ClientId})", freq, clientId);
            return "RPRT 0";
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

        private async Task<string> GetLevelAsync(string level)
        {
            switch (level.ToUpperInvariant())
            {
                case "STRENGTH":
                    return await GetSignalStrengthAsync();
                case "RFPOWER":
                    var pct = await _radio.GetRfPowerPercentAsync(CancellationToken.None);
                    if (pct < 0) pct = 0;
                    return (pct / 100.0).ToString("F6", CultureInfo.InvariantCulture);
                default:
                    return "0";
            }
        }

        private async Task<string> SetLevelAsync(string level, string value, string clientId)
        {
            if (level.ToUpperInvariant() == "RFPOWER")
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var frac))
                    return "RPRT -1";
                var percent = (int)Math.Round(Math.Clamp(frac, 0, 1) * 100);
                await _radio.SetRfPowerPercentAsync(percent, CancellationToken.None);
                _logger.LogInformation("Rigctld set_level RFPOWER: {Percent}% (client: {ClientId})", percent, clientId);
                return "RPRT 0";
            }
            // Accept-and-ignore other levels, matching Hamlib's lenient behaviour.
            return "RPRT 0";
        }

        private async Task<string> GetSignalStrengthAsync()
        {
            // Hamlib STRENGTH is dB relative to S9. The IC-7300 raw S-meter is
            // 0–255 with S9 ≈ 120 and ~0.5 dB per raw unit above it (S9+60 ≈ 241);
            // this linear approximation is plenty for display — WSJT-X never uses
            // it to decode.
            var raw = await _radio.ReadSMeterAsync(RadioVfo.A, CancellationToken.None);
            if (raw < 0) raw = _radioStateService.SMeterA ?? 0;
            var dbOverS9 = (int)Math.Round((raw - 120) * 0.5);
            return dbOverS9.ToString(CultureInfo.InvariantCulture);
        }

        private string GetInfo()
        {
            // Hamlib expects: "mfg;model;version;serial;id"
            return "Icom;IC-7300;1.0.0;000000;3073";
        }

        private static string GetDumpState()
        {
            // Minimal Hamlib dump_state response for a single-VFO IC-7300-class
            // rig. RX 30 kHz–74.8 MHz continuous; TX HF ham bands + 6 m at 2–100 W.
            // Func masks are 0 (ATU not yet exposed); level masks left broad.
            return string.Join("\n",
                "0",            // protocol version
                "2",            // rig model (2 = NET rigctl)
                "30000.000000 74800000.000000 0x1ff -1 -1 0x10000003 0x3",     // RX range (full coverage)
                "0 0 0 0 0 0 0",  // end of RX ranges
                "1800000.000000 29700000.000000 0x1ff 2 100 0x10000003 0x3",   // HF TX range
                "50000000.000000 54000000.000000 0x1ff 2 100 0x10000003 0x3",  // 6m TX range
                "0 0 0 0 0 0 0",  // end of TX ranges
                "0 0",          // end of tuning steps
                "0 0",          // end of filters
                "0",            // max RIT
                "0",            // max XIT
                "0",            // max IF-shift
                "0",            // announces
                "0",            // preamp list
                "0",            // attenuator list
                "0x00000000",   // has_get_func
                "0x00000000",   // has_set_func
                "0x000fffff",   // has_get_level
                "0x000fffff",   // has_set_level
                "0",            // has_get_parm
                "0"             // has_set_parm
            );
        }

        private async Task<string> SetBandAsync(string band, string clientId)
        {
            // WSJT-X does not use set_band; Log4OM may. We have no CI-V "band"
            // command, so accept only a numeric Hz target and QSY VFO A to it.
            if (long.TryParse(band, out var freqHz) && freqHz >= MinFrequency && freqHz <= MaxFrequency)
            {
                await _radio.SetFrequencyHzAsync(RadioVfo.A, freqHz, CancellationToken.None);
                _radioStateService.FrequencyA = freqHz;
                _logger.LogInformation("Rigctld set_band as freq: {Freq} Hz (client: {ClientId})", freqHz, clientId);
                return "RPRT 0";
            }
            return "RPRT -1";
        }
    }
}
