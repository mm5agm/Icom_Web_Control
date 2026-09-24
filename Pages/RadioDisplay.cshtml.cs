using Microsoft.AspNetCore.Mvc.RazorPages;
using Icom_Web_Control.Services;

namespace Icom_Web_Control.Pages
{
    public class RadioDisplayModel : PageModel
    {
        private readonly ISettingsService _settingsService;

        public RadioDisplayModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        public string RadioModel { get; set; } = "IC-7300MK2";

        public async Task OnGetAsync()
        {
            var settings = await _settingsService.GetSettingsAsync();
            RadioModel = settings.RadioModel ?? RadioModel;
        }
    }
}
