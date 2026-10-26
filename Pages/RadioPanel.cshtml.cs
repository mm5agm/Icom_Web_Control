using Icom_Web_Control.Services;

namespace Icom_Web_Control.Pages
{
    /// <summary>
    /// Radio-style front panel at /radio - a second view beside Classic. Same
    /// server state as Index; the page itself is drawn by the shared
    /// core/js/radio-panel module and fed by wwwroot/js/ui/radio-panel-icom.js.
    /// </summary>
    public class RadioPanelModel : IndexModel
    {
        // The IC-7300 family is a 100 W, single-receiver radio, 30 kHz-74.8 MHz.
        public int MaxPowerW => 100;
        public long MaxHz => 74_800_000;

        public RadioPanelModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }

        protected override string? ViewKey => Models.AppViews.Radio;
    }
}
