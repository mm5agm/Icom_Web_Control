using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Icom_Web_Control.Services;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

// ── Single-instance guard ────────────────────────────────────────────────────
const string MutexName = "Global\\Icom_Web_Control_SingleInstance";
var mutex = new Mutex(initiallyOwned: true, name: MutexName, out bool createdNew);

if (!createdNew)
{
    // This used to be an OK-only "already running" box, which is a dead end when
    // the running copy is a stuck one: the operator sees no window, closes
    // nothing, and every relaunch hits this same box. That is exactly what
    // GitHub #2 reported — the only way out was Task Manager. Offer the two
    // useful actions instead: go to the running copy, or end it and start fresh.
#pragma warning disable CA1416
    var me = Process.GetCurrentProcess();
    Process[] others;
    try   { others = Process.GetProcessesByName(me.ProcessName).Where(p => p.Id != me.Id).ToArray(); }
    catch { others = Array.Empty<Process>(); }

    int existingPort = LoadConfiguredHttpPort();
    string url = $"http://localhost:{existingPort}";
    string who = others.Length > 0
        ? $"(process ID {string.Join(", ", others.Select(p => p.Id))})"
        : "(its window may be minimised to the system tray)";

    var choice = MessageBox.Show(
        $"Icom Web Control is already running {who}.\n\n" +
        $"Yes\t— open the running copy at {url}\n" +
        "No\t— close the running copy and start a new one\n" +
        "Cancel\t— do nothing",
        "Already Running",
        MessageBoxButtons.YesNoCancel,
        MessageBoxIcon.Information);

    if (choice == DialogResult.Yes)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        mutex.Dispose();
        return;
    }

    if (choice != DialogResult.No)
    {
        mutex.Dispose();
        return;
    }

    // Asked to end the running copy: request a clean close first, then force it.
    foreach (var p in others)
    {
        try
        {
            if (!p.CloseMainWindow() || !p.WaitForExit(4000))
                p.Kill(entireProcessTree: true);
            p.WaitForExit(4000);
        }
        catch { /* already gone, or access denied — the mutex retry below decides */ }
    }

    // The old process holds the mutex until it actually exits; give the handle a
    // moment to be released, then try to claim it ourselves.
    mutex.Dispose();
    Thread.Sleep(500);
    mutex = new Mutex(initiallyOwned: true, name: MutexName, out createdNew);
    if (!createdNew)
    {
        MessageBox.Show(
            "The running copy of Icom Web Control could not be closed.\n\n" +
            "End \"Icom_Web_Control.exe\" in Task Manager (Ctrl+Shift+Esc), then start it again.",
            "Still Running",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        mutex.Dispose();
        return;
    }
#pragma warning restore CA1416
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

// Probe a TCP port to see if IWC can bind to it. Uses Socket.Bind on
// IPAddress.Any so we catch the full set of "port unavailable" cases:
//   - port already in use by another listener
//   - port in a Windows excluded range (WSL / Hyper-V / Docker)
//   - "socket access permissions" denial (some antivirus winsock hooks)
// All of these surface as a SocketException at bind time. We open and
// immediately close — there's a small race between this probe and Kestrel's
// real bind a few milliseconds later, but in practice that race window is
// short enough not to matter.
static bool IsPortFree(int port)
{
    // Enumerate every TCP port currently in LISTENING state on the system.
    // Way more reliable than trying to Bind() a probe socket: on Windows,
    // a second `Bind` to a port that another process is already listening
    // on can silently succeed (both end up in LISTENING; only one actually
    // receives traffic — the other "shadows" the first). SO_EXCLUSIVEADDRUSE
    // is supposed to prevent this but its semantics depend on flags both
    // sockets were created with, so we don't trust it. The active-listeners
    // enumeration sees the OS truth directly.
    var listeners = System.Net.NetworkInformation.IPGlobalProperties
        .GetIPGlobalProperties()
        .GetActiveTcpListeners();
    foreach (var endpoint in listeners)
    {
        if (endpoint.Port == port)
            return false;
    }
    return true;
}

// Pre-startup helper: read the user's configured HTTP port from
// appsettings.user.json (if it exists) without spinning up the full DI
// container. Falls back to 8080. Bounded to a sane range. We only need
// the one field so a minimal JSON parse keeps startup fast and avoids
// circular dependencies between port resolution and DI.
static int LoadConfiguredHttpPort()
{
    try
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Icom Web Control", "appsettings.user.json");
        if (!File.Exists(path)) return 8080;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("HttpPort", out var p) && p.TryGetInt32(out int port))
        {
            if (port >= 1 && port <= 65535) return port;
        }
    }
    catch { }
    return 8080;
}

