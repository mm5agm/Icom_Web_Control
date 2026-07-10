using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VcTuneController : ControllerBase
{
    private readonly VCTuneModule _module;
    private readonly IVCTuneCommandBuilder _builder;
    private readonly IVCTuneConfigurationStore _configStore;
    private readonly ISettingsService _settings;
    private readonly ILogger<VcTuneController> _logger;
    private readonly CatMultiplexerService _multiplexer;

    public VcTuneController(
        VCTuneModule module,
        IVCTuneCommandBuilder builder,
        IVCTuneConfigurationStore configStore,
        ISettingsService settings,
        ILogger<VcTuneController> logger,
        CatMultiplexerService multiplexer)
    {
        _module      = module;
        _builder     = builder;
        _configStore = configStore;
        _settings    = settings;
        _logger      = logger;
        _multiplexer = multiplexer;
    }

    // GET /api/vctune/diag?cmd=VT01%3B
    // Sends a raw CAT command directly through the multiplexer for diagnostics.
    // Example: /api/vctune/diag?cmd=VT01; to send SET ON, then /api/vctune/diag?cmd=VT0; to read status.
    [HttpGet("diag")]
    public async Task<IActionResult> Diag([FromQuery] string cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd))
            return BadRequest(new { error = "cmd query parameter required, e.g. ?cmd=VT01;" });

        _logger.LogWarning("[VcTuneController.Diag] Sending raw command: {Cmd}", cmd);
        var response = await _multiplexer.SendCommandAsync(cmd, "VcTuneDiag", ct, timeoutMs: 500);
        _logger.LogWarning("[VcTuneController.Diag] Raw response: {Response}", response ?? "(null)");

        return Ok(new { sent = cmd, response = response ?? "(no response within 500ms)" });
    }

    // GET /api/vctune/a/status
    // GET /api/vctune/b/status
    [HttpGet("{band}/status")]
    public async Task<IActionResult> GetStatus(string band, CancellationToken ct)
    {
        var vcBand = BandFromString(band);
        var result = await _module.RefreshStatusAsync(vcBand, ct);
        return Ok(ToDto(result));
    }

    // POST /api/vctune/a/on|off|default|center
    // POST /api/vctune/a/step  body: { "direction":"plus"|"minus", "amount":1-9 }
    [HttpPost("{band}/{command}")]
    public async Task<IActionResult> Execute(
        string band,
        string command,
        [FromBody] VcTuneStepRequest? request,
        CancellationToken ct)
    {
        var vcBand = BandFromString(band);

        var appSettings  = await _settings.GetSettingsAsync();
        var caps         = _configStore.GetCapabilities(appSettings.RadioModel ?? string.Empty);
        bool subInstalled = caps.SubInstallationConfirmed;

        VCTuneCommand cmd;
        try
        {
            cmd = command.ToLowerInvariant() switch
            {
                "on"      => _builder.BuildSetOn(vcBand, subInstalled),
                "off"     => _builder.BuildSetOff(vcBand, subInstalled),
                "default" => _builder.BuildSetDefault(vcBand, subInstalled),
                "center"  => _builder.BuildSetCenter(vcBand, subInstalled),
                "step"    => _builder.BuildSetStep(
                    vcBand,
                    ParseDirection(request?.Direction),
                    Math.Clamp(request?.Amount ?? 5, 0, 9),
                    subInstalled),
                _ => throw new ArgumentException($"Unknown command '{command}'."),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[VcTuneController] Command build failed: {Message}", ex.Message);
            return BadRequest(new
            {
                success       = false,
                errorCategory = "InvalidParameters",
                message       = ex.Message,
                state         = "Unknown",
                meter         = -1,
                availability  = 0,
            });
        }

        var result = await _module.ExecuteCommandAsync(cmd, ct);
        return Ok(ToDto(result));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static VCTuneBand BandFromString(string band) =>
        band.Equals("b", StringComparison.OrdinalIgnoreCase)
            ? VCTuneBand.Sub
            : VCTuneBand.Main;

    private static VCTuneDirection ParseDirection(string? direction) =>
        string.Equals(direction, "minus", StringComparison.OrdinalIgnoreCase)
            ? VCTuneDirection.Minus
            : VCTuneDirection.Plus;

    private static object ToDto(VCTuneCommandResult result) => new
    {
        success       = result.Success,
        errorCategory = result.ErrorCategory,
        message       = result.Message,
        state         = result.UpdatedSnapshot?.State.ToString() ?? "Unknown",
        meter         = result.UpdatedSnapshot?.Meter ?? -1,
        availability  = result.UpdatedSnapshot?.Availability ?? 0,
    };
}

/// <summary>Request body for the step command.</summary>
public sealed class VcTuneStepRequest
{
    public string? Direction { get; set; }
    public int?    Amount    { get; set; }
}
