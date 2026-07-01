using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Yaesu_Web_Control.Services;

/// <summary>
/// UI‑facing view model for the VC Tune preselector subsystem.
/// <para>
/// Wraps <see cref="IVcTuneService"/> (commands) and <see cref="IVCTuneStateMachine"/>
/// (state reads) to provide the data contract consumed by the server‑rendered Razor
/// view (initial values) and by the API controller endpoints (command execution).
/// </para>
/// <para>
/// Implements <see cref="INotifyPropertyChanged"/> so any host that performs
/// .NET data binding (e.g. future WinForms panels) receives change notifications.
/// The same state changes are also pushed to the frontend via SignalR by higher‑level
/// service infrastructure and not duplicated here.
/// </para>
/// <para>
/// Register as a singleton in DI:
/// <c>services.AddSingleton&lt;VCTuneViewModel&gt;();</c>
/// </para>
/// <para>
/// Usage pattern in <c>Index.cshtml.cs</c>:
/// <code>
/// await VCTune.RefreshAsync(ct);   // populate from state machine + settings
/// // then use @Model.VCTune.MainIsOn etc. in Razor
/// </code>
/// </para>
/// </summary>
public sealed class VCTuneViewModel : INotifyPropertyChanged
{
    private readonly IVcTuneService _vcTuneService;
    private readonly IVCTuneStateMachine _stateMachine;
    private readonly ISettingsService _settings;

