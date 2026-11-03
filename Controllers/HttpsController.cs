using Microsoft.AspNetCore.Mvc;
using Icom_Web_Control.Services;

namespace Icom_Web_Control.Controllers
{
    /// <summary>
    /// The optional HTTPS listener's Settings controls: whether it is listening
    /// now, and generating its self-signed certificate. HTTPS is needed for Web
    /// MIDI (the CW Paddle page) in a browser on another computer.
    /// </summary>
    [ApiController]
    [Route("api/https")]
    public class HttpsController : ControllerBase
    {
        private readonly HttpPortInfo _portInfo;
        private readonly ILogger<HttpsController> _logger;

        public HttpsController(HttpPortInfo portInfo, ILogger<HttpsController> logger)
        {
            _portInfo = portInfo;
            _logger = logger;
        }

        [HttpGet("status")]
        public IActionResult Status() => Ok(new
        {
            active = _portInfo.HttpsActive,
            port = _portInfo.HttpsPort,
            certificatePresent = HttpsCertificateService.CertificateExists,
            certificateInfo = HttpsCertificateService.DescribeCertificate()
        });

        public sealed class GenerateCertRequest
        {
            public string? SanHosts { get; set; }
        }

        [HttpPost("generate")]
        public IActionResult Generate([FromBody] GenerateCertRequest? body)
        {
            try
            {
                var hosts = (body?.SanHosts ?? "")
                    .Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                HttpsCertificateService.Generate(hosts);
                _logger.LogInformation("Generated self-signed HTTPS certificate at {Path}", HttpsCertificateService.CertificatePath);
                return Ok(new
                {
                    ok = true,
                    path = HttpsCertificateService.CertificatePath,
                    info = HttpsCertificateService.DescribeCertificate(),
                    restartRequired = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTTPS certificate generation failed");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
