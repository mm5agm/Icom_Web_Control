using Microsoft.AspNetCore.Mvc.RazorPages;
using System.IO.Ports;
using System.Collections.Generic;
using System.Threading.Tasks;
using Icom_Web_Control.Services;

namespace Icom_Web_Control.Pages
{
    public class PortsModel : PageModel
    {
        private readonly ISettingsService _settingsService;

        public PortsModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        public List<string> AvailablePorts { get; private set; } = new();
        public string ConfiguredPort { get; private set; } = "";
        public bool ConfiguredPresent { get; private set; }

        public async Task OnGetAsync()
        {
            AvailablePorts = new List<string>(SerialPort.GetPortNames());
            ConfiguredPort = (await _settingsService.GetSettingsAsync()).SerialPort;
            ConfiguredPresent = AvailablePorts.Contains(ConfiguredPort);
        }
    }
}
