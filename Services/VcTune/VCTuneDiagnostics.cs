using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services;

// ══════════════════════════════════════════════════════════════════════════════
// Diagnostic entry record
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Immutable record representing a single VC Tune diagnostic event, captured by
/// <see cref="VCTuneDiagnostics"/> and accessible via
/// <see cref="VCTuneDiagnostics.GetHistory"/>.
/// </summary>
/// <param name="Timestamp">
/// UTC instant at which the event was recorded. Precision is
/// <see cref="DateTime.UtcNow"/>.
/// </param>
/// <param name="Category">
/// Short string grouping similar events. Standard values:
/// <c>"CAT.Set"</c>, <c>"CAT.Read"</c>, <c>"CAT.Response"</c>,
/// <c>"State"</c>, <c>"Meter"</c>, <c>"Availability"</c>,
/// <c>"Error"</c>, <c>"Fallback"</c>.
/// </param>
/// <param name="Message">
/// Human-readable description of the event, suitable for display in the
/// Diagnostics page.
/// </param>
/// <param name="Band">
/// The VC Tune receiver the event relates to, or <see langword="null"/> when
/// the event is not receiver-specific.
/// </param>
/// <param name="RawData">
/// Optional unprocessed data associated with the event — for example the raw
/// CAT command string or raw response bytes — or <see langword="null"/> when
/// not applicable.
/// </param>
public sealed record VCTuneDiagnosticEntry(
    DateTime Timestamp,
    string Category,
    string Message,
    VCTuneBand? Band,
    string? RawData);

// ══════════════════════════════════════════════════════════════════════════════
// Diagnostics service
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Singleton diagnostics sink for all VC Tune preselector operations.
/// Records structured <see cref="VCTuneDiagnosticEntry"/> items in an in-memory
/// ring buffer capped at <see cref="MaxHistoryEntries"/> and forwards each event
/// to the application's <see cref="ILogger"/>.
/// <para>
/// Safety invariants:
/// <list type="bullet">
///   <item>All public methods are non-throwing — any internal exception is
///     silently swallowed so that diagnostics never disrupt CAT operations.</item>
///   <item>Methods never send CAT commands, parse responses, or mutate VC Tune
///     state.</item>
///   <item>The history buffer is thread-safe; concurrent calls from the CAT
///     dispatch thread and the SignalR hub thread are safe without external
///     locking.</item>
/// </list>
/// </para>
/// <para>
/// Register as a singleton in DI:
/// <c>services.AddSingleton&lt;VCTuneDiagnostics&gt;();</c>
/// </para>
/// </summary>
public sealed class VCTuneDiagnostics
{
    /// <summary>
    /// Maximum number of entries retained in the in-memory history ring buffer.
    /// The oldest entry is evicted when this limit is reached.
    /// </summary>
    public const int MaxHistoryEntries = 500;

    private readonly ILogger<VCTuneDiagnostics> _logger;

    // ConcurrentQueue gives lock-free Enqueue from any thread; we cap size
    // by atomically tracking the count and dequeuing the oldest entry.
    private readonly ConcurrentQueue<VCTuneDiagnosticEntry> _history = new();
    private int _count;

