// Yaesu Web Control – SDRplay Device (P/Invoke into sdrplay_api.dll)
// Implements ISdrDevice using the SDRplay API v3.
// The SDRplay API is callback-based; this class bridges the native callback
// thread to the managed consumer via a System.Threading.Channels.Channel<float[]>.
//
// Native struct layout (sdrplay_api_DeviceT, 96 bytes, x64):
//   Offset  0 : SerNo[64]          — ANSI serial number
//   Offset 64 : hwVer (byte)       — hardware version code
//   Offset 68 : tuner  (int)
//   Offset 72 : rspDuoMode (int)
//   Offset 76 : valid  (byte)
//   Offset 80 : rspDuoSampleFreq (double)
//   Offset 88 : Dev  (HANDLE / IntPtr)
//
// sdrplay_api_DeviceParamsT pointer layout (returned by GetDeviceParams):
//   Offset  0 : devParams*        → sdrplay_api_DevParamsT*
//   Offset  8 : rxChannelA*       → sdrplay_api_RxChannelParamsT*
//   Offset 16 : rxChannelB*       → sdrplay_api_RxChannelParamsT* (null for non-RSPduo)
//
// Within sdrplay_api_DevParamsT:
//   Offset 0 : ppm (double)
//   Offset 8 : fsFreq.fsHz (double)  ← sample rate
//
// Within sdrplay_api_RxChannelParamsT.tunerParams (from sdrplay_api_tuner.h):
//   Offset  0 : bwType (int)
//   Offset  4 : ifType (int)
//   Offset  8 : loMode (int)
//   Offset 12 : gain   (24 bytes)
//     gain.gRdB      @ 12  (int)
//     gain.LNAstate  @ 16  (unsigned char — NOT int)
//     gain.syncUpdate@ 17  (unsigned char)
//     gain.minGr     @ 20  (int, enum)
//     gain.gainVals  @ 24  (3 × float)
//   Offset 40 : rfFreq.rfHz (double)  ← sizeof(RfFreqT)=16 (double+uchar+7B tail padding)
//   Offset 56 : dcOffsetTuner (12 bytes) → refreshRateTime @ 64
//   Offset 72 : ctrlParams.dcOffset (2B) then ctrlParams.decimation (3B)

