using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DxClusterController : ControllerBase
    {
        private readonly DxClusterService _dxCluster;
        private readonly ISettingsService _settings;

        public DxClusterController(DxClusterService dxCluster, ISettingsService settings)
        {
            _dxCluster = dxCluster;
            _settings = settings;
        }

        // ── Watched callsigns ─────────────────────────────────────────────
        //
        // Persisted in ApplicationSettings.DxClusterWatchedCallsigns as one
        // pattern per line. The popup dialog calls GET to read the list and
        // PUT to replace it. Each pattern is either an exact callsign or a
        // prefix ending in "*" (case-insensitive). Lines starting with "#"
        // are ignored.

        public class WatchedListDto { public List<string> Patterns { get; set; } = new(); }

        [HttpGet("watched")]
        public async Task<IActionResult> GetWatched()
        {
            var s = await _settings.GetSettingsAsync();
            var patterns = (s.DxClusterWatchedCallsigns ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#"))
                .ToList();
            return Ok(new WatchedListDto { Patterns = patterns });
        }

        [HttpPut("watched")]
        public async Task<IActionResult> SaveWatched([FromBody] WatchedListDto dto)
        {
            if (dto == null) return BadRequest();
            var cleaned = (dto.Patterns ?? new List<string>())
                .Select(p => (p ?? "").Trim().ToUpperInvariant())
                .Where(p => p.Length > 0)
                .Distinct()
                .ToList();

            var s = await _settings.GetSettingsAsync();
            s.DxClusterWatchedCallsigns = string.Join("\n", cleaned);
            await _settings.SaveSettingsAsync(s);
            return Ok(new WatchedListDto { Patterns = cleaned });
        }

        /// <summary>
        /// Returns all current (non-aged-off) DX spots, newest first.
        /// Used by the frontend on page load so the spectrum overlay can
        /// render existing spots without waiting for new ones to arrive.
        /// </summary>
        [HttpGet("spots")]
        public IActionResult GetSpots() => Ok(_dxCluster.GetAllSpots());

        /// <summary>
        /// Returns the current cluster connection status — used by the
        /// spectrum-panel badge on page load (before the first SignalR
        /// push) and for ad-hoc diagnostics from a browser.
        /// </summary>
        [HttpGet("status")]
        public IActionResult GetStatus() => Ok(new
        {
            status    = _dxCluster.Status,
            detail    = _dxCluster.LastError,
            spotCount = _dxCluster.SpotCount,
        });

        /// <summary>
        /// Last ~100 raw lines received from the cluster, one per line.
        /// Use this in the browser to see the actual protocol exchange when
        /// diagnosing why spots are not appearing. Plain text response so it
        /// is easy to read directly.
        /// </summary>
        [HttpGet("recent")]
        [Produces("text/plain")]
        public IActionResult GetRecent()
        {
            var lines = _dxCluster.GetRecentLines();
            return Content(lines.Count == 0
                ? "(no lines received yet)"
                : string.Join('\n', lines), "text/plain");
        }
    }
}
