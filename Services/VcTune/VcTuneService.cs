namespace Yaesu_Web_Control.Services;

/// <summary>
/// Implements VC Tune preselector control for the FTdx101D and FTdx101MP
/// via the VT CAT command. All SET operations send a frame then issue a
/// 150 ms deferred READ to confirm the radio's actual new state.
/// <para>
/// Serialisation: shares <see cref="CatRequestSemaphore"/> with CatController
/// so VC Tune and other HTTP-triggered CAT operations never overlap on the
/// serial port. Motor-protection checks run before semaphore acquisition for
/// fast rejection without blocking other callers.
/// </para>
/// <para>
/// RadioStateService integration: Message 46 wires the five VC Tune properties
/// (<c>VCTuneOnA/B</c>, <c>VCTuneMeterA/B</c>, <c>VCTuneSubP6</c>) into
/// RadioStateService and replaces the internal backing fields in this class
/// with SignalR-broadcasting property assignments.
/// </para>
/// </summary>
public sealed class VcTuneService : IVcTuneService
{
    // ── Motor-protection constants ─────────────────────────────────────────
    private const int SemaphoreTimeoutMs   = 2000;
    private const int MinSetIntervalMs     = 250;    // all SET types
    private const int MinCenterIntervalMs  = 3000;   // CENTER only
    private const int PostSetReadDelayMs   = 150;
    private const int ReadRetryDelayMs     = 100;
    private const int ReadFreshnessWindowMs = 1000;

    private readonly ICatClient          _catClient;
    private readonly ISettingsService    _settingsService;
    private readonly ILogger<VcTuneService> _logger;
    private readonly CatRequestSemaphore _semaphore;
    private readonly RadioStateService   _radioStateService;

    // ── Internal state (replaced by RadioStateService properties in Msg 46) ─
    // Index 0 = Main, 1 = Sub. Matches VcTuneReceiver enum integer values.
    private readonly bool[]            _onState     = [false, false];
    private readonly int[]             _meterValue  = [0, 0];
    private          int               _subP6       = 0;  // P6 for SUB receiver

    // ── Session cache ──────────────────────────────────────────────────────
    // True once a probe or READ has confirmed P6=0; prevents redundant SUB
    // commands from acquiring the semaphore.
    private volatile bool _subNotInstalled = false;

    // ── Motor-protection timestamps ────────────────────────────────────────
    private readonly DateTimeOffset[] _lastSetAt    = [DateTimeOffset.MinValue, DateTimeOffset.MinValue];
    private readonly DateTimeOffset[] _lastCenterAt = [DateTimeOffset.MinValue, DateTimeOffset.MinValue];
    private readonly object           _motorLock    = new();

    // ── Read freshness ─────────────────────────────────────────────────────
    private readonly DateTimeOffset[] _lastReadAt   = [DateTimeOffset.MinValue, DateTimeOffset.MinValue];

