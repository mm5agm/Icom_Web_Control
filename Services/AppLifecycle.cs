// AppLifecycle.cs

namespace Icom_Web_Control.Services
{
    /// <summary>
    /// How the host is stopping. Settings' Restart Now sets RestartRequested
    /// before it stops the host, so a restart leaves the radio on even when
    /// PowerOffAtExit is set: the app is coming straight back.
    /// </summary>
    public static class AppLifecycle
    {
        public static volatile bool RestartRequested;
    }
}
