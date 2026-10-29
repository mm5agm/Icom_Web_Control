using Icom_Web_Control.Services;

namespace Icom_Web_Control.Pages
{
    /// <summary>
    /// The RTTY Contest view at /rtty-contest - a view beside Classic,
    /// Experimental and Radio, with only what a contest RTTY operator uses:
    /// the spectrum over the waterfall, the RTTY tuner and a small strip.
    /// Drawn by core/js/rtty-contest/contest-view.js and fed by
    /// wwwroot/js/ui/rtty-contest-icom.js from the radio's own scope.
    /// </summary>
    public class RttyContestModel : IndexModel
    {
        // The IC-7300 family is a 100 W, single-receiver radio, 30 kHz-74.8 MHz.
        public int MaxPowerW => 100;
        public long MinHz => 30_000;
        public long MaxHz => 74_800_000;

        public RttyContestModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }

        protected override string? ViewKey => Models.AppViews.RttyContest;
    }
}
