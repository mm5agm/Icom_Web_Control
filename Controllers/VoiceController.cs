using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using Yaesu_Web_Control.Services.Voice;

namespace Yaesu_Web_Control.Controllers
{
    /// <summary>
    /// HTTP entry points for the on-screen mic button. The frontend POSTs
    /// /api/voice/start on mousedown and /api/voice/stop on mouseup; status
    /// updates flow back over SignalR (the <c>VoiceStatusUpdate</c> event)
    /// rather than HTTP, so the button can react to mid-recognition state
    /// changes without polling.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public sealed class VoiceController : ControllerBase
    {
        private readonly VoiceControlService _voice;
        private readonly ILogger<VoiceController> _logger;

        public VoiceController(VoiceControlService voice, ILogger<VoiceController> logger)
        {
            _voice = voice;
            _logger = logger;
        }

        [HttpPost("start")]
        public async Task<IActionResult> Start()
        {
            var ok = await _voice.StartListeningAsync();
            return ok
                ? Ok(_voice.CurrentStatus)
                : StatusCode(503, _voice.CurrentStatus);
        }

        [HttpPost("stop")]
        public async Task<IActionResult> Stop()
        {
            await _voice.StopListeningAsync();
            return Ok(_voice.CurrentStatus);
        }

        [HttpGet("status")]
        public IActionResult Status() => Ok(_voice.CurrentStatus);

        /// <summary>
        /// Opens the user grammars folder in Windows Explorer. Used by the
        /// "Open user grammars folder" button in Settings -> Voice Control.
        /// The folder is created if missing so the user lands in a real
        /// location even on a fresh install.
        /// </summary>
        [HttpPost("open-grammars-folder")]
        public IActionResult OpenGrammarsFolder()
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var path = Path.Combine(appData, "MM5AGM", "Yaesu Web Control", "Grammars");
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
                return Ok(new { path });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Voice] Failed to open grammars folder");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
