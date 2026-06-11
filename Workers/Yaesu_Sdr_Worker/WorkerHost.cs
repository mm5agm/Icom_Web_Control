// Worker host — the runtime loop for one SDR.
//
// Lifecycle:
//   1. Open a TCP listener on the chosen port (localhost only).
//   2. Wait for YWC main to connect (single client).
//   3. Open the SDR device (sdrplay or soapy).
//   4. Configure it (IF freq, sample rate, FFT size).
//   5. Start streaming.
//   6. Loop: TryReadIqFrame → FFT → frame-write to the TCP client.
//   7. On client disconnect, cancellation, or SDR error: clean up and exit.
//
// Designed for one-shot use. If anything goes wrong, the process exits with
// a non-zero code; YWC's SdrManager (step 2 of the dual-SDR work) supervises
// and restarts. Keeping the worker dumb keeps the supervisor sensible.

using System.Net;
using System.Net.Sockets;
using Yaesu_Web_Control.Services.Sdr;

namespace Yaesu_Web_Control.Workers.Sdr;

internal sealed class WorkerHost
{
    private readonly WorkerOptions _opts;

    public WorkerHost(WorkerOptions opts) => _opts = opts;

    public async Task<int> RunAsync(CancellationToken stoppingToken)
    {
        Log($"starting (deviceKey={_opts.DeviceKey}, vfo={_opts.Vfo}, port={_opts.Port}, " +
            $"ifHz={_opts.IfFrequencyHz}, sr={_opts.SampleRateHz}, fft={_opts.FftSize})");

        // 1. Open TCP listener on localhost only.
        var listener = new TcpListener(IPAddress.Loopback, _opts.Port);
        try { listener.Start(); }
        catch (SocketException ex)
        {
            Log($"FATAL: could not bind localhost:{_opts.Port} — {ex.Message}");
            return 2;
        }
        Log($"listening on localhost:{_opts.Port}, waiting for client…");

        TcpClient? client;
        try
        {
            client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log("cancelled before client connected — exiting cleanly");
            listener.Stop();
            return 0;
        }
        finally
        {
            // Stop accepting further connections — single-client design.
            listener.Stop();
        }
        Log($"client connected from {client.Client.RemoteEndPoint}");

        // 2. Open the SDR and stream.
        var stream = client.GetStream();
        var writer = new FrameWriter(stream);
        ISdrDevice? device = null;
        int exit = 0;

        try
        {
            await writer.WriteStatusAsync("connecting", stoppingToken).ConfigureAwait(false);

            device = CreateDevice(_opts.DeviceKey);
            device.Configure(_opts.IfFrequencyHz, _opts.SampleRateHz, _opts.FftSize);
            device.StartStreaming();

            await writer.WriteStatusAsync("streaming", stoppingToken).ConfigureAwait(false);
            Log($"streaming '{device.Label}'");

            float[] iqBuffer = new float[_opts.FftSize * 2];
            ulong sequence = 0;

            // FrameIntervalMs is enforced by the SDR's read-availability — we
            // pull as fast as the device emits and let TryReadIqFrameAsync block.
            const int frameTimeoutMs = 200;

            while (!stoppingToken.IsCancellationRequested)
            {
                bool got = await device.TryReadIqFrameAsync(iqBuffer, frameTimeoutMs, stoppingToken)
                    .ConfigureAwait(false);
                if (!got) continue;

                float[] bins = FftProcessor.ComputeSpectrum(iqBuffer, _opts.FftSize);

                // Round to 1 dp before transmission (same precision as the
                // current SignalR path, keeps frames small).
                for (int i = 0; i < bins.Length; i++)
                    bins[i] = MathF.Round(bins[i], 1);

                try
                {
                    await writer.WriteSpectrumAsync(
                        ++sequence,
                        _opts.IfFrequencyHz,
                        (long)_opts.SampleRateHz,
                        bins,
                        stoppingToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    // Client went away — clean shutdown.
                    Log($"client disconnected: {ex.Message}");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("cancelled — exiting cleanly");
        }
        catch (DllNotFoundException ex)
        {
            Log($"FATAL: required DLL not found — {ex.Message}");
            try { await writer.WriteStatusAsync("nodll", CancellationToken.None); } catch { }
            try { await writer.WriteErrorAsync(ex.Message, CancellationToken.None); } catch { }
            exit = 3;
        }
        catch (Exception ex)
        {
            Log($"FATAL: streaming error — {ex.GetType().Name}: {ex.Message}");
            try { await writer.WriteStatusAsync("disconnected", CancellationToken.None); } catch { }
            try { await writer.WriteErrorAsync(ex.Message, CancellationToken.None); } catch { }
            exit = 4;
        }
        finally
        {
            try { device?.Stop(); } catch { }
            device?.Dispose();
            try { stream.Dispose(); } catch { }
            client.Dispose();
        }

        Log($"exit code {exit}");
        return exit;
    }

    private static ISdrDevice CreateDevice(string key)
    {
        if (key.StartsWith(SdrplayDevice.KeyPrefix, StringComparison.OrdinalIgnoreCase))
            return new SdrplayDevice(key);

        // Everything else is treated as a SoapySDR kwargs string.
        return new SoapySdrDevice(key);
    }

    // Log to stderr with a [SdrWorker A] prefix so YWC's Serilog file-sink (step 2)
    // can route worker output into the main log cleanly.
    private void Log(string msg) =>
        Console.Error.WriteLine($"[SdrWorker {_opts.Vfo}] {DateTime.UtcNow:HH:mm:ss.fff}  {msg}");
}

internal sealed record WorkerOptions(
    string DeviceKey,
    string Vfo,            // "A" or "B"
    int    Port,           // localhost TCP port to listen on
    long   IfFrequencyHz,
    double SampleRateHz,
    int    FftSize);
