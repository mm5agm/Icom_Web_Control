using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services;

/// <summary>
/// Top-level orchestrator for the VC Tune preselector subsystem.
/// <para>
/// <see cref="VCTuneModule"/> is the single entry point that external callers
/// (Razor page models, API controllers, and the radio-initialization pipeline)
/// should use for all VC Tune operations. It coordinates all nine VC Tune
/// subsystems — service, builder, parser, state machine, recognizer, view model,
/// configuration store, diagnostics, and help provider — so that each individual
/// operation triggers all downstream updates in the correct order.
/// </para>
/// <para>
/// Coordination contract for every public operation:
/// <list type="number">
///   <item>Log the intent via <see cref="VCTuneDiagnostics"/>.</item>
///   <item>Apply an optimistic state-machine transition (SET operations only).</item>
///   <item>Dispatch to <see cref="IVcTuneService"/>.</item>
///   <item>Parse the confirmed response via <see cref="IVCTuneResponseParser"/>.</item>
///   <item>Update <see cref="IVCTuneStateMachine"/> from the parsed response.</item>
///   <item>Update <see cref="IVCTuneConfigurationStore"/> (session state and, for SUB,
///     P6-driven capability refinement).</item>
///   <item>Notify <see cref="Voice.VCTuneRecognizer"/> of the new P6 availability.</item>
///   <item>Push the updated snapshot to <see cref="VCTuneViewModel"/>.</item>
///   <item>Log state transitions, meter changes, and any errors.</item>
/// </list>
/// </para>
/// <para>
/// Register as a singleton in DI:
/// <c>services.AddSingleton&lt;VCTuneModule&gt;();</c>
/// </para>
/// </summary>
public sealed class VCTuneModule
{
    private readonly IVcTuneService _service;
    private readonly IVCTuneCommandBuilder _builder;
    private readonly IVCTuneResponseParser _parser;
    private readonly IVCTuneStateMachine _stateMachine;
    private readonly Voice.VCTuneRecognizer _recognizer;
    private readonly VCTuneViewModel _viewModel;
    private readonly IVCTuneConfigurationStore _configStore;
    private readonly VCTuneDiagnostics _diagnostics;
    private readonly VCTuneHelpProvider _helpProvider;
    private readonly ISettingsService _settings;
    private readonly RadioStateService _radioStateService;
    private readonly ILogger<VCTuneModule> _logger;

    // Cached from the most recent InitializeAsync call; used by config-store
    // calls that need the radio model key. Empty string is safe — GetCapabilities
    // returns a conservative ForModel() default in that case.
    private string _radioModel = string.Empty;

