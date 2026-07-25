using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;
using System.Text.Json.Nodes;
using IOFile = System.IO.File;

namespace Icom_Web_Control.Pages
{
    public class LabelsModel : PageModel
    {
        private readonly IWebHostEnvironment _env;

        [BindProperty]
        public Dictionary<string, string> Labels { get; set; } = new();

        [TempData]
        public string? StatusMessage { get; set; }

        private static readonly string UserLabelsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Icom Web Control", "labels.json");

        public LabelsModel(IWebHostEnvironment env)
        {
            _env = env;
        }

        private string DefaultLabelsPath => Path.Combine(_env.WebRootPath, "i18n", "labels.default.json");

        private JsonObject LoadLabels()
        {
            try
            {
                string path = IOFile.Exists(UserLabelsPath) ? UserLabelsPath : DefaultLabelsPath;
                return JsonNode.Parse(IOFile.ReadAllText(path))!.AsObject();
            }
            catch
            {
                return JsonNode.Parse(IOFile.ReadAllText(DefaultLabelsPath))!.AsObject();
            }
        }

        public IActionResult OnGet()
        {
            var root = LoadLabels();
            foreach (var section in root)
            {
                if (section.Key == "_readme") continue;
                if (section.Value is JsonObject sectionObj)
                {
                    foreach (var entry in sectionObj)
                        Labels[$"{section.Key}.{entry.Key}"] = entry.Value?.GetValue<string>() ?? "";
                }
            }
            return Page();
        }

        public IActionResult OnPost()
        {
            // Read-modify-write: load existing JSON so unknown/custom keys are preserved.
            var root = LoadLabels();

            foreach (var kvp in Labels)
            {
                var dot = kvp.Key.IndexOf('.');
                if (dot < 0) continue;
                var section = kvp.Key[..dot];
                var key = kvp.Key[(dot + 1)..];
                if (root[section] is JsonObject sectionObj)
                    sectionObj[key] = JsonValue.Create(kvp.Value);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(UserLabelsPath)!);
            IOFile.WriteAllText(UserLabelsPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            StatusMessage = "✓ Labels saved. Reload the main page for changes to take effect.";
            return RedirectToPage();
        }

        public IActionResult OnPostReset()
        {
            if (IOFile.Exists(UserLabelsPath))
                IOFile.Delete(UserLabelsPath);

            StatusMessage = "✓ Labels reset to defaults. Reload the main page for changes to take effect.";
            return RedirectToPage();
        }
    }
}
