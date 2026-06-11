using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    public class AboutModel : PageModel
    {
        private readonly ISettingsService _settingsService;
        private readonly RadioStateService _radioStateService;

        public AboutModel(ISettingsService settingsService, RadioStateService radioStateService)
        {
            _settingsService = settingsService;
            _radioStateService = radioStateService;
        }

        // Surfaced to the view for the Diagnostics block.
        public string Version           { get; private set; } = AppVersion.Current;
        public string ReleaseDate       { get; private set; } = AppVersion.ReleaseDate;
        public string RadioModel        { get; private set; } = "—";
        public string BandPlan          { get; private set; } = "—";
        public string SerialPort        { get; private set; } = "—";
        public int    BaudRate          { get; private set; }
        public bool   IsConnected       { get; private set; }
        public string SdrDevice         { get; private set; } = "(none configured)";
        public string DxClusterHost     { get; private set; } = "(off)";
        public string DxClusterCallsign { get; private set; } = "(blank)";
        public string DotNetVersion     { get; private set; } = System.Environment.Version.ToString();
        public string OsDescription     { get; private set; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

        public async Task OnGetAsync()
        {
            var s = await _settingsService.GetSettingsAsync();
            RadioModel  = s.RadioModel;
            BandPlan    = s.BandPlan;
            SerialPort  = s.SerialPort;
            BaudRate    = s.BaudRate;
            IsConnected = _radioStateService.IsConnected;

            // Diagnostics shows whichever SDR(s) are configured. Two VFOs as
            // of v2.3.0; bug reports from users with one configured still show
            // the right device.
            var sdrKeys = new List<string>();
            if (!string.IsNullOrWhiteSpace(s.SdrDeviceKeyA)) sdrKeys.Add($"A: {s.SdrDeviceKeyA}");
            if (!string.IsNullOrWhiteSpace(s.SdrDeviceKeyB)) sdrKeys.Add($"B: {s.SdrDeviceKeyB}");
            if (sdrKeys.Count > 0)
                SdrDevice = string.Join("   ", sdrKeys);
            if (s.DxClusterEnabled && !string.IsNullOrWhiteSpace(s.DxClusterHost))
                DxClusterHost = $"{s.DxClusterHost}:{s.DxClusterPort}";
            if (!string.IsNullOrWhiteSpace(s.DxClusterLoginCallsign))
                DxClusterCallsign = s.DxClusterLoginCallsign;
        }
    }
}