    // ══════════════════════════════════════════════════════════════════════
    // Constructor
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Initialises the module with all nine VC Tune subsystems and the settings
    /// service needed to resolve the current radio model.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any parameter is null.
    /// </exception>
    public VCTuneModule(
        IVcTuneService service,
        IVCTuneCommandBuilder builder,
        IVCTuneResponseParser parser,
        IVCTuneStateMachine stateMachine,
        Voice.VCTuneRecognizer recognizer,
        VCTuneViewModel viewModel,
        IVCTuneConfigurationStore configStore,
        VCTuneDiagnostics diagnostics,
        VCTuneHelpProvider helpProvider,
        ISettingsService settings,
        RadioStateService radioStateService,
        ILogger<VCTuneModule> logger)
    {
        _service           = service           ?? throw new ArgumentNullException(nameof(service));
        _builder           = builder           ?? throw new ArgumentNullException(nameof(builder));
        _parser            = parser            ?? throw new ArgumentNullException(nameof(parser));
        _stateMachine      = stateMachine      ?? throw new ArgumentNullException(nameof(stateMachine));
        _recognizer        = recognizer        ?? throw new ArgumentNullException(nameof(recognizer));
        _viewModel         = viewModel         ?? throw new ArgumentNullException(nameof(viewModel));
        _configStore       = configStore       ?? throw new ArgumentNullException(nameof(configStore));
        _diagnostics       = diagnostics       ?? throw new ArgumentNullException(nameof(diagnostics));
        _helpProvider      = helpProvider      ?? throw new ArgumentNullException(nameof(helpProvider));
        _settings          = settings          ?? throw new ArgumentNullException(nameof(settings));
        _radioStateService = radioStateService ?? throw new ArgumentNullException(nameof(radioStateService));
        _logger            = logger            ?? throw new ArgumentNullException(nameof(logger));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Lifecycle
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Initialises the VC Tune subsystem after a radio connection is established.
    /// <list type="number">
    ///   <item>Loads persisted user preferences and capability records from the
    ///     configuration store.</item>
    ///   <item>Probes the radio hardware via <see cref="IVcTuneService.ProbeCapabilityAsync"/>
    ///     to populate baseline P5/P6 state.</item>
    ///   <item>Applies the probed state to the state machine, configuration store,
    ///     recognizer, and view model.</item>
    ///   <item>Logs an initialisation diagnostic entry.</item>
    /// </list>
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[VCTuneModule] Initializing.");

        var appSettings = await _settings.GetSettingsAsync();
        _radioModel = appSettings.RadioModel ?? string.Empty;

        // Hardware-level CAT guard: some FTdx101 revisions do not expose VC
        // Tune over CAT (e.g. ID0682 — VT VCT 0,1,+,0; → ?;?;).
        if (!RadioCapabilities.SupportsVCTuneCat(_radioStateService.Id))
        {
            _logger.LogInformation(
                "[VCTuneModule] Hardware {Id} does not support VC Tune CAT. Init skipped.",
                _radioStateService.Id);
            _viewModel.SetCatNotSupported();
            return;
        }

        // Load persisted preferences and capability records.
        await _configStore.LoadAsync(ct);
        _logger.LogDebug("[VCTuneModule] Config store loaded for model '{Model}'.", _radioModel);

        // Probe the radio to establish initial P5/P6 state for MAIN (and SUB if fitted).
        await _service.ProbeCapabilityAsync(ct);

        // Apply initial snapshots to all subsystems.
        await ApplyReadResultAsync(
            await _service.ReadVCTuneStatusAsync(VcTuneReceiver.Main, ct),
            _stateMachine.GetLastSnapshot(VCTuneBand.Main),
            ct);

        var subCaps = _configStore.GetCapabilities(_radioModel);
        if (subCaps.SupportsVCTuneSub || subCaps.SubInstallationConfirmed)
        {
            await ApplyReadResultAsync(
                await _service.ReadVCTuneStatusAsync(VcTuneReceiver.Sub, ct),
                _stateMachine.GetLastSnapshot(VCTuneBand.Sub),
                ct);
        }

        // Bring the view model fully current (also resolves radio-model capability flags).
        await _viewModel.RefreshAsync(ct);

        _diagnostics.LogError(
            VCTuneErrorType.ReadFailure,
            // Not a real error — reusing LogError with a benign sentinel to record init.
            // A dedicated LogEvent would be cleaner but is outside Message 50's scope.
            string.Empty,
            band: null);

        // Use a debug log instead of a diagnostics entry so we don't pollute the
        // user-visible history with an internal lifecycle marker.
        _logger.LogInformation("[VCTuneModule] Initialization complete. Model={Model}.", _radioModel);
    }

    /// <summary>
    /// Shuts down the VC Tune subsystem when the radio disconnects.
    /// <list type="number">
    ///   <item>Resets in-memory session state in the configuration store.</item>
    ///   <item>Clears the diagnostics history buffer.</item>
    ///   <item>Resets both receivers in the recognizer to <see cref="VcTuneAvailability.NotInstalled"/>.</item>
    ///   <item>Refreshes the view model so UI controls reflect the disconnected state.</item>
    /// </list>
    /// </summary>
    /// <param name="ct">Cancellation token (used for any async persistence on shutdown).</param>
    public Task ShutdownAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[VCTuneModule] Shutting down.");

