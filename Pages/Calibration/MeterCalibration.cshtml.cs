using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Models.Calibration;
using Yaesu_Web_Control.Services;
using System.Collections.Generic;
using System.Linq;

namespace Yaesu_Web_Control.Pages.Calibration
{
    public class MeterCalibrationModel : PageModel
    {
        private readonly ICalibrationService _calibrationService;

        public MeterCalibrationModel(ICalibrationService calibrationService)
        {
            _calibrationService = calibrationService;
        }

        [BindProperty]
        public Yaesu_Web_Control.Models.Calibration.MeterCalibration Calibration { get; set; } = new Yaesu_Web_Control.Models.Calibration.MeterCalibration
        {
            Name = "S-Meter",
            Type = "s_meter",
            IsGauge = true,
            Units = "S-units",
            Points = new List<CalibrationPoint>()
        };

        [BindProperty]
        public double CurrentRawValue { get; set; } = 0;

        public string? SaveMessage { get; set; }

        public void OnGet()
        {
            var existing = _calibrationService.Current.Meters
                .FirstOrDefault(m => m.Name == "S-Meter");
            if (existing != null)
            {
                Calibration = existing;
            }
        }

        public IActionResult OnPost(string add, string delete)
        {
            if (!string.IsNullOrEmpty(add))
            {
                Calibration.Points.Add(new CalibrationPoint { Label = "", Raw = 0 });
                return Page();
            }

            if (!string.IsNullOrEmpty(delete) && int.TryParse(delete, out int idx) && idx >= 0 && idx < Calibration.Points.Count)
            {
                Calibration.Points.RemoveAt(idx);
                return Page();
            }

            // Save button — rebuild the file, replacing/adding the S-Meter entry.
            var file = new CalibrationFile
            {
                Meters = _calibrationService.Current.Meters
                    .Where(m => m.Name != "S-Meter")
                    .ToList()
            };
            file.Meters.Add(new Yaesu_Web_Control.Models.Calibration.MeterCalibration
            {
                Name = "S-Meter",
                Type = "s_meter",
                IsGauge = true,
                Units = "S-units",
                Points = Calibration.Points
            });
            _calibrationService.Save(file);
            SaveMessage = "Calibration saved.";
            return Page();
        }
    }
}
