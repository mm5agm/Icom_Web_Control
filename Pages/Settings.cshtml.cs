using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Models;
using Yaesu_Web_Control.Services;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Yaesu_Web_Control.Pages
{
    public class SettingsModel : PageModel
    {
        private readonly ISettingsService _settingsService;
        private readonly ILogger<SettingsModel> _logger;
        private readonly RadioInitializationService _radioInitializationService;

        [BindProperty]
        public ApplicationSettings Settings { get; set; } = new();

        [TempData]
        public string? StatusMessage { get; set; } = string.Empty;

        public List<string> NetworkAddresses { get; set; } = new();

        public SettingsModel(
            ISettingsService settingsService,
            ILogger<SettingsModel> logger,
            RadioInitializationService radioInitializationService)
        {
            _settingsService = settingsService;
            _logger = logger;
            _radioInitializationService = radioInitializationService;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            Settings = await _settingsService.GetSettingsAsync();
            Settings.BandPlan = Settings.BandPlan switch { "UK" => "Region1", "USA" => "Region2", var v => v };
            NetworkAddresses = GetLocalIPAddresses();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // SdrDeviceKey is intentionally allowed to be empty (empty = no SDR configured).
            // Must be removed BEFORE ModelState.IsValid is checked, because the implicit
            // [Required] from <Nullable>enable</Nullable> would otherwise reject an empty
            // string and silently prevent the save.
            ModelState.Remove("Settings.SdrDeviceKey");

            if (!ModelState.IsValid)
            {
                NetworkAddresses = GetLocalIPAddresses();
                return Page();
            }

            // Validate Serial Port format
            if (!Settings.SerialPort.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError("Settings.SerialPort",
                    "Serial port must start with 'COM' (e.g., COM3, COM4).");
                NetworkAddresses = GetLocalIPAddresses();
                return Page();
            }

            try
            {
                // Read-modify-write: load current settings so fields not on this form
                // (external app paths, LastRadioState, etc.) are not overwritten with defaults.
                var current = await _settingsService.GetSettingsAsync();
                current.RadioModel        = Settings.RadioModel;
                current.SerialPort        = Settings.SerialPort;
                current.BaudRate          = Settings.BaudRate;
                current.WebAddress        = Settings.WebAddress;
                current.SdrDeviceKey      = Settings.SdrDeviceKey ?? string.Empty;
                current.SdrIfFrequencyHz  = Settings.SdrIfFrequencyHz;
                current.SdrSampleRateHz   = Settings.SdrSampleRateHz;
                current.SdrFftSize        = Settings.SdrFftSize;
                current.BandPlan          = Settings.BandPlan;
                // MP comes fully loaded; D has 600Hz standard plus 1.2kHz/300Hz optional.
                if (Settings.RadioModel == "FTdx101MP")
                {
                    current.InstalledRoofingFilters = new List<string> { "6", "7", "8", "9", "A" };
                }
                else if (Settings.RadioModel == "FTdx101D")
                {
                    var optionalSelected = Settings.InstalledRoofingFilters ?? new List<string>();
                    current.InstalledRoofingFilters = new List<string> { "6", "7", "9" }
                        .Concat(optionalSelected.Where(f => f is "8" or "A"))
                        .Distinct().ToList();
                }
                else if (Settings.RadioModel == "FTdx10")
                {
                    // Standard filters (1=15kHz, 2=6kHz, 3=3kHz) are always available.
                    // Optional: 4=1.2kHz (YF-130CN), 5=300Hz (YF-130CW).
                    var optionalSelected = Settings.InstalledRoofingFilters ?? new List<string>();
                    current.InstalledRoofingFilters = new List<string> { "1", "2", "3" }
                        .Concat(optionalSelected.Where(f => f is "4" or "5"))
                        .Distinct().ToList();
                }
                else if (Settings.RadioModel == "FT-710")
                {
                    current.InstalledRoofingFilters = new List<string>();
                }
                else if (Settings.RadioModel == "FTDX3000")
                {
                    // Standard: 0=Auto, 1=15kHz, 2=6kHz, 3=3kHz always present.
                    // Optional: 4=600Hz (YH-77SDE), 5=300Hz (YH-77SDE narrow).
                    var optionalSelected = Settings.InstalledRoofingFilters ?? new List<string>();
                    current.InstalledRoofingFilters = new List<string> { "0", "1", "2", "3" }
                        .Concat(optionalSelected.Where(f => f is "4" or "5"))
                        .Distinct().ToList();
                }
                if (Settings.CwMessages != null && Settings.CwMessages.Count == 5)
                    current.CwMessages = Settings.CwMessages;

                await _settingsService.SaveSettingsAsync(current);

                // Reset initialization status so app will try again
                Yaesu_Web_Control.Services.AppStatus.InitializationStatus = "initializing";

                // Automatic retry: trigger radio initialization
                await _radioInitializationService.InitializeRadioAsync();

                var accessInfo = Settings.WebAddress == "0.0.0.0"
                    ? "all network interfaces"
                    : Settings.WebAddress == "localhost"
                        ? "localhost only"
                        : $"{Settings.WebAddress}";

                StatusMessage = $"✓ Settings saved successfully! Web server will be available on {accessInfo} at port 8080. " +
                    "Please restart the application for web server changes to take effect.";

                _logger.LogInformation(
                    "Settings updated: RadioModel={RadioModel}, SerialPort={SerialPort}, BaudRate={BaudRate}, WebAddress={WebAddress}",
                    Settings.RadioModel, Settings.SerialPort, Settings.BaudRate, Settings.WebAddress);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving settings");
                StatusMessage = "❌ Error saving settings. Please try again.";
                ModelState.AddModelError(string.Empty, "An error occurred while saving settings.");
                NetworkAddresses = GetLocalIPAddresses();
                return Page();
            }

            return RedirectToPage();
        }

        private List<string> GetLocalIPAddresses()
        {
            var addresses = new List<string>();

            try
            {
                var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                                ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                foreach (var networkInterface in networkInterfaces)
                {
                    var ipProperties = networkInterface.GetIPProperties();
                    foreach (var ip in ipProperties.UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork) // IPv4 only
                        {
                            addresses.Add(ip.Address.ToString());
                        }
                    }
                }

                _logger.LogDebug("Detected network addresses: {Addresses}", string.Join(", ", addresses));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to detect network addresses");
            }

            return addresses;
        }
    }
}