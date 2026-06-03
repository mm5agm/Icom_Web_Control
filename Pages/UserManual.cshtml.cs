using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// Renders the USER_MANUAL.md file from the project root as HTML on each
    /// request. Single source of truth — every edit to the markdown shows up
    /// in the in-app manual immediately, and the GitHub-rendered version
    /// stays in lockstep with no duplicate-maintenance burden.
    /// </summary>
    public class UserManualModel : PageModel
    {
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<UserManualModel> _logger;

        // Built once at startup. Markdig pipelines are immutable and
        // thread-safe, so we keep a static instance.
        private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()                                       // tables, task lists, etc.
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)              // heading IDs that match the USER_MANUAL.md's own TOC anchors
            .UseEmojiAndSmiley()
            .Build();

        // Convert "src=\"pictures/foo.png\"" → "src=\"/pictures/foo.png\""
        // and same for href. Without the leading slash the browser resolves
        // relative to /UserManual and the request 404s. The pictures/ folder
        // is served from a dedicated static-file provider at /pictures (see
        // Program.cs).
        private static readonly Regex PicturesPathFix = new(
            @"(?<attr>(?:src|href)=)""pictures/",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public UserManualModel(IWebHostEnvironment env, ILogger<UserManualModel> logger)
        {
            _env = env;
            _logger = logger;
        }

        public string ManualHtml { get; private set; } = "";
        public string LoadError  { get; private set; } = "";

        public void OnGet()
        {
            var path = Path.Combine(_env.ContentRootPath, "USER_MANUAL.md");
            if (!System.IO.File.Exists(path))
            {
                LoadError = $"USER_MANUAL.md not found at {path}.";
                return;
            }
            try
            {
                var markdown = System.IO.File.ReadAllText(path);
                var html = Markdown.ToHtml(markdown, _pipeline);
                ManualHtml = PicturesPathFix.Replace(html, "${attr}\"/pictures/");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to render USER_MANUAL.md");
                LoadError = $"Failed to render the manual: {ex.Message}";
            }
        }
    }
}
