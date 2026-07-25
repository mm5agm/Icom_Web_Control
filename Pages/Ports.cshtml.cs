using Microsoft.AspNetCore.Mvc.RazorPages;
using System.IO.Ports;
using System.Collections.Generic;

namespace Icom_Web_Control.Pages
{
    public class PortsModel : PageModel
    {
        public List<string> AvailablePorts { get; private set; } = new();
        public bool Com6Present { get; private set; }

        public void OnGet()
        {
            AvailablePorts = new List<string>(SerialPort.GetPortNames());
            Com6Present = AvailablePorts.Contains("COM6");
        }
    }
}