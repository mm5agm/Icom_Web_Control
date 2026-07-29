using Microsoft.AspNetCore.Mvc.RazorPages;
using Icom_Web_Control.Services;

namespace Icom_Web_Control.Pages.MeterCalibration
{
    public class IndexModel : PageModel
    {
        private readonly ICalibrationService _calibration;
        private readonly ISettingsService _settings;

        public IndexModel(ICalibrationService calibration, ISettingsService settings)
        {
            _calibration = calibration;
            _settings = settings;
        }

        // Gates the developer-only "Import emailed cal → default" button. True
        // only when running from source (dotnet run / VS Code); a normal
        // installed IWC is Production, so users never see it.
        public bool IsDevelopmentMode => _calibration.IsDevelopmentMode;

        // Named in the "email calibration to developer" subject/body so Colin can
        // see at a glance which radio a submission is for.
        public string RadioModel { get; private set; } = "";

        public async Task OnGetAsync()
        {
            var settings = await _settings.GetSettingsAsync();
            RadioModel = settings.RadioModel;
        }
    }
}