    // ══════════════════════════════════════════════════════════════════════
    // INotifyPropertyChanged
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Initialises the view model with its required backend dependencies.
    /// All reactive properties start at safe defaults (meter = −1, availability = 0,
    /// state = <see cref="VCTuneState.NotInstalled"/>) until the first
    /// <see cref="RefreshAsync"/> or <see cref="Refresh"/> call.
    /// </summary>
    public VCTuneViewModel(
        IVcTuneService vcTuneService,
        IVCTuneStateMachine stateMachine,
        ISettingsService settings)
    {
        _vcTuneService = vcTuneService;
        _stateMachine = stateMachine;
        _settings = settings;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Backing fields
    // ══════════════════════════════════════════════════════════════════════

    private int _mainMeter = -1;
    private int _subMeter = -1;
    private int _mainAvailability = 0;  // P6: 0=NotInstalled, 1=Available, 2=OutOfRange
    private int _subAvailability = 0;
    private VCTuneState _mainState = VCTuneState.NotInstalled;
    private VCTuneState _subState = VCTuneState.NotInstalled;
    private bool _mainControlsVisible = false;
    private bool _subControlsSupportedByModel = false;
    private bool _catNotSupported = false;

    // ══════════════════════════════════════════════════════════════════════
    // Primary reactive properties (backed by fields, raise PropertyChanged)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// P5 preselector coupling indicator for the MAIN receiver (0–255).
    /// A higher value indicates better capacitor alignment at the current frequency.
    /// <c>−1</c> until the first READ response is received from the radio.
    /// </summary>
    public int MainMeter
    {
        get => _mainMeter;
        private set => SetField(ref _mainMeter, value);
    }

    /// <summary>
    /// P5 preselector coupling indicator for the SUB receiver (0–255).
    /// <c>−1</c> until the first READ response is received, or if the SUB
    /// board is not fitted.
    /// </summary>
    public int SubMeter
    {
        get => _subMeter;
        private set => SetField(ref _subMeter, value);
    }

    /// <summary>
    /// Raw P6 availability byte for the MAIN receiver.
    /// 0 = not installed, 1 = available, 2 = fitted but current frequency out of range.
    /// Changing this value also raises <see cref="PropertyChanged"/> for all computed
    /// properties that depend on it (<see cref="MainIsAvailable"/>,
    /// <see cref="MainWarningText"/>).
    /// </summary>
    public int MainAvailability
    {
        get => _mainAvailability;
        private set
        {
            if (SetField(ref _mainAvailability, value))
                RaiseComputedChanged(
                    nameof(MainIsAvailable),
                    nameof(MainWarningText));
        }
    }

    /// <summary>
    /// Raw P6 availability byte for the SUB receiver.
    /// Changing this value also raises <see cref="PropertyChanged"/> for
    /// <see cref="SubIsEnabled"/>, <see cref="SubIsVisible"/>, and
    /// <see cref="SubWarningText"/>.
    /// </summary>
    public int SubAvailability
    {
        get => _subAvailability;
        private set
        {
            if (SetField(ref _subAvailability, value))
                RaiseComputedChanged(
                    nameof(SubIsEnabled),
                    nameof(SubIsVisible),
                    nameof(SubWarningText));
        }
    }

    /// <summary>
    /// Current operational state of the MAIN VC Tune preselector.
    /// Changing this value also raises notifications for
    /// <see cref="MainIsOn"/>, <see cref="MainIsOff"/>, <see cref="MainIsDefault"/>,
    /// <see cref="MainIsActive"/>.
    /// </summary>
    public VCTuneState MainState
    {
        get => _mainState;
        private set
        {
            if (SetField(ref _mainState, value))
                RaiseComputedChanged(
                    nameof(MainIsOn),
                    nameof(MainIsOff),
                    nameof(MainIsDefault),
                    nameof(MainIsActive));
        }
    }

    /// <summary>
    /// Current operational state of the SUB VC Tune preselector.
    /// Changing this value also raises notifications for
    /// <see cref="SubIsOn"/>, <see cref="SubIsOff"/>, <see cref="SubIsDefault"/>,
    /// <see cref="SubIsActive"/>.
    /// </summary>
    public VCTuneState SubState
    {
        get => _subState;
        private set
        {
            if (SetField(ref _subState, value))
                RaiseComputedChanged(
                    nameof(SubIsOn),
                    nameof(SubIsOff),
                    nameof(SubIsDefault),
                    nameof(SubIsActive));
        }
    }

    /// <summary>
    /// Whether the MAIN VC Tune control panel should be shown.
    /// True for FTdx101D/MP; false for all other radio models.
    /// Set by <see cref="RefreshAsync"/>.
    /// </summary>
    public bool MainControlsVisible
    {
        get => _mainControlsVisible;
        private set => SetField(ref _mainControlsVisible, value);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Computed properties (derived from backing fields; no own state)
    // ══════════════════════════════════════════════════════════════════════

    // ── MAIN computed ─────────────────────────────────────────────────────

    /// <summary>True when MAIN VC Tune is engaged (P2 = 1).</summary>
    public bool MainIsOn => _mainState == VCTuneState.On;

    /// <summary>True when MAIN VC Tune is disengaged (P2 = 0).</summary>
    public bool MainIsOff => _mainState == VCTuneState.Off;

    /// <summary>True when the MAIN auto‑tune (DEFAULT) routine was the last command sent.</summary>
    public bool MainIsDefault => _mainState == VCTuneState.Default;

    /// <summary>
    /// True when MAIN VC Tune is in a transient motor‑driven state
    /// (<see cref="VCTuneState.Stepping"/> or <see cref="VCTuneState.Centering"/>).
    /// </summary>
    public bool MainIsActive =>
        _mainState is VCTuneState.On or VCTuneState.Stepping or VCTuneState.Centering;

    /// <summary>True when MAIN P6 = 1 (hardware present and frequency in range).</summary>
    public bool MainIsAvailable => _mainAvailability == 1;

    /// <summary>
    /// True when the radio's hardware revision does not expose VC Tune over CAT.
    /// Set by <see cref="SetCatNotSupported"/> when the ID; response identifies
    /// a hardware revision that rejects VT CAT frames (e.g. ID0682).
    /// </summary>
    public bool CatNotSupported => _catNotSupported;

    /// <summary>
    /// User‑facing warning text for the MAIN receiver availability state.
    /// <c>null</c> when no warning is needed (P6 = 1 or unknown).
    /// </summary>
    public string? MainWarningText => _catNotSupported
        ? "This radio hardware revision does not support VC Tune over CAT. VC Tune can only be operated from the front panel."
        : _mainAvailability switch
        {
            0 => "VC Tune is not installed on this receiver.",
            2 => "VC Tune is temporarily unavailable.",
            _ => null
        };

    // ── SUB computed ──────────────────────────────────────────────────────

    /// <summary>True when SUB VC Tune is engaged (P2 = 1).</summary>
    public bool SubIsOn => _subState == VCTuneState.On;

    /// <summary>True when SUB VC Tune is disengaged (P2 = 0).</summary>
    public bool SubIsOff => _subState == VCTuneState.Off;

    /// <summary>True when the SUB auto‑tune (DEFAULT) routine was the last command sent.</summary>
    public bool SubIsDefault => _subState == VCTuneState.Default;

    /// <summary>
    /// True when SUB VC Tune is in a transient motor‑driven state.
    /// </summary>
    public bool SubIsActive =>
        _subState is VCTuneState.On or VCTuneState.Stepping or VCTuneState.Centering;

    /// <summary>
    /// Whether the SUB VC Tune controls should be rendered.
    /// Hidden (false) when P6 = 0 (board not fitted) or when the radio model
    /// does not support a SUB VC Tune board.
    /// </summary>
    public bool SubIsVisible => _subControlsSupportedByModel && _subAvailability != 0;

    /// <summary>
    /// Whether the SUB VC Tune controls are interactive.
    /// Disabled (false) when P6 = 2 (board fitted but temporarily unavailable).
    /// </summary>
    public bool SubIsEnabled => _subAvailability == 1;

    /// <summary>
    /// User‑facing warning text for the SUB receiver availability state.
    /// <c>null</c> when no warning is needed.
    /// </summary>
    public string? SubWarningText => _subAvailability switch
    {
        0 => "VC Tune is not installed on the SUB receiver.",
        2 => "VC Tune is temporarily unavailable on the SUB receiver.",
        _ => null
    };

    // ══════════════════════════════════════════════════════════════════════
    // Refresh
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Asynchronously refreshes all view‑model properties from the current state‑machine
    /// snapshots and the settings service (for radio‑model capability checks).
    /// Call this once during the Razor page's <c>OnGetAsync</c> to populate initial values.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetSettingsAsync();
        MainControlsVisible = RadioCapabilities.SupportsVCTuneMain(settings.RadioModel);
        _subControlsSupportedByModel = RadioCapabilities.SupportsVCTuneSubStatic(settings.RadioModel);

        ApplySnapshot(_stateMachine.GetLastSnapshot(VCTuneBand.Main));
        ApplySnapshot(_stateMachine.GetLastSnapshot(VCTuneBand.Sub));

        // Force SubIsVisible notification because _subControlsSupportedByModel changed.
        RaiseComputedChanged(nameof(SubIsVisible));
    }

    /// <summary>
    /// Synchronously refreshes MAIN and SUB state‑machine snapshots into the view
    /// model without re‑reading settings.
    /// Suitable for calling after a command completes (where settings haven't changed).
    /// </summary>
    public void Refresh()
    {
        ApplySnapshot(_stateMachine.GetLastSnapshot(VCTuneBand.Main));
        ApplySnapshot(_stateMachine.GetLastSnapshot(VCTuneBand.Sub));
    }

    /// <summary>
    /// Updates the view model from a single <see cref="VCTuneStateSnapshot"/> without
    /// reading other data. Called directly by service code after a READ or SET that
    /// produces a new snapshot.
    /// </summary>
    public void UpdateFromSnapshot(VCTuneStateSnapshot snapshot) => ApplySnapshot(snapshot);

    /// <summary>
    /// Marks the VC Tune subsystem as unsupported over CAT for this hardware revision.
    /// Sets availability to 0 (buttons disabled in server-rendered initial HTML) and
    /// overrides <see cref="MainWarningText"/> to show the hardware-limitation message.
    /// Called by <see cref="VCTuneModule.InitializeAsync"/> when the ID; response
    /// identifies a blocked hardware revision.
    /// </summary>
    public void SetCatNotSupported()
    {
        _catNotSupported = true;
        // Treat as availability=0 so the initial server-rendered buttons are disabled.
        _mainAvailability = 0;
        _subAvailability = 0;
        RaiseComputedChanged(nameof(CatNotSupported), nameof(MainWarningText), nameof(MainIsAvailable));
    }

    // ══════════════════════════════════════════════════════════════════════
    // UI Commands
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Engages the VC Tune preselector on the specified receiver.
    /// After the service call completes, all view‑model properties are refreshed
    /// from the latest state‑machine snapshot.
    /// </summary>
    /// <param name="receiver">MAIN or SUB.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The service result. Check <see cref="VcTuneSetResult.Success"/> before
    /// presenting feedback to the user.
    /// </returns>
    public async Task<VcTuneSetResult> ExecuteOnAsync(
        VcTuneReceiver receiver, CancellationToken ct = default)
    {
        var result = await _vcTuneService.SetVCTuneOnAsync(receiver, ct);
        Refresh();
        return result;
    }

    /// <summary>
    /// Disengages the VC Tune preselector on the specified receiver.
    /// All view‑model properties are refreshed after the call completes.
    /// </summary>
    /// <param name="receiver">MAIN or SUB.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VcTuneSetResult> ExecuteOffAsync(
        VcTuneReceiver receiver, CancellationToken ct = default)
    {
        var result = await _vcTuneService.SetVCTuneOffAsync(receiver, ct);
        Refresh();
        return result;
    }

