// LaunchPage.cs
namespace Icom_Web_Control.Services
{
    // A page named on the command line, so a desktop shortcut can open IWC
    // straight on it, e.g. Icom_Web_Control.exe rtty-contest. YWC's since
    // v2.5.4-pre8 (Bruce VK2RT, YWC Discussion #257). Only these names count;
    // anything else is left to the host's own command line. With none, the
    // app opens /start, the view last used.
    public static class LaunchPage
    {
        private static readonly Dictionary<string, string> Pages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["rtty-contest"] = "/rtty-contest",
            ["radio"]        = "/radio",
            ["classic"]      = "/",
            ["experimental"] = "/flexui",
            ["settings"]     = "/Settings",
        };

        // The page's path, or null when none was named.
        public static string? Path { get; private set; }

        public static void ReadFrom(string[] args)
        {
            Path = null;
            foreach (var arg in args)
            {
                var name = arg.TrimStart('-', '/');
                if (Pages.TryGetValue(name, out var path)) { Path = path; return; }
            }
        }

        // The URL to open: the named page under rootUrl, or /start.
        public static string At(string rootUrl) =>
            rootUrl.TrimEnd('/') + (Path is null ? "/start" : Path == "/" ? "/" : Path);
    }
}
