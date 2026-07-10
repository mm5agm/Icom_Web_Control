using System.Reflection;

namespace Yaesu_Web_Control.Services;

/// <summary>
/// Immutable record describing everything needed to deploy the VC Tune module
/// into a Yaesu Web Control installation or to integrate it into a new host
/// application. Obtain an instance via the static factory
/// <see cref="CreatePackage"/>.
/// </summary>
public sealed record VCTuneDeploymentPackage
{
    /// <summary>
    /// The VC Tune module version string, derived from the containing assembly's
    /// informational version attribute at package-creation time.
    /// </summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// UTC instant at which this package descriptor was generated.
    /// </summary>
    public DateTime BuildTimestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Logical list of source modules / types that make up the VC Tune subsystem.
    /// All types compile into the single Yaesu Web Control assembly; this list is
    /// informational for code-review and auditing purposes.
    /// </summary>
    public IReadOnlyList<string> IncludedAssemblies { get; init; } = [];

    /// <summary>
    /// Runtime resource files and configuration artefacts consumed or produced by
    /// the VC Tune module.
    /// </summary>
    public IReadOnlyList<string> IncludedResources { get; init; } = [];

    /// <summary>
    /// Deployment notes covering requirements, limitations, and recommended
    /// configuration. Each entry is a self-contained note string.
    /// </summary>
    public IReadOnlyList<string> DeploymentNotes { get; init; } = [];

    /// <summary>
    /// Step-by-step guide for integrating the VC Tune module into a fresh
    /// Yaesu Web Control installation.
    /// </summary>
    public string InstallationGuide { get; init; } = string.Empty;

    /// <summary>
    /// Step-by-step guide for upgrading an existing installation that contains
    /// an earlier VC Tune implementation.
    /// </summary>
    public string UpgradeGuide { get; init; } = string.Empty;

    /// <summary>
    /// Notes on supported platforms, firmware, UI frameworks, and any breaking
    /// changes from previous releases.
    /// </summary>
    public string CompatibilityNotes { get; init; } = string.Empty;

