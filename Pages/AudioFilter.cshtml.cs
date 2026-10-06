using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Icom_Web_Control.Pages
{
    // One VFO's Twin PBT in a window of its own, so it can go on a second
    // monitor. The radio holds the two shifts, so this page and the main
    // page's dialog read the same values over /api/cat/pbt.
    public class AudioFilterModel : PageModel
    {
        [BindProperty(SupportsGet = true, Name = "vfo")]
        public string? VfoQuery { get; set; }

        public string Vfo => string.Equals(VfoQuery, "B", System.StringComparison.OrdinalIgnoreCase) ? "B" : "A";

        public void OnGet()
        {
        }
    }
}
