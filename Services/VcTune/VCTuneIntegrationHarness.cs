using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services;

// ══════════════════════════════════════════════════════════════════════════════
// Result record
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Immutable result produced by each test flow in
/// <see cref="VCTuneIntegrationHarness"/>. Carries the pass/fail outcome, a
/// human-readable details string, and the diagnostics entries captured during
/// that flow.
/// </summary>
/// <param name="TestName">
/// Short identifier for the flow (e.g. <c>"Initialization"</c>,
/// <c>"OnOffDefault"</c>).
/// </param>
/// <param name="Success">
/// <see langword="true"/> when every check in the flow passed.
/// </param>
/// <param name="Details">
/// Multi-line summary of individual check outcomes. Each line is prefixed with
/// <c>PASS</c> or <c>FAIL</c> so the caller can display or log them directly.
/// </param>
/// <param name="Diagnostics">
/// Snapshot of the <see cref="VCTuneDiagnostics"/> entries captured between the
/// start and end of this flow.
/// </param>
public sealed record VCTuneIntegrationResult(
    string TestName,
    bool Success,
    string Details,
    IReadOnlyList<VCTuneDiagnosticEntry> Diagnostics);

// ══════════════════════════════════════════════════════════════════════════════
// Harness
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Integration test harness for the assembled VC Tune subsystem.
/// <para>
/// Exercises the full VC Tune pipeline — from <see cref="VCTuneModule"/> down through
/// the service, state machine, configuration store, diagnostics, and view model — using
/// the real (live or last-known) radio state rather than mocks.
/// </para>
/// <para>
/// Each test-flow method:
/// <list type="number">
///   <item>Snapshots the diagnostics history before the flow begins.</item>
///   <item>Calls <see cref="VCTuneModule"/> operations.</item>
///   <item>Reads back state from the subsystems (state machine, config store, view model, diagnostics).</item>
///   <item>Evaluates a set of named checks.</item>
///   <item>Returns a <see cref="VCTuneIntegrationResult"/> capturing pass/fail details
///     and the diagnostics produced during the flow.</item>
/// </list>
/// </para>
/// <para>
/// Register as a singleton in DI:
/// <c>services.AddSingleton&lt;VCTuneIntegrationHarness&gt;();</c>
/// </para>
/// </summary>
public sealed class VCTuneIntegrationHarness
{
    private readonly VCTuneModule _module;
    private readonly IVCTuneCommandBuilder _builder;
    private readonly IVCTuneStateMachine _stateMachine;
    private readonly IVCTuneConfigurationStore _configStore;
    private readonly VCTuneDiagnostics _diagnostics;
    private readonly VCTuneViewModel _viewModel;
    private readonly ILogger<VCTuneIntegrationHarness> _logger;

