namespace Icom_Web_Control.Services.Video
{
    /// <summary>
    /// The six Radio Display settings, as the capture layer sees them.
    /// </summary>
    /// <remarks>
    /// This record and <see cref="IVideoSettingsSource"/> are the only thing
    /// <c>Services/Video</c> knows about the host application's settings. The
    /// capture service, the sessions and <c>VideoController</c> read and write
    /// through this seam and never touch <c>ApplicationSettings</c>, so the
    /// whole folder could move to Radio_Web_Control_Core with only the
    /// adapter (<c>ApplicationVideoSettingsSource</c>) staying behind. Keep it
    /// that way: anything the video layer needs from the app comes in here.
    /// </remarks>
    public sealed record VideoSettings(
        bool DisplayEnabled,
        string DeviceKey,
        string CaptureSize,
        int MaxWidth,
        int TargetFps,
        int JpegQuality)
    {
        /// <summary>Feature off, no device — what the capture loop assumes when a read fails.</summary>
        public static VideoSettings Disabled { get; } = new(false, "", "", 1280, 15, 85);
    }

    /// <summary>
    /// Where the video layer gets its settings from. The host application
    /// supplies one implementation backed by its own settings store.
    /// </summary>
    public interface IVideoSettingsSource
    {
        /// <summary>Authoritative read; may touch disk.</summary>
        Task<VideoSettings> GetAsync();

        /// <summary>
        /// Memory snapshot only. The capture loop calls this every
        /// <c>SettingsRefreshMs</c> from its STA thread, so it must never
        /// block on IO.
        /// </summary>
        VideoSettings GetCached();

        /// <summary>Persist a changed set (the controller's device / fps / size / quality posts).</summary>
        Task SaveAsync(VideoSettings settings);
    }
}
