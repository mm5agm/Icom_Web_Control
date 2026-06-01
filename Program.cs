using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yaesu_Web_Control.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ── Single-instance guard ────────────────────────────────────────────────────
const string MutexName = "Global\\Yaesu_Web_Control_SingleInstance";
var mutex = new Mutex(initiallyOwned: true, name: MutexName, out bool createdNew);

if (!createdNew)
{
#pragma warning disable CA1416
    MessageBox.Show(
        "Yaesu Web Control is already running.",
        "Already Running",
        MessageBoxButtons.OK,
        MessageBoxIcon.Information);
#pragma warning restore CA1416

    mutex.Dispose();
    return;
}

// Keep the mutex alive for the lifetime of the process
AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { mutex.ReleaseMutex(); } catch { } mutex.Dispose(); };

// ── Helpers ──────────────────────────────────────────────────────────────────
static bool IsPortInUseException(Exception ex)
{
    var full = ex.ToString();
    return full.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        || full.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase)
        || full.Contains("WSAEADDRINUSE", StringComparison.OrdinalIgnoreCase);
}

static string? GetPortOwner(int port)
{
    try
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName               = "netstat",
            Arguments              = "-ano",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            CreateNoWindow         = true
        });
        if (proc is null) return null;

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        foreach (var line in output.Split('\n'))
        {
            if (line.Contains($":{port}") && line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 && int.TryParse(parts[^1], out int pid))
                {
                    try   { return $"{Process.GetProcessById(pid).ProcessName} (PID {pid})"; }
                    catch { return $"PID {pid}"; }
                }
            }
        }
    }
    catch { }
    return null;
}

// ── SoapySDR native library resolver ────────────────────────────────────────
// .NET P/Invoke on Windows does not search PATH directories by default.
// Resolve SoapySDR.dll explicitly from its install location so the P/Invoke
// declarations in SoapySdrInterop are satisfied without relying on PATH.
NativeLibrary.SetDllImportResolver(
    System.Reflection.Assembly.GetExecutingAssembly(),
    static (name, _, _) =>
    {
        if (name == "SoapySDR")
        {
            // Installed layout: <app>\SoapySDR\bin\SoapySDR.dll
            var path = Path.Combine(AppContext.BaseDirectory, "SoapySDR", "bin", "SoapySDR.dll");
            // Developer fallback: C:\SoapySDR\bin\SoapySDR.dll (build machine only)
            if (!File.Exists(path))
                path = @"C:\SoapySDR\bin\SoapySDR.dll";
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr h))
                return h;
        }
        return IntPtr.Zero;   // fall back to default resolution for all other DLLs
    });

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CalibrationStorage>();
builder.Services.AddSingleton<ICalibrationService, CalibrationService>();

// ADD SIGNALR EARLY (before services that depend on IHubContext):
builder.Services.AddSignalR();

// Register the persistence service (no hub dependency)
builder.Services.AddSingleton<RadioStatePersistenceService>();

// Register RadioStateService and CatMessageBuffer as singletons
builder.Services.AddSingleton<RadioStateService>();
builder.Services.AddSingleton<CatMessageBuffer>();

// Register CatMessageDispatcher as singleton
builder.Services.AddSingleton<CatMessageDispatcher>();

// Register CatMultiplexerService as singleton
builder.Services.AddSingleton<CatMultiplexerService>();

// Register the main CAT client for the web app
builder.Services.AddSingleton<ICatClient, MultiplexedCatClient>();

// Register the rigctld server as a background service
builder.Services.AddHostedService<RigctldServer>();

// Register your settings service
builder.Services.AddSingleton<ISettingsService, SettingsService>();


// Add after existing service registrations
builder.Services.AddHostedService<MeterPollingService>();

// SDR spectrum display — reads IQ samples, computes FFT, broadcasts via SignalR
// Registered as singleton so the span-change API endpoint can call RequestRestart().
builder.Services.AddSingleton<Yaesu_Web_Control.Services.Sdr.SdrBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Yaesu_Web_Control.Services.Sdr.SdrBackgroundService>());

// Register the radio state service — reuse the same singleton instance as RadioStateService
builder.Services.AddSingleton<IRadioStateService>(sp => sp.GetRequiredService<RadioStateService>());

// Register the radio initialization service
builder.Services.AddSingleton<RadioInitializationService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<RadioInitializationService>());

// ADD THIS LINE for Razor Pages support:
builder.Services.AddRazorPages();

// Force the web host to use port 8080 on all interfaces
builder.WebHost.UseUrls("http://0.0.0.0:8080");

builder.Services.AddSingleton<BrowserLauncher>();
// builder.Services.AddHostedService<SystemTrayService>();

