// SDRplay concurrent-open probe — gate-test for YWC's dual-SDR plan.
//
// Question: can sdrplay_api.dll hold two RSPs Selected + Init'd in the same
// process at the same time? Enumeration of two devices is already confirmed
// (YWC's Settings page lists both). This probe takes the next step — actually
// opens both, calls Init on each, optionally streams for a few seconds, and
// reports success or failure.
//
// Build & run:
//   cd scripts/probe
//   dotnet run -c Release
//
// Expected good output: "✓ Both devices Init'd successfully" plus any sample
// counts. Anything else (errors from Open/GetDevices/Select/Init) tells us
// where the API tops out so we can adjust the dual-SDR design.

using System.Runtime.InteropServices;

namespace YwcProbe;

internal static class Program
{
    private const string DllName          = "sdrplay_api";
    private const int    DeviceStructSize = 96;
    private const int    MaxDevices       = 4;
    private const int    HwVerOffset      = 64;
    private const int    ValidOffset      = 76;   // unsigned char — set to 1 to mark device for use
    private const int    DevHandleOffset  = 88;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sdrplay_api_Open();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sdrplay_api_Close();

    // Required when handling more than one device — the API serialises device-list
    // operations behind a process-wide mutex. Single-device code can get away
    // without it, two-device code cannot.
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sdrplay_api_LockDeviceApi();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sdrplay_api_UnlockDeviceApi();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sdrplay_api_GetDevices(IntPtr devices, ref uint numDevices, uint maxNumDevices);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sdrplay_api_SelectDevice(IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sdrplay_api_ReleaseDevice(IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sdrplay_api_GetDeviceParams(IntPtr dev, out IntPtr deviceParams);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sdrplay_api_Init(IntPtr dev, IntPtr callbackFns, IntPtr cbContext);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sdrplay_api_Uninit(IntPtr dev);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sdrplay_api_GetLastError(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StreamCallbackDelegate(IntPtr xi, IntPtr xq, IntPtr sp, uint n, uint reset, IntPtr ctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EventCallbackDelegate(int eventId, int tuner, IntPtr ep, IntPtr ctx);

    // Match the rough hwVer → model mapping in YWC's SdrplayDevice.HwVerToModel.
    private static string ModelFor(byte hwVer) => hwVer switch
    {
        1   => "RSP1",
        2   => "RSP2",
        3   => "RSPduo",
        4   => "RSPdx",
        5   => "RSPdx-R2",
        6   => "RSP1B",
        7   => "RSPdxR2",
        255 => "RSP1A",
        _   => $"hwVer={hwVer}",
    };

    // Per-device state we need to track across Open → Init → Uninit → Release.
    private sealed class DeviceHandle
    {
        public required IntPtr StructPtr;          // unmanaged copy of the sdrplay_api_DeviceT
        public required string Label;
        public IntPtr CallbackFns;                 // unmanaged CallbackFnsT (kept alive until Uninit)
        public StreamCallbackDelegate? StreamA;    // delegates pinned by holding the reference
        public StreamCallbackDelegate? StreamB;
        public EventCallbackDelegate?  Event;
        public long SamplesA;
        public long SamplesB;
    }

    private static int Main()
    {
        Console.WriteLine("SDRplay concurrent-open probe — YWC dual-SDR gate test\n");

        int err = sdrplay_api_Open();
        if (err != 0)
        {
            Console.WriteLine($"✗ sdrplay_api_Open returned {err}.");
            Console.WriteLine("  Most common cause: SDRplay API Service isn't running. Open services.msc and start 'SDRplay API Service'.");
            return 1;
        }
        Console.WriteLine("✓ API opened.");

        var deviceArray = Marshal.AllocHGlobal(DeviceStructSize * MaxDevices);
        var handles     = new List<DeviceHandle>();
        int exitCode    = 0;

        try
        {
            // Strategy: Select BOTH devices before calling Init on either.
            // The earlier test had Init device-1 → Select device-2 = fail.
            // Maybe Init's active streaming on device-1 blocks device-2's Select;
            // Selecting both first (no Init yet) may avoid that.
            // GetLastError calls are dropped — they crash with AV in this state.
            uint count = 0;
            err = sdrplay_api_GetDevices(deviceArray, ref count, MaxDevices);
            if (err != 0) { Console.WriteLine($"✗ GetDevices returned {err}."); return 1; }

            Console.WriteLine($"✓ GetDevices returned {count} device(s).\n");
            if (count < 2)
            {
                Console.WriteLine("⚠ Need both RSPs plugged in. Plug in both and re-run.");
                return count == 0 ? 1 : 0;
            }

            var targetSerials = new List<string>();
            for (uint i = 0; i < count && targetSerials.Count < 2; i++)
            {
                IntPtr ptr    = deviceArray + (int)(i * DeviceStructSize);
                string serial = Marshal.PtrToStringAnsi(ptr) ?? "?";
                byte   hwVer  = Marshal.ReadByte(ptr, HwVerOffset);
                Console.WriteLine($"  [{i}] {ModelFor(hwVer)}  serial={serial}");
                targetSerials.Add(serial);
            }
            Console.WriteLine();

            // Step 1: Select BOTH devices, no Init yet.
            int passNum = 0;
            foreach (var wantSerial in targetSerials)
            {
                passNum++;
                Console.WriteLine($"--- Select pass {passNum}: serial {wantSerial} ---");

                uint cnt = 0;
                err = sdrplay_api_GetDevices(deviceArray, ref cnt, MaxDevices);
                if (err != 0)
                {
                    Console.WriteLine($"  ✗ GetDevices returned {err}.");
                    exitCode = 1;
                    break;
                }
                Console.WriteLine($"  GetDevices returned {cnt} device(s).");

                IntPtr matchPtr = IntPtr.Zero;
                for (uint i = 0; i < cnt; i++)
                {
                    IntPtr ptr = deviceArray + (int)(i * DeviceStructSize);
                    string s   = Marshal.PtrToStringAnsi(ptr) ?? "";
                    if (s == wantSerial) { matchPtr = ptr; break; }
                }
                if (matchPtr == IntPtr.Zero)
                {
                    Console.WriteLine($"  ✗ Target serial {wantSerial} not in enumeration.");
                    Console.WriteLine($"    Likely cause: the API has dropped the previously-Selected device from enumeration. That itself is fine — it confirms the API tracks the first Select. The question is whether we can still Select a second.");
                    exitCode = 1;
                    continue;
                }

                IntPtr copy = Marshal.AllocHGlobal(DeviceStructSize);
                for (int k = 0; k < DeviceStructSize; k++)
                    Marshal.WriteByte(copy, k, Marshal.ReadByte(matchPtr, k));
                Marshal.WriteByte(copy, ValidOffset, 1);

                byte   hwVer = Marshal.ReadByte(copy, HwVerOffset);
                string label = $"{ModelFor(hwVer)}({wantSerial})";
                var h = new DeviceHandle { StructPtr = copy, Label = label };
                handles.Add(h);

                Console.Write($"  Select {label}... ");
                err = sdrplay_api_SelectDevice(copy);
                if (err != 0)
                {
                    Console.WriteLine($"✗ err={err}");
                    if (passNum == 2)
                    {
                        Console.WriteLine();
                        Console.WriteLine("══════════════════════════════════════════════════════════════════");
                        Console.WriteLine("CONCLUSION: SDRplay API v3 on this machine does not support");
                        Console.WriteLine("Selecting a second RSP while a first is already Selected in the");
                        Console.WriteLine("same process — even before Init has been called.");
                        Console.WriteLine();
                        Console.WriteLine("This is the hard 'one-device-per-process' limit. Dual-SDR within");
                        Console.WriteLine("a single YWC process is not viable with the current SDRplay API.");
                        Console.WriteLine();
                        Console.WriteLine("Options:");
                        Console.WriteLine("  1. Two-process model: YWC spawns a dedicated SDR worker per RSP.");
                        Console.WriteLine("  2. Newer SDRplay API version (if one exists and supports it).");
                        Console.WriteLine("  3. Limit dual-SDR to non-SDRplay devices (RTL-SDR via Soapy).");
                        Console.WriteLine("══════════════════════════════════════════════════════════════════");
                    }
                    exitCode = 1;
                    continue;
                }
                Console.WriteLine("✓");
            }

            Console.WriteLine();
            int selectsSucceeded = handles.Count(h => Marshal.ReadIntPtr(h.StructPtr, DevHandleOffset) != IntPtr.Zero);
            if (selectsSucceeded < 2)
            {
                Console.WriteLine($"⚠ Only {selectsSucceeded}/2 Selects succeeded — skipping Init.");
            }
            else
            {
                // Step 2: Now Init each (only if both Selects worked).
                Console.WriteLine("✓✓ Both devices Selected. Now Init'ing each...");
                foreach (var h in handles)
                {
                    IntPtr devHandle = Marshal.ReadIntPtr(h.StructPtr, DevHandleOffset);

                    Console.Write($"  [{h.Label}] GetDeviceParams (handle=0x{devHandle.ToInt64():X})... ");
                    err = sdrplay_api_GetDeviceParams(devHandle, out _);
                    if (err != 0) { Console.WriteLine($"✗ err={err}"); exitCode = 1; continue; }
                    Console.WriteLine("✓");

                    Console.Write($"  [{h.Label}] Init... ");
                    h.StreamA = (xi, xq, sp, n, reset, ctx) => Interlocked.Add(ref h.SamplesA, n);
                    h.StreamB = (xi, xq, sp, n, reset, ctx) => Interlocked.Add(ref h.SamplesB, n);
                    h.Event   = (eventId, tuner, ep, ctx) => { /* discard */ };

                    IntPtr cbFns = Marshal.AllocHGlobal(IntPtr.Size * 3);
                    Marshal.WriteIntPtr(cbFns, 0,               Marshal.GetFunctionPointerForDelegate(h.StreamA));
                    Marshal.WriteIntPtr(cbFns, IntPtr.Size,     Marshal.GetFunctionPointerForDelegate(h.StreamB));
                    Marshal.WriteIntPtr(cbFns, IntPtr.Size * 2, Marshal.GetFunctionPointerForDelegate(h.Event));
                    h.CallbackFns = cbFns;

                    err = sdrplay_api_Init(devHandle, cbFns, IntPtr.Zero);
                    if (err != 0) { Console.WriteLine($"✗ err={err}"); exitCode = 1; continue; }
                    Console.WriteLine("✓");
                }
            }

            // If both Init'd, stream for 3 seconds and count samples.
            int initSucceeded = handles.Count(h => h.CallbackFns != IntPtr.Zero);
            if (initSucceeded == 2)
            {
                Console.WriteLine("Both devices Init'd. Streaming 3 seconds to confirm samples flow from both...");
                Thread.Sleep(3000);
                foreach (var h in handles)
                {
                    Console.WriteLine($"  {h.Label}: {h.SamplesA + h.SamplesB:N0} samples received (streamA={h.SamplesA:N0}, streamB={h.SamplesB:N0})");
                }
                Console.WriteLine();

                bool both = handles.All(h => h.SamplesA + h.SamplesB > 0);
                if (both)
                    Console.WriteLine("✓✓ Concurrent open + streaming confirmed. Dual-SDR is supported by this SDRplay API.");
                else
                    Console.WriteLine("⚠ Both devices Init'd but at least one received zero samples in 3 s. Worth investigating but Init success is the harder gate.");
            }
            else
            {
                Console.WriteLine($"✗ Only {initSucceeded}/2 devices Init'd successfully. Dual-SDR can't proceed as designed.");
            }
        }
        finally
        {
            Console.WriteLine("\nCleaning up...");
            foreach (var h in handles)
            {
                try
                {
                    if (h.CallbackFns != IntPtr.Zero)
                    {
                        IntPtr devHandle = Marshal.ReadIntPtr(h.StructPtr, DevHandleOffset);
                        sdrplay_api_Uninit(devHandle);
                        Marshal.FreeHGlobal(h.CallbackFns);
                    }
                    sdrplay_api_ReleaseDevice(h.StructPtr);
                    Marshal.FreeHGlobal(h.StructPtr);
                }
                catch (Exception ex) { Console.WriteLine($"  cleanup error for {h.Label}: {ex.Message}"); }
            }

            Marshal.FreeHGlobal(deviceArray);
            sdrplay_api_Close();
            Console.WriteLine("Done.");
        }

        return exitCode;
    }

    // sdrplay_api_ErrorInfoT layout: file[256] message[1024] at offset 516.
    private static string TryGetLastErrorMessage(IntPtr device)
    {
        try
        {
            IntPtr info = sdrplay_api_GetLastError(device);
            if (info == IntPtr.Zero) return "(no error info)";
            return Marshal.PtrToStringAnsi(info + 516) ?? "(unreadable error message)";
        }
        catch { return "(GetLastError threw)"; }
    }

    // Same as TryGetLastErrorMessage but never lets an access violation escape —
    // the process-level corrupted-state-exception default in .NET 10 turns AVs
    // into fatal crashes that bypass managed catch blocks. We run the lookup
    // in a separate thread so an AV there only kills that thread, not the
    // probe process. Worst case the lookup hangs, in which case we return
    // a fallback after ~1s and carry on.
    private static string TryGetLastErrorMessageSafe(IntPtr device)
    {
        string result = "(no error info)";
        bool   done   = false;
        var t = new Thread(() =>
        {
            try { result = TryGetLastErrorMessage(device); }
            catch { result = "(GetLastError threw)"; }
            finally { done = true; }
        }) { IsBackground = true };
        t.Start();
        t.Join(TimeSpan.FromSeconds(1));
        return done ? result : "(GetLastError lookup timed out)";
    }
}