    /// <summary>
    /// Initialises the diagnostics service with the application logger.
    /// </summary>
    public VCTuneDiagnostics(ILogger<VCTuneDiagnostics> logger)
    {
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════════════════
    // CAT operation logging
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Records that a VC Tune SET command has been dispatched to the CAT layer.
    /// </summary>
    /// <param name="command">The fully constructed command object to log.</param>
    public void LogSetCommand(VCTuneCommand command)
    {
        try
        {
            var msg = command.Type switch
            {
                VCTuneCommandType.Step =>
                    $"SET {command.Band} Step {command.Direction} x{command.StepAmount}",
                VCTuneCommandType.Center =>
                    $"SET {command.Band} Center",
                _ =>
                    $"SET {command.Band} {command.Type}",
            };

            Append(new VCTuneDiagnosticEntry(
                DateTime.UtcNow,
                "CAT.Set",
                msg,
                command.Band,
                command.RawCommand));

            _logger.LogDebug(
                "[VCTune] CAT SET | Band={Band} Type={Type} Cmd={Cmd}",
                command.Band, command.Type, command.RawCommand);
        }
        catch { /* never throw from diagnostics */ }
    }

    /// <summary>
    /// Records that a VC Tune READ command has been dispatched to the CAT layer.
    /// </summary>
    /// <param name="command">The fully constructed read-command object to log.</param>
    public void LogReadCommand(VCTuneCommand command)
    {
        try
        {
            Append(new VCTuneDiagnosticEntry(
                DateTime.UtcNow,
                "CAT.Read",
                $"READ {command.Band}",
                command.Band,
                command.RawCommand));

            _logger.LogDebug(
                "[VCTune] CAT READ | Band={Band} Cmd={Cmd}",
                command.Band, command.RawCommand);
        }
        catch { /* never throw from diagnostics */ }
    }

    /// <summary>
    /// Records the raw string returned by the radio in response to a VT command.
    /// </summary>
    /// <param name="rawResponse">
    /// The verbatim CAT response string received from the radio, including the
    /// trailing semicolon if present.
    /// </param>
    public void LogRawResponse(string rawResponse)
    {
        try
        {
            Append(new VCTuneDiagnosticEntry(
                DateTime.UtcNow,
                "CAT.Response",
                $"Response received ({rawResponse.Length} chars)",
                Band: null,
                rawResponse));

            _logger.LogDebug(
                "[VCTune] CAT RESPONSE | Raw={Raw}",
                rawResponse);
        }
        catch { /* never throw from diagnostics */ }
    }

    // ══════════════════════════════════════════════════════════════════════
    // State-machine transition logging
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Records a state-machine transition for one VC Tune receiver.
    /// No entry is written when the state is unchanged.
    /// </summary>
    /// <param name="previous">The snapshot before the transition.</param>
    /// <param name="current">The snapshot after the transition.</param>
    public void LogStateTransition(VCTuneStateSnapshot previous, VCTuneStateSnapshot current)
    {
        try
        {
            if (previous.State == current.State)
                return;

            var msg = $"{current.Band} state: {previous.State} → {current.State}";

            Append(new VCTuneDiagnosticEntry(
                current.Timestamp,
                "State",
                msg,
                current.Band,
                RawData: null));

            _logger.LogInformation(
                "[VCTune] State transition | Band={Band} {Previous} → {Current}",
                current.Band, previous.State, current.State);
        }
        catch { /* never throw from diagnostics */ }
    }

    // ══════════════════════════════════════════════════════════════════════
    // P5 / P6 update logging
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Records a change in the P5 coupling-indicator (meter) value for one
    /// receiver. No entry is written when the value is unchanged.
    /// </summary>
    /// <param name="band">The receiver whose meter value changed.</param>
    /// <param name="oldValue">The previous P5 value (0–255, or −1 if unset).</param>
    /// <param name="newValue">The new P5 value (0–255).</param>
    public void LogMeterUpdate(VCTuneBand band, int oldValue, int newValue)
    {
        try
        {
            if (oldValue == newValue)
                return;

            var pct = (int)Math.Round(newValue / 255.0 * 100);
            var msg = $"{band} meter: {oldValue} → {newValue} ({pct}%)";

            Append(new VCTuneDiagnosticEntry(
                DateTime.UtcNow,
                "Meter",
                msg,
                band,
                RawData: null));

            _logger.LogDebug(
                "[VCTune] Meter update | Band={Band} {Old} → {New} ({Pct}%)",
                band, oldValue, newValue, pct);
        }
        catch { /* never throw from diagnostics */ }
    }

    /// <summary>
    /// Records a change in the P6 availability value for one receiver.
    /// No entry is written when the value is unchanged.
    /// </summary>
    /// <param name="band">The receiver whose availability changed.</param>
    /// <param name="oldValue">The previous P6 raw byte (0 = not installed, 1 = available, 2 = out of range).</param>
    /// <param name="newValue">The new P6 raw byte.</param>
    public void LogAvailabilityUpdate(VCTuneBand band, int oldValue, int newValue)
    {
        try
        {
            if (oldValue == newValue)
                return;

            static string Describe(int v) => v switch
            {
                0 => "NotInstalled",
                1 => "Available",
                2 => "OutOfRange",
                _ => $"Unknown({v})",
            };

            var msg = $"{band} availability: {Describe(oldValue)} → {Describe(newValue)}";

            Append(new VCTuneDiagnosticEntry(
                DateTime.UtcNow,
                "Availability",
                msg,
                band,
                $"P6: {oldValue} → {newValue}"));

            _logger.LogInformation(
                "[VCTune] Availability update | Band={Band} {Old} → {New}",
                band, Describe(oldValue), Describe(newValue));
        }
        catch { /* never throw from diagnostics */ }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Error and fallback logging
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Records an error event with a structured error type and a human-readable
    /// message. This method never throws; all exceptions are swallowed.
    /// </summary>
    /// <param name="errorType">The category of the error.</param>
    /// <param name="message">A description of the error suitable for display.</param>
    /// <param name="band">
    /// The receiver the error relates to, or <see langword="null"/> if not
    /// receiver-specific.
    /// </param>
    public void LogError(VCTuneErrorType errorType, string message, VCTuneBand? band = null)
    {
        try
        {
            var category = $"Error.{errorType}";
            var entry = new VCTuneDiagnosticEntry(
                DateTime.UtcNow,
                category,
                message,
                band,
                RawData: null);

            Append(entry);

            _logger.LogWarning(
                "[VCTune] Error | Type={ErrorType} Band={Band} Message={Message}",
                errorType, band?.ToString() ?? "–", message);
        }
        catch { /* never throw from diagnostics */ }
    }

    /// <summary>
    /// Records the activation of an error-recovery fallback path (e.g. suppressing
    /// a command because the hardware is unavailable at the current frequency).
    /// </summary>
    /// <param name="band">The receiver for which the fallback was triggered.</param>
    /// <param name="errorType">
    /// The error condition that triggered the fallback.
    /// </param>
    public void LogFallbackActivation(VCTuneBand band, VCTuneErrorType errorType)
    {
        try
        {
            var msg = $"{band} fallback activated: {errorType}";

            Append(new VCTuneDiagnosticEntry(
                DateTime.UtcNow,
                "Fallback",
                msg,
                band,
                RawData: null));

            _logger.LogWarning(
                "[VCTune] Fallback activated | Band={Band} Reason={Reason}",
                band, errorType);
        }
        catch { /* never throw from diagnostics */ }
    }

    // ══════════════════════════════════════════════════════════════════════
    // History
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns a point-in-time snapshot of all retained diagnostic entries in
    /// chronological order (oldest first). The list is safe to enumerate without
    /// holding any lock; it reflects the state of the buffer at the moment of the
    /// call.
    /// </summary>
    public IReadOnlyList<VCTuneDiagnosticEntry> GetHistory() =>
        _history.ToArray();

    /// <summary>
    /// Clears the in-memory history buffer. Must be called whenever the radio
    /// disconnects so that stale entries from a previous session are not mixed
    /// with entries from the new session.
    /// </summary>
    public void ResetHistory()
    {
        try
        {
            _history.Clear();
            Interlocked.Exchange(ref _count, 0);
            _logger.LogDebug("[VCTune] Diagnostic history reset.");
        }
        catch { /* never throw from diagnostics */ }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Internal helpers
    // ══════════════════════════════════════════════════════════════════════

    // Appends an entry and evicts the oldest when MaxHistoryEntries is exceeded.
    private void Append(VCTuneDiagnosticEntry entry)
    {
        _history.Enqueue(entry);
        var current = Interlocked.Increment(ref _count);
        if (current > MaxHistoryEntries)
        {
            _history.TryDequeue(out _);
            Interlocked.Decrement(ref _count);
        }
    }
}
