using Icom_Web_Control.Models;
using Icom_Web_Control.Services.Video;

namespace Icom_Web_Control.Services
{
    /// <summary>
    /// IWC's side of the Radio Display settings seam: maps the six
    /// <c>Video*</c> fields on <see cref="ApplicationSettings"/> to and from
    /// <see cref="VideoSettings"/>. This is the one file that would stay in
    /// the app if <c>Services/Video</c> ever moved to core.
    /// </summary>
    public sealed class ApplicationVideoSettingsSource : IVideoSettingsSource
    {
        private readonly ISettingsService _settings;

        public ApplicationVideoSettingsSource(ISettingsService settings)
        {
            _settings = settings;
        }

        public async Task<VideoSettings> GetAsync() => Map(await _settings.GetSettingsAsync());

        public VideoSettings GetCached() => Map(_settings.GetCachedSettings());

        public async Task SaveAsync(VideoSettings video)
        {
            // Read-modify-write so a concurrent save of an unrelated setting is
            // not clobbered — the same discipline SettingsService itself uses.
            var current = await _settings.GetSettingsAsync();
            current.VideoDisplayEnabled = video.DisplayEnabled;
            current.VideoCaptureDeviceKey = video.DeviceKey ?? "";
            current.VideoCaptureSize = video.CaptureSize ?? "";
            current.VideoMaxWidth = video.MaxWidth;
            current.VideoTargetFps = video.TargetFps;
            current.VideoJpegQuality = video.JpegQuality;
            await _settings.SaveSettingsAsync(current);
        }

        private static VideoSettings Map(ApplicationSettings s) => new(
            s.VideoDisplayEnabled,
            s.VideoCaptureDeviceKey ?? "",
            s.VideoCaptureSize ?? "",
            s.VideoMaxWidth,
            s.VideoTargetFps,
            s.VideoJpegQuality);
    }
}
