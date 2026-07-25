
using System.Text.Json.Serialization;
namespace Icom_Web_Control.Models.Calibration;

public class CalibrationFile
{
    [JsonPropertyName("meters")]
    public List<MeterCalibration> Meters { get; set; } = new();
}
