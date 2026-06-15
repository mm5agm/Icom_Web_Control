namespace Yaesu_Web_Control.Services;

// Capability lookup for per-model behaviour differences. Currently used only
// for the dual- vs single-receiver UI decision (active-VFO greying-out and
// PTT placement on single-receiver radios), but the pattern scales: future
// per-model variations (4 m band availability, max TX power, roofing-filter
// availability) can hang off this same static class.
//
// See docs/decisions/0003-single-vs-dual-receiver-ui.md for the design
// rationale and Jacek SP3L's #34 report that drove it.
//
// Single-receiver is the safe default for unknown models — it applies the
// active/inactive UI restriction, which over-constrains an unfamiliar radio
// rather than letting it edit both VFOs' controls simultaneously when
// possibly only one set actually exists in the hardware.
public static class RadioCapabilities
{
    /// <summary>
    /// True when the radio has two independent physical receiver chains
    /// (MAIN + SUB), each with its own set of RX controls addressable
    /// separately via CAT (P1=0 for MAIN, P1=1 for SUB on commands like
    /// PA, GT, RA, RG, NR, etc.).
    /// </summary>
    public static bool IsDualReceiver(string radioModel) => radioModel switch
    {
        "FTdx101MP" or "FTdx101D" => true,
        _ => false
    };

    /// <summary>
    /// True when the radio has a single physical receiver. VFO A and VFO B
    /// are frequency-and-mode memory slots through which the single
    /// receiver is steered; the radio stores per-VFO RX-control state but
    /// CAT commands always address whichever VFO is currently active
    /// (no P1 band parameter on the receiver-side commands).
    /// </summary>
    public static bool IsSingleReceiver(string radioModel) => !IsDualReceiver(radioModel);
}
