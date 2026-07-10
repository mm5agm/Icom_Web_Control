namespace Yaesu_Web_Control.Services;

// ══════════════════════════════════════════════════════════════════════════════
// Help section record
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Immutable record representing one section of VC Tune user documentation,
/// as returned by the methods of <see cref="VCTuneHelpProvider"/>.
/// </summary>
/// <param name="Title">
/// Short heading text suitable for use as a UI section header or tooltip title.
/// </param>
/// <param name="Content">
/// Body text for the section. Plain prose; may contain newlines for paragraph
/// breaks but contains no HTML or Markdown markup.
/// </param>
public sealed record VCTuneHelpSection(string Title, string Content);

// ══════════════════════════════════════════════════════════════════════════════
// Voice-command example record
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Immutable record describing a single voice-command example for VC Tune,
/// as returned by <see cref="VCTuneHelpProvider.GetVoiceExamples"/>.
/// </summary>
/// <param name="Phrase">
/// The spoken phrase exactly as the user should say it.
/// </param>
/// <param name="Intent">
/// The recogniser intent constant this phrase activates (e.g.
/// <see cref="Yaesu_Web_Control.Services.Voice.VCTuneRecognizer.IntentOn"/>).
/// </param>
/// <param name="Description">
/// A short note explaining what the command does and any applicable
/// capability constraints.
/// </param>
public sealed record VCTuneVoiceExample(string Phrase, string Intent, string Description);

// ══════════════════════════════════════════════════════════════════════════════
// Help provider
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Singleton provider of structured, user-facing documentation for the VC Tune
/// preselector subsystem.
/// <para>
/// All content is static — this class performs no CAT operations, no radio
/// queries, and no state mutations. It is safe to inject and call from any
/// thread or Razor page.
/// </para>
/// <para>
/// Register as a singleton in DI:
/// <c>services.AddSingleton&lt;VCTuneHelpProvider&gt;();</c>
/// </para>
/// </summary>
public sealed class VCTuneHelpProvider
{
    // ══════════════════════════════════════════════════════════════════════
    // Overview
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns a high-level overview of the VC Tune preselector feature.
    /// </summary>
    public VCTuneHelpSection GetOverview() => new(
        "What is VC Tune?",
        """
        VC Tune is a motor-driven variable capacitor preselector built into the FTdx101D and FTdx101MP transceivers. When enabled, it adds a narrow-bandpass filter ahead of the first mixer, improving rejection of strong out-of-band signals that might otherwise cause intermodulation or desensitisation.

        The preselector is controlled via the VT CAT command. Yaesu Web Control lets you switch it on or off, trigger an automatic tuning sweep (Default), step the capacitor manually one click at a time, and read back the current coupling-indicator reading (P5 meter) — all from the browser UI or by voice command.

        VC Tune is most effective on the lower HF bands (roughly 160 m to 20 m) where strong broadcast stations are most likely to cause interference. Above approximately 15 MHz its benefit diminishes and the radio may report it as unavailable at that frequency.
        """);

    // ══════════════════════════════════════════════════════════════════════
    // Installation
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns notes about which hardware configurations include VC Tune.
    /// </summary>
    public VCTuneHelpSection GetInstallationNotes() => new(
        "Hardware installation",
        """
        MAIN receiver VC Tune is fitted as standard in every FTdx101D and FTdx101MP. No user installation is required and it cannot be absent on a genuine unit.

        SUB receiver VC Tune (FTdx101MP only) requires the optional VRF-101 option board to be factory- or dealer-installed inside the radio. If the board is not present, Yaesu Web Control will show the SUB VC Tune controls as unavailable. The application learns whether the board is fitted by inspecting the P6 field of the first successful VT READ response after each connection — a P6 value of 1 or 2 confirms the board is present.

        If you have had the VRF-101 installed after initially connecting the radio to Yaesu Web Control, disconnect and reconnect the CAT port so the application can re-probe the hardware. The result is remembered across sessions once confirmed.
        """);

