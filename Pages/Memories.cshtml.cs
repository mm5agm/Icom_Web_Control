using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using FTdx101_WebApp.Services;

namespace FTdx101_WebApp.Pages
{
    public class MemoriesModel : PageModel
    {
        private readonly MemoryService _memoryService;

        [TempData]
        public string? StatusMessage { get; set; }

        [BindProperty]
        public List<AppMemory> Memories { get; set; } = new();

        public MemoriesModel(MemoryService memoryService)
        {
            _memoryService = memoryService;
        }

        public IActionResult OnGet()
        {
            Memories = _memoryService.GetAll().ToList();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // Remove empty rows (no label and no frequency)
            Memories = Memories
                .Where(m => m.FrequencyHz > 0 || !string.IsNullOrWhiteSpace(m.Label))
                .ToList();

            // Assign sort order from list position
            for (int i = 0; i < Memories.Count; i++)
                Memories[i].SortOrder = i + 1;

            await _memoryService.ReplaceAllAsync(Memories);
            StatusMessage = $"✓ {Memories.Count} memories saved.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDeleteAsync(int id)
        {
            await _memoryService.DeleteAsync(id);
            StatusMessage = "✓ Memory deleted.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostAddAsync()
        {
            await _memoryService.AddAsync(new AppMemory
            {
                Label = "New Memory",
                FrequencyHz = 14074000,
                Mode = "USB"
            });
            return RedirectToPage();
        }
    }
}
