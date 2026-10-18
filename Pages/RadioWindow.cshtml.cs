using Icom_Web_Control.Services;

namespace Icom_Web_Control.Pages
{
    /// <summary>
    /// One of the radio panel's windows (CW, RTTY, Memories, Meters, RX Setup)
    /// in a browser window of its own, for a second screen:
    /// /RadioWindow?g=meters. Drawn by core/js/radio-panel/window-popout.js and
    /// filled by wwwroot/js/ui/radio-window-icom.js. Opened from the WINDOW
    /// key in the window's title bar on /radio.
    /// </summary>
    public class RadioWindowModel : IndexModel
    {
        // Same figures as RadioPanelModel: the IC-7300 family.
        public int MaxPowerW => 100;
        public long MaxHz => 74_800_000;

        public RadioWindowModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }
    }
}