    /// <summary>
    /// Runs the automatic default‑tune routine on the specified receiver.
    /// The radio sweeps the capacitor to find the peak coupling position; the
    /// confirming READ will resolve the state to <see cref="VCTuneState.On"/> or
    /// <see cref="VCTuneState.Off"/>.
    /// </summary>
    /// <param name="receiver">MAIN or SUB.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VcTuneSetResult> ExecuteDefaultAsync(
        VcTuneReceiver receiver, CancellationToken ct = default)
    {
        var result = await _vcTuneService.SetVCTuneDefaultAsync(receiver, ct);
        Refresh();
        return result;
    }

    /// <summary>
    /// Steps the VC Tune capacitor in the given direction by the given amount.
    /// Returns a rejected result immediately if <paramref name="direction"/> or
    /// <paramref name="amount"/> fail validation — the backend service is not called.
    /// </summary>
    /// <param name="receiver">MAIN or SUB.</param>
    /// <param name="direction">'+' (forward) or '−' (backward).</param>
    /// <param name="amount">Step amount 0–9.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="amount"/> is outside 0–9.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="direction"/> is not '+' or '−'.
    /// </exception>
    public async Task<VcTuneSetResult> ExecuteStepAsync(
        VcTuneReceiver receiver, char direction, int amount, CancellationToken ct = default)
    {
        ValidateDirection(direction);
        ValidateStepAmount(amount);

        var result = await _vcTuneService.SetVCTuneStepAsync(receiver, direction, amount, ct);
        Refresh();
        return result;
    }

    /// <summary>
    /// Moves the VC Tune capacitor to its mechanical centre position on the specified
    /// receiver. A motor‑protection lockout of 3 s is enforced by the backend service.
    /// </summary>
    /// <param name="receiver">MAIN or SUB.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VcTuneSetResult> ExecuteCenterAsync(
        VcTuneReceiver receiver, CancellationToken ct = default)
    {
        var result = await _vcTuneService.SetVCTuneCenterAsync(receiver, ct);
        Refresh();
        return result;
    }

    /// <summary>
    /// Reads the current VC Tune status from the radio for the specified receiver
    /// and refreshes the view‑model meter and state properties from the result.
    /// Returns cached state if a READ was performed within the last second.
    /// </summary>
    /// <param name="receiver">MAIN or SUB.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The raw read result. <see cref="VcTuneReadResult.IsValid"/> indicates
    /// whether the radio returned a parseable response.
    /// </returns>
    public async Task<VcTuneReadResult> ExecuteReadStatusAsync(
        VcTuneReceiver receiver, CancellationToken ct = default)
    {
        var result = await _vcTuneService.ReadVCTuneStatusAsync(receiver, ct);
        Refresh();
        return result;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Step‑control validation
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates a step direction character.
    /// </summary>
    /// <param name="direction">The direction to validate ('+' or '−').</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="direction"/> is not '+' or '−'.
    /// </exception>
    public static void ValidateDirection(char direction)
    {
        if (direction != '+' && direction != '-')
            throw new ArgumentException(
                $"Direction must be '+' or '-'; got '{direction}'.",
                nameof(direction));
    }

    /// <summary>
    /// Validates a step amount value.
    /// </summary>
    /// <param name="amount">The step amount to validate (0–9).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="amount"/> is outside the range 0–9.
    /// </exception>
    public static void ValidateStepAmount(int amount)
    {
        if (amount is < 0 or > 9)
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount,
                "Step amount must be in the range 0–9.");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Private helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies all fields from <paramref name="snapshot"/> to the appropriate
    /// backing properties, triggering <see cref="PropertyChanged"/> for any that changed.
    /// </summary>
    private void ApplySnapshot(VCTuneStateSnapshot snapshot)
    {
        if (snapshot.Band == VCTuneBand.Main)
        {
            MainMeter        = snapshot.Meter;
            MainAvailability = snapshot.Availability;
            MainState        = snapshot.State;
        }
        else
        {
            SubMeter        = snapshot.Meter;
            SubAvailability = snapshot.Availability;
            SubState        = snapshot.State;
        }
    }

    /// <summary>
    /// Raises <see cref="PropertyChanged"/> for one or more computed property names
    /// that have no backing field of their own.
    /// </summary>
    private void RaiseComputedChanged(params string[] propertyNames)
    {
        foreach (var name in propertyNames)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Assigns a new value to a backing field and, if the value changed, raises
    /// <see cref="PropertyChanged"/>.
    /// </summary>
    /// <typeparam name="T">The field type.</typeparam>
    /// <param name="field">Reference to the backing field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="name">
    /// The property name — populated automatically by <see cref="CallerMemberNameAttribute"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the value actually changed; <see langword="false"/>
    /// when <paramref name="field"/> already held the same value.
    /// </returns>
    private bool SetField<T>(
        ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
