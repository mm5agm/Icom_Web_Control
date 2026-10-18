using Icom_Web_Control.Services;

namespace Icom_Web_Control.Pages
{
    /// <summary>
    /// The radio panel's spectrum in a browser window of its own, at
    /// /RadioSpectrum. Drawn by core/js/radio-panel/spectrum-popout.js and fed
    /// by wwwroot/js/ui/radio-spectrum-icom.js from the radio's own scope.
    /// </summary>
    public class RadioSpectrumModel : IndexModel
    {
        public RadioSpectrumModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }
    }
}
