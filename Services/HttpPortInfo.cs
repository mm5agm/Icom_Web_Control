namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Single source of truth for the HTTP port YWC is actually listening on.
    /// Resolved once at startup in <c>Program.cs</c> by probing the user's
    /// configured port and (if necessary) the nine fallbacks above it, then
    /// registered as a singleton so every consumer — Kestrel, the browser
    /// launcher, the system-tray tooltip and right-click menu, and the
    /// Settings page UI — reads the same value.
    ///
    /// Replaces the previous pattern where the port was hardcoded as "8080"
    /// in five different places that had to be kept in sync by hand
    /// (Issue #13).
    /// </summary>
    public sealed class HttpPortInfo
    {
        public int Port { get; }

        /// <summary>Convenience: e.g. <c>http://localhost:8080</c>.</summary>
        public string RootUrl => $"http://localhost:{Port}";

        public HttpPortInfo(int port)
        {
            Port = port;
        }
    }
}
