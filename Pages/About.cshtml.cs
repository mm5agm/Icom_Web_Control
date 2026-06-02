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
        public string Version       { get; private set; } = AppVersion.Current;
        public string ReleaseDate   { get; private set; } = AppVersion.ReleaseDate;
        public string RadioModel    { get; private set; } = "—";
        public string SerialPort    { get; private set; } = "—";
        public int    BaudRate      { get; private set; }
        public bool   IsConnected   { get; private set; }
        public string DotNetVersion { get; private set; } = System.Environment.Version.ToString();
        public string OsDescription { get; private set; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

        public async Task OnGetAsync()
        {
            var s = await _settingsService.GetSettingsAsync();
            RadioModel  = s.RadioModel;
            SerialPort  = s.SerialPort;
            BaudRate    = s.BaudRate;
            IsConnected = _radioStateService.IsConnected;
        }
    }
}
