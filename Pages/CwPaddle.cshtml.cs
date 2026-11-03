using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Icom_Web_Control.Pages
{
    // CW Paddle (YWC Discussion #262): a MIDI paddle read in the browser, an
    // iambic keyer and sidetone. The keying goes over RadioHub to
    // CwKeyingService, so the page needs nothing from its model.
    public class CwPaddleModel : PageModel
    {
        public void OnGet()
        {
        }
    }
}
