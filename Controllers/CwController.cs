using Microsoft.AspNetCore.Mvc;
using Icom_Web_Control.Services;
using Icom_Web_Control.Services.Cw;

namespace Icom_Web_Control.Controllers
{
    /// <summary>
    /// The CW reader's HTTP face: start it, stop it, poll it for text.
    ///
    /// Polling rather than a WebSocket on purpose. Decoded CW arrives a few
    /// characters at a time at a handful of characters a second, so a poll
    /// every half second is both cheap and quick enough to read as live, and
    /// it needs none of the reconnection handling a socket would. Audio would
    /// be a different matter, but no audio crosses this boundary - the reader
    /// opens the recording device on the server and only text comes back.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class CwController : ControllerBase
    {
        private readonly CwReaderService _reader;
        private readonly CwQsoLogService _log;
        private readonly CwReaderModeService _readerMode;
        private readonly IRadioController _radio;
        private readonly RadioStateService _state;
        private readonly ILogger<CwController> _logger;

        public CwController(CwReaderService reader,
                            CwQsoLogService log,
                            CwReaderModeService readerMode,
                            IRadioController radio,
                            RadioStateService state,
                            ILogger<CwController> logger)
        {
            _reader = reader;
            _log = log;
            _readerMode = readerMode;
            _radio = radio;
            _state = state;
            _logger = logger;
        }

        [HttpPost("start")]
        public async Task<IActionResult> Start(CancellationToken ct)
        {
            try
            {
                await _reader.StartAsync(ct);
                return Ok(_reader.Snapshot(0));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start the CW reader");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("stop")]
        public async Task<IActionResult> Stop(CancellationToken ct)
        {
            try
            {
                await _reader.StopAsync(ct);
                return Ok(_reader.Snapshot(long.MaxValue));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop the CW reader");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("clear")]
        public IActionResult Clear()
        {
            _reader.ClearText();
            return Ok(_reader.Snapshot(long.MaxValue));
        }

        /// <summary>
        /// Status plus whatever has been decoded since the caller's cursor.
        /// Pass the Cursor from the previous reply; 0 asks for everything the
        /// reader still holds.
        /// </summary>
        [HttpGet("poll")]
        public IActionResult Poll([FromQuery] long since = 0)
            => Ok(_reader.Snapshot(since));

        /// <summary>
        /// Points for the phasor tuning aid since the caller's cursor.
        ///
        /// Separate from poll because it is only wanted while the aid is
        /// visible, and it carries roughly two hundred points a second when it
        /// is. Pass the Cursor from the previous reply; 0 asks for whatever the
        /// ring still holds.
        /// </summary>
        [HttpGet("phasor")]
        public IActionResult Phasor([FromQuery] long since = 0)
            => Ok(_reader.Phasor(since));

        /// <summary>
        /// The passband spectrum for the tuning display.
        ///
        /// No cursor, unlike phasor: this is a picture of the moment rather
        /// than a stream, so a dropped poll costs nothing and the caller never
        /// has to catch up.
        /// </summary>
        [HttpGet("spectrum")]
        public IActionResult Spectrum()
            => Ok(_reader.Spectrum());

        /// <summary>
        /// A draft log entry built from the recent copy and the radio's current
        /// state, for the operator to correct before saving.
        ///
        /// Every field comes back as a ranked list with the reason it was
        /// suggested, not as an answer. The decoder has been measured
        /// reporting full confidence on junk, so a form silently pre-filled
        /// from it would be worse than an empty one - the operator would have
        /// no reason to look at it twice.
        /// </summary>
        [HttpGet("qso/suggest")]
        public async Task<IActionResult> SuggestQso()
        {
            try
            {
                return Ok(await _log.SuggestAsync());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build a QSO suggestion");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>Appends a confirmed QSO to the ADIF log.</summary>
        [HttpPost("qso")]
        public async Task<IActionResult> SaveQso([FromBody] CwQsoSave qso, CancellationToken ct)
        {
            if (qso is null || string.IsNullOrWhiteSpace(qso.Callsign))
                return BadRequest(new { error = "A QSO needs a callsign." });

            try
            {
                return Ok(await _log.SaveAsync(qso, ct));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log a QSO");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Sets the radio up for decoding - CW mode, a narrow filter, APF -
        /// and remembers what to put back.
        /// </summary>
        [HttpPost("readermode/on")]
        public async Task<IActionResult> ReaderModeOn([FromQuery] int? filterHz, CancellationToken ct)
        {
            try
            {
                return Ok(await _readerMode.EnableAsync(filterHz, ct));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enter Reader Mode");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>Puts back the mode, filter and APF Reader Mode changed.</summary>
        [HttpPost("readermode/off")]
        public async Task<IActionResult> ReaderModeOff(CancellationToken ct)
        {
            try
            {
                return Ok(await _readerMode.RestoreAsync(ct));
            }
            catch (Exception ex)
            {
                // Worth saying out loud: a failed restore leaves the operator's
                // radio narrow with APF on, and the message is how they find
                // out rather than wondering why the band went quiet.
                _logger.LogError(ex, "Failed to leave Reader Mode");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("readermode")]
        public IActionResult ReaderModeStatus() => Ok(_readerMode.Status());

        /// <summary>
        /// The recording devices the reader can listen to, for the Settings
        /// picker.
        ///
        /// Here rather than on the settings controller because the list is
        /// WinMM's, and WinMM's device names are what the reader matches on -
        /// keeping the enumeration beside the thing that opens the device
        /// means the picker cannot offer a name the reader would then fail to
        /// find.
        /// </summary>
        [HttpGet("devices")]
        public IActionResult Devices() => Ok(CwAudioDevices.List()
            .Select(d => new { index = d.Index, name = d.Name, channels = d.Channels }));

        /// <summary>
        /// Software zero-in: move VFO A so the tone being copied lands on the
        /// operator's configured CW pitch.
        ///
        /// The Yaesu has a ZI command and does this itself. The IC-7300 has no
        /// equivalent, so the sum is done here - by <c>CwZeroIn</c> in Core, so
        /// that both applications agree about the sign - and the frequency is
        /// set over CI-V.
        ///
        /// <para>
        /// It refuses more often than it acts, and that is the point. The
        /// offset is null whenever the tone detector is not confident or the
        /// correction is larger than <c>CwZeroIn</c>'s ceiling, both of which
        /// usually mean it has locked onto the wrong signal; nudging the VFO on
        /// a bad measurement moves the station the operator was listening to
        /// out of the filter, which is a far worse outcome than a button that
        /// says it cannot help.
        /// </para>
        /// </summary>
        [HttpPost("zin")]
        public async Task<IActionResult> ZeroIn(CancellationToken ct)
        {
            if (!_reader.IsRunning)
                return Ok(new { moved = false, message = "The reader is not running." });
            if (!_radio.IsConnected)
                return Ok(new { moved = false, message = "The radio is not connected." });

            var snap = _reader.Snapshot(long.MaxValue);
            long? offset = snap.ZeroInOffsetHz;

            if (offset is null)
                return Ok(new { moved = false, message =
                    "Not sure enough of the tone to move the VFO. Tune closer by hand." });

            // Below this the tone tracker's own jitter is larger than the
            // correction, so acting would be moving the radio in response to
            // noise. The reader panel uses the same threshold before it says
            // anything about being off pitch.
            if (Math.Abs(offset.Value) < 25)
                return Ok(new { moved = false, message = "Already on pitch." });

            try
            {
                // Read the VFO rather than using the cached frequency. The
                // operator may have turned the dial since the last poll, and
                // adding an offset to a stale reading would put the radio
                // somewhere neither of us asked for.
                long now = await _radio.GetFrequencyHzAsync(RadioVfo.A, ct);
                if (now <= 0)
                    return Ok(new { moved = false, message = "The radio did not report a frequency." });

                long target = now + offset.Value;
                await _radio.SetFrequencyHzAsync(RadioVfo.A, target, ct);
                _state.FrequencyA = target;

                _logger.LogInformation(
                    "CW zero-in: tone {Tone:F0} Hz, pitch {Pitch:F0} Hz, moved {Offset:+#;-#;0} Hz to {Target} Hz",
                    snap.ToneHz, snap.PitchHz, offset.Value, target);

                return Ok(new
                {
                    moved = true,
                    offsetHz = offset.Value,
                    frequencyHz = target,
                    message = $"Moved {offset.Value:+#;-#;0} Hz.",
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CW zero-in failed");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>Where the log and this session's transcript are on disk.</summary>
        [HttpGet("files")]
        public IActionResult Files() => Ok(new
        {
            log = CwQsoLogService.LogPath,
            logExists = System.IO.File.Exists(CwQsoLogService.LogPath),
            transcript = _reader.TranscriptPath,
        });
    }
}
