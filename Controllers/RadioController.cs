using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Icom_Web_Control.Hubs;
using Icom_Web_Control.Services;
using System.Threading;

namespace Icom_Web_Control.Controllers
{
    /// <summary>
    /// Native IC-7300 radio controls that have no Yaesu-CAT ancestor in
    /// <see cref="CatController"/> — currently the IF passband filter width
    /// (CI-V 1A 03) and the FIL1/2/3 slot selector (CI-V 26). New IWC-only
    /// controls belong here rather than extending the CAT-shaped CatController;
    /// everything routes through the <see cref="IRadioController"/> seam, and
    /// the state writes it makes broadcast to the browser over SignalR on change.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class RadioController : ControllerBase
    {
        private readonly IRadioController _radio;
        private readonly ISettingsService _settings;
        private readonly IHubContext<RadioHub> _hub;
        private readonly ILogger<RadioController> _logger;

        public RadioController(IRadioController radio, ISettingsService settings, IHubContext<RadioHub> hub, ILogger<RadioController> logger)
        {
            _radio = radio;
            _settings = settings;
            _hub = hub;
            _logger = logger;
        }

        public sealed class IfWidthRequest { public int Hz { get; set; } }
        public sealed class FilterRequest { public int Fil { get; set; } }
        public sealed class CrossBandWatchRequest
        {
            public bool? Enabled { get; set; }
            public int? IntervalSeconds { get; set; }
        }

        // -- IF passband filter width (CI-V 1A 03) -----------------------------

        [HttpPost("ifwidth/{receiver}")]
        public async Task<IActionResult> SetIfWidth(string receiver, [FromBody] IfWidthRequest request,
                                                    [FromServices] Icom_Web_Control.Services.Rtty.RttyTunerModeService tunerMode)
        {
            if (!TryVfo(receiver, out var vfo))
                return BadRequest(new { error = "Unknown receiver" });
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });

            try
            {
                await _radio.SetIfFilterWidthHzAsync(vfo, request.Hz, CancellationToken.None);
                // The RTTY tuner keeps a width set by hand while it holds the
                // radio, rather than sizing over it (VFO A is the one it holds).
                if (vfo == RadioVfo.A) await tunerMode.WidthSetByHandAsync(request.Hz);
                return Ok(new { message = $"IF width {request.Hz} Hz set for VFO {vfo}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting IF width for VFO {Vfo}", vfo);
                return StatusCode(500, new { error = "Failed to set IF width" });
            }
        }

        // -- FIL1/2/3 slot selector (CI-V 26) ----------------------------------

        [HttpPost("filter/{receiver}")]
        public async Task<IActionResult> SetFilter(string receiver, [FromBody] FilterRequest request)
        {
            if (!TryVfo(receiver, out var vfo))
                return BadRequest(new { error = "Unknown receiver" });
            if (request.Fil is < 1 or > 3)
                return BadRequest(new { error = "Filter slot must be 1, 2 or 3" });
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });

            try
            {
                await _radio.SetSelectedFilterAsync(vfo, request.Fil, CancellationToken.None);
                return Ok(new { message = $"FIL{request.Fil} selected for VFO {vfo}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error selecting filter for VFO {Vfo}", vfo);
                return StatusCode(500, new { error = "Failed to select filter" });
            }
        }

        // -- Cross-band watch (pseudo-dual) ------------------------------------

        // The X-Watch keys on Classic and the Radio panel are the only place
        // these two settings are set: on/off, so the peek (it retunes the one
        // receiver and dips the audio) can be stopped for a QSO, and how often
        // it peeks. Either field may be left out. Sends nothing to the radio:
        // CivRadioController re-reads the settings on its slow poll, so the
        // peek stops within about a second.
        [HttpPost("crossband-watch")]
        public async Task<IActionResult> SetCrossBandWatch([FromBody] CrossBandWatchRequest request)
        {
            var s = await _settings.GetSettingsAsync();
            if (request.Enabled is bool on) s.PseudoDualCrossBandEnabled = on;
            if (request.IntervalSeconds is int sec) s.PseudoDualPeekIntervalSeconds = Math.Clamp(sec, 5, 60);
            await _settings.SaveSettingsAsync(s);
            await _hub.Clients.All.SendAsync("RadioStateUpdate", new { property = "CrossBandWatch", value = CrossBandWatchState(s) });
            return Ok(CrossBandWatchState(s));
        }

        // -- MOD input: DATA OFF MOD and DATA MOD (1A 05, IWC #144) ------------
        // Where transmit audio comes from. Reading and choosing a source never
        // keys the radio; the items and choices come from the radio's profile.

        public sealed class ModInputRequest
        {
            public bool Data { get; set; }
            public int Choice { get; set; }
        }

        [HttpGet("mod-input")]
        public async Task<IActionResult> GetModInput(CancellationToken ct)
        {
            var items = Services.Civ.RadioProfile.ForModel(_radio.ModelId)?.ModInput;
            if (items is null) return Ok(new { available = false });
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });
            var state = await Services.Civ.ModInput.ReadAsync(_radio, items, ct);
            return Ok(new { available = true, choices = items.Choices, dataOff = state.DataOff, data = state.Data });
        }

        [HttpPost("mod-input")]
        public async Task<IActionResult> SetModInput([FromBody] ModInputRequest request, CancellationToken ct)
        {
            var items = Services.Civ.RadioProfile.ForModel(_radio.ModelId)?.ModInput;
            if (items is null) return BadRequest(new { error = "This radio has no MOD input items" });
            if (request.Choice < 0 || request.Choice >= items.Choices.Count)
                return BadRequest(new { error = "No such choice" });
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });
            var now = await Services.Civ.ModInput.SetAsync(_radio, items, request.Data, request.Choice, ct);
            if (now != request.Choice)
            {
                _logger.LogWarning("MOD input: {Item} to {Choice} not taken (read back {Now})",
                    request.Data ? "DATA MOD" : "DATA OFF MOD", items.Choices[request.Choice], now);
                return StatusCode(502, new { error = "The radio didn't take it" });
            }
            return Ok(new { data = request.Data, choice = now });
        }

        // CrossBandWatch's value, here and in RadioHub's replay on connect.
        public static object CrossBandWatchState(Models.ApplicationSettings s)
            => new { enabled = s.PseudoDualCrossBandEnabled, intervalSeconds = Math.Clamp(s.PseudoDualPeekIntervalSeconds, 5, 60) };

        private static bool TryVfo(string receiver, out RadioVfo vfo)
        {
            switch (receiver?.ToUpperInvariant())
            {
                case "A": vfo = RadioVfo.A; return true;
                case "B": vfo = RadioVfo.B; return true;
                default:  vfo = RadioVfo.A; return false;
            }
        }
    }
}
