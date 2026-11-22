using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Icom_Web_Control.Services.Civ;

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
                { "WFM",    "WFM"     },
                { "D-STAR", "DV"      },
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
                { "WFM",     "WFM"    },
                { "DV",      "D-STAR" },
            };

        // Nominal RX passband (Hz) reported by get_mode. WSJT-X only displays
        // this; the real DSP filter is set on the radio, not here.
        private static int NominalPassband(string hamlibMode) => hamlibMode switch
        {
            "CW" or "CW-R"     => 500,
            "RTTY" or "RTTY-R" => 500,
            "AM"               => 6000,
            "FM" or "PKTFM"    => 12000,
            "WFM"              => 230000,
            "D-STAR"           => 6000,
            _                  => 3000, // USB/LSB/PKTUSB/PKTLSB
        };

        // Limits come from the radio that answered, not from Settings.
        private RadioProfile Profile => RadioProfile.OrDefault(_radio.ModelId);

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
            var pending = new StringBuilder();
            var civWarned = false;

            try
            {
                while (!cancellationToken.IsCancellationRequested && client.Connected)
                {
                    var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                    if (bytesRead == 0) break;

                    // A program set to the radio's own Hamlib model (IC-7300 and
                    // so on) with this port as its device sends binary CI-V, not
                    // rigctl text. Nothing here can answer it, so the program
                    // times out and hangs up after a few seconds. Say why.
                    if (!civWarned && LooksLikeCiv(buffer.AsSpan(0, bytesRead)))
                    {
                        civWarned = true;
                        _logger.LogWarning(
                            "[{ClientId}] This program is sending Icom CI-V, not rigctl commands. " +
                            "In its rig settings choose Hamlib NET rigctl (model 2), not the radio's own model, " +
                            "with localhost:{Port} as the device.", clientId, RigctldPort);
                    }

                    // Split on newlines. Hamlib may send several commands in one
                    // TCP write, or one command across two; keep any tail with no
                    // newline yet until the rest arrives.
                    var commands = TakeLines(pending, Encoding.ASCII.GetString(buffer, 0, bytesRead));

                    foreach (var rawCmd in commands)
                    {
                        var command = rawCmd.Trim();
                        if (string.IsNullOrEmpty(command)) continue;

                        _logger.LogDebug("[{ClientId}] Received: {Command}", clientId, command);

                        var response = await AnswerLineAsync(command,
                            (name, args, vfo) => ProcessRigctldCommandAsync(name, args, vfo, clientId));

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

        // Appends a chunk read from the socket and returns every complete
        // line in it; whatever follows the last newline stays in pending.
        internal static List<string> TakeLines(StringBuilder pending, string chunk)
        {
            pending.Append(chunk);
            var text = pending.ToString();
            var end = text.LastIndexOf('\n');
            if (end < 0)
            {
                // A client that never sends a newline mustn't grow this forever.
                if (pending.Length > 4096) pending.Clear();
                return new List<string>();
            }
            pending.Clear().Append(text, end + 1, text.Length - end - 1);
            return text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        // Icom CI-V frames start FE FE. rigctl text never contains 0xFE.
        internal static bool LooksLikeCiv(ReadOnlySpan<byte> data)
            => data.IndexOf(new byte[] { 0xFE, 0xFE }) >= 0;

        // Hamlib's extended response protocol (rigctld's --help: "+;|,"). A
        // command starting with one of these is answered as "name: args",
        // then one "Label: value" per value, then "RPRT n", joined by that
        // character ('+' means a newline). Log4OM uses it: ";V ?" once, then
        // "+\get_vfo_info VFOA" and "VFOB" every 0.6 s. Before #387 each got
        // RPRT -1, Log4OM stopped asking, and its frequency read OFFLINE.
        private const string ExtendedPrefixes = "+;|,";

        // Hamlib's one-letter commands (rigctl_parse.c), case-sensitive.
        // Before #387 x/z/r/c meant split / split freq / RIT / XIT here, but
        // in Hamlib they are split mode / XIT / repeater shift / CTCSS, and
        // the real s/S, i/I and j/J were missing, so netrigctl's split and
        // RIT commands (WSJT-X "Rig" split) got RPRT -1.
        private static readonly Dictionary<char, string> ShortNames = new()
        {
            ['f'] = "get_freq",       ['F'] = "set_freq",
            ['m'] = "get_mode",       ['M'] = "set_mode",
            ['v'] = "get_vfo",        ['V'] = "set_vfo",
            ['t'] = "get_ptt",        ['T'] = "set_ptt",
            ['s'] = "get_split_vfo",  ['S'] = "set_split_vfo",
            ['i'] = "get_split_freq", ['I'] = "set_split_freq",
            ['x'] = "get_split_mode", ['X'] = "set_split_mode",
            ['j'] = "get_rit",        ['J'] = "set_rit",
            ['z'] = "get_xit",        ['Z'] = "set_xit",
            ['l'] = "get_level",      ['L'] = "set_level",
            ['u'] = "get_func",       ['U'] = "set_func",
            ['_'] = "get_info",
            ['q'] = "quit",           ['Q'] = "quit",
        };

        // The labels Hamlib 4.6 puts on each value in an extended reply.
        // get_level and get_func values have none.
        private static readonly Dictionary<string, string[]> ReplyLabels = new()
        {
            ["get_freq"]       = new[] { "Frequency" },
            ["get_mode"]       = new[] { "Mode", "Passband" },
            ["get_vfo"]        = new[] { "VFO" },
            ["get_ptt"]        = new[] { "PTT" },
            ["get_split_vfo"]  = new[] { "Split", "TX VFO" },
            ["get_split_freq"] = new[] { "TX Frequency" },
            ["get_rit"]        = new[] { "RIT" },
            ["get_xit"]        = new[] { "XIT" },
            ["get_vfo_info"]   = new[] { "Freq", "Mode", "Width", "Split", "SatMode" },
            ["get_powerstat"]  = new[] { "Power Status" },
            ["get_info"]       = new[] { "Info" },
        };

        private static readonly HashSet<string> VfoNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "VFOA", "VFOB", "VFOC", "currVFO", "VFO", "MEM", "Main", "Sub",
            "MainA", "MainB", "SubA", "SubB", "TX", "RX",
        };

        internal static bool IsVfoB(string? vfo) => vfo is not null &&
            (vfo.Equals("VFOB", StringComparison.OrdinalIgnoreCase) ||
             vfo.StartsWith("Sub", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// One line from a client to its reply. <paramref name="plain"/>
        /// answers a long-form command (name, arguments, VFO or null) in
        /// Hamlib's plain format; this adds the extended format when asked.
        /// </summary>
        internal static async Task<string> AnswerLineAsync(
            string line, Func<string, string[], string?, Task<string>> plain)
        {
            char? sep = null;
            if (line.Length > 0 && ExtendedPrefixes.Contains(line[0]))
            {
                sep = line[0] == '+' ? '\n' : line[0];
                line = line[1..].TrimStart();
            }
            if (line.StartsWith('\\')) line = line[1..];

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "RPRT 0";

            var name = parts[0].Length == 1
                ? (ShortNames.TryGetValue(parts[0][0], out var shortName) ? shortName : null)
                : parts[0].ToLowerInvariant();
            if (name is null) return "RPRT -1";
            if (name == "quit") return "RPRT 0";

            // VFO mode (rigctld -o, Log4OM's "VFO MODE" box): every command
            // but set_vfo carries the VFO first, as in "F VFOA 14074000".
            // get_vfo_info's only argument is the VFO, so it keeps it.
            var args = parts[1..];
            string? vfo = null;
            if (name != "set_vfo" && name != "get_vfo_info" && args.Length > 0 && VfoNames.Contains(args[0]))
            {
                vfo = args[0];
                args = args[1..];
            }

            var reply = await plain(name, args, vfo);
            return sep is char c ? FormatExtended(name, parts[1..], reply, c) : reply;
        }

        internal static string FormatExtended(string name, string[] args, string reply, char sep)
        {
            var head = args.Length > 0 ? $"{name}: {string.Join(' ', args)}" : $"{name}:";
            var lines = reply.Split('\n').ToList();
            var status = "RPRT 0";
            if (lines[^1].StartsWith("RPRT"))
            {
                status = lines[^1];
                lines.RemoveAt(lines.Count - 1);
            }

            // Hamlib's own quirk, copied because clients are written against
            // it: the VFO list from "V ?" is followed by a newline, not sep.
            if (name == "set_vfo" && lines.Count > 0)
                return $"{head}{sep}{string.Join(sep, lines)}\n{status}";

            ReplyLabels.TryGetValue(name, out var labels);
            var fields = lines.Select((value, i) =>
                labels is not null && i < labels.Length ? $"{labels[i]}: {value}" : value);
            return string.Join(sep, new[] { head }.Concat(fields).Append(status));
        }

        private async Task<string> ProcessRigctldCommandAsync(string name, string[] args, string? vfo, string clientId)
        {
            string? Arg(int i) => args.Length > i ? args[i] : null;

            // Only VFO A can be set from here; a set aimed at VFO B is refused
            // rather than quietly applied to A.
            var onB = IsVfoB(vfo);

            return name switch
            {
                "get_freq"       => onB ? _radioStateService.FrequencyB.ToString() : await GetFrequencyAsync(clientId),
                "set_freq"       => !onB && Arg(0) is { } f ? await SetFrequencyAsync(f, clientId) : "RPRT -1",
                "get_mode"       => onB ? ModeReply(_radioStateService.ModeB) : await GetModeAsync(),
                "set_mode"       => !onB && Arg(0) is { } m ? await SetModeAsync(m, clientId) : "RPRT -1",
                "get_vfo"        => "VFOA",
                "set_vfo"        => Arg(0) == "?" ? "VFOA VFOB \nRPRT 0" : Arg(0) is { } v ? SetVfo(v) : "RPRT -1",
                "get_vfo_info"   => await GetVfoInfoAsync(Arg(0), clientId),
                "get_ptt"        => await GetPttAsync(),
                "set_ptt"        => Arg(0) is { } p ? await SetPttAsync(p, clientId) : "RPRT -1",
                "get_level"      => Arg(0) is { } l ? await GetLevelAsync(l) : "RPRT -1",
                "set_level"      => args.Length > 1 ? await SetLevelAsync(args[0], args[1], clientId) : "RPRT -1",
                "get_split_vfo"  => GetSplit(),
                "set_split_vfo"  => Arg(0) is { } s ? await SetSplitAsync(s, clientId) : "RPRT -1",
                "get_split_freq" => GetSplitFrequency(),
                "set_split_freq" => Arg(0) is { } sf ? await SetSplitFrequencyAsync(sf, clientId) : "RPRT -1",
                "get_rit"        => GetRit(),
                "set_rit"        => Arg(0) is { } r ? SetRit(r) : "RPRT -1",
                "get_xit"        => GetXit(),
                "set_xit"        => Arg(0) is { } x ? SetXit(x) : "RPRT -1",
                "get_func"       => Arg(0) is { } fn ? await GetFuncAsync(fn) : "RPRT -1",
                "set_func"       => args.Length > 1 ? await SetFuncAsync(args[0], args[1], clientId) : "RPRT -1",
                "get_info"       => GetInfo(),
                "set_band"       => Arg(0) is { } band ? await SetBandAsync(band, clientId) : "RPRT -1",
                "get_powerstat"  => _radioStateService.RadioPowerOn ? "1" : "0",
                "chk_vfo"        => "CHKVFO 0",
                "dump_state"     => GetDumpState(),
                _                => "RPRT -1"
            };
        }

        // get_vfo_info: frequency, mode, width, split, sat mode. Answered
        // from the cached state, as get_freq is, because Log4OM asks for
        // both VFOs every 0.6 s.
        private async Task<string> GetVfoInfoAsync(string? vfo, string clientId)
        {
            var onB = IsVfoB(vfo);
            var freq = onB ? _radioStateService.FrequencyB.ToString() : await GetFrequencyAsync(clientId);
            if (freq.StartsWith("RPRT")) return freq;
            var mode = HamlibModeOf(onB ? _radioStateService.ModeB : _radioStateService.ModeA);
            return VfoInfo(freq, mode, _splitEnabled);
        }

        internal static string VfoInfo(string freq, string mode, bool split) =>
            $"{freq}\n{mode}\n0\n{(split ? 1 : 0)}\n0";

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
            if (!Profile.CanTune(freq))
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
            return Task.FromResult(ModeReply(_radioStateService.ModeA));
        }

        private static string HamlibModeOf(string? display) =>
            !string.IsNullOrEmpty(display) && DisplayToHamlibMode.TryGetValue(display, out var mapped)
                ? mapped
                : "USB";

        private static string ModeReply(string? display)
        {
            var hamlibMode = HamlibModeOf(display);
            return $"{hamlibMode}\n{NominalPassband(hamlibMode)}";
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

        // --- Functions (get_func / set_func) ---
        // Only RIG_FUNC_TUNER is advertised (see the func masks in GetDumpState).
        // Hamlib passes the function name uppercase, e.g. "get_func TUNER".

        private async Task<string> GetFuncAsync(string func)
        {
            if (!func.Equals("TUNER", StringComparison.OrdinalIgnoreCase))
                return "RPRT -1";
            // Cache reflects the poll loop's 1C 01 read; "on" covers ON and TUNING.
            var on = _radioStateService.AtuEnabled;
            await Task.CompletedTask;
            return on ? "1" : "0";
        }

        private async Task<string> SetFuncAsync(string func, string value, string clientId)
        {
            if (!func.Equals("TUNER", StringComparison.OrdinalIgnoreCase))
                return "RPRT -1";
            // Hamlib sends 0 (off) or 1 (on). CI-V 1C 01: 0=OFF, 1=ON (in line).
            // A tuning cycle (02) is triggered by the web long-press, not exposed here.
            var on = value != "0";
            _logger.LogInformation("Rigctld set_func TUNER: {Value} (client: {ClientId})", value, clientId);
            await _radio.SetTunerAsync(on ? 1 : 0, CancellationToken.None);
            _radioStateService.AtuEnabled = on;
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
            if (!Profile.CanTune(freq))
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
            var (name, id) = Profile.Hamlib;
            return $"Icom;{name};1.0.0;000000;{id}";
        }

        private string GetDumpState() => DumpState(Profile);

        internal static string DumpState(RadioProfile profile)
        {
            // Minimal Hamlib dump_state response for a single-VFO rig, with the
            // radio's own receive and transmit ranges.
            // Func masks are 0 (ATU not yet exposed); level masks left broad.
            var lines = new List<string>
            {
                "0",            // protocol version
                "2",            // rig model (2 = NET rigctl)
                "0",            // ITU region. Hamlib reads this line before the
                                // ranges; without it the first RX range is taken
                                // as the region and every line after is one off.
            };
            foreach (var r in profile.Frequencies.Receive)
                lines.Add(FormattableString.Invariant($"{r.LoHz:F6} {r.HiHz:F6} 0x1ff -1 -1 0x10000003 0x3"));
            lines.Add("0 0 0 0 0 0 0");  // end of RX ranges
            foreach (var t in profile.Frequencies.Transmit)
                lines.Add(FormattableString.Invariant($"{t.LoHz:F6} {t.HiHz:F6} 0x1ff 2 {t.MaxWatts} 0x10000003 0x3"));
            lines.Add("0 0 0 0 0 0 0");  // end of TX ranges
            lines.AddRange(new[]
            {
                "0 0",          // end of tuning steps
                "0 0",          // end of filters
                "0",            // max RIT
                "0",            // max XIT
                "0",            // max IF-shift
                "0",            // announces
                "0",            // preamp list
                "0",            // attenuator list
                "0x00100000",   // has_get_func — RIG_FUNC_TUNER (ATU, CI-V 1C 01)
                "0x00100000",   // has_set_func — RIG_FUNC_TUNER
                "0x000fffff",   // has_get_level
                "0x000fffff",   // has_set_level
                "0",            // has_get_parm
                "0"             // has_set_parm
            });
            return string.Join("\n", lines);
        }

        private async Task<string> SetBandAsync(string band, string clientId)
        {
            // WSJT-X does not use set_band; Log4OM may. We have no CI-V "band"
            // command, so accept only a numeric Hz target and QSY VFO A to it.
            if (long.TryParse(band, out var freqHz) && Profile.CanTune(freqHz))
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