    // ══════════════════════════════════════════════════════════════════════
    // Factory
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates a fully populated <see cref="VCTuneDeploymentPackage"/> by
    /// inspecting the live <paramref name="module"/> for its assembly version
    /// and constructing all documentation strings from the known system
    /// specification.
    /// </summary>
    /// <param name="module">
    /// The assembled <see cref="VCTuneModule"/> singleton, used solely to
    /// resolve the host assembly version. The module is not exercised — no
    /// CAT commands are sent.
    /// </param>
    /// <returns>An immutable, fully populated deployment package descriptor.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="module"/> is null.
    /// </exception>
    public static VCTuneDeploymentPackage CreatePackage(VCTuneModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        var version = module.GetType().Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? module.GetType().Assembly.GetName().Version?.ToString()
            ?? "unknown";

        return new VCTuneDeploymentPackage
        {
            Version          = version,
            BuildTimestamp   = DateTime.UtcNow,
            IncludedAssemblies  = BuildAssemblyList(),
            IncludedResources   = BuildResourceList(),
            DeploymentNotes     = BuildDeploymentNotes(),
            InstallationGuide   = BuildInstallationGuide(),
            UpgradeGuide        = BuildUpgradeGuide(),
            CompatibilityNotes  = BuildCompatibilityNotes(),
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    // Private builders
    // ══════════════════════════════════════════════════════════════════════

    private static IReadOnlyList<string> BuildAssemblyList() =>
    [
        // ── Enums and value types ──────────────────────────────────────
        "Services/VcTune/VcTuneReceiver.cs          — P1 receiver enum (Main/Sub)",
        "Services/VcTune/VcTuneAvailability.cs      — P6 availability enum (NotInstalled/Available/UnavailableFrequency)",
        "Services/VcTune/VCTuneBand.cs              — Builder-layer band enum (Main/Sub, same int values as VcTuneReceiver)",
        "Services/VcTune/VCTuneCommandType.cs       — P2 command type enum (Off/On/Default/Step/Center/Read)",
        "Services/VcTune/VCTuneDirection.cs         — P3 step direction enum (Plus/Minus)",
        "Services/VcTune/VCTuneState.cs             — Operational state enum (Off/On/Default/Stepping/Centering/Unavailable/NotInstalled)",
        "Services/VcTune/VCTuneMeterDisplayMode.cs  — UI P5 display mode enum (Raw/Percentage/BarOnly/Hidden)",
        "Services/VcTune/VCTuneErrorType.cs         — Error category enum (7 values)",
        // ── Records ───────────────────────────────────────────────────
        "Services/VcTune/VcTuneReadResult.cs        — Parsed VT READ response (P1–P6 + validity)",
        "Services/VcTune/VcTuneSetResult.cs         — SET operation outcome (Success/ErrorCategory/ConfirmedStatus)",
        "Services/VcTune/VCTuneCommand.cs           — Immutable built CAT command string + metadata",
        "Services/VcTune/VCTuneResponse.cs          — Injectable-parser output record (P1–P6 typed)",
        "Services/VcTune/VCTuneStateSnapshot.cs     — Immutable per-receiver state snapshot (Band/State/Meter/Availability/Timestamp)",
        "Services/VcTune/VCTuneUserPreferences.cs   — Persisted user preferences (8 fields)",
        "Services/VcTune/VCTuneRadioCapabilities.cs — Per-model capability record with P6-driven sub-board detection",
        "Services/VcTune/VCTuneSessionState.cs      — In-memory-only session state (P5/P6, never persisted)",
        "Services/VcTune/VCTuneDiagnosticEntry.cs   — Single diagnostics history entry (via VCTuneDiagnostics.cs)",
        "Services/VcTune/VCTuneCommandResult.cs     — Unified module-level operation result",
        "Services/VcTune/VCTuneIntegrationResult.cs — Test-flow result (via VCTuneIntegrationHarness.cs)",
        "Services/VcTune/VCTuneHelpSection.cs       — Help content section record (via VCTuneHelpProvider.cs)",
        "Services/VcTune/VCTuneVoiceExample.cs      — Voice command example record (via VCTuneHelpProvider.cs)",
        "Services/VcTune/VCTuneAssemblyValidationReport.cs — Deployment readiness report record + GenerateReport()",
        "Services/VcTune/VCTuneDeploymentPackage.cs — This file — deployment descriptor + CreatePackage()",
        // ── Interfaces ────────────────────────────────────────────────
        "Services/VcTune/IVcTuneService.cs              — Backend service contract",
        "Services/VcTune/IVCTuneCommandBuilder.cs        — CAT command builder contract",
        "Services/VcTune/IVCTuneResponseParser.cs        — Injectable parser contract",
        "Services/VcTune/IVCTuneStateMachine.cs          — State machine contract",
        "Services/VcTune/IVCTuneConfigurationStore.cs    — Configuration store contract",
        // ── Implementations ───────────────────────────────────────────
        "Services/VcTune/CatRequestSemaphore.cs          — CAT bus serialisation semaphore",
        "Services/VcTune/VcTuneService.cs                — Backend CAT service (SET + READ over ICatClient)",
        "Services/VcTune/VCTuneCommandBuilder.cs         — CAT command builder (VT wire format)",
        "Services/VcTune/VCTuneResponseParser.cs         — Static internal parser + injectable class (same file)",
        "Services/VcTune/VCTuneStateMachine.cs           — Thread-safe state machine (MAIN + SUB snapshots)",
        "Services/VcTune/VCTuneViewModel.cs              — INotifyPropertyChanged view model",
        "Services/VcTune/VCTuneConfigurationStore.cs     — JSON persistence (SemaphoreSlim, temp-then-rename writes)",
        "Services/VcTune/VCTuneDiagnostics.cs            — ConcurrentQueue ring-buffer diagnostics sink",
        "Services/VcTune/VCTuneHelpProvider.cs           — Static help content (8 sections, 6 voice examples, 6 troubleshooting entries)",
        "Services/VcTune/VCTuneModule.cs                 — Top-level orchestrator (all 9 subsystems)",
        "Services/VcTune/VCTuneIntegrationHarness.cs     — 8-flow integration test harness",
        "Services/Voice/VCTuneRecognizer.cs              — SAPI grammar builder + intent dispatcher",
    ];

    private static IReadOnlyList<string> BuildResourceList() =>
    [
        "%APPDATA%\\MM5AGM\\Yaesu Web Control\\vcTune_config.json" +
            "  — VC Tune user preferences and per-model radio capabilities." +
            "  Created automatically on first save. Session state is absent by design.",

        "%APPDATA%\\MM5AGM\\Yaesu Web Control\\vcTune_config.json.tmp" +
            "  — Transient file used during atomic writes; deleted immediately after rename." +
            "  If this file is present after a crash it is safe to delete manually.",

        "Services/RadioCapabilities.cs" +
            "  — Static lookup table consumed by VCTuneRadioCapabilities.ForModel()." +
            "  Must be updated when a new radio model is added that supports VC Tune.",

        "VoiceGrammar.BuildEnGb()" +
            "  — Runtime SAPI grammar compiled from VCTuneRecognizer.GetGrammarVariants()." +
            "  Compiled in-process; no external SRGS file is required.",
    ];

    private static IReadOnlyList<string> BuildDeploymentNotes() =>
    [
        "REQUIREMENT — .NET version: .NET 10 (net10.0-windows). " +
            "The project targets Windows exclusively (UseWindowsForms=true, OutputType=WinExe). " +
            "Do not retarget to a cross-platform TFM without removing the System.Speech grammar-building code.",

        "REQUIREMENT — CAT pipeline: The VC Tune module requires IVcTuneService, which depends on " +
            "ICatClient and CatRequestSemaphore. RadioInitializationService must call " +
            "VCTuneModule.InitializeAsync() after DT0 is confirmed and the serial port is open. " +
            "VCTuneModule.ShutdownAsync() must be called in the radio-disconnect handler.",

        "REQUIREMENT — Supported radios: FTdx101D and FTdx101MP only. " +
            "MAIN VC Tune is standard on both models. " +
            "SUB VC Tune (FTdx101MP only) requires the VRF-101 option board; " +
            "presence is auto-detected from VT READ P6 responses and persisted in vcTune_config.json.",

        "CAPABILITY DETECTION — SUB VC Tune: VCTuneRadioCapabilities.SubInstallationConfirmed is " +
            "set to true the first time a VT READ for the SUB receiver returns P6 >= 1 (board present). " +
            "This result is persisted so subsequent sessions do not require a fresh probe. " +
            "If the board is removed, clear vcTune_config.json to reset.",

        "LIMITATION — P5/P6 values are session-only: The P5 coupling indicator and P6 availability " +
            "byte are never written to disk. They reset to -1 / 0 on every radio reconnect. " +
            "This is intentional: frequency changes between sessions make stored P5/P6 meaningless.",

        "LIMITATION — Motor lockout: A 3-second lockout is enforced after Center commands by " +
            "VcTuneService. Rapid Center → Step sequences within that window will be queued behind " +
            "the semaphore and may time out if CancellationToken deadline is short.",

        "LOGGING — Recommended configuration: Set the minimum level for the " +
            "Yaesu_Web_Control.Services namespace to Information in production and to Debug during " +
            "initial integration. All VC Tune log entries are prefixed [VCTune] or [VCTuneModule] " +
            "for easy filtering. Serilog structured logging is the host logger.",

        "RECOGNIZER — Recommended configuration: Ensure the System.Speech recogniser is running " +
            "with the en-GB grammar loaded before VCTuneModule.InitializeAsync() returns. " +
            "The six VC Tune grammar variants (plus/minus band variants = 12 GrammarBuilders) " +
            "are added by VCTuneRecognizer.GetGrammarVariants(). " +
            "If SAPI grammar compilation fails for a step variant (3 consecutive SemanticResultKeys), " +
            "the VoiceGrammar.Try() wrapper will log the failure and continue — other intents " +
            "remain functional.",
    ];

    private static string BuildInstallationGuide() =>
        """
        VC Tune Module — Installation Guide
        ====================================

        Step 1: Verify assembly prerequisite
          Confirm the build target is net10.0-windows and UseWindowsForms=true.
          The VC Tune module uses System.Speech (Windows-only) for voice grammar
          building. Build will fail on non-Windows TFMs.

        Step 2: Register all DI services in Program.cs
          Add the following singleton registrations in order:

            builder.Services.AddSingleton<CatRequestSemaphore>();
            builder.Services.AddSingleton<IVCTuneCommandBuilder, VCTuneCommandBuilder>();
            builder.Services.AddSingleton<IVCTuneResponseParser, VCTuneResponseParser>();
            builder.Services.AddSingleton<IVCTuneStateMachine, VCTuneStateMachine>();
            builder.Services.AddSingleton<IVCTuneConfigurationStore, VCTuneConfigurationStore>();
            builder.Services.AddSingleton<IVcTuneService, VcTuneService>();
            builder.Services.AddSingleton<VCTuneViewModel>();
            builder.Services.AddSingleton<VCTuneDiagnostics>();
            builder.Services.AddSingleton<VCTuneHelpProvider>();
            builder.Services.AddSingleton<VCTuneModule>();
            builder.Services.AddSingleton<VCTuneIntegrationHarness>();
            builder.Services.AddSingleton<Voice.VCTuneRecognizer>();

          Note: CatRequestSemaphore must be registered before IVcTuneService.
          Note: IVCTuneStateMachine must be registered before VCTuneModule.

        Step 3: Wire into the CAT pipeline
          In RadioInitializationService.StartAsync (after DT0 confirmation):

            var vcTune = host.Services.GetRequiredService<VCTuneModule>();
            await vcTune.InitializeAsync(cancellationToken);

          In the radio-disconnect handler (RadioInitializationService or equivalent):

            await vcTune.ShutdownAsync(cancellationToken);

        Step 4: Wire into the voice recognizer
          In VoiceGrammar.BuildEnGb(), add VC Tune grammar variants:

            foreach (var variant in Voice.VCTuneRecognizer.GetGrammarVariants())
                grammar.Add(variant);  // wrapped in your Try() helper

          In IntentDispatcher (or equivalent), route recognised intents:

            if (Voice.VCTuneRecognizer.IsVCTuneIntent(intent))
                await vcTuneRecognizer.DispatchAsync(intent, parameters, ct);

        Step 5: Wire into the UI view-model layer
          Inject VCTuneViewModel into Index.cshtml.cs:

            public VCTuneViewModel VCTune { get; }

          In OnGetAsync:

            await VCTune.RefreshAsync(cancellationToken);

          Use @Model.VCTune.MainIsOn, @Model.VCTune.MainMeter, etc. in Razor markup.

          After any SET command from an API controller:

            var result = await _module.ExecuteCommandAsync(command, ct);
            // result.UpdatedSnapshot carries the confirmed post-command state.

        Step 6: Enable diagnostics
          Inject VCTuneDiagnostics into the Diagnostics Razor page:

            public VCTuneDiagnostics VCTuneDiag { get; }

          In the Diagnostics page:

            var history = VCTuneDiag.GetHistory();
            // Render history entries sorted by Timestamp ascending.

          VCTuneDiagnostics.ResetHistory() is called automatically by
          VCTuneModule.ShutdownAsync(); no additional wiring required.

        Step 7: Load user preferences at startup
          VCTuneModule.InitializeAsync() calls IVCTuneConfigurationStore.LoadAsync()
          automatically. No separate startup call is required. Preferences are
          reloaded from vcTune_config.json on every InitializeAsync invocation
          (i.e., each radio reconnect).

        Step 8: Validate the assembly (optional but recommended)
          After DI container build, generate and log the validation report:

            var module  = app.Services.GetRequiredService<VCTuneModule>();
            var harness = app.Services.GetRequiredService<VCTuneIntegrationHarness>();
            var report  = VCTuneAssemblyValidationReport.GenerateReport(module, harness);
            if (!report.AllReady)
                logger.LogError("VC Tune assembly validation failed:\n{Summary}", report.Summary);
        """;

    private static string BuildUpgradeGuide() =>
        """
        VC Tune Module — Upgrade Guide
        ==============================

        This guide covers upgrading from any earlier ad-hoc VC Tune implementation
        to the formal 11-subsystem module described in Messages 43–56.

        Step 1: Remove legacy VC Tune code
          Delete any previous VcTuneHelper, VcTuneController, or inline VT-command
          logic from RadioInitializationService or Index.cshtml.cs. The new module
          owns all VT CAT operations exclusively.

        Step 2: Migrate configuration storage
          If a previous version stored VC Tune preferences inside appsettings.user.json,
          move them to vcTune_config.json by:
            a. Reading the old fields from SettingsService.
            b. Constructing a VCTuneUserPreferences record with those values.
            c. Calling IVCTuneConfigurationStore.SavePreferencesAsync() once at startup.
            d. Removing the old fields from ApplicationSettings and re-saving via SettingsService.
          vcTune_config.json is created automatically if absent; no migration script is required
          for fresh installations.

        Step 3: Update recognizer intents
          Replace any hard-coded intent-name strings with the constants on VCTuneRecognizer:
            VCTuneRecognizer.IntentOn, IntentOff, IntentDefault, IntentStep, IntentCenter,
            IntentReadStatus.
          Replace any inline GrammarBuilder construction with VCTuneRecognizer.GetGrammarVariants().
          The KeyBand, KeyDirection, KeyStep token keys are also constants on the same class.

        Step 4: Update UI bindings
          Replace any hand-rolled P5/P6 property bindings with VCTuneViewModel properties.
          Key reactive properties:
            MainMeter (int, 0–255), MainAvailability (int, 0–2), MainState (VCTuneState),
            MainIsOn, MainIsOff, MainIsActive, MainIsAvailable, MainWarningText (string?),
            SubIsVisible (bool), SubIsEnabled (bool), SubWarningText (string?).
          INotifyPropertyChanged is implemented; any host that uses data binding receives
          change notifications automatically.

        Step 5: Update diagnostics schema
          If a previous version wrote VT events to the shared YWC diagnostics log buffer
          (MAX_LOG = 200 shared entries), replace those calls with VCTuneDiagnostics methods.
          The VC Tune ring buffer (500 entries) is separate from the shared buffer and is
          reset on radio disconnect rather than growing unboundedly.

        Step 6: Validate compatibility with existing CAT command sets
          The VT wire format (VT{P1}{P2}{P3}{P4}{P5P5P5}{P6};) has not changed from the
          FTdx101 factory firmware. Run VCTuneAssemblyValidationReport.GenerateReport()
          after upgrade to confirm all subsystems are correctly wired before re-enabling
          the radio connection.
        """;

    private static string BuildCompatibilityNotes() =>
        """
        VC Tune Module — Compatibility Notes
        =====================================

        Operating systems:
          Windows 10 (x64) and Windows 11 (x64) — supported and tested.
          Windows on ARM64 — not supported (System.Speech P/Invoke is x86/x64 only).
          Linux / macOS — not supported (System.Speech, WinForms host, and sdrplay_api.dll
            are Windows-only; the VCTuneRecognizer will not compile on non-Windows TFMs).

        Architectures:
          x64 only. The project is published self-contained for win-x64.
          The sdrplay_api.dll dependency (co-located, not related to VC Tune but same build)
          enforces 64-bit. The VT CAT commands contain no architecture-specific code.

        CAT firmware versions:
          FTdx101D firmware v01.10 and later — VT command confirmed supported.
          FTdx101MP firmware v01.10 and later — VT command confirmed supported (MAIN + SUB).
          Earlier firmware may omit P6 from the response or return a shorter frame;
          VcTuneResponseParser returns IsValid=false for any response shorter than 11 characters
          (including semicolon), which is handled gracefully as a ReadFailure in VcTuneService.
          No other radio models are supported; RadioCapabilities.SupportsVCTuneMain() returns
          false for all non-FTdx101 models, and the control panel hides entirely.

        Voice recognizer versions:
          System.Speech (Windows built-in SAPI 5.4, shipped with Windows 10/11) — supported.
          Microsoft Speech Platform (SAPI 5.1) — not tested; the grammar-building API
          is compatible but recognition accuracy may differ.
          Web Speech API — not applicable (server-side .NET process, not browser-based).
          Alexa / cloud voice — not applicable to VC Tune (Alexa integration is a separate
          feature branch; VC Tune voice uses the in-browser SAPI grammar path only).

        UI frameworks:
          Kestrel-hosted Razor Pages (ASP.NET Core 10) with vanilla JavaScript — supported.
          Blazor — not tested; VCTuneViewModel implements INotifyPropertyChanged but Blazor
          state synchronisation would require additional wiring not included here.
          WinForms — VCTuneViewModel's INotifyPropertyChanged implementation is compatible
          with WinForms data binding; no additional work required if a WinForms panel is added.

        Breaking changes from previous VC Tune implementations:
          — P5 and P6 are no longer persisted. Any code that read these values from
            appsettings.user.json must be updated to use VCTuneSessionState via
            IVCTuneConfigurationStore.GetSessionState().
          — The VT READ response is now parsed by two distinct classes: the internal
            static VcTuneResponseParser (used by VcTuneService) and the injectable
            VCTuneResponseParser (used by VCTuneModule and any endpoint handlers).
            Callers must not substitute one for the other; they have different output types.
          — VCTuneBand and VcTuneReceiver are intentionally distinct enum types with the
            same integer values. Builder-layer code uses VCTuneBand; service-layer code
            uses VcTuneReceiver. Casting between them (VcTuneReceiver)(int)band is valid
            and is the intended crossing point inside VCTuneModule.ExecuteCommandAsync.
          — The CatRequestSemaphore singleton must be registered before IVcTuneService.
            Reordering these registrations in Program.cs will cause a DI resolution failure
            at startup.
        """;
}
