using Microsoft.AspNetCore.Mvc;
using Icom_Web_Control.Services;
using Icom_Web_Control.Services.Rtty;

namespace Icom_Web_Control.Controllers
{
    /// <summary>
    /// The RTTY tuning scope's HTTP face. Polled, like the CW phasor: the
    /// browser asks for the latest sweep each time it redraws, so a dropped
    /// request costs one frame and there is nothing to reconnect.
    ///
    /// The routes match Yaesu Web Control's exactly, because the page that
    /// calls them is Core's shared rtty-tuner.js and it hard-codes
    /// /api/rtty/tuner.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class RttyController : ControllerBase
    {
        private readonly RttyTunerService _tuner;
        private readonly IRadioController _radio;
        private readonly RadioStateService _state;
        private readonly ILogger<RttyController> _logger;

        public RttyController(RttyTunerService tuner,
                              IRadioController radio,
                              RadioStateService state,
                              ILogger<RttyController> logger)
        {
            _tuner = tuner;
            _radio = radio;
            _state = state;
            _logger = logger;
        }

        public sealed class StartRequest
        {
            public double MarkHz  { get; set; } = RttyTunerService.DefaultMarkHz;
            public int    ShiftHz { get; set; } = RttyTunerService.DefaultShiftHz;
            public bool   Reverse { get; set; }
        }

        /// <summary>Start the scope, or move its filters if it is already running.</summary>
        [HttpPost("tuner/start")]
        public async Task<IActionResult> Start([FromBody] StartRequest? req)
        {
            req ??= new StartRequest();
            try
            {
                var error = await _tuner.StartAsync(req.MarkHz, req.ShiftHz, req.Reverse);
                if (error != null) return BadRequest(new { error });
                return Ok(_tuner.Frame(0));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start the RTTY tuner");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>The dialog has closed. The audio is let go a couple of seconds later.</summary>
        [HttpPost("tuner/stop")]
        public IActionResult Stop()
        {
            _tuner.RequestStop();
            return Ok();
        }

        /// <summary>
        /// The latest <paramref name="points"/> points of the figure, at
        /// 24,000 a second: 500 is about 21 ms, one sweep of the scope.
        /// </summary>
        [HttpGet("tuner")]
        public IActionResult Tuner([FromQuery] int points = 500)
            => Ok(_tuner.Frame(Math.Clamp(points, 0, RadioWebControl.Core.Services.Rtty.RttyTuningScope.RingPoints)));

        /// <summary>
        /// The radio's own RTTY mark and shift, for the tuner's "From radio"
        /// button. Always 200 with an <c>ok</c> flag rather than a 404 on a
        /// miss, because the shared tuner uses the shape of this reply to
        /// decide whether to show the button at all: a 404 means "this app
        /// cannot do it, hide the button", and ok:false means "it can, but not
        /// right now" - which is a message, not a missing feature.
        /// </summary>
        [HttpGet("radio-tones")]
        public async Task<IActionResult> RadioTones()
        {
            try
            {
                var t = await _radio.GetRttyToneSettingsAsync(HttpContext.RequestAborted);
                if (t is not { } tones)
                    return Ok(new { ok = false, reason = "The radio did not answer." });

                // FSK only. In an AFSK mode the tones are the operator's
                // software's and the radio's menu is not describing them, so
                // say so rather than handing over numbers that do not apply.
                var mode = _state.ModeA ?? "";
                bool fsk = mode.StartsWith("RTTY", StringComparison.OrdinalIgnoreCase);

                return Ok(new
                {
                    ok       = true,
                    markHz   = tones.MarkHz,
                    shiftHz  = tones.ShiftHz,
                    mode,
                    fsk,
                    note     = fsk
                        ? null
                        : $"These are the radio's FSK settings; it is in {mode}, "
                          + "where your software makes the tones."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read the radio's RTTY tone settings");
                return Ok(new { ok = false, reason = ex.Message });
            }
        }

        public sealed record RadioTonesRequest(int MarkHz, int ShiftHz);

        /// <summary>
        /// Push the tuner's Mark and Shift into the radio's own RTTY menu
        /// (1A 05 00 39 / 1A 05 00 40), so the two agree without a trip to the
        /// front panel. The counterpart of the GET above and of the tuner's
        /// "From radio" button.
        ///
        /// FSK only, for the same reason the GET says so: in an AFSK mode those
        /// menu items describe a transmitter the operator is not using, and
        /// writing them would change how the radio transmits on the strength of
        /// a receive-side tuning aid.
        ///
        /// Always 200. The body says what the radio took: a null markHz or
        /// shiftHz means this radio has no rung for that value — the menu items
        /// are one-byte indexes into a short fixed list, and 450 and 850 Hz,
        /// both perfectly ordinary on the air and both offered by the tuner,
        /// simply are not on it.
        /// </summary>
        [HttpPost("radio-tones")]
        public async Task<IActionResult> SetRadioTones([FromBody] RadioTonesRequest req)
        {
            var mode = _state.ModeA ?? "";
            if (!mode.StartsWith("RTTY", StringComparison.OrdinalIgnoreCase))
                return Ok(new
                {
                    ok     = false,
                    mode,
                    fsk    = false,
                    reason = $"The radio is in {mode}, where your software makes the tones, "
                             + "so its RTTY menu was left alone."
                });

            try
            {
                var w = await _radio.SetRttyToneSettingsAsync(req.MarkHz, req.ShiftHz,
                                                              HttpContext.RequestAborted);
                return Ok(new
                {
                    ok      = w.MarkHz != null || w.ShiftHz != null,
                    mode,
                    fsk     = true,
                    markHz  = w.MarkHz,
                    shiftHz = w.ShiftHz,
                    reason  = w.MarkHz == null && w.ShiftHz == null
                        ? "The radio has no setting for those tones."
                        : null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write the radio's RTTY tone settings");
                return Ok(new { ok = false, reason = ex.Message });
            }
        }
    }
}
