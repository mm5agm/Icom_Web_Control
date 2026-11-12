using Microsoft.AspNetCore.Mvc;
using Icom_Web_Control.Services;
using Icom_Web_Control.Services.Civ;

namespace Icom_Web_Control.Controllers
{
    /// <summary>
    /// Refine for the radio's scope: GET says the step, POST saves one. The
    /// shared page module core/js/radio-panel/spectrum-refine.js calls these,
    /// with the same route YWC uses, so every view in both apps asks the same way.
    /// </summary>
    /// <remarks>
    /// The radio has one scope, so there is one step, for both panels: a
    /// pseudo-dual watch panel is a crop of the same sweeps. The pages send
    /// <c>sdrId</c> anyway, as YWC's do, and it is accepted and ignored.
    /// Nothing is sent to the radio: CivRadioController re-reads the setting on
    /// its slow poll, so a new step shows within about a second.
    /// </remarks>
    [ApiController]
    [Route("api/sdr/refine")]
    public class ScopeRefineController : ControllerBase
    {
        private readonly ISettingsService _settings;

        public ScopeRefineController(ISettingsService settings) => _settings = settings;

        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] string? sdrId = null)
        {
            var s = await _settings.GetSettingsAsync();
            return Ok(State(s.ScopeRefineLevel));
        }

        [HttpPost]
        public async Task<IActionResult> Set([FromQuery] int level, [FromQuery] string? sdrId = null)
        {
            if (level < 0 || level > ScopeRefine.MaxLevel)
                return BadRequest(new { error = $"level must be 0 to {ScopeRefine.MaxLevel}" });
            var s = await _settings.GetSettingsAsync();
            s.ScopeRefineLevel = level;
            await _settings.SaveSettingsAsync(s);
            return Ok(State(level));
        }

        private static object State(int level) => new
        {
            available = true,
            level = ScopeRefine.Clamp(level),
            max = ScopeRefine.MaxLevel,
        };
    }
}
