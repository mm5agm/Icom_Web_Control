using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Icom_Web_Control.Hubs;
using Icom_Web_Control.Models;
using Icom_Web_Control.Services.Civ;

namespace Icom_Web_Control.Services
{
    /// <summary>
    /// Phase 2 — the first real radio link. This is the single class below the
    /// <see cref="IRadioController"/> seam that emits CI-V, replacing the Phase 1
    /// <see cref="StubRadioController"/>. It proves the whole spine end-to-end:
    /// real IC-7300 MkII → CI-V transport → <see cref="RadioStateService"/> →
    /// SignalR → the web page's VFO-A display.
    ///
    /// Scope is deliberately narrow (see docs/design/iwc-clone-split-plan.md,
    /// Phase 2): connect, learn the radio's address/ID (command 19 00, never
    /// hard-coded), and poll operating frequency (command 03). Mode, S-meter,
    /// PTT and the rest arrive one command per commit in Phase 3. The unimplemented
    /// seam members return safe values so nothing above the seam throws.
    ///
    /// Like the stub, it does NOT set <see cref="RadioStateService.IsInitialized"/>:
    /// broadcasting live values to the UI is the goal, but persisting a 4 Hz
    /// frequency stream to radio_state.json is needless disk churn this early.
    /// Persistence is turned on in a later phase alongside the full read-back.
    /// </summary>
    public sealed class CivRadioController : BackgroundService, IRadioController
    {
        private const int PollIntervalMs = 150;      // ~6–7 Hz — snappy meters/dial
        // When the scope is streaming, the CI-V bus (19200 baud ≈ 1920 B/s) is
        // near-saturated by the ~550-byte sweeps; a solicited read that lands
        // mid-sweep makes the radio skip a scope segment, and the assembler then
        // discards the whole sweep — the "once-a-second" spectrum stutter. Pacing
        // the poll loop slower while the scope runs hands that bus time back to
        // the scope. Every read still happens; the S-meter just drops to ~3.5 Hz,
        // imperceptible on a gauge, and the dial/meters stay responsive.
        private const int ScopePollIntervalMs = 280; // ~3–4 Hz while scope streams
        private const int ModePollEveryNLoops = 3;    // mode changes rarely; ~2 Hz is plenty
        private const int SplitPollEveryNLoops = 4;    // split rarely toggles; ~1.5 Hz is plenty
        private const int ReconnectDelayMs = 3000;
        private const int MaxConsecutiveReadMisses = 5;
        private const int PeekWindowMs = 450;          // scope-borrow window per cross-band peek (~1 sweep)

        private readonly RadioStateService _state;
        private readonly ICivClient _bus;
        private readonly ISettingsService _settings;
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly ILogger<CivRadioController> _logger;

        // The radio's CI-V address. Starts at the IC-7300 MkII default and is
        // replaced with whatever actually answers (reply From byte) on connect.
        private byte _radioAddress = CivProtocol.DefaultRadioAddress;

        // Spectrum scope (block 6): reassembles the unsolicited 27 00 waveform
        // stream and counts broadcasts so we can re-assert "streaming" as a
        // heartbeat for clients that load after the stream is already flowing.
        private readonly CivScopeAssembler _scope = new();
        private int _scopeBroadcasts;

        // TickCount64 of the last completed sweep, written on the serial-reader
        // thread and read by the poll loop to decide whether the scope is live.
        // While it is, the loop paces itself with ScopePollIntervalMs so solicited
        // reads stop crowding the scope frames off the bus. long writes are atomic
        // on this x64-only build; Volatile keeps the reader-thread store visible.
        private long _lastSweepTicks;

        // Diagnostics: log the scope frame-drop rate periodically while streaming
        // so bus-contention stutter is a measured number. Snapshots of the
        // assembler counters at the last log, plus when that log last fired.
        private long _lastScopeLogTicks;
        private long _lastLoggedSweeps;
        private long _lastLoggedDiscards;

        // Pseudo-dual receiver (Phase 5, same-band). Cached copy of the setting,
        // refreshed on a slow poll phase so the serial-reader thread (which raises
        // OnUnsolicitedFrame) never has to await the settings store. When on, the
        // single Center-mode sweep — which is wide enough to include a nearby watch
        // VFO — is cropped around the watch VFO and re-broadcast as the other panel.
        // _watchInRange tracks whether the watch VFO currently falls inside the
        // sweep so we only emit an "out of range" status on transitions.
        private volatile bool _pseudoDual;
        private volatile bool _watchInRange = true;

        // Cross-band peek supervisor (Phase 5, optional additive layer). When the
        // two VFOs are on different bands the same-band crop can't reach the watch
        // VFO, so — if the operator opts in — the poll loop periodically borrows the
        // receiver: it selects the watch VFO for one Center sweep (tagged to the
        // watch panel via _peekWatchId), then hands it straight back. Costs a brief
        // audio dip on the listen VFO, so it's off by default and rate-limited.
        // _crossBandPeek / _peekIntervalMs are cached copies of the settings,
        // refreshed on the same slow poll phase as _pseudoDual. _peekWatchId is
        // non-null only while a borrow is in flight (read by the reader thread).
        private volatile bool _crossBandPeek;
        private volatile int _peekIntervalMs = 15000;
        private volatile string? _peekWatchId;
        private long _peekLastTicks;

        // Watch-panel span (Phase 5). The single scope has one span, so the watch
        // panel is a crop of that sweep; "ZoomIn" mode lets the operator narrow the
        // crop around the watch VFO in software without touching the physical scope.
        // _watchZoomIn caches whether that mode is active (refreshed with the other
        // pseudo-dual settings); _watchCropHalfHz is the requested crop ± half-width
        // (0 = auto/widest). When not in ZoomIn mode the crop reverts to auto.
        private volatile bool _watchZoomIn = true;
        private volatile int _watchCropHalfHz;

        // When the operator manually selects Fixed mode via the panel's scope-mode
        // badge, remember it so a reconnect's EnableScopeAsync doesn't yank the
        // scope back to Center under them. Selecting Center again clears it.
        private volatile bool _operatorFixedMode;

        // When the operator turns the scope off (via the panel toggle), remember it
        // so a reconnect's EnableScopeAsync leaves it off rather than switching it
        // back on. Turning it on again clears it.
        private volatile bool _operatorScopeOff;

        // Set by RequestScopeStatusAnnounce (a browser just connected) and cleared
        // by the next sweep, which announces regardless of where the periodic
        // counter happens to be. int rather than bool so Interlocked can
        // read-and-clear it in one step — the sweep handler and the hub run on
        // different threads.
        private int _announceScopeStatus;

        /// <inheritdoc />
        public void RequestScopeStatusAnnounce() => Interlocked.Exchange(ref _announceScopeStatus, 1);

        public CivRadioController(
            RadioStateService state,
            ICivClient bus,
            ISettingsService settings,
            IHubContext<RadioHub> hubContext,
            ILogger<CivRadioController> logger)
        {
            _state = state;
            _bus = bus;
            _settings = settings;
            _hubContext = hubContext;
            _logger = logger;

            // The radio pushes 27 00 scope frames unsolicited (they never match a
            // pending transaction), so they arrive here rather than through the
            // request/reply path.
            _bus.UnsolicitedFrame += OnUnsolicitedFrame;
        }

        public bool IsConnected => _bus.IsOpen;
        public string? ModelId { get; private set; }

        // -- IRadioController ---------------------------------------------------

        public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var settings = await _settings.GetSettingsAsync();
            var port = settings.SerialPort;
            var baud = settings.BaudRate;

            _logger.LogInformation("[CivRadioController] Connecting to {Port} @ {Baud} 8N1…", port, baud);
            if (!await _bus.OpenAsync(port, baud))
            {
                // Tell the operator *why* the port didn't open. The common
                // pre-alpha case is a wrong/re-enumerated COM number (radio off,
                // cable in a different USB socket, driver not loaded) — surface
                // the configured port and the list actually present so the fix
                // is obvious without digging through logs or Device Manager.
                var available = System.IO.Ports.SerialPort.GetPortNames();
                if (Array.IndexOf(available, port) < 0)
                {
                    var list = available.Length > 0
                        ? string.Join(", ", available.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                        : "none";
                    _state.ConnectionStatusText =
                        $"Serial port {port} not found. Ports available now: {list}. " +
                        "Check the radio is on and the USB cable is connected, then set the correct port in Settings.";
                }
                else
                {
                    _state.ConnectionStatusText =
                        $"Serial port {port} is present but could not be opened — it may be in use by another program (e.g. WSJT-X or another CAT app).";
                }
                return false;
            }

            // A port that opens is NOT proof the radio is usable: over USB the
            // COM port can stay enumerated while the radio is off, and the
            // IC-7300's CI-V circuit still answers 19 00 in soft-off/standby
            // (display dark). So neither "port open" nor "ID answered" means the
            // radio is operational. Gate the connection on the operating-frequency
            // read (03) — the same liveness signal the poll loop uses to hold the
            // link — so a powered-off radio reads as disconnected (steady red)
            // instead of flashing the power button green↔red as each reconnect
            // flags "connected" only to drop on the very next read miss.
            await IdentifyAsync(cancellationToken); // learn address/model if it answers; best-effort
            if (await ReadOperatingFrequencyAsync(cancellationToken) <= 0)
            {
                await _bus.CloseAsync();
                _state.ConnectionStatusText =
                    $"Serial port {port} opened, but the radio isn't responding — is it powered on?";
                return false;
            }

            // Link is genuinely up — clear any earlier problem banner.
            _state.ConnectionStatusText = "";

            // Seed the RX controls so every slider/dropdown shows the radio's
            // real position on first paint rather than a default. All are
            // receiver-wide, so both panels mirror the one value. Best-effort —
            // a miss just leaves the existing default.
            await SeedRxControlsAsync(cancellationToken);

            // Turn on the CI-V spectrum scope and its waveform-to-controller
            // stream so the radio starts pushing 27 00 frames we reassemble into
            // the web spectrum. Best-effort — a miss just means no scope trace.
            await EnableScopeAsync(cancellationToken);

            return true;
        }

        // -- Spectrum scope (Phase 3 block 6) ----------------------------------

        /// <summary>
        /// Enable the spectrum scope and switch on the waveform output to the
        /// controller. Normally forces Center mode (the assumption the web
        /// SpectrumPanel's axis is built on), but respects a manual Fixed choice
        /// the operator made via the badge so a reconnect doesn't override it.
        /// Sent once per connect; best-effort.
        /// </summary>
        private async Task EnableScopeAsync(CancellationToken ct)
        {
            // Respect a manual scope-off from an earlier session — don't switch the
            // scope back on under the operator on reconnect.
            if (_operatorScopeOff)
                return;

            // Switch the scope on and start waveform output first, then assert the
            // mode — the radio can power up (or be left) in Fixed mode. Force
            // Center unless the operator deliberately chose Fixed this session.
            await SendScopeSetAsync(CivProtocol.SubScopeOnOff, 0x01, "scope on", ct);
            await SendScopeSetAsync(CivProtocol.SubScopeOutput, 0x01, "scope waveform output", ct);
            bool center = !_operatorFixedMode;
            await SetScopeModeAsync(center ? CivProtocol.ScopeModeCenter : CivProtocol.ScopeModeFixed,
                center ? "scope center mode" : "scope fixed mode", ct);
        }

        /// <summary>
        /// Turn the scope + waveform output on or off (CI-V 27 10 / 27 11). On
        /// re-asserts the scope mode too; off stops the 27 00 stream. Remembers a
        /// manual off across reconnects via <see cref="_operatorScopeOff"/>.
        /// </summary>
        public async Task SetScopeEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            _operatorScopeOff = !enabled;
            if (enabled)
            {
                await SendScopeSetAsync(CivProtocol.SubScopeOnOff, 0x01, "scope on", cancellationToken);
                await SendScopeSetAsync(CivProtocol.SubScopeOutput, 0x01, "scope waveform output", cancellationToken);
                bool center = !_operatorFixedMode;
                await SetScopeModeAsync(center ? CivProtocol.ScopeModeCenter : CivProtocol.ScopeModeFixed,
                    center ? "scope center mode" : "scope fixed mode", cancellationToken);
            }
            else
            {
                // Stop the PC waveform stream first, then power the scope down.
                await SendScopeSetAsync(CivProtocol.SubScopeOutput, 0x00, "scope waveform output off", cancellationToken);
                await SendScopeSetAsync(CivProtocol.SubScopeOnOff, 0x00, "scope off", cancellationToken);
            }
        }

