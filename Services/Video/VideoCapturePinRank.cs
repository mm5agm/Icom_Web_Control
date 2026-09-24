namespace Icom_Web_Control.Services.Video
{
    /// <summary>
    /// Radio Display pin ranking shared by Windows DirectShow MJPEG, Media
    /// Foundation, and the OpenCV fallback. Mirrors <c>MacAvFoundationFps.m</c>
    /// so 15 / 30 / 60 share one size: a panel-aspect format at least 800 px
    /// wide when the dongle has one. 800 is a floor — 640×480 is last-resort only.
    /// <para>
    /// <b>IWC divergence from YWC (the only one in this folder):</b> a Yaesu
    /// panel is 4:3 (800×600) or 5:3 (800×480), so YWC's ranking prefers a
    /// 4:3 pin. The IC-7300 MkII sends its HDMI mirror at 16:9 (1280×720 by
    /// default, its only documented option), and a 16:9 picture captured on
    /// a 4:3 pin is stretched a third too tall by the dongle's scaler with
    /// nothing on screen to say so. The two numbers that encode the panel
    /// shape are therefore named here and set for 16:9. When this folder
    /// moves to core they become a per-app parameter.
    /// </para>
    /// </summary>
    internal static class VideoCapturePinRank
    {
        public const int MinWidth = 800;

        /// <summary>Aspect window a pin must fall in to count as "the radio's
        /// panel shape". 16:9 = 1.78; YWC uses 1.20–1.72 for 4:3 and 5:3.</summary>
        private const double PanelAspectMin = 1.70;
        private const double PanelAspectMax = 1.85;

        /// <summary>Height a panel-shaped pin scales to at the encode width,
        /// used to rank pins by how little scaling they need. 9/16 of the
        /// width; YWC uses 600 (4:3 at 800).</summary>
        private static int PanelHeightAt(int width) => (int)Math.Round(width * 9.0 / 16.0);

        public readonly record struct Pin(int Width, int Height, double Fps, bool Jpeg);

        public static int EncodeTarget(int maxWidth)
        {
            var t = maxWidth > 0 ? maxWidth : 800;
            if (t < 800)
                t = 800;
            if (t > 1280)
                t = 1280;
            return t;
        }

        /// <summary>
        /// 16:9 (1280×720, 1920×1080). A 4:3 pin (800×600) is used only when
        /// no panel-aspect pin ≥800 exists — and then the picture is squashed.
        /// </summary>
        public static bool IsRadioPanelAspect(int width, int height)
        {
            if (height < 1)
                return false;
            var a = (double)width / height;
            return a is >= PanelAspectMin and <= PanelAspectMax;
        }

        public static int Rank(bool jpeg, int width, int height, int minWidth, int target)
        {
            if (width < minWidth)
                return width + (jpeg ? 50 : 0);

            var scaledH = (int)Math.Round((double)target * height / width);
            var closeness = 2_000_000 - Math.Abs(width - target);
            closeness += 400_000 - Math.Abs(scaledH - PanelHeightAt(target)) * 400;
            if (IsRadioPanelAspect(width, height))
                closeness += 500_000;
            if (jpeg)
                closeness += 100_000;
            return closeness;
        }

        /// <summary>
        /// One size for 15 / 30 / 60. Probe 4:3 ≥800 at 15, then 30, then 60
        /// (do not require 60 — that is how 16:9 720p/1080p won). Then any
        /// aspect. <paramref name="requestedFps"/> is only the last-resort probe.
        /// </summary>
        public static Pin? PickSize(IReadOnlyList<Pin> pins, int requestedFps, int maxWidth)
        {
            if (pins.Count == 0)
                return null;

            var target = EncodeTarget(maxWidth);
            var wantFps = AnyCanDo(pins, 60) ? 60
                : AnyCanDo(pins, 30) ? 30
                : AnyCanDo(pins, 15) ? 15
                : requestedFps;

            return PickFormat(pins, 15, MinWidth, target, panelAspectOnly: true)
                ?? PickFormat(pins, 30, MinWidth, target, panelAspectOnly: true)
                ?? PickFormat(pins, 60, MinWidth, target, panelAspectOnly: true)
                ?? PickFormat(pins, wantFps, MinWidth, target, panelAspectOnly: false)
                ?? PickFormat(pins, requestedFps, MinWidth, target, panelAspectOnly: false);
        }

        /// <summary>
        /// Largest MJPEG mode the device offers — normally the one it passes
        /// through without its internal scaler. Used after the device is caught
        /// merging frames in a scaled mode; the picture is scaled down here
        /// instead, which costs CPU but is the only correct output such a
        /// device produces.
        /// </summary>
        public static Pin? PickNativeMjpeg(IReadOnlyList<Pin> pins)
        {
            Pin? best = null;
            foreach (var p in pins)
            {
                if (!p.Jpeg)
                    continue;
                if (best is null ||
                    (long)p.Width * p.Height > (long)best.Value.Width * best.Value.Height ||
                    ((long)p.Width * p.Height == (long)best.Value.Width * best.Value.Height &&
                     p.Fps > best.Value.Fps))
                    best = p;
            }

            return best;
        }

        /// <summary>
        /// The MJPEG pin at exactly the size the operator chose in the Radio
        /// Display panel, at the frame rate nearest <paramref name="requestedFps"/>.
        /// Null when the spec is auto/unparseable or the device has no
        /// compressed pin that size, in which case the caller falls back to the
        /// ranked pick rather than failing to open.
        /// </summary>
        public static Pin? PickRequestedSize(
            IReadOnlyList<Pin> pins, string? requestedSize, int requestedFps)
        {
            if (!VideoSizeOptions.TryParse(requestedSize, out var w, out var h))
                return null;

            return NearestFps(pins, w, h, jpeg: true, requestedFps);
        }

        /// <summary>
        /// Compressed MJPEG pin only. Never prefer 4:3 YUY2 over JPEG — that
        /// is the USB2 ~22 fps trap. 800×600 JPEG wins when the dongle has it
        /// (OBS); otherwise any JPEG ≥800 (16:9 720p/1080p).
        /// </summary>
        public static Pin? PickMjpegCapture(IReadOnlyList<Pin> pins, int requestedFps, int maxWidth)
        {
            var jpeg = pins.Where(p => p.Jpeg).ToList();
            if (jpeg.Count == 0)
                return null;

            var size = PickSize(jpeg, requestedFps, maxWidth);
            if (size is null)
                return null;

            return NearestFps(jpeg, size.Value.Width, size.Value.Height, jpeg: true, requestedFps)
                ?? size.Value;
        }

        /// <summary>
        /// Media Foundation / DirectShow passthrough pin, or null when the
        /// device has no MJPEG type (OpenCV YUY2 fallback).
        /// </summary>
        public static Pin? PickMjpegPassthrough(IReadOnlyList<Pin> pins, int requestedFps, int maxWidth) =>
            PickMjpegCapture(pins, requestedFps, maxWidth);

        /// <summary>
        /// Linux: honour the panel FPS. <see cref="PickSize"/> locks 800×600
        /// because that pin can do 15, then nearest-to-30 becomes 20 on
        /// dongles whose 800×600 MJPEG type tops out at 20. Prefer a JPEG pin
        /// that can actually run at <paramref name="requestedFps"/> — 640×480@30
        /// beats 800×600@20 when the operator asked for 30.
        /// </summary>
        public static Pin? PickMjpegCaptureMeetingFps(
            IReadOnlyList<Pin> pins, int requestedFps, int maxWidth)
        {
            var jpeg = pins.Where(p => p.Jpeg).ToList();
            if (jpeg.Count == 0)
                return null;

            var target = EncodeTarget(maxWidth);
            var size = PickFormat(jpeg, requestedFps, MinWidth, target, panelAspectOnly: true)
                ?? PickFormat(jpeg, requestedFps, MinWidth, target, panelAspectOnly: false)
                ?? PickFormat(jpeg, requestedFps, minWidth: 2, target, panelAspectOnly: false)
                ?? PickSize(jpeg, requestedFps, maxWidth);
            if (size is null)
                return null;

            return NearestFps(jpeg, size.Value.Width, size.Value.Height, jpeg: true, requestedFps)
                ?? size.Value;
        }

        /// <summary>
        /// Discrete type at the locked size whose advertised rate is nearest
        /// <paramref name="requestedFps"/> (59.94 vs 60 uses the native ratio).
        /// </summary>
        public static Pin? NearestFps(IReadOnlyList<Pin> pins, int width, int height, bool jpeg, int requestedFps)
        {
            Pin? best = null;
            var bestDiff = double.MaxValue;
            foreach (var p in pins)
            {
                if (p.Width != width || p.Height != height || p.Jpeg != jpeg)
                    continue;
                var diff = Math.Abs(p.Fps - requestedFps);
                if (best is not null && diff >= bestDiff)
                    continue;
                best = p;
                bestDiff = diff;
            }

            return best;
        }

        /// <summary>
        /// OpenCV YUY2 fallback: always 800×600 so 15 / 30 / 60 share size.
        /// Do not request 720p/1080p (uncompressed 720p is the 10 fps regression).
        /// </summary>
        public static (int Width, int Height) PreferredUncompressedSize(int maxWidth)
        {
            _ = EncodeTarget(maxWidth);
            return (800, 600);
        }

        private static bool AnyCanDo(IReadOnlyList<Pin> pins, int fps) =>
            Groups(pins).Any(g => FormatCanDo(g.MaxFps, fps));

        private static Pin? PickFormat(
            IReadOnlyList<Pin> pins,
            int fps,
            int minWidth,
            int target,
            bool panelAspectOnly)
        {
            Pin? chosen = null;
            var bestRank = int.MinValue;
            foreach (var g in Groups(pins))
            {
                if (!FormatCanDo(g.MaxFps, fps))
                    continue;
                if (g.Width < 2 || g.Height < 2)
                    continue;
                if (panelAspectOnly)
                {
                    if (g.Width < minWidth)
                        continue;
                    if (!IsRadioPanelAspect(g.Width, g.Height))
                        continue;
                }

                var rank = Rank(g.Jpeg, g.Width, g.Height, minWidth, target);
                if (chosen is not null && rank <= bestRank)
                    continue;

                chosen = new Pin(g.Width, g.Height, g.MaxFps, g.Jpeg);
                bestRank = rank;
            }

            return chosen;
        }

        private static IEnumerable<(int Width, int Height, bool Jpeg, double MaxFps)> Groups(IReadOnlyList<Pin> pins) =>
            pins
                .Where(p => p.Width >= 2 && p.Height >= 2)
                .GroupBy(p => (p.Width, p.Height, p.Jpeg))
                .Select(g => (g.Key.Width, g.Key.Height, g.Key.Jpeg, g.Max(p => p.Fps)));

        /// <summary>
        /// True when the pin can run at least <paramref name="fps"/> (Mac
        /// <c>format_can_do</c>: max ≥ request). Unknown/zero rate counts as
        /// capable so a type with no MF_MT_FRAME_RATE still participates.
        /// </summary>
        private static bool FormatCanDo(double maxFps, int fps) =>
            maxFps <= 0 || maxFps + 0.51 >= fps;
    }
}
