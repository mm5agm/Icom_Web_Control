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

        /// <summary>
        /// Extracts voice-related log lines from today's YWC log file. Used by
        /// the "Voice Control Log" panel on the Diagnostics page. A bug
        /// reporter clicks the panel, copies the output, pastes it into a
        /// GitHub issue -- without ever having to know the log file lives at
        /// %APPDATA%\MM5AGM\Yaesu Web Control\logs\ywc-YYYYMMDD.log or that
        /// they need to grep it. The full log can grow to many MB; this
        /// endpoint reads only the tail and filters server-side so the
        /// reporter sees a focused, copy-pastable list.
        ///
        /// `lines` query param caps the returned count (default 200, max 2000).
        /// Patterns matched: lines containing "[Voice]" or "[IntentDispatcher]".
        /// Lines are returned newest-last to read like a normal log file.
        /// </summary>
        [HttpGet("log")]
        public IActionResult VoiceLog([FromQuery] int lines = 200)
        {
            if (lines < 1) lines = 1;
            if (lines > 2000) lines = 2000;

            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var logDir = Path.Combine(appData, "MM5AGM", "Yaesu Web Control", "logs");
                if (!Directory.Exists(logDir))
                    return Ok(new { lines = Array.Empty<string>(), source = (string?)null, note = "Log folder doesn't exist yet." });

                // Pick today's file, falling back to whatever is newest if the
                // run started on a previous day and hasn't rolled over yet.
                var todayName = $"ywc-{DateTime.Now:yyyyMMdd}.log";
                var todayPath = Path.Combine(logDir, todayName);
                string? sourcePath = System.IO.File.Exists(todayPath)
                    ? todayPath
                    : new DirectoryInfo(logDir)
                        .GetFiles("ywc-*.log")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .FirstOrDefault()?.FullName;

                if (sourcePath == null)
                    return Ok(new { lines = Array.Empty<string>(), source = (string?)null, note = "No log files found." });

                // Read the tail. 4 MB is generous -- typical voice sessions
                // generate tens of KB of [Voice] lines amongst hundreds of KB
                // of meter polling. Reading the whole file would work but is
                // wasteful on long-running sessions where the log can be 50+ MB.
                const long tailBytes = 4 * 1024 * 1024;
                string content;
                using (var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > tailBytes)
                        fs.Seek(-tailBytes, SeekOrigin.End);
                    using var reader = new StreamReader(fs);
                    content = reader.ReadToEnd();
                }

                var matched = content
                    .Split('\n')
                    .Where(l => l.Contains("[Voice]") || l.Contains("[IntentDispatcher]"))
                    .Select(l => l.TrimEnd('\r'))
                    .ToList();

                // If we read mid-file, the first matched line might be a
                // partial -- drop it to keep the output clean.
                if (matched.Count > 0 && matched.Count > lines)
                    matched.RemoveAt(0);

                var tail = matched.Count > lines
                    ? matched.GetRange(matched.Count - lines, lines)
                    : matched;

                return Ok(new
                {
                    source = Path.GetFileName(sourcePath),
                    totalMatched = matched.Count,
                    returned = tail.Count,
                    lines = tail,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Voice] Failed to extract voice log");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