using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Yaesu_Web_Control.Services.Sdr
{
    public sealed class SdrplayDevice : ISdrDevice
    {
        // ── Constants ────────────────────────────────────────────────────────────

        private const string DllName             = "sdrplay_api";
        private const int    DeviceStructSize    = 96;
        private const int    MaxDevices          = 4;
        private const int    DevHandleOffset     = 88;
        private const int    HwVerOffset         = 64;
        private const int    DevParamsOffset     = 0;   // within DeviceParamsT
        private const int    RxChannelAOffset    = 8;   // within DeviceParamsT
        private const int    FsHzOffset          = 8;   // within DevParamsT
        private const int    RfHzOffset          = 40;  // tunerParams.rfFreq.rfHz — gain(24)+pad(4) after loMode offset 12
        private const int    CallbackFnsSize     = 24;  // 3 × IntPtr

        // Offsets within sdrplay_api_RxChannelParamsT (= tunerParams is first member at offset 0).
        // tunerParams layout: bwType(0) ifType(4) loMode(8) gain(12…35) rfFreq.rfHz(40)
        // gain.LNAstate is unsigned char at gain+4 — Marshal.WriteInt32 writes 4 bytes but
        // the next fields (syncUpdate uchar, 2-byte padding) are all safely zeroed that way.
        private const int    BwTypeOffset        = 0;   // sdrplay_api_Bw_MHzT (int)
        private const int    GrDbOffset          = 12;  // gain.gRdB     (int) — first field
        private const int    LnaStateOffset      = 16;  // gain.LNAstate (int) — second field

        // sdrplay_api_ControlParamsT starts at rxChannelA + 72 (= sizeof TunerParamsT).
        // sizeof(TunerParamsT) = 72 because RfFreqT (double+uchar) has 7 bytes tail padding
        // → sizeof(RfFreqT)=16, dcOffsetTuner starts at 56, ends at 68, padded to 72.
        //   ctrlParams.dcOffset.DCenable           @ 72
        //   ctrlParams.dcOffset.IQenable           @ 73
        //   ctrlParams.decimation.enable           @ 74
        //   ctrlParams.decimation.decimationFactor @ 75
        //   ctrlParams.decimation.wideBandSignal   @ 76
        private const int    DecimationEnableOffset = 74;
        private const int    DecimationFactorOffset = 75;

        // sdrplay_api_Bw_MHzT enum values (numeric value = bandwidth in kHz).
        private const int    BW_0_200            = 200;
        private const int    BW_0_300            = 300;
        private const int    BW_0_600            = 600;
        private const int    BW_1_536            = 1536;
        private const int    BW_5_000            = 5000;

        // Key prefix that identifies SDRplay devices across the codebase.
        public const string KeyPrefix = "sdrplay:";

        // ── P/Invoke declarations ────────────────────────────────────────────────

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_Open();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_Close();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_GetDevices(
            IntPtr devices, ref uint numDevices, uint maxNumDevices);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_SelectDevice(IntPtr device);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_ReleaseDevice(IntPtr device);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_GetDeviceParams(
            IntPtr dev, out IntPtr deviceParams);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_Init(
            IntPtr dev, IntPtr callbackFns, IntPtr cbContext);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdrplay_api_Uninit(IntPtr dev);

        // Returns a pointer to sdrplay_api_ErrorInfoT (owned by the API, do not free).
        // ErrorInfoT layout: file[256] @ 0, function[256] @ 256, line(int) @ 512, message[1024] @ 516
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sdrplay_api_GetLastError(IntPtr device);

        // ── Callback delegates (must stay rooted to prevent GC) ─────────────────

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void StreamCallbackDelegate(
            IntPtr xi, IntPtr xq,
            IntPtr streamCbParams,
            uint numSamples,
            uint reset,
            IntPtr cbContext);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EventCallbackDelegate(
            int eventId, int tuner, IntPtr eventParams, IntPtr cbContext);

        // ── Instance state ────────────────────────────────────────────────────────

        public string Key   { get; }
        public string Label { get; private set; }

        private readonly Channel<float[]> _channel =
            Channel.CreateBounded<float[]>(new BoundedChannelOptions(4)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

        // Config (set in Configure, read in callback)
        private int     _fftSize;
        private float[] _accumBuffer = [];
        private int     _accumOffset;

        // Native handles
        private IntPtr _deviceStructPtr;  // unmanaged copy of the selected DeviceT
        private IntPtr _devHandle;        // Dev field from DeviceT (HANDLE)
        private IntPtr _callbackFnsPtr;   // unmanaged CallbackFnsT struct
        private GCHandle _selfHandle;     // pins 'this' for native callback context

        // Delegate fields — kept alive by the instance
        private StreamCallbackDelegate? _streamDelegate;
        private EventCallbackDelegate?  _eventDelegate;

        private bool _apiOpen;
        private bool _streaming;
        private bool _disposed;

        // ── Constructor ───────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a device wrapper for the given key.
        /// Key format: "sdrplay:&lt;serialNumber&gt;"
        /// </summary>
        public SdrplayDevice(string key)
        {
            if (!key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Key must start with '{KeyPrefix}'.", nameof(key));

            Key   = key;
            Label = key;   // Placeholder; updated to the full model name in Configure().
        }

        // ── Public API ────────────────────────────────────────────────────────────

        // Common installation paths for the official SDRplay API on Windows x64.
        private static readonly string[] _knownDllPaths =
        [
            @"C:\Program Files\SDRplay\API\x64\sdrplay_api.dll",
            @"C:\Program Files (x86)\SDRplay\API\x64\sdrplay_api.dll",
        ];

        /// <summary>
        /// Returns all connected SDRplay devices.
        /// <paramref name="diagnosticNote"/> receives a plain-English explanation
        /// of any problem encountered (DLL missing, service not running, etc.).
        /// </summary>
        public static IReadOnlyList<SdrDeviceInfo> EnumerateDevices(out string? diagnosticNote)
        {
            diagnosticNote = null;
            var devices = new List<SdrDeviceInfo>();
            try
            {
                int err = sdrplay_api_Open();
                if (err != 0)
                {
                    diagnosticNote =
                        $"sdrplay_api.dll loaded but sdrplay_api_Open() returned error {err}. " +
                        "This usually means the SDRplay API Service is not running. " +
                        "Open Windows Services (services.msc) and check that " +
                        "'SDRplay API Service' (or 'SDRPlayService') is started. " +
                        "Also close SDR Console or any other SDR app before scanning — " +
                        "only one application can hold the API open at a time.";
                    return devices;
                }

                try
                {
                    IntPtr deviceArray = Marshal.AllocHGlobal(DeviceStructSize * MaxDevices);
                    try
                    {
                        uint count = 0;
                        err = sdrplay_api_GetDevices(deviceArray, ref count, MaxDevices);
                        if (err != 0)
                        {
                            diagnosticNote =
                                $"sdrplay_api_GetDevices() returned error {err}. " +
                                "The device may be in use by another application.";
                        }
                        else if (count == 0)
                        {
                            diagnosticNote =
                                "SDRplay API opened successfully but found 0 devices. " +
                                "Check the RSP1 is plugged in to a USB port and that the " +
                                "SDRplay USB driver is installed (Device Manager should show " +
                                "'SDRplay RSP1' under 'Software Defined Radio', not under " +
                                "'Unknown devices' or with a yellow warning icon).";
                        }
                        else
                        {
                            ReadDeviceList(deviceArray, count, devices);
                        }
                    }
                    finally { Marshal.FreeHGlobal(deviceArray); }
                }
                finally { sdrplay_api_Close(); }
            }
            catch (DllNotFoundException)
            {
                // Check whether the DLL exists somewhere we know about but isn't in PATH.
                string? found = Array.Find(_knownDllPaths, File.Exists);
                if (found != null)
                {
                    diagnosticNote =
                        $"sdrplay_api.dll is installed at '{found}' but is not on the " +
                        $"system PATH, so the app cannot load it. Copy it to the app output " +
                        $"folder: {AppContext.BaseDirectory}";
                }
                else
                {
                    diagnosticNote =
                        "sdrplay_api.dll not found. Install the official SDRplay API from " +
                        "www.sdrplay.com/softwaredownloads/ then restart the app.";
                }
            }
            catch (Exception ex)
            {
                diagnosticNote = $"SDRplay API unexpected error: {ex.GetType().Name} — {ex.Message}";
            }

            return devices;
        }

        /// <inheritdoc/>
        public void Configure(long centreFrequencyHz, double sampleRateHz, int fftSize)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(SdrplayDevice));

            _fftSize     = fftSize;
            _accumBuffer = new float[fftSize * 2];
            _accumOffset = 0;

            // Open the API
            ThrowIfError(sdrplay_api_Open(), "sdrplay_api_Open");
            _apiOpen = true;

            // Enumerate and find our device by serial number
            string serial = Key[KeyPrefix.Length..];

            IntPtr deviceArray = Marshal.AllocHGlobal(DeviceStructSize * MaxDevices);
            try
            {
                uint count = 0;
                ThrowIfError(sdrplay_api_GetDevices(deviceArray, ref count, MaxDevices), "GetDevices");

                IntPtr matchPtr = FindDeviceBySerial(deviceArray, count, serial);
                if (matchPtr == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"SDRplay device '{serial}' not found. Check it is connected.");

                // Update Label now that we know the hardware version.
                byte hwVer = Marshal.ReadByte(matchPtr, HwVerOffset);
                Label = $"SDRplay {HwVerToModel(hwVer)} ({serial})";

                // Copy the struct to our own buffer before freeing the array.
                _deviceStructPtr = Marshal.AllocHGlobal(DeviceStructSize);
                for (int i = 0; i < DeviceStructSize; i++)
                    Marshal.WriteByte(_deviceStructPtr, i, Marshal.ReadByte(matchPtr, i));
            }
            finally { Marshal.FreeHGlobal(deviceArray); }

            // Select the device (populates _deviceStructPtr.Dev)
            ThrowIfError(sdrplay_api_SelectDevice(_deviceStructPtr), "SelectDevice");
            _devHandle = Marshal.ReadIntPtr(_deviceStructPtr, DevHandleOffset);

            // Retrieve parameter pointers and set frequency + sample rate
            ThrowIfError(sdrplay_api_GetDeviceParams(_devHandle, out IntPtr deviceParamsPtr), "GetDeviceParams");

            IntPtr devParams  = Marshal.ReadIntPtr(deviceParamsPtr, DevParamsOffset);
            IntPtr rxChannelA = Marshal.ReadIntPtr(deviceParamsPtr, RxChannelAOffset);

            // Sample rate — hardware minimum is 2 MHz; use decimation for narrower spans.
            const double MinHardwareRateHz = 2_000_000;
            double hardwareRateHz  = Math.Max(sampleRateHz, MinHardwareRateHz);
            int    decimationFactor = (sampleRateHz < MinHardwareRateHz)
                ? (int)Math.Round(MinHardwareRateHz / sampleRateHz)
                : 1;

            WriteDouble(devParams, FsHzOffset, hardwareRateHz);

            if (decimationFactor > 1)
            {
                Marshal.WriteByte(rxChannelA, DecimationEnableOffset, 1);
                Marshal.WriteByte(rxChannelA, DecimationFactorOffset, (byte)decimationFactor);
            }

            // Centre frequency
            WriteDouble(rxChannelA, RfHzOffset, (double)centreFrequencyHz);

            // Analog bandwidth — must be set to match the sample rate.
            // Default after GetDeviceParams is 200 kHz, which rejects almost all of
            // the displayed span and leaves the spectrum showing only noise floor.
            int bw = sampleRateHz <=   250_000 ? BW_0_200
                   : sampleRateHz <=   500_000 ? BW_0_300
                   : sampleRateHz <= 1_200_000 ? BW_0_600
                   : sampleRateHz <= 2_600_000 ? BW_1_536
                   :                              BW_5_000;
            Marshal.WriteInt32(rxChannelA, BwTypeOffset, bw);

            // Gain — gRdB 40 (moderate IF gain reduction, safe for strong IF inputs),
            // LNAstate 0 (minimum LNA attenuation = maximum LNA sensitivity).
            // RSP1 valid ranges: gRdB 20–59, LNAstate 0–3.
            Marshal.WriteInt32(rxChannelA, GrDbOffset,     40);
            Marshal.WriteInt32(rxChannelA, LnaStateOffset,  0);
        }

        /// <inheritdoc/>
        public void StartStreaming()
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(SdrplayDevice));
            if (_streaming) return;

            // Pin 'this' so the native callback can recover the instance.
            _selfHandle = GCHandle.Alloc(this);

            // Create delegates and keep references alive on the instance.
            _streamDelegate = new StreamCallbackDelegate(StreamACallback);
            _eventDelegate  = new EventCallbackDelegate(EventCallback);

            // Build the unmanaged CallbackFnsT struct: [StreamA, StreamB, Event]
            _callbackFnsPtr = Marshal.AllocHGlobal(CallbackFnsSize);
            Marshal.WriteIntPtr(_callbackFnsPtr, 0,  Marshal.GetFunctionPointerForDelegate(_streamDelegate));
            Marshal.WriteIntPtr(_callbackFnsPtr, 8,  Marshal.GetFunctionPointerForDelegate(_streamDelegate));
            Marshal.WriteIntPtr(_callbackFnsPtr, 16, Marshal.GetFunctionPointerForDelegate(_eventDelegate));

            int initErr = sdrplay_api_Init(
                _devHandle,
                _callbackFnsPtr,
                GCHandle.ToIntPtr(_selfHandle));
            if (initErr != 0)
                throw new InvalidOperationException(
                    $"sdrplay_api: sdrplay_api_Init returned error code {initErr}" +
                    ReadLastErrorDetail(_deviceStructPtr));

            _streaming = true;
        }

        /// <inheritdoc/>
        public async ValueTask<bool> TryReadIqFrameAsync(
            float[] buffer, int timeoutMs, CancellationToken ct = default)
        {
            using var linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeoutMs);
            try
            {
                var frame = await _channel.Reader.ReadAsync(linkedCts.Token).ConfigureAwait(false);
                Array.Copy(frame, buffer, Math.Min(frame.Length, buffer.Length));
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (ChannelClosedException)     { return false; }
        }

        /// <inheritdoc/>
        public void Stop()
        {
            if (!_streaming) return;
            _streaming = false;

            try { sdrplay_api_Uninit(_devHandle); }
            catch { /* ignore errors during stop */ }

            if (_callbackFnsPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_callbackFnsPtr);
                _callbackFnsPtr = IntPtr.Zero;
            }

            if (_selfHandle.IsAllocated)
                _selfHandle.Free();

            // Release delegate references so the GC can collect them.
            _streamDelegate = null;
            _eventDelegate  = null;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();

            if (_devHandle != IntPtr.Zero)
            {
                try { sdrplay_api_ReleaseDevice(_deviceStructPtr); } catch { }
                _devHandle = IntPtr.Zero;
            }

            if (_deviceStructPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_deviceStructPtr);
                _deviceStructPtr = IntPtr.Zero;
            }

            if (_apiOpen)
            {
                try { sdrplay_api_Close(); } catch { }
                _apiOpen = false;
            }

            _channel.Writer.TryComplete();
        }

        // ── Native callbacks ─────────────────────────────────────────────────────

        private void StreamACallback(
            IntPtr xi, IntPtr xq,
            IntPtr _streamCbParams,
            uint numSamples, uint reset,
            IntPtr _cbContext)
        {
            if (reset != 0)
            {
                _accumOffset = 0;
                return;
            }

            float[] accum = _accumBuffer;
            int fftSize   = _fftSize;
            if (accum.Length == 0 || fftSize <= 0) return;

            int srcIdx    = 0;
            int available = (int)numSamples;

            while (srcIdx < available)
            {
                int space  = fftSize - _accumOffset;
                int toCopy = Math.Min(available - srcIdx, space);

                for (int i = 0; i < toCopy; i++)
                {
                    accum[(_accumOffset + i) * 2]     =
                        Marshal.ReadInt16(xi, (srcIdx + i) * 2) / 32768f;
                    accum[(_accumOffset + i) * 2 + 1] =
                        Marshal.ReadInt16(xq, (srcIdx + i) * 2) / 32768f;
                }

                _accumOffset += toCopy;
                srcIdx       += toCopy;

                if (_accumOffset >= fftSize)
                {
                    var frame = new float[fftSize * 2];
                    Array.Copy(accum, frame, fftSize * 2);
                    _channel.Writer.TryWrite(frame);
                    _accumOffset = 0;
                }
            }
        }

        private static void EventCallback(
            int _eventId, int _tuner, IntPtr _eventParams, IntPtr _cbContext)
        {
            // SDRplay events (gain change, overload, etc.) — not needed for spectrum display.
        }

        // ── Static helpers ────────────────────────────────────────────────────────

        private static void ReadDeviceList(IntPtr deviceArray, uint count, List<SdrDeviceInfo> list)
        {
            for (uint i = 0; i < count; i++)
            {
                IntPtr ptr    = deviceArray + (int)(i * DeviceStructSize);
                string serial = Marshal.PtrToStringAnsi(ptr) ?? $"device{i}";
                byte   hwVer  = Marshal.ReadByte(ptr, HwVerOffset);
                string model  = HwVerToModel(hwVer);
                string label  = $"SDRplay {model} ({serial})";

                list.Add(new SdrDeviceInfo(
                    Key:    KeyPrefix + serial,
                    Label:  label,
                    Driver: "sdrplay"));
            }
        }

        private static IntPtr FindDeviceBySerial(IntPtr deviceArray, uint count, string serial)
        {
            for (uint i = 0; i < count; i++)
            {
                IntPtr ptr       = deviceArray + (int)(i * DeviceStructSize);
                string devSerial = Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
                if (string.Equals(devSerial, serial, StringComparison.OrdinalIgnoreCase))
                    return ptr;
            }
            return IntPtr.Zero;
        }

        private static string HwVerToModel(byte hwVer) => hwVer switch
        {
            1 => "RSP1",
            2 => "RSP2",
            3 => "RSP1A",
            4 => "RSPduo",
            5 => "RSPdx",
            6 => "RSP1B",
            7 => "RSPdx R2",
            _ => $"RSP (hwVer={hwVer})"
        };

        private static void WriteDouble(IntPtr ptr, int offset, double value)
        {
            Marshal.WriteInt64(ptr, offset, BitConverter.DoubleToInt64Bits(value));
        }

        private static void ThrowIfError(int err, string operation)
        {
            if (err != 0)
                throw new InvalidOperationException(
                    $"sdrplay_api: {operation} returned error code {err}");
        }

        // Reads fields from sdrplay_api_ErrorInfoT: file[256]@0, function[256]@256, line(int)@512, message[1024]@516
        private static string ReadLastErrorDetail(IntPtr deviceStructPtr)
        {
            try
            {
                IntPtr info = sdrplay_api_GetLastError(deviceStructPtr);
                if (info == IntPtr.Zero) return " [GetLastError=null]";
                string? file    = Marshal.PtrToStringAnsi(info + 0);
                string? func    = Marshal.PtrToStringAnsi(info + 256);
                int     line    = Marshal.ReadInt32(info + 512);
                string? message = Marshal.PtrToStringAnsi(info + 516);
                return $" [{func}:{line} {message} ({file})]";
            }
            catch (Exception ex) { return $" [GetLastError threw {ex.GetType().Name}]"; }
        }
    }
}