    // ══════════════════════════════════════════════════════════════════════
    // MAIN vs SUB
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns an explanation of the difference in behaviour between the MAIN
    /// and SUB receiver VC Tune circuits.
    /// </summary>
    public VCTuneHelpSection GetMainVsSubBehaviour() => new(
        "MAIN vs SUB receiver",
        """
        The FTdx101MP has two independent receivers. Each can have its own VC Tune preselector, but the two circuits operate independently and must be controlled separately.

        MAIN — always present. Commands sent to MAIN apply to VFO A. The MAIN preselector P5 meter reading reflects the coupling at the MAIN receiver's current operating frequency.

        SUB — optional (VRF-101 board required). Commands sent to SUB apply to VFO B. When the SUB board is not installed, all SUB VC Tune controls and voice commands are disabled automatically. The SUB P5 meter is shown only when the board is confirmed present.

        Voice commands default to the MAIN receiver unless you explicitly say "on the sub receiver" or "sub". For example: "VC Tune plus three on the sub receiver" steps the SUB preselector; plain "VC Tune plus three" steps MAIN.

        The FTdx101D has only one receiver and therefore only MAIN VC Tune. The SUB controls are hidden entirely for this model.
        """);

    // ══════════════════════════════════════════════════════════════════════
    // P5 meter
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns a plain-language explanation of the P5 coupling-indicator meter.
    /// </summary>
    public VCTuneHelpSection GetMeterExplanation() => new(
        "P5 coupling indicator (meter)",
        """
        Each VT READ response includes a P5 byte (0–255) that indicates how well the preselector variable capacitor is coupled to the current operating frequency. A higher value means better coupling — the capacitor is tuned closer to resonance for this frequency.

        Yaesu Web Control displays P5 as a percentage (0 % – 100 %) by default. You can change this to a raw 0–255 value, a bar graph only, or hide the meter entirely in Settings.

        To maximise received signal, tune for the highest P5 reading by stepping the preselector with the "+" and "−" buttons (or voice commands), or use the Default (auto-tune) command to let the radio sweep automatically.

        P5 is a live, session-only value. It is never saved to disk and resets to "–" whenever the radio disconnects, because the optimal capacitor position shifts with frequency.
        """);

    // ══════════════════════════════════════════════════════════════════════
    // P6 availability
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns a plain-language explanation of the P6 availability field and its
    /// three possible values.
    /// </summary>
    public VCTuneHelpSection GetAvailabilityExplanation() => new(
        "P6 availability indicator",
        """
        Every VT READ response includes a P6 byte that reports the hardware availability of the preselector for that receiver:

        0 — Not installed. The VC Tune option board is absent (SUB only — MAIN is always present on supported models). All VC Tune commands for this receiver are blocked.

        1 — Available. The preselector is installed and can be used at the current operating frequency. ON, OFF, Default, Step, and Center commands are all permitted.

        2 — Temporarily unavailable. The preselector is installed but cannot be engaged right now. This is a transient condition — typical causes are a roofing filter transition, wideband receive mode, or the preselector settling after a rapid frequency change. The VRF-101 covers the full HF range (1.8–30 MHz) and is not restricted to specific bands. The controls will re-enable automatically on the next VT READ once the radio returns P6 = 1. If P6 = 2 persists, check that the radio is in a normal HF receive mode.

        Yaesu Web Control checks the live P6 value after every READ and will not send commands that the hardware would reject. If P6 changes (for example, because you change bands), the controls update automatically on the next READ cycle.
        """);

    // ══════════════════════════════════════════════════════════════════════
    // Commands
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns one <see cref="VCTuneHelpSection"/> per VC Tune command, describing
    /// its purpose, when to use it, and any restrictions.
    /// </summary>
    public IReadOnlyList<VCTuneHelpSection> GetCommandsHelp() =>
    [
        new("ON — engage the preselector",
            """
            Switches the variable-capacitor preselector into the signal path. Use ON when you want the benefit of the bandpass filtering, typically on the lower HF bands. If the preselector was previously OFF and the capacitor position has drifted, the P5 coupling indicator may initially be low; run Default or Step to retune.

            ON is blocked when P6 = 0 (not installed) or P6 = 2 (out of frequency range). The button and voice command will be disabled automatically in those cases.
            """),

        new("OFF — disengage the preselector",
            """
            Bypasses the variable-capacitor preselector, restoring the flat receive path. Use OFF when operating above the preselector's effective range, when performing antenna tests, or when the preselector is causing unexpected signal degradation.

            The capacitor mechanism retains its last position while OFF; switching back ON will resume from where you left off.
            """),

        new("Default — automatic sweep",
            """
            Commands the radio to perform a full motor sweep of the variable capacitor and stop at the position that gives the highest P5 coupling reading. This is the quickest way to optimise the preselector for a new frequency.

            The sweep takes several seconds. Received signals may peak noticeably during the sweep as the capacitor passes through resonance. It is safe to continue transmitting on other radios during a sweep on a receive-only receiver.

            Default is only available when P6 = 1 (available).
            """),

        new("Step — manual adjustment",
            """
            Moves the variable capacitor one or more clicks in the specified direction (+ for more capacitance / lower resonant frequency; − for less capacitance / higher resonant frequency). Step amounts range from 0 (finest, one click) to 9 (coarsest, nine clicks).

            Use Step for fine optimisation after Default, or when operating on a frequency where the automatic sweep overshoots. Watch the P5 meter and step in the direction that increases the reading.

            Step is only available when the preselector is ON and P6 = 1.
            """),

        new("Center — reset to midpoint",
            """
            Drives the variable capacitor motor to the mechanical centre of its travel range. Use Center as a neutral starting position before a Default sweep, or to recover from a situation where the capacitor is stuck near an end stop.

            Center is available whenever P6 = 1, regardless of ON/OFF state.
            """),
    ];

