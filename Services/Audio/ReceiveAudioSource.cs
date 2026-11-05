// The source of receive audio for the CW reader and the RTTY services. On
// Windows that is the WinMM device. Elsewhere there is no audio yet, so the
// features start and say why they hear nothing.
#if WINDOWS
global using ReceiveAudioSource = Icom_Web_Control.Services.Cw.WaveInCwAudioSource;
#else
global using ReceiveAudioSource = Icom_Web_Control.Services.Audio.NoReceiveAudioSource;

using RadioWebControl.Core.Services.Cw;

namespace Icom_Web_Control.Services.Audio
{
    /// <summary>Stands in for the recording device where IWC has no audio yet.</summary>
    public sealed class NoReceiveAudioSource : ICwAudioSource
    {
        public const int Rate = 48_000;

        public int SampleRate => Rate;
        public bool IsRunning { get; private set; }
        public long DroppedFrames => 0;
        public bool DeviceOpen => false;
        public string? DeviceName => null;
        public string? CaptureError { get; private set; }

        public event Action<ReadOnlyMemory<float>>? FrameAvailable { add { } remove { } }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            CaptureError = "Receive audio isn't available on this system yet.";
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }
    }
}

namespace Icom_Web_Control.Services.Cw
{
    /// <summary>No recording devices to choose from where IWC has no audio yet.</summary>
    public static class CwAudioDevices
    {
        public sealed record Device(int Index, string Name, int Channels);

        public static IReadOnlyList<Device> List() => Array.Empty<Device>();

        public static int IndexFor(string? name) => -1;
    }
}
#endif