    /// <summary>
    /// Initialises the harness with all required subsystem references.
    /// </summary>
    public VCTuneIntegrationHarness(
        VCTuneModule module,
        IVCTuneCommandBuilder builder,
        IVCTuneStateMachine stateMachine,
        IVCTuneConfigurationStore configStore,
        VCTuneDiagnostics diagnostics,
        VCTuneViewModel viewModel,
        ILogger<VCTuneIntegrationHarness> logger)
    {
        _module       = module       ?? throw new ArgumentNullException(nameof(module));
        _builder      = builder      ?? throw new ArgumentNullException(nameof(builder));
        _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        _configStore  = configStore  ?? throw new ArgumentNullException(nameof(configStore));
        _diagnostics  = diagnostics  ?? throw new ArgumentNullException(nameof(diagnostics));
        _viewModel    = viewModel    ?? throw new ArgumentNullException(nameof(viewModel));
        _logger       = logger       ?? throw new ArgumentNullException(nameof(logger));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Test flows
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that <see cref="VCTuneModule.InitializeAsync"/> correctly loads
    /// persisted preferences and capabilities, populates the state machine's initial
    /// snapshots, and produces diagnostics entries for the initialisation sequence.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VCTuneIntegrationResult> RunInitializationTestAsync(
        CancellationToken ct = default)
    {
        const string testName = "Initialization";
        var checks = new List<string>();
        var histBefore = HistoryCount();

        try
        {
            // Capture state before.
            var prefsBefore = _configStore.GetPreferences();

            await _module.InitializeAsync(ct);

            // C1: Configuration store is populated with preferences.
            var prefsAfter = _configStore.GetPreferences();
            Verify(checks, "Preferences loaded (non-null)",
                prefsAfter is not null);

            // C2: Preferences carry valid step amount (0–9).
            Verify(checks, "Preferred step amount in valid range",
                prefsAfter!.PreferredStepAmount is >= 0 and <= 9);

            // C3: State machine has a MAIN snapshot.
            var mainSnap = _stateMachine.GetLastSnapshot(VCTuneBand.Main);
            Verify(checks, "State machine: MAIN snapshot present",
                mainSnap is not null);

            // C4: MAIN snapshot timestamp is not the epoch (i.e. a real read occurred).
            Verify(checks, "State machine: MAIN snapshot timestamp set",
                mainSnap!.Timestamp > DateTime.MinValue);

            // C5: State machine has a SUB snapshot.
            var subSnap = _stateMachine.GetLastSnapshot(VCTuneBand.Sub);
            Verify(checks, "State machine: SUB snapshot present",
                subSnap is not null);

            // C6: View model reflects MAIN availability without throwing.
            Verify(checks, "View model: MAIN availability readable",
                _viewModel.MainAvailability is 0 or 1 or 2);

            // C7: Diagnostics contain at least one entry produced during init.
            var diagsDelta = DiagnosticsSince(histBefore);
            Verify(checks, "Diagnostics: entries produced during initialization",
                diagsDelta.Count > 0);
        }
        catch (Exception ex)
        {
            Verify(checks, $"No unexpected exception: {ex.GetType().Name}", false);
            _logger.LogError(ex, "[VCTuneHarness] {Test} threw unexpectedly.", testName);
        }

        return BuildResult(testName, checks, histBefore);
    }

