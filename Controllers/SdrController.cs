// Yaesu Web Control – SDR REST Controller
// Provides device enumeration for the Settings page dropdown.
// No UI logic, no SignalR, no calibration.
//
// Response shape: { devices: [...], notes: ["..."] }
// 'notes' contains plain-English installation guidance when drivers are missing.

using Yaesu_Web_Control.Services.Sdr;
using Microsoft.AspNetCore.Mvc;

namespace Yaesu_Web_Control.Controllers
{
    [ApiController]
    [Route("api/sdr")]
    public class SdrController : ControllerBase
    {
        private readonly ILogger<SdrController> _logger;

        public SdrController(ILogger<SdrController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Returns connected SDR devices plus plain-English installation notes.
        /// Always responds 200.
        /// </summary>
        [HttpGet("devices")]
        public IActionResult GetDevices()
        {
            var all   = new List<SdrDeviceInfo>();
            var notes = new List<string>();

            // ── SDRplay (sdrplay_api.dll) ────────────────────────────────────────
            var sdrplay = SdrplayDevice.EnumerateDevices(out string? sdrplayNote);
            if (sdrplay.Count > 0)
            {
                all.AddRange(sdrplay);
                _logger.LogDebug("SDR: Found {Count} SDRplay device(s)", sdrplay.Count);
            }
            else if (sdrplayNote != null)
            {
                notes.Add(sdrplayNote);
                _logger.LogWarning("SDR: SDRplay — {Note}", sdrplayNote);
            }

            // ── SoapySDR (SoapySDR.dll) ──────────────────────────────────────────
            try
            {
                var soapy = SoapySdrInterop.EnumerateDevices();

                // If the direct SDRplay path already found devices, suppress SoapySDR's
                // sdrplay-driver entries — they are the same physical hardware via an
                // inferior code path and would show as duplicates in the dropdown.
                var soapyFiltered = sdrplay.Count > 0
                    ? soapy.Where(d => !d.Driver.Equals("sdrplay", StringComparison.OrdinalIgnoreCase)).ToList()
                    : soapy;

                if (soapyFiltered.Count > 0)
                {
                    all.AddRange(soapyFiltered);
                    _logger.LogDebug("SDR: Found {Count} SoapySDR device(s)", soapyFiltered.Count);
                }
                else if (soapy.Count == 0 && all.Count == 0)
                {
                    // No devices found via any path — tell the user what SoapySDR searched.
                    string diag = SoapySdrInterop.GetPluginDiagnostics();
                    notes.Add("No SDR devices detected. " +
                              "Plugin details: | " + diag.Replace("\n", " | "));
                    _logger.LogWarning("SDR: SoapySDR no devices. {Diag}", diag);
                }
            }
            catch (DllNotFoundException ex)
            {
                bool missingDependency = ex.Message.Contains("dependencies",
                    StringComparison.OrdinalIgnoreCase);

                notes.Add(missingDependency
                    ? "SoapySDR.dll loaded but a dependency it needs is missing. " +
                      "Try re-installing the application — the installer bundles all required DLLs."
                    : "SoapySDR.dll not found. Try re-installing the application — the installer " +
                      "should have placed SoapySDR\\bin\\SoapySDR.dll in the application folder.");
                _logger.LogWarning("SDR: SoapySDR DllNotFoundException — {Msg}", ex.Message);
            }
            catch (Exception ex)
            {
                notes.Add($"SoapySDR error: {ex.Message}");
                _logger.LogWarning(ex, "SDR: SoapySDR enumeration failed");
            }

            return Ok(new
            {
                devices = all.Select(d => new { d.Key, d.Label, d.Driver }),
                notes
            });
        }
    }
}
