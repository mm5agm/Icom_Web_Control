using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Icom_Web_Control.Models;
using Icom_Web_Control.Services;

namespace Icom_Web_Control.Controllers
{
    /// <summary>
    /// Export and import the entire user settings file. Designed for moving
    /// a complete IWC configuration (band profiles, memories, CW messages,
    /// DX watch list, calibration, external app paths, etc.) between PCs or
    /// preserving it across a Windows rebuild.
    /// </summary>
    [ApiController]
    [Route("api/settings")]
    public class SettingsBackupController : ControllerBase
    {
        private readonly ISettingsService _settingsService;
        private readonly ILogger<SettingsBackupController> _logger;

        public SettingsBackupController(ISettingsService settingsService, ILogger<SettingsBackupController> logger)
        {
            _settingsService = settingsService;
            _logger = logger;
        }

        /// <summary>
        /// GET /api/settings/export — returns the user settings JSON as a downloadable file.
        /// </summary>
        [HttpGet("export")]
        public async Task<IActionResult> Export()
        {
            var path = _settingsService.GetSettingsFilePath();
            if (!System.IO.File.Exists(path))
            {
                // No settings have been saved yet — return the in-memory defaults so the
                // user gets a sensible file rather than a 404.
                var defaults = await _settingsService.GetSettingsAsync();
                var json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
                return FileFromBytes(System.Text.Encoding.UTF8.GetBytes(json));
            }
            var bytes = await System.IO.File.ReadAllBytesAsync(path);
            return FileFromBytes(bytes);
        }

        /// <summary>
        /// POST /api/settings/import — accepts a settings JSON file and replaces the current
        /// user settings with it. The previous file is preserved as appsettings.user.json.bak.
        /// Most services only read settings at startup, so the user is prompted to restart
        /// the app for all changes to apply.
        /// </summary>
        [HttpPost("import")]
        public async Task<IActionResult> Import(IFormFile? file)
        {
            if (file == null || file.Length == 0)
                return BadRequest(new { error = "No file uploaded." });

            // Read the uploaded bytes into memory first so we can validate before touching disk.
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();
            string text;
            try
            {
                text = System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return BadRequest(new { error = "Uploaded file is not valid UTF-8 text." });
            }

            // Validate: must deserialize into ApplicationSettings without throwing. Any
            // missing properties get their default values, so the file can be a subset.
            ApplicationSettings? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<ApplicationSettings>(text);
            }
            catch (JsonException ex)
            {
                return BadRequest(new { error = $"Uploaded file is not valid settings JSON: {ex.Message}" });
            }
            if (parsed == null)
                return BadRequest(new { error = "Uploaded file deserialised to null." });

            var path = _settingsService.GetSettingsFilePath();
            var dir  = Path.GetDirectoryName(path) ?? string.Empty;

            // Preserve the current settings as a .bak so the user can recover if the import
            // turns out to break something. Overwrites any prior .bak — one level of undo only.
            try
            {
                if (System.IO.File.Exists(path))
                {
                    var bak = path + ".bak";
                    System.IO.File.Copy(path, bak, overwrite: true);
                    _logger.LogInformation("Settings backed up to {Bak} before import.", bak);
                }
                Directory.CreateDirectory(dir);
                await System.IO.File.WriteAllBytesAsync(path, bytes);
                _settingsService.InvalidateCache();
                _logger.LogInformation("Settings imported successfully ({Bytes} bytes) and cache invalidated.", bytes.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write imported settings to {Path}", path);
                return StatusCode(500, new { error = $"Failed to write settings: {ex.Message}" });
            }

            return Ok(new
            {
                imported   = true,
                bytes      = bytes.Length,
                restartNeeded = true,
                message    = "Settings imported. Restart the app for all changes (radio connection, DX cluster, SDR, etc.) to take effect."
            });
        }

        private static FileContentResult FileFromBytes(byte[] bytes)
        {
            var date = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            return new FileContentResult(bytes, "application/json")
            {
                FileDownloadName = $"iwc-settings-{date}.json"
            };
        }
    }
}
