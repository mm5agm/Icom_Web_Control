using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DxClusterController : ControllerBase
    {
        private readonly DxClusterService _dxCluster;

        public DxClusterController(DxClusterService dxCluster)
        {
            _dxCluster = dxCluster;
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
    }
}