    public VcTuneService(
        ICatClient catClient,
        ISettingsService settingsService,
        ILogger<VcTuneService> logger,
        CatRequestSemaphore semaphore,
        RadioStateService radioStateService)
    {
        _catClient         = catClient;
        _settingsService   = settingsService;
        _logger            = logger;
        _semaphore         = semaphore;
        _radioStateService = radioStateService;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Public interface — SET operations
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<VcTuneSetResult> SetVCTuneOnAsync(
        VcTuneReceiver receiver,
        CancellationToken cancellationToken = default)
    {
        var capCheck = await ValidateCapabilityAsync(receiver, cancellationToken);
        if (capCheck != null) return capCheck;

        // No-op: already on
        if (_onState[(int)receiver])
        {
            _logger.LogDebug("[VcTuneService] SET ON on {Receiver}: no-op (already on). Command suppressed.", receiver);
            return VcTuneSetResult.Ok(await ReadStatusCoreAsync(receiver, cancellationToken));
        }

        string frame = $"VT VCT {(int)receiver},1,+,0;";
        return await ExecuteSetAsync(receiver, frame, "On", isCenter: false, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<VcTuneSetResult> SetVCTuneOffAsync(
        VcTuneReceiver receiver,
        CancellationToken cancellationToken = default)
    {
        var capCheck = await ValidateCapabilityAsync(receiver, cancellationToken);
        if (capCheck != null) return capCheck;

        // No-op: already off
        if (!_onState[(int)receiver])
        {
            _logger.LogDebug("[VcTuneService] SET OFF on {Receiver}: no-op (already off). Command suppressed.", receiver);
            return VcTuneSetResult.Ok(await ReadStatusCoreAsync(receiver, cancellationToken));
        }

        string frame = $"VT VCT {(int)receiver},0,+,0;";
        return await ExecuteSetAsync(receiver, frame, "Off", isCenter: false, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<VcTuneSetResult> SetVCTuneDefaultAsync(
        VcTuneReceiver receiver,
        CancellationToken cancellationToken = default)
    {
        var capCheck = await ValidateCapabilityAsync(receiver, cancellationToken);
        if (capCheck != null) return capCheck;

        // DEFAULT is never a no-op: the radio re-executes the auto-tune even
        // if it was already in a "tuned" position. Operators use this deliberately.
        string frame = $"VT VCT {(int)receiver},2,+,0;";
        return await ExecuteSetAsync(receiver, frame, "Default", isCenter: false, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<VcTuneSetResult> SetVCTuneStepAsync(
        VcTuneReceiver receiver,
        char direction,
        int amount,
        CancellationToken cancellationToken = default)
    {
        var capCheck = await ValidateCapabilityAsync(receiver, cancellationToken);
        if (capCheck != null) return capCheck;

        var paramCheck = ValidateStepParameters(direction, amount);
        if (paramCheck != null) return paramCheck;

        // VC Tune must be On before stepping
        if (!_onState[(int)receiver])
        {
            _logger.LogWarning("[VcTuneService] SET Step rejected on {Receiver}: VC Tune must be ON before stepping.", receiver);
            return VcTuneSetResult.Rejected("InvalidState",
                "VC Tune must be ON before stepping.");
        }

        string frame = $"VT VCT {(int)receiver},1,{direction},{amount};";
        return await ExecuteSetAsync(receiver, frame, $"Step{direction}{amount}", isCenter: false, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<VcTuneSetResult> SetVCTuneCenterAsync(
        VcTuneReceiver receiver,
        CancellationToken cancellationToken = default)
    {
        var capCheck = await ValidateCapabilityAsync(receiver, cancellationToken);
        if (capCheck != null) return capCheck;

        // VC Tune must be On before centering
        if (!_onState[(int)receiver])
        {
            _logger.LogWarning("[VcTuneService] SET Center rejected on {Receiver}: VC Tune must be ON before centering.", receiver);
            return VcTuneSetResult.Rejected("InvalidState",
                "VC Tune must be ON before centering.");
        }

        string frame = $"VT VCT {(int)receiver},2,+,0;";
        return await ExecuteSetAsync(receiver, frame, "Center", isCenter: true, cancellationToken);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Public interface — READ operation
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<VcTuneReadResult> ReadVCTuneStatusAsync(
        VcTuneReceiver receiver,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (!RadioCapabilities.SupportsVCTuneMain(settings.RadioModel))
        {
            _logger.LogWarning("[VcTuneService] READ rejected: VC Tune not supported on {Model}.", settings.RadioModel);
            return VcTuneReadResult.NoResponse(receiver);
        }

        if (!RadioCapabilities.SupportsVCTuneCat(_radioStateService.Id))
        {
            _logger.LogInformation(
                "[VcTuneService] READ skipped: hardware {Id} does not support VC Tune CAT.", _radioStateService.Id);
            return VcTuneReadResult.NoResponse(receiver);
        }

        // Fast-path: return from read-freshness window if recent enough
        if (IsReadFresh(receiver))
        {
            _logger.LogDebug("[VcTuneService] READ {Receiver}: using fresh cached result (within {Window}ms).",
                receiver, ReadFreshnessWindowMs);
            return BuildCachedResult(receiver);
        }

        if (!await _semaphore.WaitAsync(SemaphoreTimeoutMs, cancellationToken))
        {
            _logger.LogWarning("[VcTuneService] TIMEOUT waiting for semaphore on READ {Receiver}.", receiver);
            return VcTuneReadResult.NoResponse(receiver);
        }

        try
        {
            await EnsureConnectedAsync();
            var result = await ReadStatusCoreAsync(receiver, cancellationToken);
            if (result.IsValid)
            {
                UpdateInternalState(result);
                UpdateStateMachine(receiver, result);
                _lastReadAt[(int)receiver] = DateTimeOffset.UtcNow;
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VcTuneService] Error during READ {Receiver}.", receiver);
            return VcTuneReadResult.NoResponse(receiver);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Probe (called during init — no semaphore; exclusive init phase)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task ProbeCapabilityAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync();

        if (!RadioCapabilities.SupportsVCTuneMain(settings.RadioModel))
        {
            _logger.LogInformation("[VcTuneService] VC Tune not supported on {Model}. Probe skipped.", settings.RadioModel);
            return;
        }

        if (!RadioCapabilities.SupportsVCTuneCat(_radioStateService.Id))
        {
            _logger.LogInformation(
                "[VcTuneService] Hardware {Id} does not support VC Tune CAT. Probe skipped.", _radioStateService.Id);
            return;
        }

        // Probe MAIN
        _logger.LogInformation("[VcTuneService] Probing MAIN VC Tune...");
        var mainResult = await ReadStatusCoreAsync(VcTuneReceiver.Main, cancellationToken);
        if (mainResult.IsValid)
        {
            UpdateInternalState(mainResult);
            UpdateStateMachine(VcTuneReceiver.Main, mainResult);
            _logger.LogInformation(
                "[VcTuneService] MAIN VC Tune probed: On={On} Meter={Meter} P6={P6}",
                mainResult.IsOn, mainResult.Meter, (int)mainResult.Availability);
        }
        else
        {
            _logger.LogWarning("[VcTuneService] MAIN VC Tune probe returned no valid response.");
        }

        // Probe SUB (FTdx101MP only)
        if (!RadioCapabilities.SupportsVCTuneSubStatic(settings.RadioModel))
        {
            _subNotInstalled = true;
            _logger.LogInformation(
                "[VcTuneService] SUB VC Tune not statically supported on {Model}.", settings.RadioModel);
            return;
        }

        _logger.LogInformation("[VcTuneService] Probing SUB VC Tune...");
        var subResult = await ReadStatusCoreAsync(VcTuneReceiver.Sub, cancellationToken);
        if (subResult.IsValid)
        {
            UpdateInternalState(subResult);
            UpdateStateMachine(VcTuneReceiver.Sub, subResult);
            _subNotInstalled = subResult.Availability == VcTuneAvailability.NotInstalled;
            _logger.LogInformation(
                "[VcTuneService] SUB VC Tune probed: P6={P6} NotInstalled={NotInstalled}",
                (int)subResult.Availability, _subNotInstalled);
        }
        else
        {
            // Conservative default: treat as not installed when probe times out
            _subNotInstalled = true;
            _logger.LogWarning(
                "[VcTuneService] SUB VC Tune probe returned no valid response. Treating as not installed.");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Core SET executor
    // ══════════════════════════════════════════════════════════════════════

    private async Task<VcTuneSetResult> ExecuteSetAsync(
        VcTuneReceiver receiver,
        string frame,
        string actionDescription,
        bool isCenter,
        CancellationToken cancellationToken)
    {
        // Motor protection check — before semaphore for fast rejection
        var motorCheck = CheckMotorProtection(receiver, isCenter);
        if (motorCheck != null) return motorCheck;

        if (!await _semaphore.WaitAsync(SemaphoreTimeoutMs, cancellationToken))
        {
            _logger.LogWarning(
                "[VcTuneService] TIMEOUT waiting for semaphore on SET {Action} {Receiver}.",
                actionDescription, receiver);
            return VcTuneSetResult.Rejected("Timeout",
                "Communication timeout. The radio may or may not have applied the VC Tune command. Press Read Status to check.");
        }

        try
        {
            await EnsureConnectedAsync();

            _logger.LogInformation(
                "[VcTuneService] SET {Action} on {Receiver}: frame={Frame}",
                actionDescription, receiver, frame);

            await _catClient.SendCommandAsync(frame, "VcTune", cancellationToken);
            RecordSetTimestamp(receiver, isCenter);

            // Deferred confirmation read (motor settling time)
            await Task.Delay(PostSetReadDelayMs, cancellationToken);
            var confirmed = await ReadStatusCoreAsync(receiver, cancellationToken);

            if (confirmed.IsValid)
            {
                UpdateInternalState(confirmed);
                UpdateStateMachine(receiver, confirmed);
                _lastReadAt[(int)receiver] = DateTimeOffset.UtcNow;

                _logger.LogInformation(
                    "[VcTuneService] READ {Receiver} (post-SET confirm): P5={Meter} P6={P6} On={On} raw={Raw}",
                    receiver, confirmed.Meter, (int)confirmed.Availability,
                    confirmed.IsOn, confirmed.RawResponse);

                return VcTuneSetResult.Ok(confirmed);
            }

            _logger.LogWarning(
                "[VcTuneService] SET {Action} on {Receiver}: confirmation READ returned no valid response.",
                actionDescription, receiver);
            return VcTuneSetResult.SentUnconfirmed(
                "The radio did not respond. Meter reading unavailable.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "[VcTuneService] TIMEOUT on SET {Action} {Receiver}: frame={Frame}",
                actionDescription, receiver, frame);
            return VcTuneSetResult.Rejected("Timeout",
                "Communication timeout. Status is uncertain — press Read Status to check.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[VcTuneService] Error during SET {Action} {Receiver}: frame={Frame}",
                actionDescription, receiver, frame);
            return VcTuneSetResult.Rejected("InternalError",
                "An unexpected error occurred during VC Tune operation.");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Core READ (no semaphore — called from within a semaphore window or probe)
    // ══════════════════════════════════════════════════════════════════════

    private async Task<VcTuneReadResult> ReadStatusCoreAsync(
        VcTuneReceiver receiver,
        CancellationToken cancellationToken)
    {
        string command = receiver == VcTuneReceiver.Main ? "VT0;" : "VT1;";

        string raw = await _catClient.SendCommandAsync(command, "VcTune", cancellationToken);

        if (string.IsNullOrEmpty(raw))
        {
            _logger.LogWarning(
                "[VcTuneService] READ {Receiver}: null response. Retrying in {Delay}ms.",
                receiver, ReadRetryDelayMs);

            await Task.Delay(ReadRetryDelayMs, cancellationToken);
            raw = await _catClient.SendCommandAsync(command, "VcTune", cancellationToken);

            if (string.IsNullOrEmpty(raw))
            {
                _logger.LogError(
                    "[VcTuneService] READ {Receiver}: null response after retry. Entering READ fallback state.",
                    receiver);
                return VcTuneReadResult.NoResponse(receiver);
            }
        }

        var result = VcTuneResponseParser.Parse(raw);

        if (!result.IsValid)
        {
            _logger.LogWarning(
                "[VcTuneService] UNEXPECTED RESPONSE from {Receiver}: raw={Raw}",
                receiver, raw);
        }

        return result;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Validation helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates radio-model and receiver-capability rules.
    /// Returns a non-null rejection result on failure; null means pass.
    /// </summary>
    private async Task<VcTuneSetResult?> ValidateCapabilityAsync(
        VcTuneReceiver receiver,
        CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetSettingsAsync();

        if (!RadioCapabilities.SupportsVCTuneMain(settings.RadioModel))
        {
            _logger.LogWarning(
                "[VcTuneService] Rejected: VC Tune not supported on {Model}.", settings.RadioModel);
            return VcTuneSetResult.Rejected("NotInstalled",
                "VC Tune is not supported on this radio model.");
        }

        if (!RadioCapabilities.SupportsVCTuneCat(_radioStateService.Id))
        {
            _logger.LogWarning(
                "[VcTuneService] Rejected: hardware {Id} does not support VC Tune CAT.", _radioStateService.Id);
            return VcTuneSetResult.Rejected("CatNotSupported",
                "This radio hardware revision does not support VC Tune over CAT. " +
                "VC Tune can only be operated from the front panel.");
        }

        if (receiver != VcTuneReceiver.Sub)
            return null; // MAIN always passes

        // Fast rejection for known-not-installed SUB (no semaphore needed)
        if (_subNotInstalled)
        {
            _logger.LogWarning(
                "[VcTuneService] Rejected: SUB VC Tune not installed (session cache).");
            return VcTuneSetResult.Rejected("NotInstalled",
                "VC Tune is not installed on the SUB receiver.");
        }

        switch (_subP6)
        {
            case 0:
                _subNotInstalled = true;
                _logger.LogWarning(
                    "[VcTuneService] Rejected: SUB VC Tune not installed (P6=0).");
                return VcTuneSetResult.Rejected("NotInstalled",
                    "VC Tune is not installed on the SUB receiver.");

            case 2:
                _logger.LogWarning(
                    "[VcTuneService] Rejected: SUB VC Tune unavailable at current frequency (P6=2).");
                return VcTuneSetResult.Rejected("UnavailableFrequency",
                    "VC Tune is installed but temporarily unavailable. Wait a moment and try again.");
        }

        return null; // P6 = 1, pass
    }

    /// <summary>
    /// Validates step-command parameters. Returns non-null rejection on failure; null means pass.
    /// </summary>
    private static VcTuneSetResult? ValidateStepParameters(char direction, int amount)
    {
        if (direction != '+' && direction != '-')
            return VcTuneSetResult.Rejected("InvalidParameters",
                "VC Tune parameters were invalid: direction must be '+' or '-'.");

        if (amount < 0 || amount > 9)
            return VcTuneSetResult.Rejected("InvalidParameters",
                "VC Tune parameters were invalid: step amount must be 0–9.");

        return null;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Motor-protection helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks whether the inter-command rate limit has elapsed for this receiver.
    /// Returns a rejection result if the limit has not elapsed; null means pass.
    /// </summary>
    private VcTuneSetResult? CheckMotorProtection(VcTuneReceiver receiver, bool isCenter)
    {
        lock (_motorLock)
        {
            int idx = (int)receiver;
            var now = DateTimeOffset.UtcNow;

            double elapsedSet = (now - _lastSetAt[idx]).TotalMilliseconds;
            if (elapsedSet < MinSetIntervalMs)
            {
                int remainingMs = (int)(MinSetIntervalMs - elapsedSet);
                _logger.LogInformation(
                    "[VcTuneService] Motor protection: SET on {Receiver} rejected. Inter-command delay not elapsed ({Elapsed:F0}ms < {Min}ms).",
                    receiver, elapsedSet, MinSetIntervalMs);
                return VcTuneSetResult.Rejected("MotorProtection",
                    $"VC Tune commands are rate-limited. Please wait {remainingMs} ms.");
            }

            if (isCenter)
            {
                double elapsedCenter = (now - _lastCenterAt[idx]).TotalMilliseconds;
                if (elapsedCenter < MinCenterIntervalMs)
                {
                    int remainingMs = (int)(MinCenterIntervalMs - elapsedCenter);
                    _logger.LogInformation(
                        "[VcTuneService] Motor protection: CENTER on {Receiver} rejected. Center lockout not elapsed ({Elapsed:F0}ms < {Min}ms).",
                        receiver, elapsedCenter, MinCenterIntervalMs);
                    return VcTuneSetResult.Rejected("MotorProtection",
                        $"VC Tune center is rate-limited. Please wait {remainingMs} ms.");
                }
            }

            return null;
        }
    }

    private void RecordSetTimestamp(VcTuneReceiver receiver, bool isCenter)
    {
        lock (_motorLock)
        {
            int idx = (int)receiver;
            _lastSetAt[idx] = DateTimeOffset.UtcNow;
            if (isCenter)
                _lastCenterAt[idx] = DateTimeOffset.UtcNow;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Internal state management
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Updates internal state backing fields from a valid READ result.
    /// Message 46 replaces these assignments with RadioStateService property
    /// writes that broadcast changes to SignalR.
    /// </summary>
    private void UpdateInternalState(VcTuneReadResult result)
    {
        int idx = (int)result.Receiver;
        _onState[idx]    = result.IsOn;
        _meterValue[idx] = result.Meter;

        if (result.Receiver == VcTuneReceiver.Sub)
            _subP6 = (int)result.Availability;
    }

    /// <summary>
    /// Stub: drives VC Tune state-machine transitions from a READ result.
    /// Fully implemented in Message 45.
    /// </summary>
    private void UpdateStateMachine(VcTuneReceiver receiver, VcTuneReadResult result)
    {
        // State machine implementation provided in Message 45.
        // Hook is called here after every READ so that once Message 45 lands
        // the state machine receives all updates without further changes here.
    }

    // ══════════════════════════════════════════════════════════════════════
    // Read-freshness helpers
    // ══════════════════════════════════════════════════════════════════════

    private bool IsReadFresh(VcTuneReceiver receiver)
    {
        var elapsed = (DateTimeOffset.UtcNow - _lastReadAt[(int)receiver]).TotalMilliseconds;
        return elapsed < ReadFreshnessWindowMs;
    }

    /// <summary>
    /// Builds a <see cref="VcTuneReadResult"/> from the current internal state
    /// for use when a READ falls within the freshness window.
    /// NOTE: RawResponse is empty for cached results. Message 46 will supply
    /// the last raw response from RadioStateService once that integration lands.
    /// </summary>
    private VcTuneReadResult BuildCachedResult(VcTuneReceiver receiver)
    {
        int idx = (int)receiver;
        return new VcTuneReadResult(
            receiver,
            IsOn: _onState[idx],
            IsDefault: false,
            LastDirection: '+',
            LastStepAmount: 0,
            Meter: _meterValue[idx],
            Availability: receiver == VcTuneReceiver.Sub
                ? (VcTuneAvailability)_subP6
                : VcTuneAvailability.Available,
            RawResponse: string.Empty,
            IsValid: true);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Connection helper
    // ══════════════════════════════════════════════════════════════════════

    private async Task EnsureConnectedAsync()
    {
        if (!_catClient.IsConnected)
        {
            var settings = await _settingsService.GetSettingsAsync();
            await _catClient.ConnectAsync(settings.SerialPort, settings.BaudRate);
        }
    }
}
