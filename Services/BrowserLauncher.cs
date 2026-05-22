// BrowserLauncher.cs
using System.Diagnostics;

namespace Yaesu_Web_Control.Services
{
    public class BrowserLauncher
    {
        private bool _opened = false;
        private readonly object _lock = new();

        public void OpenOnce(string url)
        {
            lock (_lock)
            {
                if (_opened) return;
                _opened = true;
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch { /* Optionally log error */ }
            }
        }
    }
}