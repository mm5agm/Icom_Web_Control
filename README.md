# Icom Web Control

![Status](https://img.shields.io/badge/Status-released-brightgreen?style=flat-square)
![Licence](https://img.shields.io/badge/Licence-GPL--3.0-blue?style=flat-square)
![Latest release](https://img.shields.io/badge/Download-v1.1.0-brightgreen?style=flat-square)
![Downloads](https://img.shields.io/github/downloads/mm5agm/Icom_Web_Control/latest/Icom_Web_Control_Setup.exe?label=Downloads&style=flat-square)

> **v1.1.0 — current release.** IWC controls the **Icom IC-7300** and **IC-7300 MkII** end-to-end: frequency/mode, S-meter and Po/SWR/ALC, PTT, band/VFO/split, RF power, the RX DSP panel, the CI-V spectrum scope, ATU, voice control, a CW reader and sender, and a rigctld bridge for WSJT-X. The two radios speak near-identical CI-V and IWC drives both the same way — set the CI-V address to `94` for the original, `B6` for the MkII. Development and bench testing has been on a single IC-7300 MkII, and an owner of the original IC-7300 has confirmed v1.0.6 working on his radio. That is still only two radios between them, so if anything behaves unexpectedly — on either — please report it. I'm building Icom Web Control (**IWC**) as a sibling to my [Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control) (YWC) project, for Icom CI-V transceivers. The two are deliberately separate applications with separate repositories — YWC stays Yaesu-only, IWC stays Icom-only.
>
> **[⬇ Download the latest installer](https://github.com/mm5agm/Icom_Web_Control/releases/latest)**

## 📦 Releases and pre-releases

Some entries on the [releases page](https://github.com/mm5agm/Icom_Web_Control/releases) are marked **Pre-release**. Those are my ongoing work — bug fixes and improvements published as I finish them, rather than held back for weeks until there is enough for a numbered release.

They have had less testing than the full releases. If you want the quiet life, use the **⬇ Download** link above: GitHub's `latest` always points at the newest *full* release and skips pre-releases entirely.

If something has been biting you and a pre-release says it is fixed, or you want the newest features and do not mind the odd rough edge, go ahead and install it — and please tell me how you get on. That feedback is what turns a pre-release into a full one.

**Pre-releases never nag you — if you are on a full release.** IWC's in-app update banner will only ever offer you another full release, so if you don't go looking for a pre-release you will never be told one exists, and trying one is always a deliberate trip to the releases page. There is no setting that changes this and nothing to switch on by accident. **If you are already running a pre-release**, the banner does tell you when a newer pre-release — or the finished version — arrives, because being told is the point of testing one. (GitHub's own **Watch → Releases** notifications are a separate thing and *do* include pre-releases — see [Staying informed about updates](#staying-informed-about-updates) below.)

## ✨ Added since the last release

**v1.1.0 is the current release**, and it is the first with the Morse pair:

- **CW Reader** — a **CW Read** button opens a reader that listens to the radio's own USB audio and prints what it hears as text, with a small spectrum and a tuning phasor. **Reader Mode** sets the radio up in one press and puts everything back when you leave; **ZIN** zero-beats the signal; confirmed contacts go to a **Log QSO** form that appends to an ADIF file. The decoder is the one my Yaesu app uses, unchanged. [§18](USER_MANUAL.md#18-cw-reader).
- **CW Send** — type a line, press Enter, and the radio keys it over CI-V. **Stop** cuts in mid-piece; speed and break-in are the keyer's own. Not yet used on air by a CW operator. [§19](USER_MANUAL.md#19-cw-send).

Everything else in v1.1.0 is a fix, listed below. Full detail in the [v1.1.0 notes](#v110-2026-09-17).

Since v1.1.0, in the **v1.2.0-pre1** pre-release — a pre-release, so the ⬇ Download link above still gives you v1.1.0; get it from the [releases page](https://github.com/mm5agm/Icom_Web_Control/releases):

- **“Find my radio” sets IWC up for you.** Settings → Radio & CAT now lists the COM ports your PC has instead of asking you to type one, and a **Find my radio** button tries each of them at each speed until an Icom answers, then fills in the radio model, the port and the baud rate. It takes a couple of seconds, it tells you if a port is already in use by another program, and nothing is saved until you press Save Settings. A fresh install now starts with no port chosen rather than guessing at COM8. [§3](USER_MANUAL.md#3-first-time-setup).
- **Original IC-7300 owners get the radio’s menu steps on the page.** Choose **IC-7300** as your radio model and Settings shows the CI-V menu items to set and the order to set them in — including the one that catches everybody, where the baud item you can see is the wrong one. [§15.7](USER_MANUAL.md#157-i-have-the-original-ic-7300-not-the-mkii--what-do-i-need-to-set-differently).
- **A tuning step you can set.** The mouse wheel over the spectrum moved a fixed 1 kHz, which is no use for RTTY or for zero-beating CW. Each VFO now has its own step, **1 Hz to 1 MHz**, set four ways that are all the same number: the **Step** box on the spectrum panel, a **right-click on the spectrum**, **clicking the digit you want to move** on the frequency display, or the voice command *“set step size one hertz”*. [§5.4](USER_MANUAL.md#54-spectrum-display).
- **Testers get told about new test builds.** The update banner used to announce full releases only, to everybody, which left anyone on a pre-release hearing nothing until the finished version shipped. It now decides from the build you are running: on a full release nothing changes and there is no setting that could change it, and on a pre-release you are offered newer pre-releases, clearly labelled, with what is in them. [§5.1](USER_MANUAL.md#51-top-bar).
- **Put your memories in the order you want** - on the Memories page, click the **Label**, **Frequency** or **Mode** heading to sort the list, or use the row buttons to move one memory to the top, up or down, then Save. The order is the order of the Mem panel tiles and the order **Export to Radio** writes them, so it is how you choose which 99 go to the radio. [§8.1](USER_MANUAL.md#81-memories-editor).

## 🔧 Fixed since the last release

One line per fix, newest first, with the build that has it. A pre-release installs exactly like a release and carries everything before it; each one is written up under [Release notes](#release-notes). *Not yet in a build* means the fix is in the code and will be in the next pre-release or release.

| Fixed | Issue | In build |
|---|---|---|
| A first-time user could not tell whether a failed connection was the wrong COM port, the wrong baud rate, a missing USB driver or a cable, and the app offered no way to find out. Settings now lists the ports, **Find my radio** identifies the radio and fills the settings in, and the start-up banner links straight to it. | [#43](https://github.com/mm5agm/Icom_Web_Control/issues/43) | v1.2.0-pre1 |
| A fresh install arrived set to COM8 — the developer’s own port — so on anyone else’s PC it reported that a port was missing rather than that none had been chosen yet. The port now starts blank, and the message says so. | [#43](https://github.com/mm5agm/Icom_Web_Control/issues/43) | v1.2.0-pre1 |
| Loading a memory bank kept only label, frequency, mode and clarifier, and silently dropped the IF width, NB, NR, AGC, power and notes that Save to Mem had captured. Banks now load every field; a bank saved by an older version still has them in the file and gets them back on its next load. | — | v1.2.0-pre1 |
| Every Save on the Memories page renumbered the memories from 1, so after deleting or moving a row the Mem panel tiles below it pointed at the wrong memory until the panel happened to reload. Ids are now kept across Save. | — | v1.2.0-pre1 |
| The Settings page hid the Radio Model and Serial Port fields under a collapsed heading beneath a read-only summary, so a new user sent there by the "port not found" banner could not see anything to change. Radio & CAT now opens itself while the radio is not connected, the banner link lands on it, and the summary card has a Change button. | [#43](https://github.com/mm5agm/Icom_Web_Control/issues/43) | v1.2.0-pre1 |
| Clicking a signal on the spectrum knocked CW-R back to CW (and RTTY-R to RTTY). | — | v1.1.0 |
| The two-panel spectrum kept borrowing the receiver for VFO B's panel even with **VFO A** only chosen. | — | v1.1.0 |
| "Band up" said "successful" and did nothing if it landed during a cross-band peek. | — | v1.1.0 |
| VFO B's panel showed your own band's trace between peeks. | — | v1.1.0 |
| Voice control clipped the first syllable of every command; confidence now 0.7–0.97, and "set frequency to …" is no longer cut off after "frequency". | — | v1.1.0 |
| Windows resetting the microphone killed voice control until a restart. | — | v1.1.0 |
| The log file was 17 MB a day; now a couple of megabytes. | — | v1.1.0 |
| Draggable panels could be dragged off the screen. | — | v1.1.0 |

## What this is

IWC is a web-based control panel and panadapter for Icom transceivers, cloned from YWC and re-fitted for Icom's CI-V protocol. The plumbing YWC already got right — the real-time SignalR pipeline, the meter gauges, the spectrum display, the settings and rigctld bridge, and the voice control — is being kept; the Yaesu CAT layer is being replaced with a fresh CI-V layer behind a clean radio-control seam.

**Voice control is a first-class requirement, not an add-on** — several of the operators I build for are partially sighted, so hands-free operation matters from day one.

## First target radios: Icom IC-7300 and IC-7300 MkII

Both are single-receiver HF + 6 m (+ 4 m on European versions) transceivers that speak Icom's CI-V protocol and stream a spectrum scope **over CI-V** (`27 00`) — so no external SDR is needed, unlike some setups.

- **IC-7300 MkII** — the primary bench radio, tested end-to-end. CI-V over USB Type-C, default address `B6`, 475-point scope. Its rear LAN port offers a faster scope feed later.
- **IC-7300** (original) — near-identical over CI-V, default address `94`, CI-V over USB Type-B. **Confirmed working on v1.0.6** by Gerry M9YKD, who runs it on his own original 7300 ([#5](https://github.com/mm5agm/Icom_Web_Control/issues/5)). Set the CI-V address to `94` in Settings. One extra step the MkII does not need: the original only streams its band scope with **CI-V USB Port** set to **Unlink from [REMOTE]** and **CI-V USB Baud Rate** at **115200** — Settings warns you if you pick anything lower.

Other Icom CI-V radios (IC-705, IC-7610, IC-9700, …) share the same protocol family and will be added if there is a user that will do testing of the radio functions- not programming

## Status & plan

**`v1.1.0` is the current release**, and `v1.0.0` was the first — IWC controls an IC-7300 or IC-7300 MkII end-to-end (see the summary at the top), bench-tested against a single MkII and confirmed by an owner on an original IC-7300. The full build plan — how IWC is carved out of YWC, what's kept, what's rebuilt, and the phased CI-V roadmap — lives in [docs/design/iwc-clone-split-plan.md](docs/design/iwc-clone-split-plan.md).

## Release notes

### v1.2.0-pre1 (2026-09-22)

> **A pre-release.** Download `Icom_Web_Control_Setup.exe` from this release and install it over the top — nothing to uninstall first, and your settings, memories, calibration and voice phrases are all kept. The ⬇ Download link at the top of this README still points at v1.1.0, which is the current full release; this build is a deliberate trip to the releases page.
>
> **What most needs testing:** everything below about the original IC-7300 has been reasoned from the manuals and built against a MkII. I do not have an original to test on. If you have one, this is the build to try — and please say whether **Find my radio** identified it, and whether the menu checklist matched the menus actually in front of you.

- **Setting IWC up for the first time no longer asks you to know which COM port your radio is on.** Settings → Radio & CAT used to be a text box you typed `COM3` into, with no way of finding out what to type — and it arrived pre-filled with `COM8`, which is my bench radio's port and almost certainly not yours, so a new install reported the wrong port rather than no port. It is now a **list of the COM ports your PC has**, and beside it a **Find my radio** button: switch the radio on, press it, and IWC tries every port at every CI-V speed until an Icom answers, then fills in the radio model, the port and the baud rate for you. It takes about two seconds. If a port is already held by another program — WSJT-X, Log4OM, another CAT application — it says so by name rather than reporting the radio as missing, because two programs cannot share one serial port. Nothing is saved until you press **Save Settings**, so it is safe to press as often as you like. A fresh install now starts with no port chosen and says so in plain words. User Manual, [§3](USER_MANUAL.md#3-first-time-setup) and [§6.1](USER_MANUAL.md#61-radio-connection).

- **If you have the original IC-7300, Settings now shows you the radio's own menu steps, in the order the radio will let you do them.** Choose **IC-7300** as the radio model and a checklist appears on the page: unlink the CI-V USB port from [REMOTE], set the USB baud rate to 115200, check Echo Back and USB Serial Function, and leave USB SEND alone. **It includes the step that catches people.** The baud item you can see in that menu — plain *CI-V Baud Rate*, offering 4800, 9600, 19200 and Auto — belongs to the round [REMOTE] socket on the back panel and does nothing whatever for a USB connection. The one you actually want, *CI-V USB Baud Rate*, is hidden until the USB port is unlinked. A first-time user quite reasonably concluded his radio did not offer 115200 at all, and since the radio connects perfectly well at 19200, nothing looks wrong until you notice the band scope is permanently blank. User Manual, [§15.7](USER_MANUAL.md#157-i-have-the-original-ic-7300-not-the-mkii--what-do-i-need-to-set-differently).

- **The Settings page opens on Radio & CAT when the radio is not connected**, instead of showing a read-only summary with the fields you need folded away under a heading. The start-up banner links straight there, and the summary card has a **Change** button. Reported as [#43](https://github.com/mm5agm/Icom_Web_Control/issues/43), where someone sent to Settings by the "port not found" message found nothing there they could edit.

- **The mouse wheel over the spectrum now moves whatever you tell it to.** It was a fixed 1 kHz — far too coarse for RTTY, and useless for zero-beating a CW signal. Each VFO now has its own tuning step, anywhere from **1 Hz to 1 MHz**, and there are four ways to set it: the **Step** box on the spectrum panel's control bar, a **right-click on the spectrum** (keyboard-navigable, current step ticked), **clicking the digit you want to move** on the frequency display, or the **voice nudge step** — dropdown or spoken, and 1 Hz is now offered there too. They are all views of one number per VFO, so setting it one way changes it everywhere, and it is remembered across reloads. Ported from Yaesu Web Control, where Bruce VK2RT [asked for it](https://github.com/mm5agm/Yaesu_Web_Control/discussions/168); the step itself is shared code, so nothing had to be written twice. **A 1 Hz step is confirmed working on the IC-7300 MkII** — but the radio's own display reads only seven digits, so it will not show the change unless you turn on the rig's **1 Hz step Fine Tuning function** by touching and holding the Hz digits on its screen. IWC reads the frequency back from the radio, so its display is right either way ([§15.8](USER_MANUAL.md#158-i-set-a-1-hz-tuning-step-and-the-radios-display-doesnt-change)). User Manual, [§5.4](USER_MANUAL.md#54-spectrum-display).

- **Put your memories in the order you want.** On the Memories page, click the **Label**, **Frequency** or **Mode** heading to sort the list, or use the row buttons to move one memory to the top, up or down, then Save. That order is the order of the Mem panel tiles and the order **Export to Radio** writes them in, so it is how you choose which 99 go to the radio. User Manual, [§8.1](USER_MANUAL.md#81-memories-editor).

- **The update banner now tells you what is in the release.** It only ever said "a newer version is available", which is not enough to decide whether to interrupt what you are doing — and the GitHub releases page was no better, because the release itself carried nothing but a pointer back to this README. Both now carry the actual notes: the banner lists what changed, and the releases page shows the full entry. Pre-releases get their notes too — and, as of the next item, testers now see them.

- **If you are testing a pre-release, the banner now keeps you up to date.** Until now it only ever announced full releases, to anybody — which is right for an operator who never asked for a test build, but left testers stranded: install v1.2.0-pre1 and IWC would say nothing at all until v1.2.0 itself shipped, however many pre-releases fixed things in between. That is how somebody ends up reporting a fault that was fixed three builds ago. **The banner now decides from the version you are running.** On a full release nothing has changed and there is no setting to change it — you will never be offered a test build. On a pre-release you are told when a newer pre-release or the finished version appears, it is labelled **Pre-release** so you know what you are being offered, and it lists what has changed. Nightly `unstable-` builds are never offered. Shared code, so Yaesu Web Control gets the same.

- **Fixed: loading a memory bank quietly dropped most of each memory.** Only the label, frequency, mode and clarifier came back; the IF width, NB, NR, AGC, power and notes that **Save to Mem** had captured were lost on load. Banks now load every field, and a bank saved by an older version still has them in the file, so they come back on its next load.

- **Fixed: every Save on the Memories page renumbered the memories from 1.** After deleting or moving a row, the Mem panel tiles below it pointed at the wrong memory until the panel happened to reload. Ids are kept across Save now — which is also what makes the new ordering safe to use.

### v1.1.0 (2026-09-17)

> **Upgrading from v1.0.6?** Download `Icom_Web_Control_Setup.exe` and install it
> over the top — nothing to uninstall first, and your settings, memories,
> calibration and voice phrases are all kept. This is the first release with the
> **CW Reader** and **CW Send** panels, so the version number moves to 1.1.

- **IWC now reads Morse.** A **CW Read** button on the main panel opens a reader that listens to the radio's own USB audio and prints what it hears as text, with a small spectrum and a tuning phasor so you can see the tone you are decoding. **Reader Mode** sets the radio up for it in one press — CW, a 250 Hz filter, the audio peak filter and AGC MID — and puts every one of those back when you leave it. **ZIN** zero-beats the signal for you; the IC-7300 has no CI-V command for that, so IWC measures the tone and moves the VFO itself. Confirmed contacts go to a **Log QSO** form that appends to an ADIF file Log4OM and GridTracker already watch. The decoder is the one my Yaesu app uses, unchanged — only the radio wiring is new — and the manual has a frank section on what a machine can and cannot copy ([§18](USER_MANUAL.md#18-cw-reader)).

  *Two bench findings are worth recording. Icom's "CW" is the lower-sideband case whatever the display name says, which decided the sign of the zero-in correction; and the reader now asks the radio for its sidetone pitch when it starts rather than trusting a cached 600 Hz, because a pitch set on the front panel had it building the detector around the wrong tone.*

- **And sends it.** **CW Send** is the other half: a box you type into, and the radio keys what you typed over CI-V, in 30-character pieces on word boundaries. **Stop** cuts in even mid-piece. Speed and break-in are the keyer's own and follow the radio's knob and BK-IN button, and the panel says "sidetone only" when break-in is off so you know nothing is going out. Like the keyer panel it has not yet been used on air by a CW operator — see the note in [§19](USER_MANUAL.md#19-cw-send).

- **Clicking a signal on the spectrum no longer knocks CW-R back to CW.** The click follows the band plan's mode, which is right for taking DATA-U to phone or phone to CW, but it also reset a reverse-sideband choice you had made to dodge QRM. The CW and RTTY reverse pairs are now left alone.

- **The two-panel spectrum stopped hopping when you only wanted one.** With the pseudo-dual receiver's cross-band peek on, the radio kept borrowing the receiver every 15 seconds to refresh VFO B's panel even after you had chosen **VFO A** only. The peek now runs only while a browser is actually showing the VFO B panel, and each tab reports its own choice, so several open tabs cannot argue about it.

- **"Band up" said "successful" and did nothing.** If a voice or button command landed during one of those borrows, it was applied to VFO B and undone when the receiver was handed back. Commands are now held for the fraction of a second the borrow lasts and then applied to your own VFO.

- **VFO B's panel showed your own band's trace between peeks.** A sweep the radio had already started before it retuned was being credited to VFO B along with the real one, so B sat on a stale 20 m trace with one big spike until the next peek. Only sweeps that really are on B's band reach B now.

- **Voice control hears you properly again.** The microphone gain control was setting itself from the *previous* 50 ms of audio, so the first loud syllable of every command went in clipped and the recogniser's confidence sat at 0.2–0.5 — right on the reject threshold. Gain now follows a peak envelope with a limiter behind it; the same microphone now scores 0.7–0.97. Separately, "set frequency to …" was being cut off at the breath after "frequency" and returned as the shorter "status frequency"; the engine now waits 1.5 s for the rest.

- **Windows resetting the microphone no longer kills voice control until a restart.** Toggling Voice Clarity, a sample-rate change or re-plugging the mic all re-initialise the device underneath the app, and every PTT after that heard nothing. IWC now notices on the next press and reopens the microphone itself. The manual's voice section has been brought up to date at the same time — it still described the Windows default-device dance from before IWC had its own **Microphone** and **Announcement speaker** pickers.

- **The log file is a fraction of the size.** Three messages — the band recalculation on every poll and a pair logged for every status fetch from every open tab — were 86 % of a 120,000-line day. A day's log was running to 17 MB; it should now be a couple of megabytes of things that actually happened. Seven days are kept, as before.

- **Draggable panels can no longer be dragged off the screen.** The CW Reader and CW Send headers are clamped to the viewport, below the navigation bar and above the bottom edge, so the grab handle is always reachable.

- **Under the hood:** the shared core's 131 tests now run on every pull request into `develop`, on Linux, which is also what proves the shared code has nothing Windows-only in it. The scope trace was measured over 40-sweep averages on two bands to check it does not carry the permanent centre spike the Yaesu app had (it does not, and could not — these are the radio's own bins), and two comments that claimed the sweep centre equals the VFO were corrected: in SSB the radio centres on the passband, 1.5 kHz off the carrier.

### v1.0.6 (2026-08-16)

> **Upgrading from v1.0.5?** Download `Icom_Web_Control_Setup.exe` and install it
> over the top — there is nothing to uninstall first, and your settings, memories,
> calibration and voice phrases are all kept. **This is the release to be on:** it
> is the first one in which upgrading reliably gives you the version you just
> installed, for the reason set out in the second item below. If you have been
> running one of the v1.0.6 pre-releases, this is the same code plus the transmit
> meter change.

- **The transmit meters now dim when you are not transmitting.** Power, SWR, ALC, Compression and Id sit at zero the whole time you are receiving, which is five needles drawing the eye to nothing. They now fade to about half strength on receive and come up to full the instant you key the radio. **Dimmed rather than hidden, deliberately:** hiding them would collapse the meter row and jump everything below it up the page on every press of PTT and back down on release, which for anyone who has zoomed in to read the screen is a good deal worse than five idle dials — and it would move the gauges out from under a screen reader and the voice control. Nothing moves; the needles simply come forward when they have something to say. **Vd is left at full strength**, because unlike the other five the radio reports the PA supply voltage on receive too, so that gauge is genuinely live. User Manual, Section 5.2.

- **Upgrading could leave you running the previous version's screen code, and the fixes you had just installed would not be there.** After installing over the top, the spectrum could come up wrong — or not at all — until you pressed **Ctrl+F5**. Anyone who did not know to do that carried on seeing the old behaviour and would have reported the fix as not working, which is the worst way for a bug fix to fail. **What was happening:** the browser caches IWC's screen code, and with nothing telling it otherwise it decides for itself how long to trust its copy — for a file that was already a few weeks old when it was first cached, that can be a day or more, during which it will not even ask whether the file has changed. Installing a new version replaces the file on disk and has no effect whatever on that decision. So you got the new page with the old code behind it. **This release fixes it in two ways:** every screen module is now requested under a URL that carries the version number, so a new version always fetches fresh code; and IWC now tells the browser to check with it before reusing anything, which costs nothing on a machine talking to itself and closes the hole for good. **You should never need Ctrl+F5 again** — and if you are coming from v1.0.5 or an earlier v1.0.6 pre-release, this is the build where the Fixed-mode spectrum fix and the Firefox meter fix below will actually reach you.

- **IWC now tells you *which* build you are running.** v1.0.6-pre1, pre2 and pre3 all displayed themselves as plain "v1.0.6" — in the title bar, on the About page, in the system tray and in the diagnostics you would paste into a bug report. There was no way for you to tell three different builds apart, and no way for me to tell from a report which one you were on. Pre-releases now show their full label — **v1.0.6-pre4** rather than v1.0.6 — everywhere the version appears: title bar, About page, system tray and diagnostics. The update banner understands the difference too, which is why anyone left on a v1.0.6 pre-release is offered this release rather than being told they already have it.

- **In the scope's Fixed mode, the spectrum was drawn in the wrong place.** Fixed mode is the one where the band stays still and your marker moves across it — the right view for watching a whole segment. IWC got the segment wrong. Tuned to 14.075 with the radio showing 14.000–14.350, the trace, the frequency scale, the band-plan markers, the DX spots and the guard rails were all shifted by about 100 kHz: the FT8 pile-up sat under a scale reading 14.16, spots pointed at the wrong signals, and clicking on a signal tuned you 100 kHz away from it. **What was happening:** IWC worked out the visible window itself, as *your VFO plus and minus half the span*, and ignored the window the radio states in every sweep. In Centre mode those two are the same thing, so it looked right for six releases; in Fixed mode they are not, and everything drawn on top inherited the error. **Fixed:** the window now comes from the sweep the radio sends, so Fixed mode shows the segment the radio is showing and Centre mode is unchanged. User Manual, Section 5.4.

- **The scope's Centre/Fixed control is a proper labelled button now, and Fixed sticks.** Until now the only way to change scope mode from IWC was to click the small **CENT**/**FIX** badge on the canvas — undiscoverable, and unreachable with a keyboard or a screen reader. There is now a labelled **Centre / Fixed** button beside **Hold** that does the same job; the badge still works for anyone who had found it. The button shows the mode the radio actually reports, so changing it on the radio's own screen moves the button too. **And your choice is remembered:** IWC asserts a scope mode when it connects, which is why the radio kept reverting to Centre every time you started the app — whichever mode you last chose *in IWC* is now the one it asserts, across restarts. Choosing Fixed on the radio's front panel is deliberately not remembered, because IWC has no way to tell a mode you meant from one the radio happened to be left in. User Manual, Section 5.4.

- **The VFO marker is findable again.** The line showing where you are tuned was drawn in nearly the same blue as the trace itself, at one pixel and half transparent, and then the band markers, guard rails and DX spots were painted over the top of it — so on a quiet band it was faint and on a busy one it was gone. Its frequency label, meanwhile, was pinned to the middle of the panel regardless of where the marker was, which in Fixed mode meant a label reading 14.075 sitting above 14.17. The marker is now **magenta**, drawn last so nothing covers it, with the frequency label attached to the line where it belongs. Tune outside the window the radio is showing and the label parks against the nearer edge with an arrow, so you can see which way you have gone off screen. User Manual, Section 5.4.

- **The baud-rate advice was wrong for the MkII, and so was the reason given for it** ([#2](https://github.com/mm5agm/Icom_Web_Control/issues/2)). IWC's Settings page and five places in the manual told every owner to match this app's baud rate to a **CI-V USB Baud Rate** menu on the radio. **The IC-7300 MkII has no such menu.** Its *CI-V Baud Rate* setting governs the [REMOTE] jack only — the manual says so — and does nothing to the USB port, so anyone hunting for it on a MkII was looking for something that does not exist, and anyone who found the [REMOTE] setting and "matched" it changed nothing. On a MkII, IWC's own box is the only thing that sets the rate: **leave it at 19200**. On the **original IC-7300** the old advice still holds, and 115200 is still the rate to use. The related claim that a higher baud rate would speed the band scope up has also gone, because it is not true: measured on a MkII with the scope streaming, 19200 and 115200 both deliver **4.1 sweeps per second**, identical to three decimal places. That is the rate the radio delivers over its USB port whatever you set the baud rate to, and it is why the trace cannot show CW keying — a dot at 20 WPM lasts 60 ms and the display redraws every 240. User Manual, Sections 6.1 and 14.2.

- **You can now see how fast the band scope is actually arriving** ([#2](https://github.com/mm5agm/Icom_Web_Control/issues/2)). "The spectrum looks slow" was impossible to answer without guessing, because nothing in IWC reported the rate. The **Diagnostics** page now opens with a **Band scope delivery** panel showing the sweeps per second reaching IWC, measured over the last three seconds, alongside the number of sweeps assembled and the number discarded since the app started. **About 4 sweeps per second is normal over USB and there is nothing to fix if that is what you see** — the radio splits each sweep into eleven CI-V segments and paces them out about 21 ms apart, which fills roughly 89% of the gap between sweeps, and no baud rate changes that. A rate near zero with the scope switched on, or a discard count climbing alongside the assembled count, are the two readings that point at a real fault. If you are reporting a scope problem, this panel is the number to quote — it now reads across as **Band scope delivery 4.1 sweeps/sec**, with the figure beside its heading rather than pushed out to the far right of the panel where it was easy to read straight past. User Manual, Section 11.

- **The last two voice commands that did nothing now work.** "Band up" / "band down" and "filter wider" / "filter narrower" were recognised by the speech engine but never reached the radio — earlier builds spoke an "isn't available yet" message rather than pretending to have worked. Both are now wired: **band up/down** moves one amateur band and lands on that band's default frequency, skipping bands your band plan does not include and stopping at the ends rather than wrapping, and **filter wider/narrower** moves the IF passband one step along the radio's own ladder and speaks the new width — "Filter narrower, 2400 hertz". The step is the radio's own, not a fixed number of hertz: 50 Hz below 500 Hz, 100 Hz above it, 200 Hz in AM. Both say when they have reached an end — "Already on the highest band", "Already at the narrowest, 50 hertz" — instead of repeating the ordinary read-back, and FM, which has no adjustable width, says so. **Every voice command IWC recognises now reaches the radio.** User Manual, Section 17.

- **The Windows *Apps & features* entry no longer repeats its version number.** It read "Icom Web Control 1.0.5" in the name column *and* 1.0.5 again in the version column. The name is now just **Icom Web Control**, which is also what makes searching for it behave. Upgrading tidies the old entry away.

- **Meter needles no longer smear or fly off their scale in Firefox** ([#2](https://github.com/mm5agm/Icom_Web_Control/issues/2)). On transmit — the moment the meter readings move fastest — a needle could appear to detach from its gauge, be drawn well past the end of its arc, or leave a small red fragment sitting above or to the left of the gauge for a fraction of a second. It cleared itself and came back, over and over, for as long as you were transmitting. This only ever happened in **Firefox**; Edge, Chrome and the other Chromium browsers were unaffected, which is why it went unnoticed for six releases. **What was happening:** each needle was drawn as a 400 ms sweep to its new position, but fresh readings arrive from the radio every 150 ms or so, so a new sweep started before the last one had finished — up to three overlapping at once. Chromium throws the superseded frames away; Firefox keeps them, and the leftovers smear into a needle that looks like it has run off the end of the dial. **Fixed by removing the animation altogether:** needles now go straight to each new reading. At six to seven updates a second there was nothing left for the animation to smooth, so the meters move exactly as before — just without the wreckage. Nothing to change and nothing to set. User Manual, Section 14.2.

  *Found because Steve (Albergsteve) photographed what he was seeing and stuck with the report until it was reproducible. It needed a Firefox install and a script that churned the meter values as fast as a transmit does — on a quiet receive screen the gauges look perfect.*

- **Under the hood: the first pieces of code shared with Yaesu Web Control.** IWC and its sibling [Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control) had grown two copies of several files that do nothing radio-specific — the DX cluster spot record, the ADIF log parser, the meter calibration maths. Those now live in one shared component, [Radio Web Control Core](https://github.com/mm5agm/Radio_Web_Control_Core), which both apps build against, so a fix reaches both instead of one. **Nothing you can see changes** — the files chosen to move first were the ones already identical in both apps, precisely so behaviour could not change. It is mentioned here only because this is the first build to carry it: if IWC fails to start, or the DX spots, the log lookup or the meter scaling misbehave in this release and did not in v1.0.5, that is worth reporting.

### v1.0.5 (2026-08-09)

- **RX Bass and Treble, alongside the audio filter.** The **RX Filter** button on each VFO panel is now **RX Tone**, and its dialog carries the whole of the radio's *SET > Tone Control > RX* menu: the HPF and LPF edges it already had, plus the **Bass** and **Treble** shelves (−5 to +5, 0 flat) it did not. Everything applies to the VFO's current mode, as it does on the radio. Bass and Treble exist for SSB, AM and FM only — in CW and RTTY the radio has the filter edges but no shelves, and in the DATA modes it disables tone control altogether — so the dialog greys out whatever does not apply and says why. A **Flat** button returns both shelves to 0. User Manual, Section 5.7.

- **The Start Menu entry is now where you would look for it.** It was being filed inside a folder named **MM5AGM**, so it sorted under **M** and searching Start for "Icom" found nothing — the shortcut was there, but effectively hidden. It now sits at the top level as **Icom Web Control**, and upgrading removes the old entry so you are not left with two. The desktop shortcut is unchanged. Also tidied at the same time: the Windows *Apps & features* entry is now written to the 64-bit part of the registry, where a 64-bit-only application belongs. Nothing you can see changes — Windows always showed it — and upgrading cleans up the old one. User Manual, Section 2.

- **The Power slider now follows the radio.** Turn the **RF POWER** knob on the radio's front panel and IWC's slider and label move with it, within about a second. Until now IWC only ever read the power setting back immediately after *it* had set it, so a change made at the radio never reached the app: the slider stayed where IWC had last put it and could disagree with the radio indefinitely — including on a freshly opened page, which showed the last value that computer had sent rather than the radio's actual setting. Opening IWC on a second computer or in another tab now shows the truth straight away. The slider will not move under your hand while you are dragging it. User Manual, Section 5.3.

- **Twin PBT explains what it is for.** The dialog's two sliders had shipped since the first release with no manual entry at all, and its help text described only half of what they do. Moving them in *opposite* directions narrows the receive filter, which is what the old text said; moving them *together*, to the same value, slides the whole passband sideways without narrowing it — an **IF Shift**, and the quickest way to push a strong neighbouring signal out of the passband. The dialog now says both. The manual adds three things the dialog cannot: Twin PBT works in SSB, CW, RTTY and AM but does nothing in FM; the radio remembers the setting per band; and changing the **IF Width** resets both shifts to centre, so set the width first. User Manual, Section 5.7.

### v1.0.4 (2026-08-09)

**Two faults that could each stop IWC working completely, on a radio and a PC with nothing wrong with them.** Between them they account for every "it won't connect" report received since launch.

**1. A radio that answered perfectly could be reported as not responding** ([#2](https://github.com/mm5agm/Icom_Web_Control/issues/2), [#5](https://github.com/mm5agm/Icom_Web_Control/issues/5)).

If the radio's **CI-V USB Echo Back** setting was on, IWC could not connect to it at all. The banner said *"Serial port COMx opened, but the radio isn't responding — is it powered on?"* — while the radio sat there in perfect health, and other CAT software talked to it on that very same port.

- **What was happening.** With echo back on, the radio repeats the PC's own commands back to it. IWC asks "is anyone there?" as its first question, and that question alone is broadcast to every address so that both the IC-7300 and the MkII will answer it. The echo of that broadcast came back looking enough like a reply to be mistaken for one — and IWC read the radio's CI-V address out of it, which gave it *the PC's* address instead. From that moment every command was addressed to the computer rather than to the radio. Nothing answered, and IWC reported exactly what it saw.
- **Fixed properly:** IWC now recognises its own echo and ignores it, so **this release connects whether echo back is on or off** and there is nothing to set.
- **On v1.0.4-pre1 or earlier?** You do not have to upgrade to get working — switch **CI-V USB Echo Back** off at **MENU → SET → Connectors → CI-V** (the MkII has an (A) and a (B); switch both off) and the version you already have will connect.
- The **Radio not connected** banner now also links straight to the list of COM ports your PC has, so a wrong port number can be spotted without going near Device Manager. The User Manual explains all three banner messages and what each one actually means (Section 14.2).

*Found because Steve stuck with it for a fortnight and mentioned, almost in passing, that N1MM could see his radio on the same port — which proved the fault was mine and not his. Gerry reported the same symptom independently. Reproduced on the bench by switching that one radio setting on.*

**2. IWC no longer needs an internet connection.** If your shack PC is online, this half of the release changes nothing you can see. If it is not, this is the one that makes IWC work at all.

- **The control panel could open with no meters, no icons and no value that ever changed.** Up to v1.0.3 the page fetched three files — the meter-gauge library, the icon font, and the library that carries live updates from the radio to the browser — from public servers on the internet rather than from your own PC. Without the last of those, the page's script stopped before it started: you got the layout and the buttons, but dead gauges, a frequency that never moved, and empty boxes where the icons should be.
  - This went unnoticed for four releases because it is invisible on any PC that has ever been online. Browsers keep their own copy of those files for a year, so once they had arrived they kept working — including with the network unplugged. Only a PC that had **never** been online saw the failure, which is a perfectly ordinary way to run a shack computer and one I had not thought about.
  - All three files now ship inside IWC and are served from your own PC. **Nothing on the page is fetched from the internet any more.**
- **What still uses the internet, and what happens without it.** Exactly two things, both optional and both already well-behaved offline: the **DX cluster** spot feed, which is off until you switch it on and simply shows *Disconnected* if it cannot reach the server, and the **update check**, which stays silent rather than complaining. Everything else — the radio link, meters, spectrum, voice control, the rigctld bridge for WSJT-X — is local and always was.
- **Documentation:** the User Manual now says plainly what needs an internet connection and what does not (Section 1), and the symptom above is in Troubleshooting (Section 14.2) for anyone still on an older build.

*Thanks to Steve for the screenshot that showed the page stuck on "Transferring data from cdn.jsdelivr.net…" — without it this would still be sitting there.*

**Also in this release**

- **Switching the scope off now gives you the screen space back.** The **Scope** switch stopped the trace but left the whole panel — header, span buttons, Range / Speed / Bright bar, spectrum and waterfall — sitting there as dead space. It now collapses, and everything below it moves up. The switch stays on screen with a reminder beside it, so the way back is never hidden. User Manual, Section 5.4.
- **A blank spectrum on the original IC-7300 now explains itself.** The original IC-7300 — not the MkII — only sends band scope data when its **CI-V USB Port** is set to **Unlink from [REMOTE]** *and* its **CI-V USB Baud Rate** is **115200**. Below that it refuses the command outright. Since IWC's default is 19200 and the radio's own default is *Auto* (which follows the PC down), a stock IWC talking to a stock IC-7300 gave a spectrum panel that sat on *"Waiting for the radio's band scope…"* for ever, while frequency, mode and every meter worked perfectly — with nothing on screen to suggest why. IWC used to swallow that refusal into a log line.
  - The panel now says **"The radio refused to send scope data"** and prints the reason underneath, naming the exact radio menu and the rate to set it to. The status badge reads **Scope blocked**.
  - **Settings warns you before it happens.** Choose **IC-7300** with anything below 115200 and the warning appears next to the Baud Rate box as you pick it, rather than after the fact.
  - MkII owners are unaffected — the MkII has no such restriction and never sees either message. User Manual, Sections 3, 6.1, 6.3 and 14.2.
- **The S-meter history strip is now shown by default.** The 30-second strip-chart to the left of the S-meter has been in IWC since the first release, but it shipped hidden behind the **S-hist** button in the toolbar, so almost nobody found it. It is now on when you first run IWC. It plots the signal trace, its peak hold and the noise floor over the last half-minute, which makes QSB, interference spikes and a noise source switching on visible at a glance in a way the needle alone cannot show. The **S-hist** button still hides it, and **if you had already turned it off, it stays off** — the new default only applies where no choice had been made. User Manual, Section 5.2.

### v1.0.3 (2026-08-05)

**The first full release since v1.0.0.** If you have been staying on full releases — which is what the ⬇ Download link and the in-app update banner give you — this is a large step: everything in the **v1.0.1** and **v1.0.2** pre-release notes below arrives with it. That includes the two operator bug fixes ([#1](https://github.com/mm5agm/Icom_Web_Control/issues/1), [#2](https://github.com/mm5agm/Icom_Web_Control/issues/2)), eleven more voice-controlled functions, region-aware band edges, and working voice macros. Read those two sections as part of this release.

Two changes are new since v1.0.2:

- **The "discuss this" links now land somewhere you can actually post.** The links on the **About** page, in **Settings**, and in the documentation pointed at a Discussions category that is announcement-only, so anyone following them found a page with no way to reply. They now open the right category with a new post ready to write.
- **Calibrations sent in by different operators are now combined instead of overwriting each other.** When you use **✉ Email calibration to developer**, your measurements used to replace whatever the previous contributor sent, so the shipped table was only ever as good as the last person's radio. Every contribution is now kept separately and the shipped table is the **median** across all of them, which is what turns several operators' measurements into a meter that reads correctly on an average radio rather than on one particular one. If two operators disagree sharply on a point, that disagreement is now visible rather than silently averaged away.
  - Nothing about this is visible in your copy of IWC and nothing you do changes — it is entirely on the development side. It is here because it is the reason **sending your calibration in is worth doing**: with one contributor per model the table is one radio's opinion, and with several it starts being the radio's.

### v1.0.2 (2026-08-04) — pre-release

The first two bug reports from operators, a much larger voice vocabulary, band-plan accuracy, a visible start-up, and the voice macros finally reaching the radio.

**If you reported [#1](https://github.com/mm5agm/Icom_Web_Control/issues/1) or [#2](https://github.com/mm5agm/Icom_Web_Control/issues/2), this is the build to try.** Please still send your log file (`%APPDATA%\MM5AGM\Icom Web Control\logs\`) — #1 fixes the dead end you hit, but not necessarily the reason your radio sent no scope data in the first place, and the log is what will tell us that.

- **The spectrum panel could vanish completely, taking the Scope switch with it** ([#1](https://github.com/mm5agm/Icom_Web_Control/issues/1)). If no scope sweep ever reached IWC — the scope switched off, or a radio not sending scope data at all — the whole spectrum card stayed hidden, and since the **Scope** on/off switch sits inside that card there was no way to switch it back on. You got a page with no spectrum, no waterfall and no explanation. The card now appears whenever the radio is connected, and tells you which of those it is: **Scope off**, **Waiting for the radio's band scope…**, or the live trace.
  - The **Scope** switch now also remembers its real position across a restart, and corrects itself the moment a sweep proves the scope is running. Previously it could show *off* over a live trace, and the next click would then turn the scope on by sending "off".
  - The **About** page's diagnostics block now reports the band scope directly — on or off, how many sweeps have arrived, how many were dropped, how long ago the last one was. That line replaces `SDR device`, which was left over from the Yaesu app IWC was cloned from and always read "(none configured)". Paste it into any report about a missing spectrum.
- **"Icom Web Control is already running" is no longer a dead end** ([#2](https://github.com/mm5agm/Icom_Web_Control/issues/2)). A copy that failed to exit blocked every attempt to start the app behind an OK-only dialog — with no window to close, Task Manager was the only way out. The dialog now names the stuck process and offers to **open** the running copy in your browser, or to **close** it and start a fresh one.
  - And the app is much less likely to get into that state: if it is still alive ten seconds after being asked to shut down, it now exits anyway rather than lingering invisibly and blocking the next start.
- **Voice control learned eleven more controls.** Attenuator, AGC, RF gain, squelch, noise reduction, noise blanker, notch, APF, TX power, mic gain and speech processor can all be set by voice now — "r f gain seventy", "squelch zero", "noise reduction on", "t x power twenty five", "a g c fast". Levels use a fixed vocabulary (zero, ten, twenty, twenty five, thirty, forty, fifty, sixty, seventy, seventy five, eighty, ninety, one hundred / maximum / full); see §17 of the [User Manual](USER_MANUAL.md) for the full list.
  - Those same controls also gained the accessibility label keys they had been advertising. The **Accessibility Labels** page listed 28 entries that matched nothing on screen, so renaming them for a screen reader did nothing. They are now attached to the real controls.
  - **Your saved phrase pack resets to defaults again** (pack schema 8 → 9). The bundled **US English** pack is rebuilt to match — an older one would silently fall back to the UK defaults on import.
- **Voice macros now actually work.** "Noise reduction on", "noise blanker off" and "copy a to b" were recognised, spoke a cheerful "successful" back at you, and did nothing at all — the six default macros still carried the *Yaesu* command strings IWC inherited when it was cloned, which an Icom radio has no idea what to do with. They now send proper CI-V commands, and the confirmation you hear is the truth.
  - **"Fine step up" / "fine step down" are gone.** They were the Yaesu microphone UP/DN keys and CI-V has no equivalent. "Tune up" / "tune down" with the step set to 10 Hz does the same job.
  - **Your saved phrase pack is replaced by the new defaults the first time you run this version** — its custom commands were in the old radio's format and could not be sent. The old pack is kept in **Show version history** if you need to look at it.
  - **Custom Commands are now written as CI-V hex** — `16 40 01;` is noise reduction on, straight out of the CI-V table in the IC-7300 manual. So a custom command can now reach anything in the radio's command set, not just the handful IWC has buttons for. See §17.6 of the [User Manual](USER_MANUAL.md).
- **Band buttons and the toolbar now use *your* band plan.** IWC had two sets of band edges and only one of them knew about your region: the display used a single hard-coded worldwide table, so a UK operator on 3.900 MHz was told "80m" even though that is outside the Region 1 allocation. Both now resolve against the **Band Plan** you chose in Settings.
  - **Behaviour change to expect:** on frequencies outside your region's allocation the band button no longer lights up normally — it turns **red**, on whichever band you were nearest to. Region 1 operators will see this above 3.800, above 7.200, and below 1.810 MHz, where the old table let those frequencies pass as in-band. That is the correct answer, not a fault.
  - DX-spot filtering and the spectrum's band shading are unaffected — they deliberately use worldwide envelopes.
- **The Segment dropdown now shows where you actually are.** It tracks the live frequency wherever it comes from (spectrum click, front-panel knob, on-screen keyboard), and it is bounded by the band edges: previously the highest segment kept claiming your frequency however far above the band you tuned. Out of band it reads **OOB** on red, and selecting it can no longer tune the radio.
- **A proper start-up screen.** The "Initialising" overlay was never actually styled — it rendered as a strip at the top of a half-built page. It is now a full-screen panel that stays up until the spectrum appears, so the layout stops rearranging itself under you. A **Continue anyway** button is there if you ever need it.
- **The spectrum appears sooner** when you open IWC in a new tab or reload the page. The panel was waiting for a periodic status broadcast that could be up to 29 sweeps away; a browser that connects now gets one on the next sweep.
- **The update banner is now guaranteed to ignore pre-releases.** It already only asked GitHub for the newest *full* release, but that was an unwritten assumption; it is now documented, guarded in code, and written into the project's rules so it can't drift. Nothing changes for you — pre-releases stay something you go and fetch on purpose.
- **The scope panel no longer says "No SDR".** IWC has no SDR — the trace is the radio's own band scope over CI-V — but the panel inherited its status wording from the Yaesu app it was cloned from. The badge now reads **Scope off**, and the message drawn on an empty panel tells you to switch the scope on rather than to go and install a driver that was never part of this application.
- **All sixteen spectrum span buttons can now have their labels edited** on the Accessibility Labels page. The page was offering four entries that matched nothing on screen, while the buttons that do exist were unreachable — so a screen-reader user could not rename any of them. Both VFOs' ±2.5k through ±500k buttons are now listed.
- **The VC Tune controls are gone.** They were a Yaesu preselector that no Icom radio has; the buttons were already hidden on every supported model and the commands behind them had been removed when IWC was carved out. Nothing you could reach has changed.

### v1.0.1 (2026-08-01) — pre-release

Meter-calibration fixes. Nothing here changes rig control; if v1.0.0 is working for you there is no urgency.

- **"Reload From File" now genuinely re-reads the file.** It was reloading from memory, so a calibration changed by anything other than the page itself — a hand edit, or a second copy of IWC sharing the same file — stayed invisible until you restarted the app. Reverting an edit could appear to work while the old values were still in force.
- **The ✉ Email calibration button no longer claims success when the copy failed.** Opening your mail app takes focus off the browser, which can make the clipboard copy fail; that failure was being swallowed and reported as "copied". It now says so plainly and tells you to paste the JSON from the email body instead.
- **Removed the inherited Yaesu default calibration tables.** They were unreachable from the Settings dropdown, and a blank or unrecognised Radio Model could seed a new install with FTdx101MP calibration data. The generic fallback is now the IC-7300 MkII table.
- Corrected the on-page help and the manual, which claimed development builds save calibration to the installation folder. Calibration has always saved to `%APPDATA%\MM5AGM\Icom Web Control\calibration.user.json`.

### v1.0.0 (2026-08-01)

First public release. Carved from Yaesu Web Control and re-fitted for Icom CI-V, targeting the **IC-7300 MkII** (CI-V over USB, default address `B6`).

- **Rig control:** frequency and mode per VFO (incl. DATA modes), band / VFO / split, RF power set, ATU, and radio power on/off.
- **Metering:** S-meter plus Po / SWR / ALC gauges, polled at ~10 Hz.
- **Spectrum scope over CI-V** (`27 00`, 475 points) — no external SDR needed. Two-stage smoothing and an **auto noise-floor** display: the floor is tracked per sweep and pinned near the bottom, with a single **Range** slider that scales the peaks. Span, on/off, and CENT/FIX controls.
- **RX DSP panel**, Twin PBT, and RX/TX tone controls mapped to CI-V.
- **Voice control** (a first-class feature for partially sighted operators) — hands-free tuning, mode, status queries and TX, via Windows SAPI.
- **rigctld bridge** so WSJT-X / JTAlert / Log4OM can share the radio.
- Memory-channel banks carried over from YWC (read/save without writing to the transceiver unless you choose to).
- Tested on IC-7300 MkII firmware: Main CPU 1.02, Front CPU 1.01, DSP Program 1.01, DSP Data 1.00, FPGA 1.01.

Known limitations: single radio / single operator tested; tablet testing limited; the **CW keyer is present but untested** — I don't operate CW, so I can't verify it, and feedback from CW operators is especially welcome; installer is unsigned (see the security-warning notes below).

## 📝 CW Reader

The **CW Read** button on the main panel opens a reader that listens to the radio's receive audio and prints the Morse it hears as text. It needs no extra hardware — it decodes the same USB audio the radio already sends the PC, and it opens that device *shared*, so WSJT-X or your logger can keep using it at the same time. It never transmits.

I wrote my own decoder rather than reading the radio's. Neither the IC-7300 nor the FTdx101 will hand its decoded characters back over the control interface: both expose the decoder's *settings* and neither exposes its *output*, so a passthrough was never available on either brand.

**I want to be honest about what a Morse decoder is.** On a strong, clean, machine-sent signal it is close to perfect. On a marginal one it prints plausible-looking rubbish that looks exactly like good copy, and it has no idea which it is doing — in my own testing it reported full confidence on nearly six hundred characters of complete junk. Every design decision in this feature follows from that:

- **The status line says what it is actually seeing** — signal or no signal, the tone it is tracking against the pitch you are tuned to, the filter width, the search window, the SNR, and how far off pitch the station is. When more than one station is inside the filter, or the tone is breaking up rather than keying, it says so in words and prints nothing rather than guessing.
- **Nothing is corrected or hidden.** Colour marks what *looks like* QSO traffic — `CQ`, `DE`, `73`, signal reports, and callsigns — laid over exactly what was decoded.
- **The log form offers, it does not assert.** A field the radio or the clock knows (frequency, band, mode, time) is filled in. A field the *decoder* thinks it knows (callsign, report, name, QTH) starts empty, with suggestions beside it as buttons, each carrying the reason it was suggested — *follows DE*, *sent 3 times*. One click fills the box. That costs one click on a good decode and saves a wrong log entry on a bad one, because a callsign silently pre-filled from junk is worse than an empty box.

**Reader Mode** is one button that sets the radio up the way the decoder wants it — CW mode, a narrow IF filter (250 Hz by default), APF on, AGC MID — and puts your mode, width, APF and AGC back exactly as they were when you press Stop. This matters more than it sounds: on my own bench the FTdx101MP's built-in decoder could not read a signal I could copy by ear with the filters wide open, and was still poor at 600 Hz. What a decoder is fed matters more than how it decodes. Every width IWC offers is one the IC-7300 actually has, so what you pick is what you get. The saved settings live on the host rather than in the browser tab, so the restore still works after a page reload or from a second browser.

**ZIN** moves the VFO so the tone you are copying lands on your CW pitch. The IC-7300 has no CI-V command for this — that is a Yaesu feature — so IWC works the correction out from the decoded tone and sets the frequency itself. It refuses whenever it is not sure of the note or the correction is large, and says so, because both usually mean it has locked onto the wrong signal and moving the VFO would lose the station you were actually listening to.

Every session also writes a **timestamped transcript** to `CW Transcripts\` in the app data folder, written as the text arrives rather than held until you close the panel — the thing a transcript most needs to survive is a crash, and a crash happens while something is arriving. A session that decodes nothing leaves no file. Confirmed contacts append to a plain **ADIF** file, which Log4OM and GridTracker both pick up with nothing else to configure.

The decoder itself lives in [Radio_Web_Control_Core](https://github.com/mm5agm/Radio_Web_Control_Core), the shared library behind IWC and Yaesu Web Control, because a Morse decoder does not know what a radio is. Not one line of it had to change to serve a second brand of radio — the two apps read Morse identically, and differ only in how each radio is asked for a narrow filter.

Full details, including the status-line reference and troubleshooting, are in [USER_MANUAL.md §18 CW Reader](USER_MANUAL.md#18-cw-reader).

## ⌨️ CW Send

The **CW Send** button is the other half: a box you type into, and the radio keys what you typed. Read in one panel, answer in the other. Press Enter and the line goes out through the IC-7300's own text-keying command (CI-V `17`) in pieces of up to 30 characters; the character under the key is highlighted as it goes; **Stop** or Escape halts the radio at once, because — unlike a Yaesu — the IC-7300 has a real stop command. With break-in off the line plays to the sidetone and nothing is transmitted, and the panel says so. Like the CW Keyer panel, it has yet to be exercised on air by a CW operator, so feedback is welcome. Details in [USER_MANUAL.md §19 CW Send](USER_MANUAL.md#19-cw-send).

## 📖 Why This Application Exists

I'm building this for the same reason I built [Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control) — I can't see my radio's controls without a magnifying glass. On top of that, Icom has tucked a lot of the IC-7300's controls away on menu pages that you reach through the touchscreen, and I find getting to the page I want a bit hit-and-miss — sometimes I land on it, most times I don't. Could just be fat-finger syndrome. Accessibility is a first-class goal: support for partially sighted users through NVDA and Windows Narrator, and **voice control** so the radio can be operated hands-free. As a ham who uses WSJT-X, JTAlert, and Log4OM, I like being able to start them from the app rather than opening each one separately. It carries over YWC's memory channel banks and the functions to read and save them — you don't need to save to the transceiver unless you specifically want them on it (taking the radio to another location, for example). Please read the settings carefully, as you can overwrite the transceiver's memories.

Tablet testing has been limited — feedback from tablet users is particularly welcome.

## 🌱 Why Sponsorship Matters

I'm retired and maintain this project on a limited income, funding all development tools personally. AI-assisted coding has been invaluable for building features quickly, but it isn't free.

If this project has helped you, please consider sponsoring it. Even small contributions make a real difference and help keep the development tools running.

## 🎨 Skins and appearance

IWC's look is being built around swappable **skins** — a planned feature that will let you restyle the whole panel (layout, controls and colours), including a front-panel replica of the radio. If there's a skin or a look you'd like to see, I'd love to hear about it. Please post your suggestions in [Discussions](https://github.com/mm5agm/Icom_Web_Control/discussions/new?category=ideas) so other users can add to them, or send them to me directly.

## ⚠️ Windows Security Warnings on First Install

Because the installer is not code-signed, Windows and third-party antivirus tools will warn you before it runs. This is expected — the file is not malware. Follow these steps if you hit a block:

**Norton (or other antivirus) flags the file as malware**
This is a false positive caused by the executable being unsigned and newly downloaded. In Norton, go to **Security → History**, find the quarantined file, and choose **Restore & Exclude** (or the equivalent Allow option in your antivirus).

**Right-click → Properties → Unblock**
Windows marks files downloaded from the internet as untrusted. Before running the installer, right-click the file, choose **Properties**, and if you see an **Unblock** checkbox at the bottom of the General tab, tick it and click OK.

**"This app can't run on your PC" — Smart App Control**
If Smart App Control is enabled it will block unsigned apps entirely. Go to **Settings → Privacy & Security → Windows Security → App & Browser Control → Smart App Control** and switch it to **Off**, then restart your PC and try again.

The screenshot below shows the Smart App Control setting:

![Smart App Control Screenshot](pictures/SmartAppControl.png)

These are one-time steps — once the app is installed you won't see them again.

## Staying informed about updates

IWC carries over YWC's in-app update check — a banner appears the first time you run it after a new **full** release lands. If you are running a pre-release, the banner also tells you about newer pre-releases; if you are on a full release it never will. To also be notified outside the app, **any one of these works** (note that both of these fire for pre-releases whatever you are running):

- **GitHub release notifications** (most reliable, free, no spam):
  1. Make sure you're signed in to GitHub
  2. Visit https://github.com/mm5agm/Icom_Web_Control
  3. Click the **Watch** dropdown at the top-right of the page
  4. Choose **Custom** and tick only **Releases**
  5. Save — you'll get one email per release, and nothing in between

- **RSS / Atom feed** — if you use a feed reader (Feedly, NewsBlur, Inoreader, Thunderbird, etc.), subscribe to https://github.com/mm5agm/Icom_Web_Control/releases.atom — new releases appear in your reader without any account or email signup.

## ⚠️ Warning

**IWC keys your transmitter.** It has been developed and bench-tested against a single IC-7300 MkII by one operator, with one original IC-7300 confirmed working by its owner — yours may be the third radio it has ever seen. It uses only the official Icom CI-V commands as documented, but no software is bug-free and **you use it entirely at your own risk.**

- **Test into a dummy load first.** Confirm transmit, power, and mode behave before you put a signal on air.
- **Always verify transmit frequency, power level, and mode** before transmitting — do not assume the app and the radio agree.
- **If Voice control is enabled it keys the radio.** A misheard command can start a transmission; keep an eye (and ear) on the radio's TX state.
- **Memory operations can overwrite your transceiver's memories.** Read the Settings and the memory functions carefully before saving to the radio.

If something looks wrong, stop transmitting and check the radio's own display. Please report anything you find on the [GitHub issues page](https://github.com/mm5agm/Icom_Web_Control/issues).

## Licence

GPL-3.0, the same as YWC. See [LICENSE](LICENSE).

---

*Colin Campbell, MM5AGM*
