using Microsoft.AspNetCore.Mvc;
using Icom_Web_Control.Services;
using Icom_Web_Control.Services.Cw;

namespace Icom_Web_Control.Controllers
{
    /// <summary>
    /// Backs the "set this up now" pop-outs (core/js/dialogs/feature-setup.js).
    ///
    /// A panel can be visible and offering a feature that cannot actually
    /// work until something is chosen in Settings - the CW reader needs the
    /// radio's recording device, the DX spots panel needs a cluster host and
    /// a login callsign. Rather than send the operator off to Settings to
    /// hunt for the right field, each panel offers to fix its own
    /// prerequisite in place, and this controller is what those pop-outs
    /// write through.
    ///
    /// It is deliberately NOT a general settings-write endpoint. Each action
    /// touches a named, hard-coded set of fields and nothing else, so a bug
    /// or a stray request here can never rewrite unrelated configuration.
    /// Anything outside that whitelist belongs on the Settings page.
    /// </summary>
    [ApiController]
    [Route("api/feature-setup")]
    public class FeatureSetupController : ControllerBase
    {
        private readonly ISettingsService _settings;
        private readonly ILogger<FeatureSetupController> _logger;

        public FeatureSetupController(ISettingsService settings, ILogger<FeatureSetupController> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        // ── Current state ──────────────────────────────────────────────────
        // What the pop-outs pre-fill their fields from. Only the fields the
        // pop-outs can write are returned, plus the recording devices to
        // choose from, named as the reader matches them (WinMM product
        // names, the same list the Settings picker shows).

        [HttpGet("state")]
        public async Task<IActionResult> State()
        {
            var s = await _settings.GetSettingsAsync();
            object[] devices;
            try
            {
                devices = CwAudioDevices.List()
                    .Select(d => (object)new { key = d.Name, name = d.Name, likelyRadio = LooksLikeRadio(d.Name) })
                    .ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FeatureSetup] Could not list recording devices");
                devices = Array.Empty<object>();
            }
            return Ok(new
            {
                dxCluster = new
                {
                    enabled = s.DxClusterEnabled,
                    host = s.DxClusterHost ?? "",
                    port = s.DxClusterPort,
                    callsign = s.DxClusterLoginCallsign ?? ""
                },
                cwAudio = new
                {
                    rxDevice = s.CwAudioDeviceName ?? "",
                    devices
                }
            });
        }

        // The IC-7300's built-in codec enumerates as "USB Audio CODEC".
        private static bool LooksLikeRadio(string? name) =>
            !string.IsNullOrWhiteSpace(name)
            && (name.Contains("USB Audio", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Icom", StringComparison.OrdinalIgnoreCase));

        // ── DX cluster ─────────────────────────────────────────────────────
        // Writes DxClusterEnabled / Host / Port / LoginCallsign and nothing
        // else. DxClusterService re-reads settings every 15 s while idle, so
        // no restart is needed.

        public class DxClusterRequest
        {
            public bool Enabled { get; set; } = true;
            public string Host { get; set; } = "";
            public int Port { get; set; } = 7300;
            public string Callsign { get; set; } = "";
        }

        [HttpPost("dx-cluster")]
        public async Task<IActionResult> SaveDxCluster([FromBody] DxClusterRequest? req)
        {
            req ??= new DxClusterRequest();
            var host = (req.Host ?? "").Trim();
            var callsign = (req.Callsign ?? "").Trim().ToUpperInvariant();

            if (req.Enabled)
            {
                if (host.Length == 0)
                    return Ok(new { success = false, error = "Enter a cluster host, for example gb7ujs.ddns.net." });
                if (callsign.Length == 0)
                    return Ok(new { success = false, error = "Enter your callsign - the cluster uses it to log you in." });
                if (req.Port <= 0 || req.Port > 65535)
                    return Ok(new { success = false, error = $"Port {req.Port} is not a valid TCP port." });
            }

            var s = await _settings.GetSettingsAsync();
            s.DxClusterEnabled = req.Enabled;
            s.DxClusterHost = host;
            s.DxClusterPort = req.Port <= 0 || req.Port > 65535 ? 7300 : req.Port;
            s.DxClusterLoginCallsign = callsign;
            await _settings.SaveSettingsAsync(s);

            _logger.LogInformation("[FeatureSetup] DX cluster set to {Host}:{Port} as {Call} (enabled={Enabled})",
                host, s.DxClusterPort, callsign, req.Enabled);

            return Ok(new
            {
                success = true,
                message = req.Enabled
                    ? "Saved. The cluster connects within about 15 seconds."
                    : "Saved. The DX cluster is now off."
            });
        }

        // ── CW reader audio ────────────────────────────────────────────────
        // Writes CwAudioDeviceName and nothing else. The reader reads it when
        // it starts, so the operator starts the reader again to use it.

        public class AudioRxRequest
        {
            public string Device { get; set; } = "";
        }

        [HttpPost("audio-rx")]
        public async Task<IActionResult> SaveAudioRx([FromBody] AudioRxRequest? req)
        {
            var device = (req?.Device ?? "").Trim();
            if (device.Length == 0)
                return Ok(new { success = false, error = "Choose the radio's USB recording device." });

            var s = await _settings.GetSettingsAsync();
            s.CwAudioDeviceName = device;
            await _settings.SaveSettingsAsync(s);

            _logger.LogInformation("[FeatureSetup] CW reader recording device set to {Device}", device);

            return Ok(new { success = true, message = "Saved. Start the CW reader again." });
        }
    }
}
