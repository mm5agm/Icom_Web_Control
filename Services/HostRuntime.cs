namespace Icom_Web_Control.Services;

/// <summary>
/// Detects a container, where there is no desktop: no browser to open, and
/// nothing should exit because the last tab closed. From YWC (d348a50f).
/// </summary>
public static class HostRuntime
{
    public static bool IsContainer { get; } =
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase)
        || File.Exists("/.dockerenv");
}