    // ══════════════════════════════════════════════════════════════════════
    // Voice examples
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the list of supported voice-command examples for VC Tune, including
    /// the recognised phrase, the intent it activates, and a short description.
    /// </summary>
    public IReadOnlyList<VCTuneVoiceExample> GetVoiceExamples() =>
    [
        new(
            "VC Tune on",
            Voice.VCTuneRecognizer.IntentOn,
            "Switches MAIN VC Tune ON. Requires P6 = 1 (available at the current frequency)."),

        new(
            "VC Tune off",
            Voice.VCTuneRecognizer.IntentOff,
            "Switches MAIN VC Tune OFF. Always permitted when MAIN is installed."),

        new(
            "VC Tune default",
            Voice.VCTuneRecognizer.IntentDefault,
            "Triggers an automatic capacitor sweep on MAIN. The radio finds the best position and stops."),

        new(
            "VC Tune plus three",
            Voice.VCTuneRecognizer.IntentStep,
            "Steps the MAIN preselector capacitor forward (more capacitance) by 3 clicks. Replace 'plus' with 'minus' to step back; replace 'three' with any digit zero to nine."),

        new(
            "VC Tune minus one on the sub receiver",
            Voice.VCTuneRecognizer.IntentStep,
            "Steps the SUB preselector capacitor back (less capacitance) by 1 click. The phrase 'on the sub receiver' or just 'sub' routes the command to the SUB board. Requires the VRF-101 option and P6 = 1 for the SUB receiver."),

        new(
            "Read VC Tune status",
            Voice.VCTuneRecognizer.IntentReadStatus,
            "Sends a VT READ command to the radio and refreshes the P5 meter and P6 availability indicator for the MAIN receiver. Add 'sub' to read the SUB receiver instead."),
    ];

