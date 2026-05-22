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
}