        /// <summary>
        /// Set the scope mode (CI-V 27 14) to Center or Fixed. Public entry point
        /// for the click-the-badge control; delegates to the retrying setter.
        /// </summary>
        public Task SetScopeModeAsync(bool center, CancellationToken cancellationToken = default)
        {
            // Remember a manual Fixed choice so a later reconnect respects it
            // instead of forcing Center (see EnableScopeAsync).
            _operatorFixedMode = !center;
            byte mode = center ? CivProtocol.ScopeModeCenter : CivProtocol.ScopeModeFixed;
            return SetScopeModeAsync(mode, center ? "scope center mode" : "scope fixed mode", cancellationToken);
        }

        /// <summary>
        /// Set the scope mode (27 14). The payload is two bytes — the Main-scope
        /// selector (00) then the mode — see <see cref="CivProtocol.ScopeMain"/>.
        /// Retried a few times because a front-panel/reconnect can leave the
        /// scope in Fixed mode, which misaligns the (Center-assuming) web panel.
        /// </summary>
        private async Task SetScopeModeAsync(byte mode, string what, CancellationToken ct)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdScope, CivProtocol.SubScopeMode, CivProtocol.ScopeMain, mode);
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
                if (reply != null && reply.Cmd == CivProtocol.AckOk)
                    return;
                if (attempt < 3)
                    await Task.Delay(150, ct);
            }
            _logger.LogWarning("[CivRadioController] {What} was not acknowledged", what);
        }

        /// <summary>Send a 27-family scope set (27 &lt;sub&gt; &lt;val&gt;) and expect an ack.</summary>
        private async Task SendScopeSetAsync(byte sub, byte val, string what, CancellationToken ct)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdScope, sub, val);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] {What} was not acknowledged", what);
        }

        /// <summary>
        /// Set the scope span (Center mode). <paramref name="spanHz"/> is the radio
        /// SPAN ± half-width in Hz (2500 … 500000) sent as 27 15 &lt;5-byte BCD&gt;.
        /// The waveform-info span field reads back the same value, which the
        /// assembler doubles for the displayed full width.
        /// </summary>
        public async Task SetScopeSpanAsync(int spanHz, CancellationToken cancellationToken = default)
        {
            // The radio stores the span field as the sent value ÷ 100 (verified
            // on the IC-7300 MkII: sending 250000 yields span field 2500). So to
            // set span field F (the ± half-width in Hz the waveform reports), send
            // F × 100 as the 5-byte BCD payload. Values that don't map to a valid
            // span field are NG-rejected by the radio.
            var body = new byte[2 + 5];
            body[0] = CivProtocol.CmdScope;
            body[1] = CivProtocol.SubScopeSpan;
            var bcd = CivProtocol.EncodeBcd((long)spanHz * 100, 5);
            Array.Copy(bcd, 0, body, 2, 5);
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, body);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] scope span ±{SpanHz} Hz was not acknowledged", spanHz);
        }

        /// <summary>
        /// Set the watch panel's crop ± half-width (Phase 5 "ZoomIn" span mode).
        /// Display-only: it stores the requested half-width that
        /// <see cref="BroadcastWatchCrop"/> applies on the next sweep — no CI-V is
        /// sent, so the physical scope and the primary panel are untouched.
        /// </summary>
        public Task SetWatchCropSpanAsync(int halfHz, CancellationToken cancellationToken = default)
        {
            _watchCropHalfHz = Math.Max(0, halfHz);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Feed unsolicited frames to the scope assembler. Runs on the serial
        /// reader thread; the assembler is single-threaded by that contract, and
        /// the SignalR broadcast is fire-and-forget so we never block the reader.
        /// </summary>
        private void OnUnsolicitedFrame(object? sender, CivFrame frame)
        {
            if (frame.Cmd != CivProtocol.CmdScope)
                return;
            var sweep = _scope.Add(frame);
            if (sweep == null)
                return;

            // Mark the scope live so the poll loop backs off and lets the sweep
            // frames have the bus (see ScopePollIntervalMs).
            Volatile.Write(ref _lastSweepTicks, Environment.TickCount64);

            // The radio's live scope mode (tracks front-panel changes), carried on
            // every SpectrumUpdate so the panel can label CENT / FIX etc.
            string mode = ScopeModeName(sweep.Mode);

            // Re-assert "streaming" on the first sweep and every ~30 thereafter
            // so a client that loads mid-stream un-hides its spectrum panel.
            // The counter runs from app start, not from connect, so on its own it
            // leaves a newly-connected browser waiting up to 29 sweeps with the
            // panel still hidden — RadioHub asks for an immediate announce on
            // connect and that request is honoured here. The read-and-clear is on
            // its own line so the request is consumed even on a sweep where the
            // periodic announce was due anyway.
            bool requested = Interlocked.Exchange(ref _announceScopeStatus, 0) == 1;
            bool announce = _scopeBroadcasts++ % 30 == 0 || requested;

            if (!_pseudoDual)
            {
                if (announce)
                    SendHub("SdrStatus", new { sdrId = "A", status = "streaming" });
                SendHub("SpectrumUpdate", new
                {
                    sdrId = "A",
                    bins = sweep.BinsDb,
                    centreHz = sweep.CentreHz,
                    spanHz = sweep.SpanHz,
                    mode,
                });
                return;
            }

            // A cross-band peek is in progress: the receiver is momentarily tuned
            // to the watch band, so this whole sweep belongs to the watch panel —
            // route it there and leave the primary panel frozen for the ~0.4 s dip.
            string? peek = _peekWatchId;
            if (peek != null)
            {
                if (announce)
                    SendHub("SdrStatus", new { sdrId = peek, status = "streaming" });
                SendHub("SpectrumUpdate", new
                {
                    sdrId = peek,
                    bins = sweep.BinsDb,
                    centreHz = sweep.CentreHz,
                    spanHz = sweep.SpanHz,
                    mode,
                });
                return;
            }

            // Pseudo-dual receiver (Phase 5, same-band): the Center-mode sweep is
            // centred on the operating VFO, so it feeds the primary panel; a window
            // cropped around the watch VFO feeds the other panel. No extra CI-V, no
            // scope-mode churn — the audio VFO never moves. When the watch VFO is
            // outside the sweep (cross-band, or the span is too narrow) the watch
            // panel reports "out of range"; the optional peek layer fills that gap
            // later.
            RadioVfo active = ActiveVfo;
            string primaryId = active == RadioVfo.A ? "A" : "B";
            string watchId   = active == RadioVfo.A ? "B" : "A";
            long watchHz     = active == RadioVfo.A ? _state.FrequencyB : _state.FrequencyA;

            if (announce)
                SendHub("SdrStatus", new { sdrId = primaryId, status = "streaming" });
            SendHub("SpectrumUpdate", new
            {
                sdrId = primaryId,
                bins = sweep.BinsDb,
                centreHz = sweep.CentreHz,
                spanHz = sweep.SpanHz,
                mode,
            });

            BroadcastWatchCrop(sweep, watchId, watchHz, announce, mode);
        }

        /// <summary>
        /// Phase 5 same-band watch panel: crop the widest window centred on the
        /// watch VFO that still fits inside the primary sweep and broadcast it as
        /// <paramref name="watchId"/>. The frontend panel centres on its own VFO
        /// frequency, so a slice tagged with <c>centreHz = watchHz</c> lands
        /// correctly with no frontend change. Out-of-range (cross-band) emits an
        /// "outofrange" status on transitions, and streaming is re-asserted on the
        /// <paramref name="announce"/> heartbeat for late-loading clients.
        /// </summary>
        private void BroadcastWatchCrop(ScopeSweep sweep, string watchId, long watchHz, bool announce, string mode)
        {
            int n = sweep.BinsDb.Length;
            long lo = sweep.CentreHz - sweep.SpanHz / 2;
            long hi = sweep.CentreHz + sweep.SpanHz / 2;

            // Need the watch VFO comfortably inside the sweep (a little margin each
            // side) and enough bins to be worth drawing.
            long margin = sweep.SpanHz / 20;
            bool inRange = n >= 16 && sweep.SpanHz > 0 && watchHz > 0
                           && watchHz > lo + margin && watchHz < hi - margin;

            long halfB = 0, wLo = 0, wHi = 0;
            int iLo = 0, iHi = 0;
            if (inRange)
            {
                // Widest symmetric crop that fits inside the sweep. In "ZoomIn"
                // mode a requested narrower half-width tightens it around the watch
                // VFO (display-only, no CI-V, no effect on the primary); a request
                // wider than what fits just clamps to this geometric max.
                long geoMax = Math.Min(watchHz - lo, hi - watchHz);
                int req = _watchZoomIn ? _watchCropHalfHz : 0;
                halfB = req > 0 ? Math.Min(geoMax, req) : geoMax;
                wLo = watchHz - halfB;
                wHi = watchHz + halfB;
                double binsPerHz = (double)(n - 1) / sweep.SpanHz;
                iLo = Math.Clamp((int)Math.Round((wLo - lo) * binsPerHz), 0, n - 1);
                iHi = Math.Clamp((int)Math.Round((wHi - lo) * binsPerHz), 0, n - 1);
                if (iHi - iLo < 8)
                    inRange = false;
            }

            if (!inRange)
            {
                // Cross-band peek fills this panel by borrowing the receiver every
                // few seconds; between borrows keep the last peeked trace frozen
                // rather than wiping it with an "off-screen" overlay. Only when
                // peek is off do we tell the panel the watch VFO is unreachable.
                if (!_crossBandPeek && (announce || _watchInRange))
                    SendHub("SdrStatus", new { sdrId = watchId, status = "outofrange" });
                _watchInRange = false;
                return;
            }

            if (announce || !_watchInRange)
                SendHub("SdrStatus", new { sdrId = watchId, status = "streaming" });
            _watchInRange = true;

            var slice = new float[iHi - iLo + 1];
            Array.Copy(sweep.BinsDb, iLo, slice, 0, slice.Length);
            SendHub("SpectrumUpdate", new
            {
                sdrId = watchId,
                bins = slice,
                centreHz = watchHz,
                spanHz = wHi - wLo,
                mode,
            });
        }

        /// <summary>
        /// Map the waveform header's scope-mode byte (field ④) to the short label
        /// the radio's own screen uses. Center is the mode IWC forces and the web
        /// panel assumes; the others mean the panel's axis may not line up.
        /// </summary>
        private static string ScopeModeName(byte mode) => mode switch
        {
            0x00 => "CENT",
            0x01 => "FIX",
            0x02 => "SCROLL-C",
            0x03 => "SCROLL-F",
            _    => "?",
        };

        /// <summary>
        /// Cross-band peek (Phase 5): when the two VFOs are on different bands the
        /// same-band crop can't reach the watch VFO, so momentarily borrow the
        /// receiver — select the watch VFO, let a Center sweep arrive (tagged as the
        /// watch panel via <see cref="_peekWatchId"/>), then hand it straight back.
        /// Costs a brief (~0.4 s) audio dip on the listen VFO, which is why it's
        /// opt-in and rate-limited. Called every poll loop but a no-op unless the
        /// operator enabled it, the interval has elapsed, and the watch VFO isn't
        /// already visible in the same-band crop. Runs on the poll thread, so the
        /// borrow blocks meter reads for its window — acceptable, audio is dipping
        /// anyway. The caller guards on connected + !transmitting.
        /// </summary>
        private async Task MaybePeekWatchBandAsync(CancellationToken ct)
        {
            if (!_crossBandPeek)
                return;
            long now = Environment.TickCount64;
            if (now - _peekLastTicks < _peekIntervalMs)
                return;

            // Same-band crop already covers the watch VFO — no borrow needed. Reset
            // the clock so a later cross-band retune waits a full interval rather
            // than dipping the instant the VFOs part.
            if (_watchInRange)
            {
                _peekLastTicks = now;
                return;
            }

            RadioVfo active = ActiveVfo;
            RadioVfo watch = active == RadioVfo.A ? RadioVfo.B : RadioVfo.A;
            string watchId = active == RadioVfo.A ? "B" : "A";

            // Borrow the receiver. Route arriving sweeps to the watch panel via
            // _peekWatchId; send the selects over the bus directly so the persistent
            // ActiveVfo / "listening" badge never flips.
            _peekWatchId = watchId;
            try
            {
                await RawSelectVfoAsync(watch, ct);
                await DelayQuiet(PeekWindowMs, ct);
            }
            finally
            {
                // Always hand the receiver back — leaving it on the watch VFO would
                // strand the audio there — and retry, since a dropped select is far
                // worse here than elsewhere. Clear the peek tag only once restored.
                for (int i = 0; i < 3 && !await RawSelectVfoAsync(active, ct); i++)
                    await DelayQuiet(30, ct);
                _peekWatchId = null;
                _peekLastTicks = Environment.TickCount64;
            }
        }

        /// <summary>
        /// Select a VFO (command 07) over the bus WITHOUT mutating
        /// <see cref="RadioStateService.ActiveVfo"/> — used by the peek supervisor to
        /// borrow the receiver transiently. Returns whether the radio acknowledged.
        /// </summary>
        private async Task<bool> RawSelectVfoAsync(RadioVfo vfo, CancellationToken ct)
        {
            byte v = vfo == RadioVfo.B ? CivProtocol.VfoSelectB : CivProtocol.VfoSelectA;
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSelectVfo, v);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
            return reply != null && reply.Cmd == CivProtocol.AckOk;
        }

        private void SendHub(string property, object value)
            => _ = _hubContext.Clients.All.SendAsync("RadioStateUpdate", new { property, value });

        /// <summary>
        /// One-shot read of the receiver-wide RX controls at connect, mirrored
        /// into both A/B state fields. Best-effort; a missed read leaves the
        /// existing value. The slow poll keeps AF gain live thereafter.
        /// </summary>
        private async Task SeedRxControlsAsync(CancellationToken ct)
        {
            var af = await GetAfGainAsync(ct);        if (af >= 0)  { _state.AfGainA = af;   _state.AfGainB = af; }
            var rf = await GetRfGainAsync(ct);        if (rf >= 0)  { _state.RfGainA = rf;   _state.RfGainB = rf; }
            var sq = await GetSquelchAsync(ct);       if (sq >= 0)  { _state.SquelchA = sq;  _state.SquelchB = sq; }
            var nrl = await GetNrLevelAsync(ct);      if (nrl >= 0) { _state.NrLevelA = nrl; _state.NrLevelB = nrl; }
            var nbl = await GetNbLevelAsync(ct);      if (nbl >= 0) { _state.NbLevelA = nbl; _state.NbLevelB = nbl; }
            var np = await GetNotchPositionAsync(ct); if (np >= 0)  { _state.ManualNotchFreqA = np; _state.ManualNotchFreqB = np; }
            var pa = await GetPreampAsync(ct);        if (pa >= 0)  { _state.IpoA = pa.ToString(); _state.IpoB = pa.ToString(); }
            var agc = await GetAgcAsync(ct);          if (agc >= 0) { _state.AgcA = agc.ToString(); _state.AgcB = agc.ToString(); }
            _state.NbA = await GetNoiseBlankerAsync(ct)   ? "1" : "0"; _state.NbB = _state.NbA;
            _state.NrA = await GetNoiseReductionAsync(ct) ? "1" : "0"; _state.NrB = _state.NrA;
            _state.AutoNotchA = await GetAutoNotchAsync(ct)   ? "1" : "0"; _state.AutoNotchB = _state.AutoNotchA;
            _state.ManualNotchA = await GetManualNotchAsync(ct) ? "1" : "0"; _state.ManualNotchB = _state.ManualNotchA;
            var mnw = await GetManualNotchWidthAsync(ct); if (mnw >= 0) { _state.ManualNotchWidthA = mnw.ToString(); _state.ManualNotchWidthB = _state.ManualNotchWidthA; }
            var ifs = await GetIfFilterShapeAsync(ct);    if (ifs >= 0) { _state.IfShapeA = ifs.ToString(); _state.IfShapeB = _state.IfShapeA; }
            _state.AttA = await GetAttenuatorAsync(ct) ? "20" : "00"; _state.AttB = _state.AttA;
        }

        // Rotating RX-control poll: mirror front-panel changes to AGC, preamp,
        // NB, NR, notch, attenuator and the levels back to the app. These move
        // rarely, so we read exactly one per poll loop and cycle through the
        // set — each control refreshes roughly every couple of seconds without
        // crowding out the S-meter / dial. AF gain has its own faster phase.
        //
        // Every branch reads the raw int (or command-11 byte) and only writes
        // state when the read succeeded (>= 0): a bus miss must never blink a
        // control to OFF. The state setters broadcast only on change, so a
        // steady radio produces no SignalR traffic here.
        private int _rxPollIndex;
        private const int RxControlCount = 16;
        private const int RxControlsPerLoop = 2;   // ~1.3 s to sweep all 16

        private async Task PollNextRxControlAsync(CancellationToken ct)
        {
            switch (_rxPollIndex % RxControlCount)
            {
                case 0:  { int v = await ReadFunc16Async(CivProtocol.SubAgc, ct);    if (v >= 0) { _state.AgcA = v.ToString(); _state.AgcB = _state.AgcA; } break; }
                case 1:  { int v = await ReadFunc16Async(CivProtocol.SubPreamp, ct); if (v >= 0) { _state.IpoA = v.ToString(); _state.IpoB = _state.IpoA; } break; }
                case 2:  { int v = await ReadFunc16Async(CivProtocol.SubNoiseBlanker, ct);   if (v >= 0) { _state.NbA = v == 1 ? "1" : "0"; _state.NbB = _state.NbA; } break; }
                case 3:  { int v = await ReadFunc16Async(CivProtocol.SubNoiseReduction, ct); if (v >= 0) { _state.NrA = v == 1 ? "1" : "0"; _state.NrB = _state.NrA; } break; }
                case 4:  { int v = await ReadFunc16Async(CivProtocol.SubAutoNotch, ct);   if (v >= 0) { _state.AutoNotchA = v == 1 ? "1" : "0"; _state.AutoNotchB = _state.AutoNotchA; } break; }
                case 5:  { int v = await ReadFunc16Async(CivProtocol.SubManualNotch, ct); if (v >= 0) { _state.ManualNotchA = v == 1 ? "1" : "0"; _state.ManualNotchB = _state.ManualNotchA; } break; }
                case 6:  { int v = await ReadAttenuatorRawAsync(ct); if (v >= 0) { _state.AttA = v == CivProtocol.AttOn20dB ? "20" : "00"; _state.AttB = _state.AttA; } break; }
                case 7:  { int v = await GetRfGainAsync(ct);        if (v >= 0) { _state.RfGainA = v;   _state.RfGainB = v; } break; }
                case 8:  { int v = await GetSquelchAsync(ct);       if (v >= 0) { _state.SquelchA = v;  _state.SquelchB = v; } break; }
                case 9:  { int v = await GetNrLevelAsync(ct);       if (v >= 0) { _state.NrLevelA = v;  _state.NrLevelB = v; } break; }
                case 10: { int v = await GetNbLevelAsync(ct);       if (v >= 0) { _state.NbLevelA = v;  _state.NbLevelB = v; } break; }
                case 11: { int v = await GetNotchPositionAsync(ct); if (v >= 0) { _state.ManualNotchFreqA = v; _state.ManualNotchFreqB = v; } break; }
                case 12: { int v = await ReadFunc16Async(CivProtocol.SubManualNotchWidth, ct); if (v >= 0) { _state.ManualNotchWidthA = v.ToString(); _state.ManualNotchWidthB = _state.ManualNotchWidthA; } break; }
                case 13: { int v = await ReadFunc16Async(CivProtocol.SubIfFilterShape, ct);    if (v >= 0) { _state.IfShapeA = v.ToString(); _state.IfShapeB = _state.IfShapeA; } break; }
                case 14: { int v = await GetTunerAsync(ct); if (v >= 0) { _state.AtuEnabled = v != CivProtocol.TunerOff; _state.AtuTuning = v == CivProtocol.TunerTune; } break; }
                case 15: await PollIfWidthAndFilterAsync(ct); break;
            }
            _rxPollIndex++;
        }

        /// <summary>
        /// Poll the operating VFO's selected filter slot and IF passband width
        /// in one pass. The 1A 03 width command is receiver-wide, so it only
        /// reflects the operating VFO; the mode read (26) also yields the FIL
        /// slot, so both come from a single extra transaction plus (for modes
        /// that have a width) the 1A 03 read. FM clears the width to "".
        /// </summary>
        private async Task PollIfWidthAndFilterAsync(CancellationToken ct)
        {
            var vfo = ActiveVfo;
            var m = await ReadVfoModeRawAsync(SelectorFor(vfo), ct);
            if (!m.ok) return;

            if (m.filter is >= 0x01 and <= 0x03)
            {
                string f = m.filter.ToString();
                if (vfo == RadioVfo.A) _state.SelectedFilterA = f; else _state.SelectedFilterB = f;
            }

            var group = FilterWidthCodec.GroupForModeByte(m.mode);
            if (group == FilterWidthCodec.Group.None)
            {
                if (vfo == RadioVfo.A) _state.IfWidthA = ""; else _state.IfWidthB = "";
                return;
            }

            int code = await ReadMenuByteAsync(CivProtocol.SubIfWidth, ct);
            if (code < 0) return;
            int hz = FilterWidthCodec.CodeToHz(group, code);
            if (hz < 0) return;
            if (vfo == RadioVfo.A) _state.IfWidthA = hz.ToString(); else _state.IfWidthB = hz.ToString();
        }

        /// <summary>Read the attenuator (command 11) as its raw byte. -1 on a miss; 0x20 = 20 dB.</summary>
        private async Task<int> ReadAttenuatorRawAsync(CancellationToken ct)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, CivProtocol.CmdAttenuator);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdAttenuator, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.CmdAttenuator || reply.Data.Length < 1)
                return -1;
            return reply.Data[0];
        }

        public Task DisconnectAsync() => _bus.CloseAsync();

        // Per-VFO addressing (block 5): the operating (active) VFO keeps the
        // hardware-proven operating commands 03/05 — frequency is also the poll
        // loop's liveness signal, so it must never regress to an unproven path.
        // The *other* VFO is reached without disturbing operation via 25 01
        // (unselected). If 25 is unsupported, only the watch VFO goes blank; the
        // link and the operating VFO are unaffected.

        public Task<long> GetFrequencyHzAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
            => vfo == ActiveVfo
                ? ReadOperatingFrequencyAsync(cancellationToken)                       // command 03
                : ReadFrequencyBySelectorAsync(CivProtocol.VfoUnselected, cancellationToken); // 25 01

        /// <summary>Read the operating VFO frequency (command 03). -1 on any miss.</summary>
        private async Task<long> ReadOperatingFrequencyAsync(CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, CivProtocol.CmdReadFrequency);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdReadFrequency, cancellationToken: cancellationToken);
            if (reply == null || reply.Cmd != CivProtocol.CmdReadFrequency || reply.Data.Length < 5)
                return -1;
            return CivProtocol.DecodeBcd(reply.Data.AsSpan(0, 5));
        }

        /// <summary>
        /// Read a VFO frequency by selected/unselected selector (command 25).
        /// Reply is <c>25 &lt;sel&gt; &lt;5 BCD LE&gt;</c>; -1 on any miss.
        /// </summary>
        private async Task<long> ReadFrequencyBySelectorAsync(byte sel, CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdVfoFrequency, sel);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdVfoFrequency, cancellationToken: cancellationToken);
            // Reply body: 25 <sel> <5 BCD> → Data = [sel, b0..b4].
            if (reply == null || reply.Cmd != CivProtocol.CmdVfoFrequency
                || reply.Data.Length < 6 || reply.Data[0] != sel)
                return -1;
            return CivProtocol.DecodeBcd(reply.Data.AsSpan(1, 5));
        }

        public async Task SetFrequencyHzAsync(RadioVfo vfo, long frequencyHz, CancellationToken cancellationToken = default)
        {
            var bcd = CivProtocol.EncodeBcd(frequencyHz, 5);
            byte[] body;
            if (vfo == ActiveVfo)
            {
                // Operating VFO — command 05 (proven).
                body = new byte[1 + bcd.Length];
                body[0] = CivProtocol.CmdSetFrequency;
                Array.Copy(bcd, 0, body, 1, bcd.Length);
            }
            else
            {
                // Other VFO — command 25 01, without switching the operating VFO.
                body = new byte[2 + bcd.Length];
                body[0] = CivProtocol.CmdVfoFrequency;
                body[1] = CivProtocol.VfoUnselected;
                Array.Copy(bcd, 0, body, 2, bcd.Length);
            }

            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, body);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
            {
                if (vfo == RadioVfo.A) _state.FrequencyA = frequencyHz;
                else _state.FrequencyB = frequencyHz;
            }
            else
            {
                _logger.LogWarning("[CivRadioController] Set frequency {Hz} Hz was not acknowledged", frequencyHz);
            }
        }

        /// <summary>
        /// The VFO the radio is currently operating on, tracked from our own 07
        /// sends (there is no CI-V read for the active VFO). Maps
        /// <see cref="RadioStateService.ActiveVfo"/> (0=A/1=B) to <see cref="RadioVfo"/>.
        /// Front-panel A/B presses are the one known desync — a poll can't detect them.
        /// </summary>
        private RadioVfo ActiveVfo => _state.ActiveVfo == 1 ? RadioVfo.B : RadioVfo.A;

        private static int VfoIndex(RadioVfo vfo) => vfo == RadioVfo.B ? 1 : 0;

        private void SetVfoFrequency(RadioVfo vfo, long hz)
        {
            if (vfo == RadioVfo.A) _state.FrequencyA = hz; else _state.FrequencyB = hz;
        }

        private void SetVfoMode(RadioVfo vfo, string mode)
        {
            if (vfo == RadioVfo.A) _state.ModeA = mode; else _state.ModeB = mode;
        }

        // -- Mode (Phase 3 blocks 2 + 5) ---------------------------------------
        //
        // Block 5 folds the former two-frame 06 + 1A 06 dance into the single
        // atomic command 26, which carries <mode> <data> <filter> together and
        // addresses either VFO via the selected/unselected selector. Mode reads
        // are best-effort in the poll loop (a miss never drops the link), so
        // unlike frequency there's no need to keep the legacy 04 path.

        public async Task<string> GetModeAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
        {
            byte sel = SelectorFor(vfo);
            var m = await ReadVfoModeRawAsync(sel, cancellationToken);
            if (!m.ok)
                return vfo == RadioVfo.A ? (_state.ModeA ?? "") : (_state.ModeB ?? "");
            return NameForMode(m.mode, m.data != 0);
        }

        public async Task SetModeAsync(RadioVfo vfo, string mode, CancellationToken cancellationToken = default)
        {
            if (!ModeNameToIcom.TryGetValue(mode, out var target))
            {
                // Modes with no IC-7300 CI-V equivalent (PSK, FM-N, AM-N, …).
                _logger.LogWarning("[CivRadioController] Unsupported mode '{Mode}' — ignored", mode);
                return;
            }

            byte sel = SelectorFor(vfo);

            // Preserve the VFO's current filter (FIL1/2/3); default FIL1 if it
            // can't be read. In command 26 the filter is always 1–3 (unlike the
            // 1A 06 form where it was 00 when data was off).
            byte filter = 0x01;
            var cur = await ReadVfoModeRawAsync(sel, cancellationToken);
            if (cur.ok && cur.filter is >= 0x01 and <= 0x03) filter = cur.filter;

            byte dataByte = target.Data ? (byte)0x01 : (byte)0x00;
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdVfoMode, sel, target.BaseByte, dataByte, filter);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
            {
                _logger.LogWarning("[CivRadioController] Set mode '{Mode}' (26) was not acknowledged", mode);
                return;
            }

            var name = NameForMode(target.BaseByte, target.Data);
            if (vfo == RadioVfo.A) _state.ModeA = name; else _state.ModeB = name;
        }

        /// <summary>
        /// Read a VFO's mode/data/filter (command 26). Reply is
        /// <c>26 &lt;sel&gt; &lt;mode&gt; &lt;data&gt; &lt;filter&gt;</c>;
        /// <c>ok=false</c> on any miss.
        /// </summary>
        private async Task<(bool ok, byte mode, byte data, byte filter)> ReadVfoModeRawAsync(
            byte sel, CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdVfoMode, sel);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdVfoMode, cancellationToken: cancellationToken);
            // Reply body: 26 <sel> <mode> <data> <filter> → Data = [sel, mode, data, filter].
            if (reply == null || reply.Cmd != CivProtocol.CmdVfoMode
                || reply.Data.Length < 4 || reply.Data[0] != sel)
                return (false, 0, 0, 0);
            return (true, reply.Data[1], reply.Data[2], reply.Data[3]);
        }

        /// <summary>Map an A/B target to the 25/26 selected(00)/unselected(01) byte.</summary>
        private byte SelectorFor(RadioVfo vfo)
            => vfo == ActiveVfo ? CivProtocol.VfoSelected : CivProtocol.VfoUnselected;

        // CI-V base-mode byte (+ DATA flag) → the display strings already spoken
        // by RadioStateService, the web mode dropdown, voice, and rigctld. The
        // "-U"/"-L" suffix follows the existing UI vocabulary — CW normal is the
        // USB side ("CW-U"), CW-R the LSB side ("CW-L"); RTTY normal is the LSB
        // side ("RTTY-L"), RTTY-R the USB side. Data variants: USB-D→"DATA-U",
        // LSB-D→"DATA-L", FM-D→"DATA-FM".
        private static string NameForMode(byte baseByte, bool data) => baseByte switch
        {
            0x00 => data ? "DATA-L" : "LSB",
            0x01 => data ? "DATA-U" : "USB",
            0x02 => "AM",   // AM-data has no distinct UI string; report as AM
            0x03 => "CW-U",
            0x04 => "RTTY-L",
            0x05 => data ? "DATA-FM" : "FM",
            0x07 => "CW-L",
            0x08 => "RTTY-U",
            _ => $"?{baseByte:X2}",
        };

        // Display string → (base-mode byte, DATA flag). Covers every mode the
        // IC-7300 exposes over CI-V; Yaesu-only strings (PSK, FM-N, AM-N,
        // DATA-FM-N) are intentionally absent and rejected by SetModeAsync.
        private static readonly Dictionary<string, (byte BaseByte, bool Data)> ModeNameToIcom =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["LSB"] = (0x00, false),
                ["USB"] = (0x01, false),
                ["AM"] = (0x02, false),
                ["CW-U"] = (0x03, false), ["CW"] = (0x03, false),
                ["RTTY-L"] = (0x04, false), ["RTTY"] = (0x04, false),
                ["FM"] = (0x05, false),
                ["CW-L"] = (0x07, false), ["CW-R"] = (0x07, false),
                ["RTTY-U"] = (0x08, false), ["RTTY-R"] = (0x08, false),
                ["DATA-L"] = (0x00, true),
                ["DATA-U"] = (0x01, true),
                ["DATA-FM"] = (0x05, true),
            };

        // -- Meters (Phase 3 blocks 3–4) ---------------------------------------

        public async Task<int> ReadSMeterAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
        {
            // Command 15 02, level 0–255 as two big-endian BCD bytes
            // (00 00=S0, 01 20=S9, 02 41=S9+60 dB).
            int level = await ReadMeterAsync(CivProtocol.SubSMeter, cancellationToken);
            if (level < 0)
                return vfo == RadioVfo.A ? (_state.SMeterA ?? 0) : (_state.SMeterB ?? 0);
            return level;
        }

        /// <summary>
        /// Read one meter from the 15 family (S-meter 02, Po 11, SWR 12). All
        /// share the same reply shape — 15 &lt;sub&gt; &lt;d1&gt; &lt;d2&gt; with a
        /// 0–255 big-endian BCD level. Returns -1 on any miss (wrong sub, short
        /// frame, no reply) so callers can decide the fallback.
        /// </summary>
        private async Task<int> ReadMeterAsync(byte sub, CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdReadMeter, sub);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdReadMeter, cancellationToken: cancellationToken);
            // Reply body is 15 <sub> <d1> <d2>: Data = [sub, d1, d2].
            if (reply == null || reply.Cmd != CivProtocol.CmdReadMeter
                || reply.Data.Length < 3 || reply.Data[0] != sub)
                return -1;
            return CivProtocol.BcdByte(reply.Data[1]) * 100 + CivProtocol.BcdByte(reply.Data[2]);
        }

        // -- PTT / TX status + TX meters (Phase 3 block 4) ---------------------

        // The poll loop keeps _state.IsTransmitting live (via ReadTransmitAsync),
        // so on-demand callers (rigctld, voice) get the cached value cheaply
        // rather than firing another bus transaction at ~7 Hz.
        public Task<bool> GetTransmitAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_state.IsTransmitting);

        public async Task SetTransmitAsync(bool transmit, CancellationToken cancellationToken = default)
        {
            // Software PTT: command 1C 00 with a data byte (01=TX, 00=RX).
            byte v = transmit ? (byte)0x01 : (byte)0x00;
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdTransmit, CivProtocol.SubTxStatus, v);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
                _state.IsTransmitting = transmit;
            else
                _logger.LogWarning("[CivRadioController] Set PTT {State} was not acknowledged",
                    transmit ? "TX" : "RX");
        }

        /// <summary>
        /// Read the transmit state (command 1C 00, no data byte). Returns the
        /// last known state on a miss so a dropped frame never spuriously
        /// flips TX/RX.
        /// </summary>
        private async Task<bool> ReadTransmitAsync(CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdTransmit, CivProtocol.SubTxStatus);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdTransmit, cancellationToken: cancellationToken);
            // Reply body is 1C 00 <status>: Data = [00, status].
            if (reply != null && reply.Cmd == CivProtocol.CmdTransmit
                && reply.Data.Length >= 2 && reply.Data[0] == CivProtocol.SubTxStatus)
                return reply.Data[1] != 0;
            return _state.IsTransmitting;
        }

        // -- Antenna tuner (CI-V 1C 01) ----------------------------------------

        /// <summary>
        /// Read the antenna-tuner state (command 1C 01, no data byte):
        /// 0=OFF, 1=ON, 2=TUNING. -1 on any miss. The poll loop keeps
        /// _state.AtuEnabled / _state.AtuTuning live from this.
        /// </summary>
        public async Task<int> GetTunerAsync(CancellationToken cancellationToken = default)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdTransmit, CivProtocol.SubTuner);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdTransmit, cancellationToken: cancellationToken);
            // Reply body is 1C 01 <status>: Data = [01, status].
            if (reply != null && reply.Cmd == CivProtocol.CmdTransmit
                && reply.Data.Length >= 2 && reply.Data[0] == CivProtocol.SubTuner)
                return reply.Data[1];
            return -1;
        }

        public async Task SetTunerAsync(int state, CancellationToken cancellationToken = default)
        {
            if (state < 0 || state > 2) return;
            byte v = (byte)state;
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdTransmit, CivProtocol.SubTuner, v);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
            {
                // 00=OFF, 01=ON, 02=start tuning. A tuning cycle leaves the tuner
                // ON when it finishes; reflect "enabled" for both 01 and 02.
                _state.AtuEnabled = state != CivProtocol.TunerOff;
                _state.AtuTuning = state == CivProtocol.TunerTune;
            }
            else
            {
                _logger.LogWarning("[CivRadioController] Set tuner {State} was not acknowledged", state);
            }
        }

        // -- RF output power set (CI-V 14 0A) ----------------------------------

        /// <summary>
        /// Read RF output power (command 14 0A). The reply carries a 0–255 level
        /// as two big-endian BCD bytes (same form as the 15-family meters); we
        /// return it as a 0–100 % value. -1 on any miss.
        /// </summary>
        public async Task<int> GetRfPowerPercentAsync(CancellationToken cancellationToken = default)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSetLevel, CivProtocol.SubRfPower);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdSetLevel, cancellationToken: cancellationToken);
            // Reply body is 14 0A <d1> <d2>: Data = [0A, d1, d2].
            if (reply == null || reply.Cmd != CivProtocol.CmdSetLevel
                || reply.Data.Length < 3 || reply.Data[0] != CivProtocol.SubRfPower)
                return -1;
            int level = CivProtocol.BcdByte(reply.Data[1]) * 100 + CivProtocol.BcdByte(reply.Data[2]);
            return (int)Math.Round(Math.Clamp(level, 0, 255) * 100.0 / 255.0);
        }

        /// <summary>
        /// Set RF output power (command 14 0A). The 0–100 % level is scaled to the
        /// radio's 0–255 range and sent as two big-endian BCD bytes.
        /// </summary>
        public async Task SetRfPowerPercentAsync(int percent, CancellationToken cancellationToken = default)
        {
            int level = (int)Math.Round(Math.Clamp(percent, 0, 100) * 255.0 / 100.0);
            byte d1 = (byte)(level / 100);                    // 0–2: a single BCD digit is its own value
            int rem = level % 100;
            byte d2 = (byte)(((rem / 10) << 4) | (rem % 10)); // packed BCD of the low two digits
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSetLevel, CivProtocol.SubRfPower, d1, d2);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] Set RF power {Percent}% (level {Level}) was not acknowledged",
                    percent, level);
        }

        // -- AF (volume) level (CI-V 14 01) ------------------------------------
        // Receiver-wide 0–255 audio level; same 14-family form as RF power/PBT.

        public Task<int> GetAfGainAsync(CancellationToken cancellationToken = default)
            => ReadLevel14Async(CivProtocol.SubAfGain, cancellationToken);

        public Task SetAfGainAsync(int value, CancellationToken cancellationToken = default)
            => WriteLevel14Async(CivProtocol.SubAfGain, value, "AF gain", cancellationToken);

        // -- Twin PBT (CI-V 14 07 / 14 08) -------------------------------------
        // Same wire form as RF power (14 0A): two big-endian BCD bytes 00 00–02 55
        // where 01 28 (=128) is centre. Read and write share one helper each.

        public Task<int> GetPbtInnerAsync(CancellationToken cancellationToken = default)
            => ReadLevel14Async(CivProtocol.SubPbtInner, cancellationToken);

        public Task SetPbtInnerAsync(int value, CancellationToken cancellationToken = default)
            => WriteLevel14Async(CivProtocol.SubPbtInner, value, "PBT inner", cancellationToken);

        public Task<int> GetPbtOuterAsync(CancellationToken cancellationToken = default)
            => ReadLevel14Async(CivProtocol.SubPbtOuter, cancellationToken);

        public Task SetPbtOuterAsync(int value, CancellationToken cancellationToken = default)
            => WriteLevel14Async(CivProtocol.SubPbtOuter, value, "PBT outer", cancellationToken);

        /// <summary>
        /// Read a 14-family level (command 14 &lt;sub&gt;). The reply carries a
        /// 0–255 value as two big-endian BCD bytes. Returns -1 on any miss.
        /// </summary>
        private async Task<int> ReadLevel14Async(byte sub, CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSetLevel, sub);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdSetLevel, cancellationToken: cancellationToken);
            // Reply body is 14 <sub> <d1> <d2>: Data = [sub, d1, d2].
            if (reply == null || reply.Cmd != CivProtocol.CmdSetLevel
                || reply.Data.Length < 3 || reply.Data[0] != sub)
                return -1;
            return CivProtocol.BcdByte(reply.Data[1]) * 100 + CivProtocol.BcdByte(reply.Data[2]);
        }

        /// <summary>
        /// Write a 14-family level (command 14 &lt;sub&gt;) as two big-endian BCD
        /// bytes. The value is clamped to 0–255.
        /// </summary>
        private async Task WriteLevel14Async(byte sub, int value, string what, CancellationToken cancellationToken)
        {
            int level = Math.Clamp(value, 0, 255);
            byte d1 = (byte)(level / 100);                    // 0–2: a single BCD digit is its own value
            int rem = level % 100;
            byte d2 = (byte)(((rem / 10) << 4) | (rem % 10)); // packed BCD of the low two digits
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSetLevel, sub, d1, d2);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] Set {What} to {Level} was not acknowledged", what, level);
        }

        // -- RX controls (receiver-wide) ---------------------------------------
        // 14-family levels reuse the shared BCD helpers; the 16-family functions
        // and the attenuator get their own small read/write helpers below.

        public Task<int> GetRfGainAsync(CancellationToken ct = default) => ReadLevel14Async(CivProtocol.SubRfGain, ct);
        public Task SetRfGainAsync(int value, CancellationToken ct = default) => WriteLevel14Async(CivProtocol.SubRfGain, value, "RF gain", ct);
        public Task<int> GetSquelchAsync(CancellationToken ct = default) => ReadLevel14Async(CivProtocol.SubSquelch, ct);
        public Task SetSquelchAsync(int value, CancellationToken ct = default) => WriteLevel14Async(CivProtocol.SubSquelch, value, "squelch", ct);
        public Task<int> GetNrLevelAsync(CancellationToken ct = default) => ReadLevel14Async(CivProtocol.SubNrLevel, ct);
        public Task SetNrLevelAsync(int value, CancellationToken ct = default) => WriteLevel14Async(CivProtocol.SubNrLevel, value, "NR level", ct);
        public Task<int> GetNbLevelAsync(CancellationToken ct = default) => ReadLevel14Async(CivProtocol.SubNbLevel, ct);
        public Task SetNbLevelAsync(int value, CancellationToken ct = default) => WriteLevel14Async(CivProtocol.SubNbLevel, value, "NB level", ct);
        public Task<int> GetNotchPositionAsync(CancellationToken ct = default) => ReadLevel14Async(CivProtocol.SubNotchPos, ct);
        public Task SetNotchPositionAsync(int value, CancellationToken ct = default) => WriteLevel14Async(CivProtocol.SubNotchPos, value, "notch position", ct);

        public Task<int> GetPreampAsync(CancellationToken ct = default) => ReadFunc16Async(CivProtocol.SubPreamp, ct);
        public Task SetPreampAsync(int value, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubPreamp, Math.Clamp(value, 0, 2), "preamp", ct);
        public Task<int> GetAgcAsync(CancellationToken ct = default) => ReadFunc16Async(CivProtocol.SubAgc, ct);
        public Task SetAgcAsync(int value, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubAgc, Math.Clamp(value, 1, 3), "AGC", ct);

        public async Task<bool> GetNoiseBlankerAsync(CancellationToken ct = default) => await ReadFunc16Async(CivProtocol.SubNoiseBlanker, ct) == 1;
        public Task SetNoiseBlankerAsync(bool on, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubNoiseBlanker, on ? 1 : 0, "noise blanker", ct);
        public async Task<bool> GetNoiseReductionAsync(CancellationToken ct = default) => await ReadFunc16Async(CivProtocol.SubNoiseReduction, ct) == 1;
        public Task SetNoiseReductionAsync(bool on, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubNoiseReduction, on ? 1 : 0, "noise reduction", ct);
        public async Task<bool> GetAutoNotchAsync(CancellationToken ct = default) => await ReadFunc16Async(CivProtocol.SubAutoNotch, ct) == 1;
        public Task SetAutoNotchAsync(bool on, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubAutoNotch, on ? 1 : 0, "auto notch", ct);
        public async Task<bool> GetManualNotchAsync(CancellationToken ct = default) => await ReadFunc16Async(CivProtocol.SubManualNotch, ct) == 1;
        public Task SetManualNotchAsync(bool on, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubManualNotch, on ? 1 : 0, "manual notch", ct);
        public Task<int> GetManualNotchWidthAsync(CancellationToken ct = default) => ReadFunc16Async(CivProtocol.SubManualNotchWidth, ct);
        public Task SetManualNotchWidthAsync(int value, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubManualNotchWidth, Math.Clamp(value, 0, 2), "manual notch width", ct);
        public Task<int> GetIfFilterShapeAsync(CancellationToken ct = default) => ReadFunc16Async(CivProtocol.SubIfFilterShape, ct);
        public Task SetIfFilterShapeAsync(int value, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubIfFilterShape, Math.Clamp(value, 0, 1), "IF filter shape", ct);

        // -- IF passband filter width + FIL slot (CI-V 1A 03 / 26) -------------
        //
        // Width (1A 03) is a single BCD code whose Hz meaning depends on the
        // current mode group — FilterWidthCodec is the one place that mapping
        // lives (mirrored in wwwroot/js/ui/ic7300-if-width.js for the dropdown).
        // The command is receiver-wide, so it reflects the operating VFO's mode;
        // we interpret the code using the requested VFO's own mode group.
        //
        // The FIL slot (1/2/3) rides in the command 26 mode frame, so selecting
        // a slot is a re-send of the current mode/data with the filter byte
        // changed — the mirror of SetModeAsync, which preserves the filter.

        public async Task<int> GetIfFilterWidthHzAsync(RadioVfo vfo, CancellationToken ct = default)
        {
            var group = await ReadModeGroupAsync(vfo, ct);
            if (group == FilterWidthCodec.Group.None)
                return -1;                                   // FM / unknown — no adjustable width
            int code = await ReadMenuByteAsync(CivProtocol.SubIfWidth, ct);
            if (code < 0) return -1;
            int hz = FilterWidthCodec.CodeToHz(group, code);
            if (hz < 0) return -1;
            if (vfo == RadioVfo.A) _state.IfWidthA = hz.ToString(); else _state.IfWidthB = hz.ToString();
            return hz;
        }

        public async Task SetIfFilterWidthHzAsync(RadioVfo vfo, int hz, CancellationToken ct = default)
        {
            var group = await ReadModeGroupAsync(vfo, ct);
            if (group == FilterWidthCodec.Group.None)
            {
                _logger.LogWarning("[CivRadioController] IF width not adjustable in the current mode — ignored");
                return;
            }
            int code = FilterWidthCodec.HzToCode(group, hz);
            byte bcd = (byte)(((code / 10) << 4) | (code % 10));   // one BCD digit-pair; code ≤ 49
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdMenu, CivProtocol.SubIfWidth, bcd);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
            {
                _logger.LogWarning("[CivRadioController] Set IF width {Hz} Hz (code {Code}) was not acknowledged", hz, code);
                return;
            }
            int snapped = FilterWidthCodec.CodeToHz(group, code);
            if (vfo == RadioVfo.A) _state.IfWidthA = snapped.ToString(); else _state.IfWidthB = snapped.ToString();
        }

        public async Task<int> GetSelectedFilterAsync(RadioVfo vfo, CancellationToken ct = default)
        {
            var m = await ReadVfoModeRawAsync(SelectorFor(vfo), ct);
            if (!m.ok || m.filter is < 0x01 or > 0x03) return -1;
            if (vfo == RadioVfo.A) _state.SelectedFilterA = m.filter.ToString(); else _state.SelectedFilterB = m.filter.ToString();
            return m.filter;
        }

        public async Task SetSelectedFilterAsync(RadioVfo vfo, int fil, CancellationToken ct = default)
        {
            if (fil is < 1 or > 3)
            {
                _logger.LogWarning("[CivRadioController] Invalid filter slot {Fil} — ignored", fil);
                return;
            }
            byte sel = SelectorFor(vfo);
            var cur = await ReadVfoModeRawAsync(sel, ct);
            if (!cur.ok)
            {
                _logger.LogWarning("[CivRadioController] Could not read current mode to change filter — ignored");
                return;
            }
            // Re-send command 26 with mode/data preserved, only the filter byte changed.
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdVfoMode, sel, cur.mode, cur.data, (byte)fil);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
            {
                _logger.LogWarning("[CivRadioController] Select filter FIL{Fil} (26) was not acknowledged", fil);
                return;
            }
            if (vfo == RadioVfo.A) _state.SelectedFilterA = fil.ToString(); else _state.SelectedFilterB = fil.ToString();
        }

        /// <summary>Read the given VFO's mode and map it to its filter-width group. None on a miss.</summary>
        private async Task<FilterWidthCodec.Group> ReadModeGroupAsync(RadioVfo vfo, CancellationToken ct)
        {
            var m = await ReadVfoModeRawAsync(SelectorFor(vfo), ct);
            return m.ok ? FilterWidthCodec.GroupForModeByte(m.mode) : FilterWidthCodec.Group.None;
        }

        /// <summary>Read a one-byte menu value (1A &lt;sub&gt;) as BCD. Reply is 1A &lt;sub&gt; &lt;byte&gt;. -1 on a miss.</summary>
        private async Task<int> ReadMenuByteAsync(byte sub, CancellationToken ct)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdMenu, sub);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdMenu, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.CmdMenu
                || reply.Data.Length < 2 || reply.Data[0] != sub)
                return -1;
            return CivProtocol.BcdByte(reply.Data[1]);
        }

        // -- RX Tone Control: HPF/LPF audio filter (CI-V 1A 05 <item>) ---------
        //
        // The RX high-pass (low-cut) and low-pass (high-cut) audio filter edges
        // are per-mode menu items in the 1A 05 family. Which item applies is
        // decided by the requested VFO's current mode (SSB/AM/FM/CW/RTTY each own
        // one); SSB-DATA has no Tone Control on the radio, so we report it as
        // unavailable rather than writing a menu item that would be ignored.
        // The wire data is two BCD bytes HH LL; RxToneCodec is the one place the
        // code↔Hz mapping and Through sentinel (0 Hz) live.

        public async Task<(int hpfHz, int lpfHz)> GetRxFilterAsync(RadioVfo vfo, CancellationToken ct = default)
        {
            byte item = await RxToneItemForVfoAsync(vfo, ct);
            if (item == RxToneUnavailable) return (-1, -1);

            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdMenu, CivProtocol.SubRxTone, 0x00, item);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdMenu, cancellationToken: ct);
            // Reply body: 1A 05 00 <item> HH LL → Data = [05, 00, item, HH, LL].
            if (reply == null || reply.Cmd != CivProtocol.CmdMenu
                || reply.Data.Length < 5 || reply.Data[0] != CivProtocol.SubRxTone
                || reply.Data[2] != item)
                return (-1, -1);
            int hpf = RxToneCodec.HpfCodeToHz(CivProtocol.BcdByte(reply.Data[3]));
            int lpf = RxToneCodec.LpfCodeToHz(CivProtocol.BcdByte(reply.Data[4]));
            return (hpf, lpf);
        }

        public async Task SetRxFilterAsync(RadioVfo vfo, int hpfHz, int lpfHz, CancellationToken ct = default)
        {
            byte item = await RxToneItemForVfoAsync(vfo, ct);
            if (item == RxToneUnavailable)
            {
                _logger.LogWarning("[CivRadioController] RX Tone Control not available in the current mode — ignored");
                return;
            }

            int hpfCode = RxToneCodec.HpfHzToCode(hpfHz);
            int lpfCode = RxToneCodec.LpfHzToCode(lpfHz);
            byte hh = (byte)(((hpfCode / 10) << 4) | (hpfCode % 10));   // BCD, code ≤ 20
            byte ll = (byte)(((lpfCode / 10) << 4) | (lpfCode % 10));   // BCD, code ≤ 25
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdMenu, CivProtocol.SubRxTone, 0x00, item, hh, ll);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] Set RX filter HPF {Hpf} / LPF {Lpf} Hz was not acknowledged", hpfHz, lpfHz);
        }

        /// <summary>Sentinel item byte meaning "no RX Tone Control in this mode".</summary>
        private const byte RxToneUnavailable = 0xFF;

        /// <summary>
        /// Map the requested VFO's current mode to its 1A 05 Tone-Control item
        /// byte (the literal BCD menu number). SSB-DATA and any unreadable/unknown
        /// mode yield <see cref="RxToneUnavailable"/>.
        /// </summary>
        private async Task<byte> RxToneItemForVfoAsync(RadioVfo vfo, CancellationToken ct)
        {
            var m = await ReadVfoModeRawAsync(SelectorFor(vfo), ct);
            if (!m.ok) return RxToneUnavailable;
            return m.mode switch
            {
                0x00 or 0x01 => m.data != 0 ? RxToneUnavailable : (byte)0x01, // LSB/USB → SSB (not DATA)
                0x02 => 0x04,                                                  // AM
                0x05 => 0x07,                                                  // FM
                0x03 or 0x07 => 0x10,                                          // CW / CW-R
                0x04 or 0x08 => 0x11,                                          // RTTY / RTTY-R
                _ => RxToneUnavailable,
            };
        }

        // Single source of truth for the 1A 05 HPF/LPF code ↔ Hz mapping. The
        // radio codes each edge in one BCD byte; we surface Hz with 0 = Through
        // (no filtering) as the natural sentinel for both edges.
        //   HPF: code 0 = Through, 1–20 = 100–2000 Hz  (×100, low-cut)
        //   LPF: code 25 = Through, 5–24 = 500–2400 Hz (×100, high-cut)
        private static class RxToneCodec
        {
            public static int HpfCodeToHz(int code)
                => code <= 0 ? 0 : Math.Clamp(code, 1, 20) * 100;

            public static int HpfHzToCode(int hz)
                => hz <= 0 ? 0 : Math.Clamp((int)Math.Round(hz / 100.0), 1, 20);

            public static int LpfCodeToHz(int code)
                => code >= 25 ? 0 : (code < 5 ? 0 : Math.Clamp(code, 5, 24) * 100);

            public static int LpfHzToCode(int hz)
                => hz <= 0 ? 25 : Math.Clamp((int)Math.Round(hz / 100.0), 5, 24);
        }

        // Single source of truth for the CI-V 1A 03 width code ↔ Hz mapping,
        // per mode group. The IC-7300 packs the width into one BCD byte whose
        // meaning depends on the mode: SSB/CW and RTTY share a 50 Hz-stepped low
        // range (codes 0–9 → 50–500 Hz) then a 100 Hz-stepped high range from
        // code 10 (RTTY stops at 2700 Hz, SSB/CW at 3600 Hz); AM is a flat
        // 200 Hz step (codes 0–49 → 200 Hz–10 kHz). FM has no adjustable width.
        private static class FilterWidthCodec
        {
            public enum Group { None, SsbCw, Rtty, Am }

            private static readonly int[] SsbCwHz = BuildStepped(41);  // codes 0..40 → 50..3600
            private static readonly int[] RttyHz  = BuildStepped(32);  // codes 0..31 → 50..2700
            private static readonly int[] AmHz    = BuildAm();          // codes 0..49 → 200..10000

            // Shared SSB/CW/RTTY curve: 0–9 → (c+1)*50, else 600+(c-10)*100.
            private static int[] BuildStepped(int count)
            {
                var a = new int[count];
                for (int c = 0; c < count; c++) a[c] = c <= 9 ? (c + 1) * 50 : 600 + (c - 10) * 100;
                return a;
            }
            private static int[] BuildAm()
            {
                var a = new int[50];
                for (int c = 0; c < 50; c++) a[c] = 200 + c * 200;
                return a;
            }

            private static int[]? Table(Group g) => g switch
            {
                Group.SsbCw => SsbCwHz,
                Group.Rtty  => RttyHz,
                Group.Am    => AmHz,
                _ => null,
            };

            public static Group GroupForModeByte(byte mode) => mode switch
            {
                0x00 or 0x01 or 0x03 or 0x07 => Group.SsbCw,   // LSB/USB/CW/CW-R (+DATA)
                0x04 or 0x08 => Group.Rtty,                    // RTTY/RTTY-R
                0x02 => Group.Am,                              // AM
                _ => Group.None,                               // FM (0x05) / unknown
            };

            /// <summary>Code → Hz for the group; -1 if the code is out of range or the group has no width.</summary>
            public static int CodeToHz(Group g, int code)
            {
                var t = Table(g);
                return (t == null || code < 0 || code >= t.Length) ? -1 : t[code];
            }

            /// <summary>Nearest valid code for the requested Hz within the group (0 if the group has no width).</summary>
            public static int HzToCode(Group g, int hz)
            {
                var t = Table(g);
                if (t == null) return 0;
                int best = 0, bestErr = int.MaxValue;
                for (int c = 0; c < t.Length; c++)
                {
                    int err = Math.Abs(t[c] - hz);
                    if (err < bestErr) { bestErr = err; best = c; }
                }
                return best;
            }
        }

        /// <summary>Read a 16-family function (16 &lt;sub&gt;). Reply is 16 &lt;sub&gt; &lt;val&gt;. -1 on a miss.</summary>
        private async Task<int> ReadFunc16Async(byte sub, CancellationToken ct)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSetFunc, sub);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdSetFunc, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.CmdSetFunc
                || reply.Data.Length < 2 || reply.Data[0] != sub)
                return -1;
            return reply.Data[1];
        }

        /// <summary>Write a 16-family function (16 &lt;sub&gt; &lt;val&gt;) and expect an ack.</summary>
        private async Task WriteFunc16Async(byte sub, int value, string what, CancellationToken ct)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSetFunc, sub, (byte)value);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] Set {What} to {Value} was not acknowledged", what, value);
        }

        public async Task<bool> GetAttenuatorAsync(CancellationToken ct = default)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, CivProtocol.CmdAttenuator);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdAttenuator, cancellationToken: ct);
            // Reply body: 11 <val> → Data = [val]; 0x20 = 20 dB on.
            if (reply == null || reply.Cmd != CivProtocol.CmdAttenuator || reply.Data.Length < 1)
                return false;
            return reply.Data[0] == CivProtocol.AttOn20dB;
        }

        public async Task SetAttenuatorAsync(bool on, CancellationToken ct = default)
        {
            byte val = on ? CivProtocol.AttOn20dB : CivProtocol.AttOff;
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdAttenuator, val);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] Set attenuator {State} was not acknowledged", on ? "20 dB" : "off");
        }

        // -- CW keyer (CI-V 17 / 14 0C / 14 09 / 14 0F / 16 47) ----------------
        //
        // The memory-keyer send (17) transmits ASCII characters as Morse; the
        // settings ride the shared 14 (level) and 16 (function) helpers. Speed,
        // pitch and break-in delay convert between the operator's natural units
        // (WPM / Hz / dots) and the radio's 0–255 code — CwKeyerCodec owns those
        // linear mappings. These are the IC-7300 equivalents of the Yaesu
        // KY/KS/KP/BI/SD commands the inherited panel used to drive.

        // Characters the IC-7300 memory keyer accepts (17 <ASCII…>). Each keyed
        // byte IS the character's ASCII code; 0x5E ('^') marks "no inter-char
        // space" and is passed through verbatim if the caller includes it.
        private static bool IsCwSendable(char c) =>
            (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
            c == ' ' || c == '/' || c == '?' || c == '.' || c == '-' || c == ',' ||
            c == ':' || c == '\'' || c == '(' || c == ')' || c == '=' || c == '+' ||
            c == '"' || c == '@' || c == '^';

        public async Task<string> SendCwMessageAsync(string message, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(message)) return "";
            // Keep only sendable characters, cap at the radio's 30-char limit.
            var clean = new string((message ?? "").Where(IsCwSendable).Take(30).ToArray());
            if (clean.Length == 0) return "";

            // Body = 17 followed by one ASCII byte per character.
            var body = new byte[1 + clean.Length];
            body[0] = CivProtocol.CmdCwSend;
            for (int i = 0; i < clean.Length; i++) body[i + 1] = (byte)clean[i];
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, body);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] CW send '{Msg}' was not acknowledged", clean);
            return clean;
        }

        public async Task StopCwAsync(CancellationToken ct = default)
        {
            // 17 FF aborts a message already keying.
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdCwSend, CivProtocol.CwStop);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] CW stop (17 FF) was not acknowledged");
        }

        public async Task<int> GetCwSpeedWpmAsync(CancellationToken ct = default)
        {
            int code = await ReadLevel14Async(CivProtocol.SubCwSpeed, ct);
            return code < 0 ? -1 : CwKeyerCodec.CodeToWpm(code);
        }

        public Task SetCwSpeedWpmAsync(int wpm, CancellationToken ct = default)
            => WriteLevel14Async(CivProtocol.SubCwSpeed, CwKeyerCodec.WpmToCode(wpm), "CW speed", ct);

        public async Task<int> GetCwPitchHzAsync(CancellationToken ct = default)
        {
            int code = await ReadLevel14Async(CivProtocol.SubCwPitch, ct);
            return code < 0 ? -1 : CwKeyerCodec.CodeToPitchHz(code);
        }

        public Task SetCwPitchHzAsync(int hz, CancellationToken ct = default)
            => WriteLevel14Async(CivProtocol.SubCwPitch, CwKeyerCodec.PitchHzToCode(hz), "CW pitch", ct);

        public async Task<double> GetCwBreakInDelayDotsAsync(CancellationToken ct = default)
        {
            int code = await ReadLevel14Async(CivProtocol.SubCwBreakInDelay, ct);
            return code < 0 ? -1 : CwKeyerCodec.CodeToDots(code);
        }

        public Task SetCwBreakInDelayDotsAsync(double dots, CancellationToken ct = default)
            => WriteLevel14Async(CivProtocol.SubCwBreakInDelay, CwKeyerCodec.DotsToCode(dots), "CW break-in delay", ct);

        public Task<int> GetCwBreakInAsync(CancellationToken ct = default)
            => ReadFunc16Async(CivProtocol.SubCwBreakIn, ct);

        public Task SetCwBreakInAsync(int mode, CancellationToken ct = default)
            => WriteFunc16Async(CivProtocol.SubCwBreakIn, Math.Clamp(mode, 0, 2), "CW break-in", ct);

        // Linear code (0–255) ↔ operator-unit mappings for the three CW levels.
        //   Speed : 0–255 = 6–48 WPM
        //   Pitch : 0–255 = 300–900 Hz
        //   Delay : 0–255 = 2.0–13.0 dots
        private static class CwKeyerCodec
        {
            public static int WpmToCode(int wpm)
                => (int)Math.Round((Math.Clamp(wpm, 6, 48) - 6) / 42.0 * 255.0);
            public static int CodeToWpm(int code)
                => (int)Math.Round(6 + Math.Clamp(code, 0, 255) / 255.0 * 42.0);

            public static int PitchHzToCode(int hz)
                => (int)Math.Round((Math.Clamp(hz, 300, 900) - 300) / 600.0 * 255.0);
            public static int CodeToPitchHz(int code)
                => (int)Math.Round(300 + Math.Clamp(code, 0, 255) / 255.0 * 600.0);

            public static int DotsToCode(double dots)
                => (int)Math.Round((Math.Clamp(dots, 2.0, 13.0) - 2.0) / 11.0 * 255.0);
            public static double CodeToDots(int code)
                => Math.Round((2.0 + Math.Clamp(code, 0, 255) / 255.0 * 11.0) * 10.0) / 10.0;
        }

        // -- VFO select / exchange / split (Phase 3 block 5) -------------------

        public async Task SelectVfoAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
        {
            // Command 07 00/01 — set-only; no read of the active VFO exists, so
            // we mirror the change into RadioStateService.ActiveVfo, which is the
            // source of truth SelectorFor / ActiveVfo read back.
            byte v = vfo == RadioVfo.B ? CivProtocol.VfoSelectB : CivProtocol.VfoSelectA;
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSelectVfo, v);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
                _state.ActiveVfo = vfo == RadioVfo.B ? 1 : 0;
            else
                _logger.LogWarning("[CivRadioController] Select VFO {Vfo} was not acknowledged", vfo);
        }

        public async Task ExchangeVfosAsync(CancellationToken cancellationToken = default)
        {
            // Command 07 B0 — swap A↔B; the selected VFO letter is unchanged, so
            // ActiveVfo stays put. Swap the cached freq/mode for a snappy UI; the
            // poll re-reads both within a couple of loops regardless.
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSelectVfo, CivProtocol.VfoExchange);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
            {
                (_state.FrequencyA, _state.FrequencyB) = (_state.FrequencyB, _state.FrequencyA);
                (_state.ModeA, _state.ModeB) = (_state.ModeB, _state.ModeA);
            }
            else
            {
                _logger.LogWarning("[CivRadioController] Exchange VFOs was not acknowledged");
            }
        }

        public async Task EqualizeVfosAsync(CancellationToken cancellationToken = default)
        {
            // Command 07 A0 — make both VFOs equal (the unselected takes the
            // selected VFO's contents). Mirror selected → other in cache.
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSelectVfo, CivProtocol.VfoEqualize);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
            {
                if (ActiveVfo == RadioVfo.A) { _state.FrequencyB = _state.FrequencyA; _state.ModeB = _state.ModeA; }
                else                          { _state.FrequencyA = _state.FrequencyB; _state.ModeA = _state.ModeB; }
            }
            else
            {
                _logger.LogWarning("[CivRadioController] Equalize VFOs was not acknowledged");
            }
        }

        public async Task<bool> GetSplitAsync(CancellationToken cancellationToken = default)
        {
            // Command 0F with no data reads split; reply 0F <00=off|01=on>.
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, CivProtocol.CmdSplit);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdSplit, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.CmdSplit && reply.Data.Length >= 1)
                return reply.Data[0] != 0;
            return _state.SplitMode > 0; // keep last known on a miss
        }

        public async Task SetSplitAsync(bool on, CancellationToken cancellationToken = default)
        {
            byte v = on ? CivProtocol.SplitOn : CivProtocol.SplitOff;
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSplit, v);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
            {
                // Preserve a UI-set quick-split (2); only sync the on/off axis.
                if (on) { if (_state.SplitMode == 0) _state.SplitMode = 1; }
                else _state.SplitMode = 0;
            }
            else
            {
                _logger.LogWarning("[CivRadioController] Set split {State} was not acknowledged", on ? "ON" : "OFF");
            }
        }

        // -- Power on/off (Phase 3 block 7, command 18) ------------------------

        public Task SetPowerAsync(bool on, CancellationToken cancellationToken = default)
            => on ? PowerOnAsync(cancellationToken) : PowerOffAsync(cancellationToken);

        /// <summary>
        /// Power the radio down (18 00). The radio ACKs FB then powers off, so
        /// the ack is best-effort — a null reply here is expected, not an error.
        /// We flag RadioPowerOn false immediately; the poll loop's own
        /// miss-detection then drops the link naturally once the radio stops
        /// answering (no forced close from this thread, which would race the
        /// poll's in-flight transaction).
        /// </summary>
        private async Task PowerOffAsync(CancellationToken ct)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdPower, CivProtocol.PowerOff);
            await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
            _state.RadioPowerOn = false;
            _logger.LogInformation("[CivRadioController] Power OFF (18 00) sent");
        }

        /// <summary>
        /// Power the radio up (18 01). The asleep CI-V circuit is woken with a
        /// burst of 0xFE bytes sized to the baud rate, sent in one write with the
        /// frame appended so no inter-byte gap lets it doze off again. Over USB
        /// the port is unpowered while the radio is off, so this can only succeed
        /// over a separately-powered CI-V remote-jack link; on USB the front-panel
        /// switch is the only way up. On success the poll loop reconnects and
        /// re-identifies once the radio's bus starts answering.
        /// </summary>
        private async Task PowerOnAsync(CancellationToken ct)
        {
            var settings = await _settings.GetSettingsAsync();

            if (!_bus.IsOpen && !await _bus.OpenAsync(settings.SerialPort, settings.BaudRate))
            {
                _logger.LogWarning("[CivRadioController] Power ON: CI-V port unavailable — the USB port is unpowered while the radio is off");
                return;
            }

            int wake = CivProtocol.PowerOnWakePreambleCount(settings.BaudRate);
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdPower, CivProtocol.PowerOn);
            var burst = new byte[wake + frame.Length];
            for (int i = 0; i < wake; i++) burst[i] = CivProtocol.Preamble; // 0xFE
            Array.Copy(frame, 0, burst, wake, frame.Length);

            await _bus.SendRawAsync(burst, ct);
            _state.RadioPowerOn = true;
            _logger.LogInformation("[CivRadioController] Power ON (18 01) sent with {Wake}× FE wake preamble", wake);
        }

        // -- Radio memory channels (CI-V 1A 00) --------------------------------
        //
        // The 1A 00 content frame carries 47 data bytes after the channel number:
        //   [chHi chLo] (2)  channel number, BCD 00 01–00 99
        //   [split/sel] (1)  hi nibble SPLIT (0/1), lo nibble SELECT (0=off)
        //   [RX block]  (14) freq(5 BCD LE) mode(1) filter(1) data/tone(1)
        //                    rptrTone(3) toneSql(3)
        //   [TX block]  (14) same layout, used when split is on
        //   [name]      (16) ASCII, space-padded
        // Tone frequency is 3 BCD bytes 0-0 / 100-10 Hz / 1-0.1 Hz; 88.5 Hz
        // (00 08 85) is the radio default and inert while the tone type is OFF,
        // so it is a safe filler for channels we write with no tone.

        private const int MemoryNameLength = 16;
        private static readonly byte[] DefaultToneFreq = { 0x00, 0x08, 0x85 }; // 88.5 Hz

        public async Task<RadioMemoryChannel?> ReadMemoryChannelAsync(int channel, CancellationToken cancellationToken = default)
        {
            if (channel is < 1 or > 99) return null;
            byte chLo = (byte)(((channel / 10) << 4) | (channel % 10));

            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdMenu, CivProtocol.SubMemoryChannel, 0x00, chLo);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdMenu, cancellationToken: cancellationToken);

            // Reply body: 1A 00 <chHi> <chLo> [<content…>] → Data = [00, chHi, chLo, …].
            if (reply == null || reply.Cmd != CivProtocol.CmdMenu
                || reply.Data.Length < 3 || reply.Data[0] != CivProtocol.SubMemoryChannel)
                return null; // transaction miss — distinct from an empty channel

            var d = reply.Data;

            // A blank channel echoes just the number with no content payload.
            if (d.Length < 18)
                return new RadioMemoryChannel { Channel = channel, IsEmpty = true };

            int p = 3;                                          // skip sub + 2 ch bytes
            byte split = d[p]; p += 1;
            long rxFreq = CivProtocol.DecodeBcd(d.AsSpan(p, 5)); p += 5;
            byte rxMode = d[p]; p += 1;
            byte rxFilter = d[p]; p += 1;
            byte rxDataTone = d[p]; p += 1;
            p += 6;                                             // rptr tone (3) + tone sql (3)

            long txFreq = rxFreq; byte txMode = rxMode; byte txDataTone = rxDataTone;
            if (d.Length >= 32)                                 // TX block present
            {
                txFreq = CivProtocol.DecodeBcd(d.AsSpan(p, 5)); p += 5;
                txMode = d[p]; p += 1;
                p += 1;                                         // TX filter (unused by the app)
                txDataTone = d[p]; p += 1;
                p += 6;                                         // TX rptr tone (3) + tone sql (3)
            }

            string name = "";
            if (d.Length >= p + MemoryNameLength)
                name = DecodeName(d.AsSpan(p, MemoryNameLength));

            return new RadioMemoryChannel
            {
                Channel       = channel,
                IsEmpty       = false,
                FrequencyHz   = rxFreq,
                Mode          = NameForMode(rxMode, (rxDataTone >> 4) != 0),
                Filter        = rxFilter is >= 1 and <= 3 ? rxFilter : 1,
                SplitOn       = (split >> 4) != 0,
                TxFrequencyHz = txFreq,
                TxMode        = NameForMode(txMode, (txDataTone >> 4) != 0),
                Name          = name,
            };
        }

        public async Task<bool> WriteMemoryChannelAsync(RadioMemoryChannel memory, CancellationToken cancellationToken = default)
        {
            if (memory.Channel is < 1 or > 99) return false;
            byte chLo = (byte)(((memory.Channel / 10) << 4) | (memory.Channel % 10));

            var body = new List<byte>(49)
            {
                CivProtocol.CmdMenu, CivProtocol.SubMemoryChannel,
                0x00, chLo,
                (byte)(memory.SplitOn ? 0x10 : 0x00),          // split hi nibble, select 0
            };
            AppendModeBlock(body, memory.FrequencyHz, memory.Mode, memory.Filter);
            // TX block: mirror RX when not splitting (the manual's recommendation).
            if (memory.SplitOn)
                AppendModeBlock(body, memory.TxFrequencyHz, memory.TxMode, memory.Filter);
            else
                AppendModeBlock(body, memory.FrequencyHz, memory.Mode, memory.Filter);
            body.AddRange(EncodeName(memory.Name));

            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, body.ToArray());
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            bool ok = reply != null && reply.Cmd == CivProtocol.AckOk;
            if (!ok)
                _logger.LogWarning("[CivRadioController] Write memory channel {Ch} was not acknowledged", memory.Channel);
            return ok;
        }

        public async Task<bool> ClearMemoryChannelAsync(int channel, CancellationToken cancellationToken = default)
        {
            if (channel is < 1 or > 99) return false;
            byte chLo = (byte)(((channel / 10) << 4) | (channel % 10));

            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdMenu, CivProtocol.SubMemoryChannel, 0x00, chLo, 0xFF);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            bool ok = reply != null && reply.Cmd == CivProtocol.AckOk;
            if (!ok)
                _logger.LogWarning("[CivRadioController] Clear memory channel {Ch} was not acknowledged", channel);
            return ok;
        }

        // -- Raw command escape hatch (voice macros only) ----------------------
        // See IRadioController.SendRawCommandAsync. The body comes from a user's
        // Custom Command; framing and address stay ours, so a macro chooses the
        // command but never the frame. Set commands answer FB/FA — a read
        // command in a macro answers with data instead and is reported as an
        // unacknowledged send, which is honest: nothing consumes the reply.

        public async Task<bool> SendRawCommandAsync(IReadOnlyList<byte> commandBody, CancellationToken cancellationToken = default)
        {
            if (commandBody == null || commandBody.Count == 0) return false;

            var body = commandBody as byte[] ?? commandBody.ToArray();
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, body);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            bool ok = reply != null && reply.Cmd == CivProtocol.AckOk;
            if (!ok)
                _logger.LogWarning("[CivRadioController] Raw command {Body} was not acknowledged",
                    CivMacroCodec.Describe(body));
            return ok;
        }

        /// <summary>Append a 14-byte RX-or-TX block: freq(5) mode(1) filter(1) data/tone(1) rptrTone(3) toneSql(3).</summary>
        private static void AppendModeBlock(List<byte> body, long freqHz, string mode, int filter)
        {
            body.AddRange(CivProtocol.EncodeBcd(freqHz, 5));
            var (baseByte, data) = ModeNameToIcom.TryGetValue(mode, out var m) ? m : ((byte)0x01, false); // default USB
            byte fil = filter is >= 1 and <= 3 ? (byte)filter : (byte)0x01;
            body.Add(baseByte);
            body.Add(fil);
            body.Add((byte)(data ? 0x10 : 0x00));               // DATA hi nibble, TONE type OFF
            body.AddRange(DefaultToneFreq);                     // repeater tone (inert while OFF)
            body.AddRange(DefaultToneFreq);                     // tone squelch  (inert while OFF)
        }

        /// <summary>16 ASCII bytes, space-padded, disallowed characters mapped to space.</summary>
        private static byte[] EncodeName(string name)
        {
            var bytes = new byte[MemoryNameLength];
            for (int i = 0; i < MemoryNameLength; i++)
            {
                char c = i < name.Length ? name[i] : ' ';
                bytes[i] = c is >= ' ' and <= '~' ? (byte)c : (byte)' ';
            }
            return bytes;
        }

        /// <summary>Decode a fixed-width memory name, trimming trailing spaces and nulls.</summary>
        private static string DecodeName(ReadOnlySpan<byte> raw)
        {
            Span<char> chars = stackalloc char[raw.Length];
            int n = 0;
            foreach (var b in raw)
                chars[n++] = b is >= 0x20 and <= 0x7E ? (char)b : ' ';
            return new string(chars).TrimEnd(' ', '\0');
        }

        // -- Connect / identify -------------------------------------------------

        /// <summary>
        /// Read the transceiver ID (19 00). The reply's From byte is the radio's
        /// real CI-V address; its data byte is the model's default address, which
        /// distinguishes IC-7300 MkII (B6) from the classic IC-7300 (94) and other
        /// Icoms — hence "read the ID, don't hard-code." The query is addressed to
        /// the CI-V broadcast address (0x00) so any Icom answers regardless of its
        /// address — otherwise a classic IC-7300 (94) would ignore a frame sent to
        /// the B6 default and never connect. Returns false when the radio doesn't
        /// answer at all: the port opened but nothing is on the bus (radio powered
        /// off or asleep), which is not a real connection.
        /// </summary>
        private async Task<bool> IdentifyAsync(CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(CivProtocol.BroadcastAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdReadId, CivProtocol.SubReadId);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdReadId, cancellationToken: cancellationToken);
            if (reply == null)
                return false;

            if (reply.From != 0x00)
                _radioAddress = reply.From;

            byte idByte = _radioAddress;
            if (reply.Data.Length >= 2)
                idByte = reply.Data[^1]; // 19 00 <id>

            ModelId = MapModel(idByte);
            _state.RadioModel = ModelId;
            _logger.LogInformation("[CivRadioController] Radio identified: {Model} (CI-V address {Addr:X2})",
                ModelId, _radioAddress);
            return true;
        }

        private static string MapModel(byte idByte) => idByte switch
        {
            0xB6 => "IC-7300MK2",
            0x94 => "IC-7300",
            _ => $"Icom({idByte:X2})",
        };

        // -- Hosted poll loop ---------------------------------------------------

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[CivRadioController] Phase-3 CI-V link starting.");
            int misses = 0;
            long loop = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_bus.IsOpen)
                {
                    bool ok;
                    try
                    {
                        ok = await ConnectAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Connect touches the serial port (open, ID, freq read,
                        // close), any of which can throw while the radio is being
                        // powered off and its USB port vanishes. Swallow it here —
                        // this call sits outside the poll try/catch below, so an
                        // escape would fault the BackgroundService and take the
                        // whole host down. Treat it as a failed connect and retry.
                        _logger.LogWarning(ex, "[CivRadioController] Connect attempt threw — retrying");
                        ok = false;
                    }

                    if (!ok)
                    {
                        await SetConnectedAsync(false);
                        await DelayQuiet(ReconnectDelayMs, stoppingToken);
                        continue;
                    }

                    misses = 0;
                    await SetConnectedAsync(true);
                }

                try
                {
                    // Which VFO the radio is operating on decides how each panel
                    // is addressed: the active VFO rides the proven operating
                    // commands, the other is the "watch" VFO read via 25/26.
                    RadioVfo active = ActiveVfo;
                    RadioVfo other = active == RadioVfo.A ? RadioVfo.B : RadioVfo.A;

                    // Operating frequency (command 03) is the liveness signal —
                    // the only read that counts misses and can drop the link.
                    long hz = await ReadOperatingFrequencyAsync(stoppingToken);
                    if (hz > 0)
                    {
                        misses = 0;
                        // The operating-freq read is the liveness signal, so a good
                        // read means we're connected — regardless of how the port
                        // got opened. It can be opened out-of-band by PowerOnAsync
                        // (IWC's power button sends 18 01 on a port it opens itself),
                        // which skips the reconnect block above and its
                        // SetConnectedAsync(true); without this the init spinner
                        // would spin forever even though control works.
                        if (!_state.IsConnected)
                            await SetConnectedAsync(true);
                        SetVfoFrequency(active, hz); // broadcasts on change
                    }
                    else if (++misses >= MaxConsecutiveReadMisses)
                    {
                        _logger.LogWarning("[CivRadioController] {Misses} consecutive frequency-read misses — dropping link", misses);
                        await _bus.CloseAsync();
                        await SetConnectedAsync(false);
                        continue; // link is down — don't chase further reads
                    }

                    // Watch VFO frequency (command 25 01) — best-effort, every
                    // other loop. A miss (or an unsupported 25) just leaves the
                    // last value; it never affects liveness.
                    if (loop % 2 == 1)
                    {
                        long otherHz = await ReadFrequencyBySelectorAsync(CivProtocol.VfoUnselected, stoppingToken);
                        if (otherHz > 0) SetVfoFrequency(other, otherHz);
                    }

                    // TX/RX status (command 1C 00) every loop — cheap, and it
                    // decides which meters are worth reading. Best-effort: a miss
                    // keeps the last state and never drops the link.
                    bool transmitting = await ReadTransmitAsync(stoppingToken);
                    _state.IsTransmitting = transmitting; // broadcasts on change

                    if (transmitting)
                    {
                        // Transmitting: the S-meter is meaningless; the TX needles
                        // — Po (15 11), SWR (15 12), ALC (15 13), COMP (15 14) and
                        // Id (15 16) — are live. A -1 miss leaves the last value.
                        int po = await ReadMeterAsync(CivProtocol.SubPoMeter, stoppingToken);
                        if (po >= 0) _state.PowerMeter = po;
                        int swr = await ReadMeterAsync(CivProtocol.SubSwrMeter, stoppingToken);
                        if (swr >= 0) _state.SWRMeter = swr;
                        int alc = await ReadMeterAsync(CivProtocol.SubAlcMeter, stoppingToken);
                        if (alc >= 0) _state.ALCMeter = alc;
                        int comp = await ReadMeterAsync(CivProtocol.SubCompMeter, stoppingToken);
                        if (comp >= 0) _state.CompressionMeter = comp;
                        int id = await ReadMeterAsync(CivProtocol.SubIdMeter, stoppingToken);
                        if (id >= 0) _state.IDDMeter = id;
                    }
                    else
                    {
                        // Receiving: S-meter (command 15 02) is the fast-moving
                        // meter. Zero the TX-only needles once so they don't hang
                        // at their last transmit reading after unkey.
                        _state.SMeterA = await ReadSMeterAsync(RadioVfo.A, stoppingToken);
                        if (_state.PowerMeter is not (null or 0)) _state.PowerMeter = 0;
                        if (_state.SWRMeter is not (null or 0)) _state.SWRMeter = 0;
                        if (_state.ALCMeter is not (null or 0)) _state.ALCMeter = 0;
                        if (_state.CompressionMeter is not (null or 0)) _state.CompressionMeter = 0;
                        if (_state.IDDMeter is not (null or 0)) _state.IDDMeter = 0;
                    }

                    // Vd — the PA supply rail (15 15) — is present in both states
                    // (idle voltage on RX, sagging under load on TX), so read it
                    // independently of the TX/RX branch but slowly; it moves
                    // gently and shouldn't crowd out the S-meter.
                    if (loop % SplitPollEveryNLoops == 0)
                    {
                        int vd = await ReadMeterAsync(CivProtocol.SubVdMeter, stoppingToken);
                        if (vd >= 0) _state.VDDMeter = vd;
                    }

                    // Mode (command 26) changes rarely, so poll it less often to
                    // keep the bus free for the meter, and interleave the two
                    // VFOs onto different phases. Skip "?XX" (unmapped) values.
                    if (loop % ModePollEveryNLoops == 0)
                    {
                        var modeName = await GetModeAsync(active, stoppingToken);
                        if (!string.IsNullOrEmpty(modeName) && !modeName.StartsWith('?'))
                            SetVfoMode(active, modeName);
                    }
                    else if (loop % ModePollEveryNLoops == 1)
                    {
                        var modeName = await GetModeAsync(other, stoppingToken);
                        if (!string.IsNullOrEmpty(modeName) && !modeName.StartsWith('?'))
                            SetVfoMode(other, modeName);
                    }

                    // Split state (command 0F) — slow-moving; refresh a few times
                    // a second. Reading only distinguishes on/off, so preserve a
                    // UI-set quick-split (2). In split, the watch VFO transmits.
                    if (loop % SplitPollEveryNLoops == 2)
                    {
                        bool split = await GetSplitAsync(stoppingToken);
                        if (split) { if (_state.SplitMode == 0) _state.SplitMode = 1; }
                        else _state.SplitMode = 0;
                        _state.TxVfo = VfoIndex(split ? other : active);
                    }

                    // AF (volume) level (14 01) — mirror front-panel AF-knob
                    // turns back to the app slider. Receiver-wide and slow, so
                    // ride a spare split-poll phase (~1.5 Hz). A miss leaves the
                    // last value; the state setter only broadcasts on change, so
                    // this is quiet unless the knob actually moved.
                    if (loop % SplitPollEveryNLoops == 3)
                    {
                        int af = await GetAfGainAsync(stoppingToken);
                        if (af >= 0) { _state.AfGainA = af; _state.AfGainB = af; }
                    }

                    // Refresh the cached pseudo-dual flag occasionally so the
                    // scope-broadcast path (on the serial-reader thread) never has
                    // to await the settings store. Cheap and slow-moving.
                    if (loop % SplitPollEveryNLoops == 1)
                    {
                        var s = await _settings.GetSettingsAsync();
                        _pseudoDual = s.PseudoDualReceiverEnabled;
                        _crossBandPeek = s.PseudoDualReceiverEnabled && s.PseudoDualCrossBandEnabled;
                        _peekIntervalMs = Math.Clamp(s.PseudoDualPeekIntervalSeconds, 5, 60) * 1000;
                        _watchZoomIn = string.Equals(s.PseudoDualWatchSpanMode, "ZoomIn", StringComparison.OrdinalIgnoreCase);
                    }

                    // A couple of RX controls per loop, cycling through AGC/
                    // preamp/NB/NR/notch/att/levels so front-panel changes to any
                    // of them make it back to the app within about a second.
                    for (int i = 0; i < RxControlsPerLoop; i++)
                        await PollNextRxControlAsync(stoppingToken);

                    // Cross-band peek: briefly borrow the receiver to refresh a
                    // watch VFO on a different band. Internally rate-limited and a
                    // no-op unless the operator enabled it; never during TX.
                    if (!transmitting)
                        await MaybePeekWatchBandAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CivRadioController] Radio poll failed");
                }

                loop++;
                await DelayQuiet(ScopeAwarePollIntervalMs(), stoppingToken);
            }

            await _bus.CloseAsync();
        }

        private async Task SetConnectedAsync(bool connected)
        {
            _state.IsConnected = connected;
            _state.RadioPowerOn = connected;

            // Leave the frontend's "connecting…" overlay once we have a link, in
            // the same way the Phase 1 stub did.
            AppStatus.InitializationStatus = connected ? "complete" : "initializing";
            try
            {
                await _hubContext.Clients.All.SendAsync("InitializationStatus", AppStatus.InitializationStatus);
            }
            catch
            {
                // No clients connected yet — the value is also polled via
                // /api/cat/status/init.
            }
        }

        // Choose the inter-poll delay based on whether the scope is currently
        // streaming, and — while it is — log the frame-drop rate every ~5 s so the
        // effect is measurable. The scope counts as live if a sweep completed
        // within the last second (a sweep arrives a few times a second when on).
        private int ScopeAwarePollIntervalMs()
        {
            long now = Environment.TickCount64;
            bool scopeStreaming = now - Volatile.Read(ref _lastSweepTicks) < 1000;
            if (!scopeStreaming)
                return PollIntervalMs;

            // Seed the baseline the first time we see the scope live so the first
            // logged window measures a real 5 s, not everything since startup.
            if (_lastScopeLogTicks == 0)
            {
                _lastScopeLogTicks = now;
                _lastLoggedSweeps = _scope.SweepsCompleted;
                _lastLoggedDiscards = _scope.SweepsDiscarded;
            }
            else if (now - _lastScopeLogTicks >= 5000)
            {
                long sweeps = _scope.SweepsCompleted;
                long discards = _scope.SweepsDiscarded;
                long dSweeps = sweeps - _lastLoggedSweeps;
                long dDiscards = discards - _lastLoggedDiscards;
                long attempts = dSweeps + dDiscards;
                if (attempts > 0)
                {
                    _logger.LogDebug(
                        "[CivRadioController] Scope: {Sweeps} sweeps, {Discards} dropped ({Pct:0.0}%) in last {Secs:0.0}s",
                        dSweeps, dDiscards, 100.0 * dDiscards / attempts,
                        (now - _lastScopeLogTicks) / 1000.0);
                }
                _lastScopeLogTicks = now;
                _lastLoggedSweeps = sweeps;
                _lastLoggedDiscards = discards;
            }

            return ScopePollIntervalMs;
        }

        private static async Task DelayQuiet(int ms, CancellationToken ct)
        {
            try { await Task.Delay(ms, ct); }
            catch (OperationCanceledException) { /* shutting down */ }
        }
    }
}