    // ══════════════════════════════════════════════════════════════════════
    // Troubleshooting
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns one <see cref="VCTuneHelpSection"/> per troubleshooting topic,
    /// each with a symptom heading and recommended user actions.
    /// </summary>
    public IReadOnlyList<VCTuneHelpSection> GetTroubleshootingGuide() =>
    [
        new("VC Tune controls are disabled — 'CAT not supported on this hardware'",
            """
            Symptom: All VC Tune controls are disabled and the panel shows a message stating that VC Tune CAT control is not available on this hardware revision.

            Cause: The radio's ID; response identified a hardware revision that does not expose VC Tune control over CAT. The physical preselector is present and works normally from the front panel buttons, but this firmware/hardware combination does not respond to VT CAT commands.

            Confirmed affected hardware: FTdx101MP ID0682 (MAIN V01-28 / DISPLAY V01-51 / DSP V01-20 — fully up to date). Sending VT VCT frames to this hardware returns '?;?;' (CAT error).

            What you can still do:
            • Operate VC Tune directly from the large VC TUNE button on the radio's front panel.
            • The radio's internal auto-tune and step functions work normally from the front panel.
            • All other CAT-controlled features (frequency, mode, power, S-meter, SDR) continue to work normally.

            This is not a software bug and will not be fixed by a software update — the limitation is in the radio firmware and hardware.
            """),

        new("VC Tune shows 'Not installed' (P6 = 0)",
            """
            Symptom: The VC Tune panel shows a 'Not installed' warning and all controls are disabled.

            Cause: The radio's VT READ response returned P6 = 0 for this receiver. For MAIN, this should never happen on a genuine FTdx101D or FTdx101MP — check that the selected radio model in Settings matches your actual hardware. For SUB, it means the VRF-101 option board is absent or not recognised.

            Recommended actions:
            • Confirm the radio model in Settings → Radio → Model.
            • For SUB: verify the VRF-101 board is correctly seated (dealer service may be required).
            • Disconnect and reconnect the CAT port to trigger a fresh hardware probe.
            • Check the Diagnostics page for recent VT READ responses and compare the P6 value in the raw response.
            """),

        new("VC Tune shows 'Temporarily unavailable' (P6 = 2)",
            """
            Symptom: The VC Tune ON button and Step commands are greyed out. The panel shows a 'Temporarily unavailable' warning.

            Cause: The radio's VT READ response returned P6 = 2. This is a transient condition — common causes are a roofing filter transition, wideband receive mode, or the preselector settling after a rapid frequency change. The VRF-101 covers the full HF range (1.8–30 MHz); P6 = 2 is not a band restriction.

            Recommended actions:
            • Wait a moment and retry — P6 = 2 normally clears within a second or two as the radio settles.
            • If P6 = 2 persists, verify the radio is in a standard HF receive mode (not wideband or bypass mode).
            • The controls will re-enable automatically on the next VT READ once P6 returns to 1.
            • No repair is needed — this is a temporary hardware state, not a fault.
            """),

        new("P5 meter is not updating",
            """
            Symptom: The coupling-indicator value stays at '–' or a stale number even after clicking the Read button or issuing a voice Read command.

            Recommended actions:
            • Check that the CAT port is connected (green indicator in the status bar).
            • Open the Diagnostics page and verify that VT READ commands are being sent and a response is being received.
            • Confirm that VC Tune is ON — P5 has limited meaning while the preselector is bypassed.
            • If using the SDR spectrum display, ensure the VT READ poll is not being blocked by heavy CAT traffic from another application (e.g. WSJT-X). Disconnect other CAT clients temporarily to test.
            """),

        new("Commands are being rejected",
            """
            Symptom: Pressing ON, Step, or Default has no effect. The Diagnostics page logs 'CommandRejected' errors.

            Recommended actions:
            • Verify P6 = 1 is shown in the control panel. Commands are silently blocked when P6 = 0 or P6 = 2.
            • Check that no other application is sending conflicting CAT commands at the same time.
            • Inspect the raw VT READ response on the Diagnostics page — the P2 field should reflect the last command sent.
            • If the radio is in transmit (PTT active), some CAT commands may be queued or refused. Wait for the radio to return to receive.
            """),

        new("Voice commands are not being recognised",
            """
            Symptom: Saying "VC Tune on" (or similar) has no effect; no intent is logged in Diagnostics.

            Recommended actions:
            • Confirm that voice control is enabled in Settings → Voice Control → Enable.
            • Check that a microphone is selected and the input level is not muted in Windows Sound settings.
            • Speak clearly and use the exact phrases listed in the Voice Command Examples section. Variations in phrasing (e.g. "turn on VC Tune") are not currently supported.
            • Check the Voice Diagnostics section on the Diagnostics page for rejected grammar entries — a SAPI grammar compilation failure at startup would be listed there.
            • If recognition was working previously and stopped, try restarting Yaesu Web Control.
            """),

        new("Fallback activation logged in Diagnostics",
            """
            Symptom: The Diagnostics page shows 'Fallback activated' entries for VC Tune.

            Cause: A command was intercepted and suppressed by the application's safety layer before it could be sent to the radio. This happens when the application detects that the command would be refused by the hardware (e.g. ON while P6 = 2) and blocks it to avoid polluting the CAT bus with invalid requests.

            Recommended actions:
            • A fallback is not an error — it is the application protecting you from a rejected command.
            • Read the fallback reason in the Diagnostics entry (e.g. UnavailableFrequency, NotInstalled).
            • Address the underlying condition (change frequency, confirm hardware) and try again.
            • If fallbacks appear unexpectedly when the preselector should be available, trigger a manual Read to refresh the P6 state.
            """),
    ];
}
