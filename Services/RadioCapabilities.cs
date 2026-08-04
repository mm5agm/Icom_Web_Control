namespace Icom_Web_Control.Services;

// Capability lookup for per-model behaviour differences.
//
// Both models IWC offers (IC-7300 and IC-7300 MkII) are single-receiver with
// one antenna jack, so every method here currently returns the same answer for
// every configured model. That is deliberate: the lookups stay in one place so
// that adding a model which differs is a change here and nowhere else, and so
// the callers keep reading as "ask what this radio can do" rather than
// hard-coding an assumption at each site.
//
// See docs/decisions/0003-single-vs-dual-receiver-ui.md for the design
// rationale behind the single- vs dual-receiver UI split.
//
// Single-receiver is the safe default for unknown models — it applies the
// active/inactive UI restriction, which over-constrains an unfamiliar radio
// rather than letting it edit both VFOs' controls simultaneously when
// possibly only one set actually exists in the hardware.
public static class RadioCapabilities
{
    /// <summary>
    /// True when the radio has two independent physical receiver chains, each
    /// with its own separately addressable set of RX controls. No Icom model
    /// IWC supports does; the method exists so the assumption is stated once.
    /// </summary>
    public static bool IsDualReceiver(string radioModel) => false;

    /// <summary>
    /// True when the radio has a single physical receiver. VFO A and VFO B
    /// are frequency-and-mode memory slots through which the single
    /// receiver is steered; the radio stores per-VFO RX-control state but
    /// CI-V commands always address whichever VFO is currently active.
    /// </summary>
    public static bool IsSingleReceiver(string radioModel) => !IsDualReceiver(radioModel);

    /// <summary>
    /// True when the radio has more than one antenna jack and IWC should
    /// expose a per-VFO antenna selector. Showing a selector for a control
    /// that does nothing is just visual noise, so single-antenna radios get
    /// it hidden. The IC-7300 family has a single SO-239 ANT jack.
    /// </summary>
    public static bool HasAntennaSelector(string radioModel) => radioModel switch
    {
        "IC-7300" or "IC-7300MK2" => false,
        _                         => true
    };

    /// <summary>
    /// Returns the P1 character for a per-VFO command, given the user's (or
    /// voice command's) targeted receiver ("A" or "B"). On single-receiver
    /// radios always "0" -- the firmware hard-codes that position and rejects
    /// P1=1; on dual-receiver "0" for A, "1" for B.
    /// Shared by CatController (mouse/keyboard input) and IntentDispatcher
    /// (voice input) so the routing rule lives in exactly one place.
    /// </summary>
    public static string VfoP1(bool isSingleReceiver, string receiver) =>
        isSingleReceiver
            ? "0"
            : (receiver.Equals("B", StringComparison.OrdinalIgnoreCase) ? "1" : "0");

    /// <summary>
    /// Returns true if the per-VFO state write should target *B (vs *A) for
    /// a targeted receiver. On single-receiver radios the change always
    /// applies to whichever VFO is currently active -- the targeted panel
    /// is a hint, not an addressable target -- so this mirrors
    /// <paramref name="activeVfo"/> (0 = A, 1 = B). On dual-receiver radios
    /// the target wins outright.
    /// </summary>
    public static bool VfoIsB(bool isSingleReceiver, int activeVfo, string receiver) =>
        isSingleReceiver
            ? activeVfo == 1
            : receiver.Equals("B", StringComparison.OrdinalIgnoreCase);
}
