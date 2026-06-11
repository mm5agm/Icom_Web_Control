using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Models.Calibration;

[ApiController]
[Route("api/calibration")]
public class CalibrationController : ControllerBase
{
    private readonly ICalibrationService _service;

    public CalibrationController(ICalibrationService service)
    {
        _service = service;
    }

    [HttpGet("all")]
    public IActionResult GetAll()
    {
        var all = _service.GetAllCalibrationTables();
        return Ok(all);
    }

    [HttpGet("file")]
    public IActionResult GetCalibrationFile()
    {
        return Ok(new
        {
            calibration = _service.Current,
            saveTargetPath = _service.GetSavePath(),
            mode = _service.IsDevelopmentMode ? "development" : "user"
        });
    }

    [HttpPost("file")]
    public IActionResult SaveCalibrationFile([FromBody] CalibrationFile file)
    {
        if (file == null)
        {
            return BadRequest(new { error = "Calibration file payload is required." });
        }

        _service.Save(file);
        return Ok(new
        {
            ok = true,
            saveTargetPath = _service.GetSavePath(),
            mode = _service.IsDevelopmentMode ? "development" : "user"
        });
    }

    /// <summary>
    /// Reset the user's calibration file to the model-specific defaults
    /// shipped with the app. Used when the user has changed radio model in
    /// Settings (the calibration table that came with the previous model is
    /// no longer right for the new one) or when they want to wipe their own
    /// tweaks and start over.
    /// </summary>
    [HttpPost("reset")]
    public IActionResult ResetCalibration()
    {
        _service.ResetToDefault();
        return Ok(new
        {
            ok = true,
            calibration = _service.Current,
            saveTargetPath = _service.GetSavePath(),
        });
    }
}
