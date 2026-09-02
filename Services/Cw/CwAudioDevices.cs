using NAudio.Wave;

namespace Icom_Web_Control.Services.Cw
{
    /// <summary>
    /// The recording devices the CW reader can listen to.
    ///
    /// This uses WinMM (NAudio's <c>WaveIn</c>) rather than adding a
    /// cross-platform audio package. IWC targets <c>net10.0-windows</c> and
    /// nothing else, NAudio.WinMM is already referenced for the voice output,
    /// and the reader needs one mono input stream - not the full duplex,
    /// low-latency path a remote-audio feature would want. Adding a second
    /// audio stack for one input stream would be the expensive answer to a
    /// cheap question.
    ///
    /// <b>Names are truncated to 31 characters.</b> That is WinMM's
    /// <c>MAXPNAMELEN</c>, not a display choice, and it is why a device is
    /// matched on the truncated name rather than on anything longer that a
    /// user might paste in from Windows' own sound settings. Two IC-7300s on
    /// one PC therefore look identical here; the index disambiguates them, and
    /// that is what is stored.
    /// </summary>
    public static class CwAudioDevices
    {
        public sealed record Device(int Index, string Name, int Channels);

        /// <summary>Every WinMM recording device, in WinMM's own order.</summary>
        public static IReadOnlyList<Device> List()
        {
            var list = new List<Device>();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                try
                {
                    var caps = WaveInEvent.GetCapabilities(i);
                    list.Add(new Device(i, caps.ProductName, caps.Channels));
                }
                catch
                {
                    // A device that cannot be described is one the operator
                    // cannot usefully choose, so it is left out rather than
                    // listed as a blank row they will try and fail to select.
                }
            }
            return list;
        }

        /// <summary>
        /// The device index for a stored name, or -1 if it is not present.
        ///
        /// Stored by name rather than index because indices renumber whenever
        /// a USB device is plugged in or removed, and an index that has
        /// silently come to mean a different device is how an operator ends up
        /// decoding their webcam microphone. A name that no longer matches
        /// anything is reported as missing so the reader can say so.
        /// </summary>
        public static int IndexFor(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;

            string wanted = name.Trim();
            foreach (var d in List())
            {
                if (string.Equals(d.Name, wanted, StringComparison.OrdinalIgnoreCase))
                    return d.Index;
            }

            // WinMM truncates at 31 characters. A name saved by an older build,
            // or typed from Windows' sound settings, can be longer than what
            // WinMM will ever report, so compare on the truncated form too.
            string trimmed = wanted.Length > 31 ? wanted[..31] : wanted;
            foreach (var d in List())
            {
                if (string.Equals(d.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                    return d.Index;
            }

            return -1;
        }
    }
}
