using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace Icom_Web_Control.Controllers
{
    /// <summary>
    /// "Start fresh test log" and "Download test log" on the Diagnostics page,
    /// ported from Yaesu Web Control. Lets a tester send a log with one click
    /// and a drag into a GitHub issue instead of navigating %APPDATA%.
    /// </summary>
    [ApiController]
    [Route("api/diagnostics")]
    public class DiagnosticsController : ControllerBase
    {
        private readonly ILogger<DiagnosticsController> _logger;

        // Written verbatim into the Serilog file by MarkFreshLog and searched for
        // by DownloadTodayLog. Serilog holds today's log open for writing, so we
        // can't delete or truncate it to "start fresh" - instead we drop a
        // marker and let the download return only the lines after the last one.
        internal const string Marker = "===== NEW TEST CAPTURE";

        public DiagnosticsController(ILogger<DiagnosticsController> logger) => _logger = logger;

        private static string? TodayLogPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var logDir = Path.Combine(appData, "MM5AGM", "Icom Web Control", "logs");
            if (!Directory.Exists(logDir)) return null;

            // Today's file, falling back to the newest one if the app started on
            // a previous day and hasn't rolled over to today's file yet.
            var todayPath = Path.Combine(logDir, $"iwc-{DateTime.Now:yyyyMMdd}.log");
            if (System.IO.File.Exists(todayPath)) return todayPath;

            return new DirectoryInfo(logDir).GetFiles("iwc-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }

        // Drops a marker line into the log so the tester can start a clean
        // capture without the whole accumulated file.
        [HttpPost("log/mark")]
        public IActionResult MarkFreshLog()
        {
            _logger.LogWarning("{Marker} {Time} =====", Marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            return Ok(new { marked = true });
        }

        // Serves today's log as a download. By default returns only the lines
        // from the last "start fresh" marker on; ?full=true for the whole file.
        [HttpGet("log")]
        public IActionResult DownloadTodayLog([FromQuery] bool full = false)
        {
            var sourcePath = TodayLogPath();
            if (sourcePath == null) return NotFound("No log files found yet.");

            // Serilog keeps the file open for writing, so read it with a shared
            // read/write/delete handle rather than File(path, ...), which would
            // fail with a sharing violation on the live log.
            string content;
            using (var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
            {
                content = reader.ReadToEnd();
            }

            if (!full) content = SinceLastMarker(content);

            var bytes = Encoding.UTF8.GetBytes(content);
            return File(bytes, "text/plain", Path.GetFileName(sourcePath));
        }

        // Everything from the start of the last marker's line, or the whole
        // text if there is no marker.
        internal static string SinceLastMarker(string content)
        {
            int idx = content.LastIndexOf(Marker, StringComparison.Ordinal);
            if (idx < 0) return content;
            int lineStart = content.LastIndexOf('\n', idx);
            return lineStart >= 0 ? content[(lineStart + 1)..] : content[idx..];
        }
    }
}
