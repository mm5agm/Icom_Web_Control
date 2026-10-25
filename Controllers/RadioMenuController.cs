using Microsoft.AspNetCore.Mvc;
using Icom_Web_Control.Services;
using Icom_Web_Control.Services.RadioMenu;

namespace Icom_Web_Control.Controllers
{
    /// <summary>
    /// Saves the radio's own SET menu to a text file that opens in Notepad.
    /// Read-only: every item is fetched with a 1A 05 read and nothing is
    /// written to the radio. Phase 8 of docs/design/iwc-clone-split-plan.md,
    /// capture half.
    /// </summary>
    [ApiController]
    [Route("api/radiomenu")]
    public class RadioMenuController : ControllerBase
    {
        private const int Attempts = 3;

        private readonly IRadioController _radio;
        private readonly ISettingsService _settings;
        private readonly ILogger<RadioMenuController> _logger;

        public RadioMenuController(IRadioController radio, ISettingsService settings, ILogger<RadioMenuController> logger)
        {
            _radio = radio;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>
        /// GET /api/radiomenu/export — reads every menu item and returns an .ini file.
        /// 270 reads on an IC-7300MK2, several seconds at 19200 baud.
        /// </summary>
        [HttpGet("export")]
        public async Task<IActionResult> Export(CancellationToken ct)
        {
            var model = (await _settings.GetSettingsAsync()).RadioModel;
            var table = RadioMenuFile.TableFor(model);
            if (table is null)
                return BadRequest(new { error = $"Saving the radio menu is not available for the {model} yet." });
            if (!_radio.IsConnected)
                return BadRequest(new { error = "The radio is not connected." });

            var readings = new List<RadioMenuReading>(table.Count);
            foreach (var item in table)
                readings.Add(new RadioMenuReading(item, await ReadAsync(item, ct)));

            int missing = readings.Count(r => r.Data is null);
            _logger.LogInformation("Radio menu save: {Read} of {Total} items read from the {Model}",
                readings.Count - missing, readings.Count, model);
            if (missing == readings.Count)
                return StatusCode(504, new { error = "The radio did not answer any menu reads." });

            var now = DateTime.Now;
            var ini = RadioMenuFile.ToIni(model, now, readings);
            var name = $"IWC radio menu {model} {now:yyyy-MM-dd HHmm}.ini";
            Response.Headers["X-Menu-Missing"] = missing.ToString();
            // UTF-8 with BOM so older Notepad shows [▲] and [▼] properly.
            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(ini)).ToArray();
            return File(bytes, "text/plain", name);
        }

        private async Task<byte[]?> ReadAsync(RadioMenuItem item, CancellationToken ct)
        {
            for (int i = 0; i < Attempts; i++)
            {
                var data = await _radio.ReadMenuItemAsync(item.Number, ct);
                if (data is not null) return data;
            }
            _logger.LogWarning("Radio menu save: no answer for 1A 05 {Address}", item.Address);
            return null;
        }
    }
}