// Register WSJT-X UDP listener as a singleton so it can be injected into controllers
builder.Services.AddSingleton<WsjtxUdpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WsjtxUdpService>());

// Register process status cache service for efficient process lookups
builder.Services.AddSingleton<ProcessStatusCacheService>();

// Register radio memories service
builder.Services.AddSingleton<Yaesu_Web_Control.Services.MemoryService>();
builder.Services.AddSingleton<Yaesu_Web_Control.Services.MemoryBankService>();

// Register DX cluster service — single instance shared between controllers and
// the background hosted service so the API can read the spot buffer.
builder.Services.AddSingleton<Yaesu_Web_Control.Services.DxClusterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Yaesu_Web_Control.Services.DxClusterService>());

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddFilter((category, level) =>
{
    // Show Information and above for WsjtxUdpService
    if (!string.IsNullOrEmpty(category) && category.Contains("Yaesu_Web_Control.Services.WsjtxUdpService"))
        return level >= LogLevel.Information;
    // Show Information and above for DxClusterService so we can see the
    // raw protocol exchange when diagnosing why no spots appear.
    if (!string.IsNullOrEmpty(category) && category.Contains("Yaesu_Web_Control.Services.DxClusterService"))
        return level >= LogLevel.Information;
    // Show Warning and above for everything else
    return level >= LogLevel.Warning;
});
builder.Logging.AddDebug();


try
{
    var app = builder.Build();

    // Middleware to force Content-Language: en on all responses
    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() => {
            if (!context.Response.Headers.ContainsKey("Content-Language"))
            {
                context.Response.Headers.Append("Content-Language", "en");
            }
            return System.Threading.Tasks.Task.CompletedTask;
        });
        await next();
    });

    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }
    else
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    app.UseStaticFiles();
    var picturesPath = System.IO.Path.Combine(app.Environment.ContentRootPath, "pictures");
    if (System.IO.Directory.Exists(picturesPath))
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(picturesPath),
            RequestPath = "/pictures"
        });
    }
    app.UseRouting();
    app.UseAuthorization();
    //app.MapGet("/", () => "ROOT ROUTE HIT");

    app.MapRazorPages();
    app.MapControllers();

    // MAP SIGNALR HUB:
    app.MapHub<Yaesu_Web_Control.Hubs.RadioHub>("/radioHub");

    app.MapGet("/api/status/init", () => new { status = Yaesu_Web_Control.Services.AppStatus.InitializationStatus });

    // Serve accessible labels from AppData — copy default on first run so users can find and edit it.
    app.MapGet("/i18n/labels.json", (IWebHostEnvironment env) =>
    {
        var userPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control", "labels.json");

        if (!File.Exists(userPath))
        {
            var defaultPath = Path.Combine(env.WebRootPath, "i18n", "labels.default.json");
            Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
            File.Copy(defaultPath, userPath);
        }

        return Results.File(userPath, "application/json");
    });

    app.MapPost("/api/sdr/span", async (
        [Microsoft.AspNetCore.Mvc.FromQuery] double hz,
        Yaesu_Web_Control.Services.ISettingsService settings,
        Yaesu_Web_Control.Services.Sdr.SdrBackgroundService sdr) =>
    {
        double[] valid = [250_000, 500_000, 1_024_000, 2_048_000, 2_500_000, 3_200_000];
        if (Array.IndexOf(valid, hz) < 0) return Results.BadRequest("Invalid span value.");
        var s = await settings.GetSettingsAsync();
        s.SdrSampleRateHz = hz;
        await settings.SaveSettingsAsync(s);
        sdr.RequestRestart();
        return Results.Ok();
    });

    // Open browser automatically when app starts (but not when debugging in Visual Studio)
    var browserLauncher = app.Services.GetRequiredService<BrowserLauncher>();
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStarted.Register(() =>
    {
        browserLauncher.OpenOnce("http://localhost:8080");
    });

    app.Run();
}
catch (Exception ex)
{
    var msg = $"[FATAL] Application failed to start: {ex.Message}\n{ex.StackTrace}";
    Console.Error.WriteLine(msg);
    try { File.AppendAllText("fatal_startup_error.log", $"{DateTime.Now:u} {msg}\n"); } catch { }

#pragma warning disable CA1416
    if (IsPortInUseException(ex))
    {
        var owner = GetPortOwner(8080);
        var portMsg = owner is not null
            ? $"Port 8080 is already in use by {owner}.\n\nClose that application and try again."
            : "Port 8080 is already in use by another application.\n\nClose that application and try again.";
        MessageBox.Show(portMsg, "Port In Use", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    else
    {
        MessageBox.Show(
            $"Yaesu Web Control failed to start:\n\n{ex.Message}",
            "Startup Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
#pragma warning restore CA1416

    throw;
}