// ── Serilog file logging ────────────────────────────────────────────────────
// IWC is a WinExe (no console window) so stdout-based loggers are invisible.
// Wire up Serilog with a rolling-daily file sink under %APPDATA% so we have a
// readable record of what the app did — essential for diagnosing shutdown
// hangs, CAT timeouts, SDR init failures and anything else the user can't see.
var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "MM5AGM", "Icom Web Control", "logs");
try { Directory.CreateDirectory(logDir); } catch { /* fall through, Serilog will surface the problem */ }

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.SignalR", Serilog.Events.LogEventLevel.Warning)
    // Keep Hosting.Lifetime at Information so we see exactly when
    // StopApplication is called and when each hosted service's StopAsync runs
    // — invaluable for diagnosing shutdown stalls.
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
    .Enrich.FromLogContext()
    // Wrap the file sink in Serilog.Sinks.Async so log writes happen on a
    // dedicated background thread rather than on the calling thread. The bare
    // synchronous File sink — especially with shared:true (a cross-process
    // mutex taken per event) — blocks whichever thread logs, and under the
    // startup logging flood (54-command init burst + concurrent meter polling,
    // each CAT response emitting several lines) that blocked dozens of
    // thread-pool threads at once. The pool then injected replacements at only
    // ~1/sec, dilating the whole app — including the init burst's Task.Delay
    // continuations — to roughly 1 Hz, so the init sequence never reached the
    // DT0 step and the app hung at "Initializing". Intermittent because it
    // depends on disk / AV / file-lock timing (issue #73, wa6auf). Dropped
    // shared:true as well — IWC is the only writer of this file.
    .WriteTo.Async(a => a.File(
        Path.Combine(logDir, "iwc-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        flushToDiskInterval: TimeSpan.FromSeconds(1),
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"))
    .CreateLogger();

Log.Information("Icom Web Control starting (v{Version})", Icom_Web_Control.AppVersion.Display);

// Raise the thread-pool floor so cold start doesn't bottleneck on the pool's
// ~1/sec starvation-recovery thread injection. Startup fires many concurrent
// hosted services (radio init burst, meter polling, rigctld, SignalR) at once;
// if any of them briefly blocks a pool thread, a low floor forces new work to
// wait ~1s per thread for the pool to grow. A modest floor absorbs that spike.
// Belt-and-suspenders alongside the async logging sink above (issue #73).
{
    ThreadPool.GetMinThreads(out int minWorker, out int minIo);
    int targetWorker = Math.Max(minWorker, Math.Max(16, Environment.ProcessorCount * 2));
    int targetIo = Math.Max(minIo, 16);
    ThreadPool.SetMinThreads(targetWorker, targetIo);
    Log.Information("ThreadPool min threads set: worker {Worker} (was {OldWorker}), IO {Io} (was {OldIo}); processors={Cpu}",
        targetWorker, minWorker, targetIo, minIo, Environment.ProcessorCount);
}

var builder = WebApplication.CreateBuilder(args);

// Cap the host's overall shutdown timeout. Default is 30 s (which we hit on
// every tray Exit before adding this cap); 2 s is plenty for our user
// services to wind down their StopAsync routines. Tracked in the project
// todo memory.
builder.Services.Configure<HostOptions>(opts =>
{
    opts.ShutdownTimeout = TimeSpan.FromSeconds(2);

    // Never let one background service's unhandled exception stop the whole
    // host. The default (StopHost) means a single throw from e.g. the CI-V
    // poll loop while the radio's USB port is yanked would kill the app,
    // leaving the web page up with no comms. Individual services already log
    // and recover; this is the backstop so a missed case degrades one service
    // instead of taking everything (SignalR, voice, rigctld) down with it.
    opts.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

builder.Services.AddSingleton<CalibrationContributionsStore>();
builder.Services.AddSingleton<CalibrationStorage>();
builder.Services.AddSingleton<ICalibrationService, CalibrationService>();

// ADD SIGNALR EARLY (before services that depend on IHubContext):
builder.Services.AddSignalR();

// Register the persistence service (no hub dependency)
builder.Services.AddSingleton<RadioStatePersistenceService>();

// Band edges for the operator's own IARU region, read from
// wwwroot/bandplan.default.json — the same file the browser overlays at
// startup. RadioStateService resolves BandA/BandB through this, so the server
// and the waterfall can no longer disagree about where a band ends.
builder.Services.AddSingleton<IBandPlanService, BandPlanService>();

// Register RadioStateService as a singleton
builder.Services.AddSingleton<RadioStateService>();

// ── IRadioController seam (Phase 2) ─────────────────────────────────────────
// The semantic, protocol-free seam IWC introduces. Phase 2 backs it with the
// real CI-V link: CivBusService owns the serial port and frames the bus;
// CivRadioController is the single class below the seam that emits CI-V and,
// as a hosted service, connects to the IC-7300 MkII and polls VFO-A frequency
// into RadioStateService → SignalR. The Phase 1 canned StubRadioController is
// retained in the tree (unregistered) as a no-hardware fallback for reference.
// See docs/design/iwc-clone-split-plan.md.
builder.Services.AddSingleton<Icom_Web_Control.Services.Civ.ICivClient, Icom_Web_Control.Services.Civ.CivBusService>();

// No-hardware preview mode: set IWC_USE_STUB_RADIO=1 to back the seam with the
// canned StubRadioController instead of the real CI-V link. Lets the pseudo-dual
// two-panel spectrum UI (and gauges) be demoed/developed without a radio plugged
// in. The real CivRadioController is still registered as a plain singleton so
// anything resolving it directly keeps working; it just isn't hosted (won't open
// the serial port) in stub mode.
var useStubRadio = string.Equals(
    Environment.GetEnvironmentVariable("IWC_USE_STUB_RADIO"), "1", StringComparison.Ordinal);

builder.Services.AddSingleton<CivRadioController>();
if (useStubRadio)
{
    builder.Services.AddSingleton<StubRadioController>();
    builder.Services.AddSingleton<IRadioController>(sp => sp.GetRequiredService<StubRadioController>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<StubRadioController>());
}
else
{
    builder.Services.AddSingleton<IRadioController>(sp => sp.GetRequiredService<CivRadioController>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<CivRadioController>());
}

// Register the rigctld server as a background service
builder.Services.AddHostedService<RigctldServer>();

// Register your settings service
builder.Services.AddSingleton<ISettingsService, SettingsService>();

// Meter polling was the Yaesu CAT MeterPollingService — deleted in the carve.
// Meter reads (S-meter, Po/SWR/ALC) now come through the CI-V seam
// (CivRadioController), so there is no separate hosted poller to register.

// Register the radio state service — reuse the same singleton instance as RadioStateService
builder.Services.AddSingleton<IRadioStateService>(sp => sp.GetRequiredService<RadioStateService>());

// The Yaesu RadioInitializationService (serial connect + read-burst + state
// restore) was deleted in the carve. Its job — connect the radio and restore
// state at startup — is now owned by CivRadioController (hosted) over CI-V, and
// the Connect / Test-Connection endpoints call IRadioController.ConnectAsync.

// ADD THIS LINE for Razor Pages support:
builder.Services.AddRazorPages();

// ── HTTP port resolution ────────────────────────────────────────────────────
// Pick the port BEFORE Kestrel binds, so we can fall back gracefully if the
// user's configured port (default 8080) is held by another program. We try
// the configured port plus the nine above it; whichever is free first wins.
// The chosen port is published as a singleton HttpPortInfo so the browser
// launcher, system tray, and Settings UI all read the same value (Issue #13).
int basePort = LoadConfiguredHttpPort();
int chosenPort = -1;
var triedPorts = new List<int>();
for (int candidate = basePort; candidate < basePort + 10 && candidate <= 65535; candidate++)
{
    triedPorts.Add(candidate);
    if (IsPortFree(candidate))
    {
        chosenPort = candidate;
        break;
    }
}
if (chosenPort < 0)
{
#pragma warning disable CA1416
    var diag = string.Join("\n",
        triedPorts.Select(p => $"  {p,5} — {GetPortOwner(p) ?? "unknown / Windows-reserved"}"));
    MessageBox.Show(
        $"Icom Web Control couldn't find a free TCP port to listen on.\n\n" +
        $"Tried ports {triedPorts.First()}–{triedPorts.Last()}:\n\n{diag}\n\n" +
        $"Either close one of those programs, or open Icom Web Control's\n" +
        $"Settings page on a working installation and change the HttpPort\n" +
        $"value in %APPDATA%\\MM5AGM\\Icom Web Control\\appsettings.user.json\n" +
        $"to a free port (e.g. 9080), then restart.",
        "No free port available",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);
#pragma warning restore CA1416
    return;
}

// Force the web host to use the chosen port on all interfaces.
builder.WebHost.UseUrls($"http://0.0.0.0:{chosenPort}");

// Publish the chosen port so every consumer reads from one source of truth.
builder.Services.AddSingleton(new HttpPortInfo(chosenPort));

builder.Services.AddSingleton<BrowserLauncher>();
// System tray icon — gives operators a visible "IWC is running" indicator
// and a clean Exit menu. Implemented as an STA-threaded hosted service.
builder.Services.AddHostedService<SystemTrayService>();

// Register WSJT-X UDP listener as a singleton so it can be injected into controllers
builder.Services.AddSingleton<WsjtxUdpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WsjtxUdpService>());

// Register process status cache service for efficient process lookups
builder.Services.AddSingleton<ProcessStatusCacheService>();

// Register radio memories service
builder.Services.AddSingleton<Icom_Web_Control.Services.MemoryService>();
builder.Services.AddSingleton<Icom_Web_Control.Services.MemoryBankService>();

// Register DX cluster service — single instance shared between controllers and
// the background hosted service so the API can read the spot buffer.
builder.Services.AddSingleton<Icom_Web_Control.Services.DxClusterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Icom_Web_Control.Services.DxClusterService>());

// Voice control (in-process SAPI). VoiceControlService is the IHostedService
// that owns the SpeechRecognitionEngine; IntentDispatcher maps recognised
// intents to CAT actions; VoiceTtsService speaks confirmation phrases;
// VoiceController exposes /api/voice/*. See docs/VoiceControl/v1-plan.md.
builder.Services.AddSingleton<Icom_Web_Control.Services.Voice.IntentDispatcher>();
builder.Services.AddSingleton<Icom_Web_Control.Services.Voice.VoiceTtsService>();
builder.Services.AddSingleton<Icom_Web_Control.Services.Voice.VoicePhraseStore>();
builder.Services.AddSingleton<Icom_Web_Control.Services.Voice.VoiceControlService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Icom_Web_Control.Services.Voice.VoiceControlService>());

// CW reader. Singletons, all four: one recording device, one decoder, one
// piece of decoded text however many browser tabs are open. Reader Mode in
// particular has to be a singleton or the record of what the operator's filter
// used to be would be per-request, which is the same bug as keeping it in the
// browser tab. Nothing here starts until the operator presses Start - the
// device is not opened at boot.
builder.Services.AddSingleton<Icom_Web_Control.Services.Cw.WaveInCwAudioSource>();
builder.Services.AddSingleton<Icom_Web_Control.Services.Cw.CwReaderService>();
builder.Services.AddSingleton<Icom_Web_Control.Services.Cw.CwQsoLogService>();
builder.Services.AddSingleton<Icom_Web_Control.Services.Cw.CwReaderModeService>();

// Route everything through Serilog (file sink configured above). The previous
// console + filter chain is gone — it was invisible in a WinExe anyway, and
// the file sink captures Information+ globally so we can read what happened
// after the fact without a console window.
builder.Logging.ClearProviders();
builder.Host.UseSerilog();


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

    // Static files must be REVALIDATED, not trusted from cache.
    //
    // Without an explicit Cache-Control, ASP.NET Core sends only Last-Modified
    // and ETag, which leaves the browser free to apply heuristic freshness —
    // commonly a tenth of the file's age. A file that was already a fortnight
    // old when the browser cached it therefore stays "fresh" for a day or more,
    // and the browser will not so much as ask whether it changed.
    //
    // Installing a new version over the top replaces the file on disk but does
    // nothing to that cached copy, so v1.0.6 shipped its two headline fixes —
    // the Fixed-mode spectrum window and the Firefox meter needles — to users
    // who carried on running the old JavaScript and saw neither. Ctrl+F5 fixed
    // it, which is not something an operator should have to know.
    //
    // "no-cache" does NOT mean "do not store": the browser keeps the file and
    // revalidates it, so an unchanged file costs one conditional GET answered
    // with a 304 and no body. On a localhost or LAN app that is free, and it is
    // the price of never shipping a fix that fails to arrive.
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
        }
    });
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
    app.MapHub<Icom_Web_Control.Hubs.RadioHub>("/radioHub");

    // `detail` carries a human-readable reason the link isn't up (e.g. the
    // configured serial port isn't present) so the init overlay can show the
    // cause instead of an indefinite "Initializing…" spinner. Empty when OK.
    app.MapGet("/api/status/init", (Icom_Web_Control.Services.RadioStateService state) =>
        new { status = Icom_Web_Control.Services.AppStatus.InitializationStatus, detail = state.ConnectionStatusText ?? "" });

    app.MapGet("/api/ports", async (Icom_Web_Control.Services.ISettingsService settingsService) =>
    {
        var ports = System.IO.Ports.SerialPort.GetPortNames();
        var configured = (await settingsService.GetSettingsAsync()).SerialPort;
        return new { ports, configuredPort = configured, configuredPresent = ports.Contains(configured) };
    });

    // Serve accessible labels from AppData — copy default on first run so users can find and edit it.
    app.MapGet("/i18n/labels.json", (IWebHostEnvironment env) =>
    {
        var userPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Icom Web Control", "labels.json");

        if (!File.Exists(userPath))
        {
            var defaultPath = Path.Combine(env.WebRootPath, "i18n", "labels.default.json");
            Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
            File.Copy(defaultPath, userPath);
        }

        return Results.File(userPath, "application/json");
    });

    // Open browser automatically when app starts (but not when debugging in Visual Studio)
    var browserLauncher = app.Services.GetRequiredService<BrowserLauncher>();
    var portInfo        = app.Services.GetRequiredService<HttpPortInfo>();
    var lifetime        = app.Services.GetRequiredService<IHostApplicationLifetime>();

    // Lifecycle-event log fences so we can see in the Serilog file exactly
    // when each shutdown phase fires. Helps diagnose "what's the framework
    // doing for 30 s between ApplicationStopping and the first hosted-service
    // StopAsync" — see project todo memory.
    lifetime.ApplicationStopping.Register(() => Log.Information("[Lifecycle] ApplicationStopping fired"));
    lifetime.ApplicationStopped.Register(()  => Log.Information("[Lifecycle] ApplicationStopped fired"));

    // Hard-exit watchdog. Shutdown is capped (HostOptions.ShutdownTimeout = 2 s)
    // and each service unwinds its own blocking reads, but a process that
    // *doesn't* exit is uniquely bad here: the single-instance mutex then blocks
    // every relaunch, so the operator is locked out of the app entirely with no
    // window to close (GitHub #2). Whatever the cause of a stall — a serial read
    // wedged in the driver, a non-background thread nobody joined — 10 seconds
    // after shutdown starts we leave. Logged so the file still names the stall.
    lifetime.ApplicationStopping.Register(() =>
    {
        var bail = new Thread(() =>
        {
            Thread.Sleep(10_000);
            Log.Warning("[Lifecycle] Still alive 10s after ApplicationStopping — forcing exit");
            Log.CloseAndFlush();
            Environment.Exit(0);
        })
        { IsBackground = true, Name = "ShutdownWatchdog" };
        bail.Start();
    });

    lifetime.ApplicationStarted.Register(() =>
    {
        browserLauncher.OpenOnce(portInfo.RootUrl);
    });

    app.Run();
    Log.Information("app.Run() returned cleanly — flushing logs and exiting");
    Log.CloseAndFlush();
}
catch (Exception ex)
{
    var msg = $"[FATAL] Application failed to start: {ex.Message}\n{ex.StackTrace}";
    Console.Error.WriteLine(msg);
    try { File.AppendAllText("fatal_startup_error.log", $"{DateTime.Now:u} {msg}\n"); } catch { }
    Log.Fatal(ex, "Application failed to start");
    Log.CloseAndFlush();

#pragma warning disable CA1416
    if (IsPortInUseException(ex))
    {
        // We pre-probed the port before configuring Kestrel, so this catch is
        // only reached if the chosen port was grabbed by another process in
        // the race window between probe and bind. Report whichever port we
        // actually chose, not the hardcoded default.
        var owner = GetPortOwner(chosenPort);
        var portMsg = owner is not null
            ? $"Port {chosenPort} is already in use by {owner}.\n\nClose that application and try again."
            : $"Port {chosenPort} is already in use by another application.\n\nClose that application and try again.";
        MessageBox.Show(portMsg, "Port In Use", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    else
    {
        MessageBox.Show(
            $"Icom Web Control failed to start:\n\n{ex.Message}",
            "Startup Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
#pragma warning restore CA1416

    throw;
}


