using Microsoft.AspNetCore.Mvc;
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
        private readonly ILogger<RadioController> _logger;

        public RadioController(IRadioController radio, ILogger<RadioController> logger)
        {
            _radio = radio;
            _logger = logger;
        }

        public sealed class IfWidthRequest { public int Hz { get; set; } }
        public sealed class FilterRequest { public int Fil { get; set; } }

        // -- IF passband filter width (CI-V 1A 03) -----------------------------

        [HttpPost("ifwidth/{receiver}")]
        public async Task<IActionResult> SetIfWidth(string receiver, [FromBody] IfWidthRequest request)
        {
            if (!TryVfo(receiver, out var vfo))
                return BadRequest(new { error = "Unknown receiver" });
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });

            try
            {
                await _radio.SetIfFilterWidthHzAsync(vfo, request.Hz, CancellationToken.None);
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