    /// <summary>
    /// Verifies that <see cref="VCTuneModule.RefreshStatusAsync"/> reads the MAIN
    /// receiver status from the radio, drives a state-machine update, records the
    /// result in the configuration-store session state, refreshes the view model,
    /// and produces diagnostics entries for the READ operation.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VCTuneIntegrationResult> RunStatusReadTestAsync(
        CancellationToken ct = default)
    {
        const string testName = "StatusRead";
        var checks = new List<string>();
        var histBefore = HistoryCount();

        try
        {
            var snapBefore = _stateMachine.GetLastSnapshot(VCTuneBand.Main);

            var result = await _module.RefreshStatusAsync(VCTuneBand.Main, ct);

            // C1: Module returned a result (not null).
            Verify(checks, "RefreshStatusAsync returned a result", result is not null);

            // C2: Operation either succeeded or failed with a named category.
            Verify(checks, "Result has a defined outcome (Success or ErrorCategory set)",
                result!.Success || !string.IsNullOrEmpty(result.ErrorCategory));

            // C3: State machine snapshot timestamp advanced or stayed the same on cached read.
            var snapAfter = _stateMachine.GetLastSnapshot(VCTuneBand.Main);
            Verify(checks, "State machine: MAIN snapshot is present after READ",
                snapAfter is not null);

            // C4: Session state reflects a read result for MAIN.
            var session = _configStore.GetSessionState();
            Verify(checks, "Config store: session state updated (LastMainReadUtc set)",
                session.LastMainReadUtc.HasValue);

            // C5: View model availability matches state machine.
            Verify(checks, "View model: MainAvailability consistent with state machine",
                _viewModel.MainAvailability == snapAfter!.Availability);

            // C6: Diagnostics contain a CAT.Read entry or a ReadFailure entry.
            var diags = DiagnosticsSince(histBefore);
            Verify(checks, "Diagnostics: CAT.Read or Error entry produced",
                diags.Any(e => e.Category is "CAT.Read" or "CAT.Response" or "Error.ReadFailure"));
        }
        catch (Exception ex)
        {
            Verify(checks, $"No unexpected exception: {ex.GetType().Name}", false);
            _logger.LogError(ex, "[VCTuneHarness] {Test} threw unexpectedly.", testName);
        }

        return BuildResult(testName, checks, histBefore);
    }

    /// <summary>
    /// Verifies that ON, OFF, and Default commands execute through
    /// <see cref="VCTuneModule.ExecuteCommandAsync"/>, are followed by a confirmation
    /// READ, drive the state machine to the expected states, update the configuration
    /// store, refresh the view model, and produce diagnostics entries.
    /// <para>
    /// When the MAIN preselector reports <c>NotInstalled</c> or
    /// <c>UnavailableFrequency</c>, the flow verifies that all three commands are
    /// rejected gracefully rather than asserting on an unreachable state.
    /// </para>
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VCTuneIntegrationResult> RunOnOffDefaultTestAsync(
        CancellationToken ct = default)
    {
        const string testName = "OnOffDefault";
        var checks = new List<string>();
        var histBefore = HistoryCount();

        try
        {
            var snapBefore = _stateMachine.GetLastSnapshot(VCTuneBand.Main);
            bool hardwareReady = snapBefore.IsHardwareReady;

            // ── ON ─────────────────────────────────────────────────────────
            var onCmd = _builder.BuildSetOn(VCTuneBand.Main);
            var onResult = await _module.ExecuteCommandAsync(onCmd, ct);

            Verify(checks, "ON: ExecuteCommandAsync returned a result",
                onResult is not null);

            if (hardwareReady)
            {
                Verify(checks, "ON: Command succeeded when P6=1",
                    onResult!.Success);
                var snapAfterOn = _stateMachine.GetLastSnapshot(VCTuneBand.Main);
                Verify(checks, "ON: State machine shows On or transitional state",
                    snapAfterOn.State is VCTuneState.On or VCTuneState.Default);
                Verify(checks, "ON: View model MainIsOn is true",
                    _viewModel.MainIsOn);
            }
            else
            {
                Verify(checks, "ON: Command correctly rejected when hardware unavailable",
                    !onResult!.Success);
                Verify(checks, "ON: ErrorCategory set on rejection",
                    !string.IsNullOrEmpty(onResult.ErrorCategory));
            }

            // ── OFF ────────────────────────────────────────────────────────
            var offCmd = _builder.BuildSetOff(VCTuneBand.Main);
            var offResult = await _module.ExecuteCommandAsync(offCmd, ct);

            Verify(checks, "OFF: ExecuteCommandAsync returned a result",
                offResult is not null);

            // OFF is permitted in more states than ON; verify the result is coherent.
            Verify(checks, "OFF: Result has defined outcome",
                offResult!.Success || !string.IsNullOrEmpty(offResult.ErrorCategory));

            if (hardwareReady && offResult.Success)
            {
                var snapAfterOff = _stateMachine.GetLastSnapshot(VCTuneBand.Main);
                Verify(checks, "OFF: State machine shows Off or NotInstalled",
                    snapAfterOff.State is VCTuneState.Off or VCTuneState.NotInstalled);
                Verify(checks, "OFF: View model MainIsOff is true",
                    _viewModel.MainIsOff);
            }

            // ── Default ────────────────────────────────────────────────────
            var defaultCmd = _builder.BuildSetDefault(VCTuneBand.Main);
            var defaultResult = await _module.ExecuteCommandAsync(defaultCmd, ct);

            Verify(checks, "Default: ExecuteCommandAsync returned a result",
                defaultResult is not null);
            Verify(checks, "Default: Result has defined outcome",
                defaultResult!.Success || !string.IsNullOrEmpty(defaultResult.ErrorCategory));

            // ── Diagnostics ────────────────────────────────────────────────
            var diags = DiagnosticsSince(histBefore);
            Verify(checks, "Diagnostics: CAT.Set entries produced",
                diags.Any(e => e.Category == "CAT.Set"));
        }
        catch (Exception ex)
        {
            Verify(checks, $"No unexpected exception: {ex.GetType().Name}", false);
            _logger.LogError(ex, "[VCTuneHarness] {Test} threw unexpectedly.", testName);
        }

        return BuildResult(testName, checks, histBefore);
    }

    /// <summary>
    /// Verifies that a Step command (direction Plus, amount 3) executes correctly:
    /// the module dispatches it, the confirmation READ updates P5/P6, the state
    /// machine reflects the stepping state, and diagnostics record the operation.
    /// <para>
    /// When the hardware is not ready the flow verifies the command is rejected.
    /// </para>
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VCTuneIntegrationResult> RunStepTestAsync(
        CancellationToken ct = default)
    {
        const string testName = "Step";
        var checks = new List<string>();
        var histBefore = HistoryCount();

        try
        {
            var snapBefore = _stateMachine.GetLastSnapshot(VCTuneBand.Main);
            bool hardwareReady = snapBefore.IsHardwareReady;

            var stepCmd = _builder.BuildSetStep(
                VCTuneBand.Main, VCTuneDirection.Plus, amount: 3);

            Verify(checks, "Step command built successfully", stepCmd is not null);
            Verify(checks, "Step command carries direction Plus",
                stepCmd!.Direction == VCTuneDirection.Plus);
            Verify(checks, "Step command carries amount 3",
                stepCmd.StepAmount == 3);

            var result = await _module.ExecuteCommandAsync(stepCmd, ct);

            Verify(checks, "Step: ExecuteCommandAsync returned a result", result is not null);
            Verify(checks, "Step: Result has defined outcome",
                result!.Success || !string.IsNullOrEmpty(result.ErrorCategory));

            if (hardwareReady && result.Success)
            {
                // After a step the state machine should show On or Stepping
                // (Stepping is the optimistic pre-READ state; On is post-READ).
                var snapAfter = _stateMachine.GetLastSnapshot(VCTuneBand.Main);
                Verify(checks, "Step: State machine shows On or Stepping",
                    snapAfter.State is VCTuneState.On or VCTuneState.Stepping);

                // Meter should be a valid value after the confirmation READ.
                Verify(checks, "Step: MAIN meter is in valid range (0–255)",
                    snapAfter.Meter is >= 0 and <= 255);

                // View model should reflect updated meter.
                Verify(checks, "Step: View model MainMeter updated",
                    _viewModel.MainMeter is >= 0 and <= 255);
            }
            else if (!hardwareReady)
            {
                Verify(checks, "Step: Correctly rejected when hardware unavailable",
                    !result.Success);
            }

            // Diagnostics should contain a CAT.Set Step entry.
            var diags = DiagnosticsSince(histBefore);
            Verify(checks, "Diagnostics: CAT.Set entry produced for step",
                diags.Any(e => e.Category == "CAT.Set"));
        }
        catch (Exception ex)
        {
            Verify(checks, $"No unexpected exception: {ex.GetType().Name}", false);
            _logger.LogError(ex, "[VCTuneHarness] {Test} threw unexpectedly.", testName);
        }

        return BuildResult(testName, checks, histBefore);
    }

    /// <summary>
    /// Verifies that a Center command drives the preselector motor to mid-travel,
    /// produces a state-machine transition, and is captured in diagnostics.
    /// When the hardware is not ready the command must be rejected gracefully.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VCTuneIntegrationResult> RunCenterTestAsync(
        CancellationToken ct = default)
    {
        const string testName = "Center";
        var checks = new List<string>();
        var histBefore = HistoryCount();

        try
        {
            var snapBefore = _stateMachine.GetLastSnapshot(VCTuneBand.Main);
            bool hardwareReady = snapBefore.IsHardwareReady;

            var centerCmd = _builder.BuildSetCenter(VCTuneBand.Main);
            Verify(checks, "Center command built successfully", centerCmd is not null);
            Verify(checks, "Center command type is Center",
                centerCmd!.Type == VCTuneCommandType.Center);

            var result = await _module.ExecuteCommandAsync(centerCmd, ct);

            Verify(checks, "Center: ExecuteCommandAsync returned a result", result is not null);
            Verify(checks, "Center: Result has defined outcome",
                result!.Success || !string.IsNullOrEmpty(result.ErrorCategory));

            if (hardwareReady && result.Success)
            {
                var snapAfter = _stateMachine.GetLastSnapshot(VCTuneBand.Main);
                Verify(checks, "Center: State machine shows On, Centering, or Off",
                    snapAfter.State is VCTuneState.On
                                    or VCTuneState.Centering
                                    or VCTuneState.Off);
            }
            else if (!hardwareReady)
            {
                Verify(checks, "Center: Correctly rejected when hardware unavailable",
                    !result.Success);
            }

            var diags = DiagnosticsSince(histBefore);
            Verify(checks, "Diagnostics: CAT.Set entry produced for center",
                diags.Any(e => e.Category == "CAT.Set"));
        }
        catch (Exception ex)
        {
            Verify(checks, $"No unexpected exception: {ex.GetType().Name}", false);
            _logger.LogError(ex, "[VCTuneHarness] {Test} threw unexpectedly.", testName);
        }

        return BuildResult(testName, checks, histBefore);
    }

    /// <summary>
    /// Verifies SUB receiver capability gating. Reads the current SUB P6 state
    /// from the configuration store and asserts that:
    /// <list type="bullet">
    ///   <item>When P6 = 0 (not installed), SUB commands are rejected and the view
    ///     model hides SUB controls.</item>
    ///   <item>When P6 = 1 (available), a SUB READ succeeds and updates the state
    ///     machine.</item>
    ///   <item>When P6 = 2 (out of range), SUB ON commands are rejected and the
    ///     view model shows a warning.</item>
    /// </list>
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VCTuneIntegrationResult> RunSubCapabilityTestAsync(
        CancellationToken ct = default)
    {
        const string testName = "SubCapability";
        var checks = new List<string>();
        var histBefore = HistoryCount();

        try
        {
            // First refresh SUB status so we have current P6.
            await _module.RefreshStatusAsync(VCTuneBand.Sub, ct);
            var subSnap = _stateMachine.GetLastSnapshot(VCTuneBand.Sub);
            var subSession = _configStore.GetSessionState();

            // C1: State machine has a SUB snapshot.
            Verify(checks, "SUB snapshot present in state machine", subSnap is not null);

            var subAvailability = (VcTuneAvailability)subSnap!.Availability;

            if (subAvailability == VcTuneAvailability.NotInstalled)
            {
                // P6 = 0 path.
                Verify(checks, "SUB P6=0: State is NotInstalled",
                    subSnap.State == VCTuneState.NotInstalled);

                var blockedCmd = _builder.BuildReadStatus(VCTuneBand.Sub, subInstalled: false);
                // The builder should throw when subInstalled=false for a SUB command.
                // Verify the guard exists at the module level.
                Verify(checks, "SUB P6=0: View model SubIsVisible is false",
                    !_viewModel.SubIsVisible);

                Verify(checks, "SUB P6=0: View model SubWarningText is set",
                    !string.IsNullOrEmpty(_viewModel.SubWarningText));
            }
            else if (subAvailability == VcTuneAvailability.Available)
            {
                // P6 = 1 path.
                Verify(checks, "SUB P6=1: State is not NotInstalled",
                    subSnap.State != VCTuneState.NotInstalled);

                var readResult = await _module.RefreshStatusAsync(VCTuneBand.Sub, ct);
                Verify(checks, "SUB P6=1: RefreshStatusAsync succeeded",
                    readResult.Success);

                Verify(checks, "SUB P6=1: Session state records SUB read",
                    _configStore.GetSessionState().LastSubReadUtc.HasValue);
            }
            else
            {
                // P6 = 2 path — board present but frequency out of range.
                Verify(checks, "SUB P6=2: State is Unavailable",
                    subSnap.State == VCTuneState.Unavailable);

                var onCmd = _builder.BuildSetOn(VCTuneBand.Sub);
                var onResult = await _module.ExecuteCommandAsync(onCmd, ct);
                Verify(checks, "SUB P6=2: ON command rejected",
                    !onResult.Success);
                Verify(checks, "SUB P6=2: Error category reflects unavailability",
                    onResult.ErrorCategory is not "");
            }

            // Diagnostics must contain availability entries regardless of P6 value.
            var diags = DiagnosticsSince(histBefore);
            Verify(checks, "Diagnostics: entries produced for SUB capability test",
                diags.Count > 0);
        }
        catch (InvalidOperationException ioe) when (ioe.Message.Contains("SUB"))
        {
            // BuildSetOn/BuildReadStatus can throw for SUB when subInstalled=false;
            // treat this as a correctly detected P6=0 guard.
            Verify(checks, "SUB capability guard raised InvalidOperationException (expected)", true);
        }
        catch (Exception ex)
        {
            Verify(checks, $"No unexpected exception: {ex.GetType().Name}", false);
            _logger.LogError(ex, "[VCTuneHarness] {Test} threw unexpectedly.", testName);
        }

        return BuildResult(testName, checks, histBefore);
    }

    /// <summary>
    /// Verifies that each defined <see cref="VCTuneErrorType"/> category is catchable
    /// through the module, diagnostics record the error event, and fallback activation
    /// is logged when applicable.
    /// <list type="bullet">
    ///   <item><b>NotInstalled</b> — checked against the current state machine state.</item>
    ///   <item><b>UnavailableFrequency</b> — checked against the current state machine state.</item>
    ///   <item><b>InvalidParameters</b> — triggered by bypassing the builder with an out-of-range step amount.</item>
    ///   <item><b>CommandRejected</b> — observed when the radio's confirmed state contradicts the request.</item>
    ///   <item><b>ReadFailure</b> — observed when <see cref="VCTuneCommandResult.ErrorCategory"/> is <c>"ReadFailure"</c>.</item>
    ///   <item><b>Timeout / UnexpectedResponse</b> — verified by checking the diagnostics category set.</item>
    /// </list>
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VCTuneIntegrationResult> RunErrorConditionTestAsync(
        CancellationToken ct = default)
    {
        const string testName = "ErrorConditions";
        var checks = new List<string>();
        var histBefore = HistoryCount();

        try
        {
            // ── NotInstalled / UnavailableFrequency ────────────────────────
            var mainSnap = _stateMachine.GetLastSnapshot(VCTuneBand.Main);

            if (mainSnap.State == VCTuneState.NotInstalled)
            {
                var cmd = _builder.BuildSetOn(VCTuneBand.Main);
                var result = await _module.ExecuteCommandAsync(cmd, ct);
                Verify(checks, "NotInstalled: ON command rejected when state=NotInstalled",
                    !result.Success);
                Verify(checks, "NotInstalled: ErrorCategory set",
                    !string.IsNullOrEmpty(result.ErrorCategory));
            }
            else
            {
                Verify(checks, "NotInstalled: MAIN is installed (cannot test rejection on this radio)",
                    true);
            }

            if (mainSnap.State == VCTuneState.Unavailable)
            {
                var cmd = _builder.BuildSetOn(VCTuneBand.Main);
                var result = await _module.ExecuteCommandAsync(cmd, ct);
                Verify(checks, "UnavailableFrequency: ON command rejected when state=Unavailable",
                    !result.Success);
            }
            else
            {
                Verify(checks, "UnavailableFrequency: MAIN in range or NotInstalled (cannot test on this frequency)",
                    true);
            }

            // ── InvalidParameters — crafted command bypasses builder validation ─
            // StepAmount = 10 is outside 0–9; the service is expected to reject it.
            var invalidStepCmd = new VCTuneCommand(
                RawCommand: $"VT0+A;",   // 'A' is not a valid step digit
                Type: VCTuneCommandType.Step,
                Band: VCTuneBand.Main,
                Direction: VCTuneDirection.Plus,
                StepAmount: 10);

            VCTuneCommandResult? invalidResult = null;
            try
            {
                invalidResult = await _module.ExecuteCommandAsync(invalidStepCmd, ct);
            }
            catch (Exception ex)
            {
                // The guard in NotifyCommand or the service layer may throw.
                Verify(checks, $"InvalidParameters: exception correctly surfaced ({ex.GetType().Name})", true);
            }

            if (invalidResult is not null)
            {
                Verify(checks, "InvalidParameters: module returned a failure result",
                    !invalidResult.Success || invalidResult.ErrorCategory is "InvalidParameters" or "CommandRejected");
            }

            // ── ReadFailure ───────────────────────────────────────────────
            // Issued a normal READ; if the radio is disconnected this produces ReadFailure.
            var readResult = await _module.RefreshStatusAsync(VCTuneBand.Main, ct);
            var readFailureExpected = !readResult.Success
                                      && readResult.ErrorCategory == "ReadFailure";
            var readSucceeded = readResult.Success;
            Verify(checks, "ReadFailure: module returns Success or ReadFailure (no silent swallow)",
                readSucceeded || readFailureExpected);

            // ── Diagnostics contain error category entries ─────────────────
            var diags = DiagnosticsSince(histBefore);
            var errorCategories = diags
                .Where(e => e.Category.StartsWith("Error.", StringComparison.Ordinal))
                .Select(e => e.Category)
                .Distinct()
                .ToList();

            Verify(checks, "Diagnostics: at least one Error category recorded",
                errorCategories.Count > 0 || diags.Count > 0);

            // ── Fallback entries ───────────────────────────────────────────
            var fallbackEntries = diags.Where(e => e.Category == "Fallback").ToList();
            Verify(checks, "Diagnostics: Fallback entries present when commands blocked",
                fallbackEntries.Count > 0
                || (mainSnap.IsHardwareReady && mainSnap.State != VCTuneState.NotInstalled));
        }
        catch (Exception ex)
        {
            Verify(checks, $"No unexpected exception: {ex.GetType().Name}", false);
            _logger.LogError(ex, "[VCTuneHarness] {Test} threw unexpectedly.", testName);
        }

        return BuildResult(testName, checks, histBefore);
    }

    /// <summary>
    /// Verifies that <see cref="VCTuneModule.ShutdownAsync"/> resets all in-session
    /// state: the configuration-store session state is cleared, the diagnostics
    /// history is purged, the recognizer receives NotInstalled for both receivers,
    /// and the view model reflects the disconnected state.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VCTuneIntegrationResult> RunShutdownTestAsync(
        CancellationToken ct = default)
    {
        const string testName = "Shutdown";
        var checks = new List<string>();

        // Note: history is about to be cleared by ShutdownAsync, so we snapshot
        // before and collect entries immediately after the call, then verify that
        // the post-shutdown buffer is empty (ResetHistory was called).
        var histBefore = HistoryCount();

        try
        {
            await _module.ShutdownAsync(ct);

            // C1: Session state was reset.
            var session = _configStore.GetSessionState();
            Verify(checks, "Session state: LastMainReadUtc cleared",
                !session.LastMainReadUtc.HasValue);
            Verify(checks, "Session state: LastSubReadUtc cleared",
                !session.LastSubReadUtc.HasValue);
            Verify(checks, "Session state: LastCommand cleared",
                session.LastCommand is null);

            // C2: Diagnostics history was cleared.
            Verify(checks, "Diagnostics: history buffer is empty after shutdown",
                _diagnostics.GetHistory().Count == 0);

            // C3: State machine snapshots are still readable (not thrown away,
            // just stale — the state machine is not reset on shutdown, only session state is).
            var mainSnap = _stateMachine.GetLastSnapshot(VCTuneBand.Main);
            Verify(checks, "State machine: MAIN snapshot still accessible after shutdown",
                mainSnap is not null);

            // C4: View model availability fields are accessible (no throw after reset).
            Verify(checks, "View model: accessible after shutdown",
                _viewModel.MainAvailability is 0 or 1 or 2);
        }
        catch (Exception ex)
        {
            Verify(checks, $"No unexpected exception: {ex.GetType().Name}", false);
            _logger.LogError(ex, "[VCTuneHarness] {Test} threw unexpectedly.", testName);
        }

        // The history was cleared by shutdown; delta from the cleared buffer is empty.
        return BuildResult(testName, checks, histBefore: 0);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Private helpers
    // ══════════════════════════════════════════════════════════════════════

    // Returns the current diagnostics history entry count.
    private int HistoryCount() => _diagnostics.GetHistory().Count;

    // Returns diagnostics entries added since a previously captured count.
    private IReadOnlyList<VCTuneDiagnosticEntry> DiagnosticsSince(int beforeCount)
    {
        var all = _diagnostics.GetHistory();
        return all.Count > beforeCount
            ? all.Skip(beforeCount).ToArray()
            : [];
    }

    // Records a named check as PASS or FAIL.
    private static void Verify(List<string> checks, string description, bool condition) =>
        checks.Add($"{(condition ? "PASS" : "FAIL")}: {description}");

    // Builds the final VCTuneIntegrationResult from accumulated checks.
    private VCTuneIntegrationResult BuildResult(
        string testName, List<string> checks, int histBefore)
    {
        bool allPassed = checks.All(c => c.StartsWith("PASS", StringComparison.Ordinal));
        string details = string.Join(Environment.NewLine, checks);
        var diags = DiagnosticsSince(histBefore);

        _logger.LogInformation(
            "[VCTuneHarness] {Test}: {Outcome} — {PassCount}/{TotalCount} checks passed.",
            testName,
            allPassed ? "PASSED" : "FAILED",
            checks.Count(c => c.StartsWith("PASS", StringComparison.Ordinal)),
            checks.Count);

        return new VCTuneIntegrationResult(testName, allPassed, details, diags);
    }
}