        _configStore.ResetSessionState();
        _diagnostics.ResetHistory();

        _recognizer.UpdateCapability(VCTuneBand.Main, VcTuneAvailability.NotInstalled);
        _recognizer.UpdateCapability(VCTuneBand.Sub,  VcTuneAvailability.NotInstalled);

        _viewModel.Refresh();

        _logger.LogInformation("[VCTuneModule] Shutdown complete.");
        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Status refresh
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the current VC Tune status for the specified receiver from the radio
    /// and propagates the result through all subsystems.
    /// </summary>
    /// <param name="band">Which receiver to refresh (MAIN or SUB).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="VCTuneCommandResult"/> whose <see cref="VCTuneCommandResult.UpdatedSnapshot"/>
    /// reflects the post-READ state, or a failure result when no valid response was received.
    /// </returns>
    public async Task<VCTuneCommandResult> RefreshStatusAsync(
        VCTuneBand band, CancellationToken ct = default)
    {
        if (!RadioCapabilities.SupportsVCTuneCat(_radioStateService.Id))
            return VCTuneCommandResult.Fail("CatNotSupported",
                "This radio hardware revision does not support VC Tune over CAT. " +
                "VC Tune can only be operated from the front panel.");

        var receiver = (VcTuneReceiver)(int)band;
        var readCommand = _builder.BuildReadStatus(
            band,
            subInstalled: _configStore.GetCapabilities(_radioModel).SubInstallationConfirmed);

        _diagnostics.LogReadCommand(readCommand);

        var snapshotBefore = _stateMachine.GetLastSnapshot(band);
        var readResult = await _service.ReadVCTuneStatusAsync(receiver, ct);

        await ApplyReadResultAsync(readResult, snapshotBefore, ct);

        var snapshot = _stateMachine.GetLastSnapshot(band);
        return readResult.IsValid
            ? VCTuneCommandResult.Ok(snapshot)
            : new VCTuneCommandResult(false, "ReadFailure",
                $"VC Tune READ for {band} returned no valid response.", snapshot);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Command execution
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes a fully constructed <see cref="VCTuneCommand"/> through the full
    /// VC Tune pipeline and returns a unified <see cref="VCTuneCommandResult"/>.
    /// <para>
    /// For SET commands (On / Off / Default / Step / Center):
    /// <list type="number">
    ///   <item>Logs the command to diagnostics.</item>
    ///   <item>Applies an optimistic state-machine transition.</item>
    ///   <item>Records the command in the session state.</item>
    ///   <item>Dispatches to the appropriate <see cref="IVcTuneService"/> method.</item>
    ///   <item>Applies the confirmation READ result through all subsystems.</item>
    /// </list>
    /// </para>
    /// <para>
    /// For READ commands: delegates directly to <see cref="RefreshStatusAsync"/>.
    /// </para>
    /// </summary>
    /// <param name="command">
    /// The fully built command, as produced by <see cref="IVCTuneCommandBuilder"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="VCTuneCommandResult"/> reflecting the post-command radio state.
    /// </returns>
    public async Task<VCTuneCommandResult> ExecuteCommandAsync(
        VCTuneCommand command, CancellationToken ct = default)
    {
        if (command.Type == VCTuneCommandType.Read)
            return await RefreshStatusAsync(command.Band, ct);

        var receiver      = (VcTuneReceiver)(int)command.Band;
        var snapshotBefore = _stateMachine.GetLastSnapshot(command.Band);

        _diagnostics.LogSetCommand(command);

        // Optimistic state transition — surfaces guard violations before the CAT round-trip.
        try
        {
            _stateMachine.NotifyCommand(command.Band, command.Type);
        }
        catch (InvalidOperationException ex)
        {
            _diagnostics.LogError(VCTuneErrorType.InvalidParameters, ex.Message, command.Band);
            _logger.LogWarning(
                "[VCTuneModule] Command blocked by state-machine guard: {Message}", ex.Message);
            return VCTuneCommandResult.Fail("InvalidParameters", ex.Message);
        }

        _configStore.RecordCommand(command.Band, command.Type);

        // Dispatch to the appropriate service method.
        VcTuneSetResult setResult = command.Type switch
        {
            VCTuneCommandType.On =>
                await _service.SetVCTuneOnAsync(receiver, ct),

            VCTuneCommandType.Off =>
                await _service.SetVCTuneOffAsync(receiver, ct),

            VCTuneCommandType.Default =>
                await _service.SetVCTuneDefaultAsync(receiver, ct),

            VCTuneCommandType.Step =>
                await _service.SetVCTuneStepAsync(
                    receiver,
                    command.Direction == VCTuneDirection.Minus ? '-' : '+',
                    command.StepAmount ?? 0,
                    ct),

            VCTuneCommandType.Center =>
                await _service.SetVCTuneCenterAsync(receiver, ct),

            _ =>
                VcTuneSetResult.Rejected("InvalidParameters",
                    $"Unhandled command type '{command.Type}'."),
        };

        // Propagate the confirmation READ result through all subsystems.
        if (setResult.ConfirmedStatus is { } confirmed)
        {
            await ApplyReadResultAsync(confirmed, snapshotBefore, ct);
        }
        else
        {
            // No confirmed status — still refresh view model from current state machine.
            _viewModel.Refresh();

            if (!setResult.Success)
            {
                var errorType = MapErrorCategory(setResult.ErrorCategory);
                _diagnostics.LogError(errorType, setResult.Message, command.Band);

                if (setResult.CommandRejected)
                    _diagnostics.LogFallbackActivation(command.Band, errorType);
            }
        }

        var finalSnapshot = _stateMachine.GetLastSnapshot(command.Band);
        return setResult.Success
            ? VCTuneCommandResult.Ok(finalSnapshot)
            : VCTuneCommandResult.Fail(setResult.ErrorCategory, setResult.Message);
    }

    // ══════════════════════════════════════════════════════════════════════
    // State query
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the most recent <see cref="VCTuneStateSnapshot"/> for the given receiver.
    /// The snapshot reflects the last successful VT READ or post-SET confirmation READ.
    /// This is a synchronous, non-blocking call.
    /// </summary>
    /// <param name="band">Which receiver to query.</param>
    public VCTuneStateSnapshot GetState(VCTuneBand band) =>
        _stateMachine.GetLastSnapshot(band);

    // ══════════════════════════════════════════════════════════════════════
    // Help content
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns structured help content for the named section. Section names are
    /// matched case-insensitively. Returns an empty list for unrecognised names.
    /// <para>
    /// Recognised section names:
    /// <c>"overview"</c>, <c>"installation"</c>, <c>"main-vs-sub"</c>,
    /// <c>"meter"</c>, <c>"availability"</c>, <c>"commands"</c>,
    /// <c>"voice"</c>, <c>"troubleshooting"</c>.
    /// </para>
    /// </summary>
    /// <param name="sectionName">Case-insensitive section identifier.</param>
    /// <returns>
    /// One or more <see cref="VCTuneHelpSection"/> items representing the requested
    /// content, or an empty list when <paramref name="sectionName"/> is not recognised.
    /// </returns>
    public IReadOnlyList<VCTuneHelpSection> GetHelpSection(string sectionName) =>
        sectionName.ToLowerInvariant() switch
        {
            "overview"       => [_helpProvider.GetOverview()],
            "installation"   => [_helpProvider.GetInstallationNotes()],
            "main-vs-sub"    => [_helpProvider.GetMainVsSubBehaviour()],
            "meter"          => [_helpProvider.GetMeterExplanation()],
            "availability"   => [_helpProvider.GetAvailabilityExplanation()],
            "commands"       => _helpProvider.GetCommandsHelp(),
            "voice"          => VoiceExamplesAsHelpSections(),
            "troubleshooting"=> _helpProvider.GetTroubleshootingGuide(),
            _                => [],
        };

    // ══════════════════════════════════════════════════════════════════════
    // Private helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies all downstream updates that follow a completed VT READ — state machine,
    /// configuration store, recognizer, diagnostics, and view model. Safe to call on both
    /// successful and unsuccessful <see cref="VcTuneReadResult"/> instances; invalid
    /// results are logged and return early without touching any state.
    /// </summary>
    private async Task ApplyReadResultAsync(
        VcTuneReadResult readResult,
        VCTuneStateSnapshot snapshotBefore,
        CancellationToken ct)
    {
        var band = (VCTuneBand)(int)readResult.Receiver;

        if (!readResult.IsValid)
        {
            _diagnostics.LogError(
                VCTuneErrorType.ReadFailure,
                $"VT READ for {band} returned no valid response.",
                band);
            return;
        }

        // 1. Log the raw wire response.
        if (!string.IsNullOrEmpty(readResult.RawResponse))
            _diagnostics.LogRawResponse(readResult.RawResponse);

        // 2. Update the state machine from the parsed response.
        if (_parser.TryParse(readResult.RawResponse, out var vcResponse) && vcResponse is not null)
            _stateMachine.UpdateState(vcResponse);

        // 3. Update configuration-store session state (never touches disk for P5/P6).
        _configStore.RecordReadResult(readResult.Receiver, readResult);

        // 4. For SUB, refine the persisted capability record from the live P6 value.
        if (readResult.Receiver == VcTuneReceiver.Sub && !string.IsNullOrEmpty(_radioModel))
            await _configStore.RefineSubCapabilityFromP6Async(
                _radioModel, (int)readResult.Availability, ct);

        // 5. Update the recognizer's P6 availability cache.
        _recognizer.UpdateCapability(band, readResult.Availability);

        // 6. Log P5/P6 changes (no-ops when values are unchanged).
        _diagnostics.LogMeterUpdate(band, snapshotBefore.Meter, readResult.Meter);
        _diagnostics.LogAvailabilityUpdate(
            band, snapshotBefore.Availability, (int)readResult.Availability);

        // 7. Log any state transition.
        var snapshotAfter = _stateMachine.GetLastSnapshot(band);
        _diagnostics.LogStateTransition(snapshotBefore, snapshotAfter);

        // 8. Push the new snapshot to the view model.
        _viewModel.UpdateFromSnapshot(snapshotAfter);
    }

    // Maps a VcTuneSetResult.ErrorCategory string to the matching VCTuneErrorType.
    private static VCTuneErrorType MapErrorCategory(string category) => category switch
    {
        "CommandRejected"        => VCTuneErrorType.CommandRejected,
        "ReadFailure"            => VCTuneErrorType.ReadFailure,
        "Timeout"                => VCTuneErrorType.Timeout,
        "InvalidParameters"      => VCTuneErrorType.InvalidParameters,
        "UnexpectedResponse"     => VCTuneErrorType.UnexpectedResponse,
        "UnavailableFrequency"   => VCTuneErrorType.UnavailableFrequency,
        "NotInstalled"           => VCTuneErrorType.NotInstalled,
        _                        => VCTuneErrorType.CommandRejected,
    };

    // Converts the voice-example list to VCTuneHelpSection format for the unified API.
    private IReadOnlyList<VCTuneHelpSection> VoiceExamplesAsHelpSections() =>
        _helpProvider.GetVoiceExamples()
            .Select(e => new VCTuneHelpSection(e.Phrase, e.Description))
            .ToArray();
}
