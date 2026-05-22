using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Yaesu_Web_Control.Models.Calibration
{
    // Represents a single calibration point for a meter
    public class CalibrationPoint
    {
        [JsonPropertyName("Radio")]
        public string? Radio { get; set; }

        // Backward compatibility for old files; these are normalized into Radio and not re-saved.
        [JsonPropertyName("label")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Label { get; set; }

        [JsonPropertyName("raw")]
        public double Raw { get; set; }

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(Radio))
            {
                Radio = Label;
            }

            Label = null;
        }

        public double? TryGetRadioNumeric()
        {
            if (string.IsNullOrWhiteSpace(Radio))
            {
                return null;
            }

            if (double.TryParse(Radio, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return null;
        }
    }

    // Represents calibration and metadata for a meter
    public class MeterCalibration
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "numeric";

        [JsonPropertyName("points")]
        public List<CalibrationPoint> Points { get; set; } = new();

        [JsonPropertyName("isGauge")]
        public bool IsGauge { get; set; }

        [JsonPropertyName("units")]
        public string Units { get; set; } = string.Empty;

        public void Normalize()
        {
            foreach (var point in Points)
            {
                point.Normalize();
            }
        }
    }
}
