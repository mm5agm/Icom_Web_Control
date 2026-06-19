using Microsoft.AspNetCore.Mvc;
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

        public VoiceController(VoiceControlService voice)
        {
            _voice = voice;
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
    }
}
