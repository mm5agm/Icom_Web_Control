# Icom Web Control

![Status](https://img.shields.io/badge/Status-released-brightgreen?style=flat-square)
![Licence](https://img.shields.io/badge/Licence-GPL--3.0-blue?style=flat-square)
![Latest release](https://img.shields.io/badge/Download-v1.4.0-brightgreen?style=flat-square)
![Downloads](https://img.shields.io/github/downloads/mm5agm/Icom_Web_Control/latest/Icom_Web_Control_Setup.exe?label=Downloads&style=flat-square)

> **v1.4.0 — current release.** IWC controls the **Icom IC-7300** and **IC-7300 MkII** end-to-end: frequency/mode, S-meter and Po/SWR/ALC, PTT, band/VFO/split, RF power, the RX DSP panel, the CI-V spectrum scope, ATU, voice control, a CW reader and sender, a RTTY tuning scope, and a rigctld bridge for WSJT-X. The two radios speak near-identical CI-V and IWC drives both the same way — set the CI-V address to `94` for the original, `B6` for the MkII. Development and bench testing has been on a single IC-7300 MkII, and an owner of the original IC-7300 has confirmed v1.0.6 working on his radio. That is still only two radios between them, so if anything behaves unexpectedly — on either — please report it. I'm building Icom Web Control (**IWC**) as a sibling to my [Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control) (YWC) project, for Icom CI-V transceivers. The two are deliberately separate applications with separate repositories — YWC stays Yaesu-only, IWC stays Icom-only.
>
> **[⬇ Download the latest installer](https://github.com/mm5agm/Icom_Web_Control/releases/latest)**

## 📦 Releases and pre-releases

Some entries on the [releases page](https://github.com/mm5agm/Icom_Web_Control/releases) are marked **Pre-release**. Those are my ongoing work — bug fixes and improvements published as I finish them, rather than held back for weeks until there is enough for a numbered release.

They have had less testing than the full releases. If you want the quiet life, use the **⬇ Download** link above: GitHub's `latest` always points at the newest *full* release and skips pre-releases entirely.

If something has been biting you and a pre-release says it is fixed, or you want the newest features and do not mind the odd rough edge, go ahead and install it — and please tell me how you get on. That feedback is what turns a pre-release into a full one.

**Pre-releases never nag you — if you are on a full release.** IWC's in-app update banner will only ever offer you another full release, so if you don't go looking for a pre-release you will never be told one exists, and trying one is always a deliberate trip to the releases page. There is no setting that changes this and nothing to switch on by accident. **If you are already running a pre-release**, the banner does tell you when a newer pre-release — or the finished version — arrives, because being told is the point of testing one. (GitHub's own **Watch → Releases** notifications are a separate thing and *do* include pre-releases — see [Staying informed about updates](#staying-informed-about-updates) below.)

## ✨ Added since the last release

**Not yet in a build** — in the code, and will be in the next pre-release. The first five are RTTY, and together they turn the tuner into a receive station:

- **The RTTY panel decodes now.** What was the RTTY Tuner is one **RTTY** panel in two columns: the crossed-ellipse scope and its controls on the right, a **Decode** pane printing the text on the left, and a **Send** box underneath showing where sending will go — **Send is greyed out and does not transmit**, and [§20.6](USER_MANUAL.md#206-sending) says why it is harder than it looks. The decoder has no Mark, Shift, Speed or Rev of its own: it uses the ones in the Tune column beside it, so getting the cross upright is the whole of setting it up, and switching the radio between RTTY and RTTY-R needs nothing pressed. [§20.5](USER_MANUAL.md#205-decoding-the-text).
- **Auto works out the shift and the speed for you.** Press **Auto** and it listens for four seconds and reports what it heard — and it reports what it *measured*, not the nearest entry on a list. The radio's own menu offers three shifts and its decoder is fixed at 45.45 baud; that is the radio's limit, not this one's, so a weather station on 450 Hz shift at 50 baud is ordinary listening here. It refuses rather than guesses when the two halves of what it heard do not agree — and it now also refuses when only one of the two tones was in range at all. That case used to come back *confident*: with the other tone outside what the measurement can see, it measured the surviving tone against a piece of its own skirt, fitted a speed to it beautifully, and reported a true 450 Hz shift at 50 baud as 115 Hz at 100 baud with a score of 0.92 out of 1 — overwriting the shift and speed you had. It now says which case it is and writes nothing, while keeping the dial nudge that usually makes a second press work. [§20.3](USER_MANUAL.md#203-auto-working-out-what-you-are-listening-to).
- **The panel sets the radio up for RTTY, and puts it back.** Open it in CW or AM and it switches to RTTY-L, remembering the mode and filter you were in and restoring both when you close it — the same bargain **Reader Mode** makes for CW. It leaves DATA and SSB alone, because there the tones are your software's and the radio is deliberately a plain SSB set. The filter is only ever **widened**, never narrowed — and how wide it asks for is now measured rather than assumed. The usual RTTY rule of thumb is the shift plus a little, but this radio centres its RTTY passband on the **mark** tone, so the space tone sits a whole shift away from the middle of the filter and is the first thing lost: on the bench a 550 Hz filter put the space tone of a 450 Hz shift signal 33 dB down and decoded *nothing*, where 1200 Hz read it cleanly. It asks for about twice the shift now, and if even the widest filter cannot reach both tones it says so and asks you to move the dial, rather than reporting a widen that did not help. [§20.9](USER_MANUAL.md#209-what-the-tuner-changes-on-your-radio), [§6.8](USER_MANUAL.md#68-rtty-tuner).
- **A confident Auto tunes the signal in.** Having measured where the tones actually are, it moves the VFO so the mark lands where the tuner is listening, and says in the status line that it moved and by how much. It will not move on a weak answer, will not move less than 25 Hz, and will not move the mark outside the IF filter you are listening through — never less than 500 Hz, whatever the filter — so it corrects mistuning and never goes hunting. [§20.3](USER_MANUAL.md#203-auto-working-out-what-you-are-listening-to).
- **RTTY Contest Mode.** A switch on the Settings page that leaves the RTTY panel as just the tuner — the tone controls, the crossed figure and its readouts, in a narrow panel, with **Decode** and **Send** left out. For contesting, where your logger already has a decoder and the one thing it cannot draw is the figure. It also holds the panel still: the two lines under the scope keep their height instead of reflowing it under your mouse every time the status changes. The same switch is in Yaesu Web Control. [§20.10](USER_MANUAL.md#2010-contest-mode-the-tuner-on-its-own).
- **The Experimental UI.** **Experimental UI** in the top bar opens the main page as a workspace of panels — Toolbar, Clarifier, Meters, Spectrum, VFO A and VFO B — that you can drag, dock side by side or as tabs, resize, hide, and pop out into windows of their own, with named layouts saved in IWC. It is the Classic page's own controls rearranged, not a copy, so everything behaves as it does there; the Classic page is unchanged and is still where IWC opens. The workspace is Fabio Valente's (CR7CDC), from Yaesu Web Control. [§5.19](USER_MANUAL.md#519-experimental-ui).

**v1.4.0 is the current release.** Its headline is pop-out windows — the panels on a second monitor:

- **Pop-out windows for a second monitor.** The CW Reader, CW Send, RTTY Tuner, Twin PBT and DX Spots list can each open in a window of their own with their **↗** button, at any size and on any screen; **Reattach** puts them back, at the size you left the window. A pop-out window keeps IWC running on its own, as the main page does. [§5.18](USER_MANUAL.md#518-pop-out-windows).
- **The CW Reader and RTTY Tuner close themselves when the mode leaves them behind** — the reader outside CW, the tuner outside RTTY, DATA and SSB — once the mode has held for two seconds, so a band change passing through another mode does not close them. A pop-out window pauses instead of closing. [§18](USER_MANUAL.md#18-cw-reader), [§20](USER_MANUAL.md#20-rtty).
- **The RTTY Tuner can be resized**, and **Tones only** hides everything but the scope so it can be made very small. [§20](USER_MANUAL.md#20-rtty).
- **CW Send won't send in the wrong mode.** Outside CW it says so, keeps the line in the box, and offers to switch the transmit VFO to CW. [§19.1](USER_MANUAL.md#191-sending-a-line).
- **The spectrum panel's height is yours to set** — a **Height** setting from 50% to 300% of the old fixed 280 pixels, and a grip along the bottom of the panel you can drag or drive from the keyboard, remembered per VFO. [§5.4](USER_MANUAL.md#54-spectrum-display).

Also in this release, and described under the [v1.4.0 notes](#v140-2026-10-07): **movable windows now sit on top of the page** with the one you last clicked in front, the **DX Spots list opens where you can see it**, the **CW Reader's Tune switch is remembered**, the **Twin PBT dialog re-reads the radio** when you come back to it, and a page left open and untouched **no longer has the app shut down underneath it**.

Previous releases: the **RTTY Tuner** and the self-recovering band scope arrived in v1.3.0 — full detail in the [v1.3.0 notes](RELEASE_HISTORY.md#v130-2026-09-25); **Find my radio**, the original IC-7300 menu checklist, the settable tuning step and memory ordering in the [v1.2.0-pre1 notes](RELEASE_HISTORY.md#v120-pre1-2026-09-22).

## 🔧 Fixed since the last release

One line per fix, newest first, with the build that has it. A pre-release installs exactly like a release and carries everything before it; each one is written up under [Release notes](#release-notes). *Not yet in a build* means the fix is in the code and will be in the next pre-release or release.

| Fixed | Issue | In build |
|---|---|---|
| Folding the VFO panels away and then switching the **Scope** off left the main page with nothing on it and no way back: the **VFOs** button that unfolds them lives in the spectrum panel's header, and the Scope switch collapses that header, button and all. Switching the Scope back on was the only escape. The panels now come back when the scope goes away, and a reload with the Scope already off starts with them showing. | — | v1.4.0 |
| Clicking a RTTY segment on the spectrum on 20, 15 or 10 m set the radio to **RTTY-R** instead of plain RTTY, which hands the radio's own decoder mark and space the wrong way round — so it sat there decoding nothing on a signal that was tuned correctly. Twelve band-plan entries were affected, in all four regions. | — | v1.4.0 |
| The DX Spots list opened wherever it sat in the page's layout the first time, which on the main page was below the bottom of the window - and the page doesn't scroll that far, so pressing the button seemed to do nothing. It now opens across the top of the window, and a place remembered from a bigger screen is brought back into view. | — | v1.4.0 |
| In the DX Spots list, a click on the small gap between the **All bands** switch and its label did nothing. It now flips the switch. | — | v1.4.0 |
| The CW Reader's **Tune** switch was never remembered: it came back off every time the page was reloaded. It now stays as you left it. | — | v1.4.0 |
| The spectrum's Range, Speed and Bright sliders had a faint track that was hard to see on the dark panel, and the keyboard focus ring on the spectrum's resize grip all but vanished against the VFO card under it. The sliders are now drawn like the Notch slider, and the grip is lighter, with a larger mark and an amber focus ring. | — | v1.4.0 |
| A page left open but untouched - the About page, say - could stop counting as connected after a quiet spell, and IWC would then shut down underneath it, so the next click gave *connection refused*. Every page now keeps its connection alive the same way, with longer timeouts on both sides. | — | v1.4.0 |
| The Twin PBT dialog showed what the radio's knobs were set to when it opened and never looked again, so a turn of the knobs at the radio left it out of date. It now re-reads whenever you come back to it. | — | v1.4.0 |
| The spectrum panel was a fixed 280 pixels tall, on a 4K monitor as much as on a laptop. It now has a **Height** setting on its control bar, from 50% to 300% of that normal height, and a grip along the bottom of the panel that can be dragged (or driven from the keyboard) to anything from 40 to 1000 pixels, remembered per VFO ([§5.4](USER_MANUAL.md#54-spectrum-display)). | — | v1.4.0 |
| A movable window (CW reader, RTTY tuner, DX spots and the rest) dragged over the meters or the spectrum let parts of what was underneath show through it, and when two overlapped, the one further down the page always won. Every movable window now sits on top of the page, and the one you last clicked or opened comes to the front. | — | v1.4.0 |
| The app shut itself down about 30 seconds after the last browser tab disconnected, with no way to ask it not to — which is wrong whenever something other than a browser is the reason it is up: WSJT-X or Log4OM on the rigctld bridge, a DX cluster feed collecting spots, or a shack PC driven over RDP. **Settings → Web Server** now has a switch to keep it running. | — | v1.3.0 |
| When the radio refused to send scope data, IWC knew exactly which radio setting was to blame — and said so only as unselectable text painted on the spectrum, where a screen reader could not reach it and it could not be copied into a bug report. The **About** diagnostics block, the one the manual asks you to paste, could only say "no sweep has ever arrived". All three now name the cause and the menu to change. | [#47](https://github.com/mm5agm/Icom_Web_Control/issues/47) | v1.3.0 |
| The band scope could freeze and stay frozen until the app was restarted. IWC now notices the radio has stopped sending waveform data and asks for it again. | — | v1.3.0 |
| Opening the radio's own SET menu stops it sending scope data — by design, not a fault — but IWC just showed an empty panel with no explanation. It now says the SET menu is what has stopped it. | — | v1.3.0 |
| Clicking the spectrum near the FT8 segment answered with DATA-U and took you out of RTTY, which has no band-plan segment of its own. The automatic mode change can now be turned off in Settings → §6.1. | — | v1.3.0 |
| The Radio & CAT dropdowns on the Settings page were sized to the page rather than to their contents, cutting off long COM port descriptions. | — | v1.3.0 |
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

**`v1.4.0` is the current release**, and `v1.0.0` was the first — IWC controls an IC-7300 or IC-7300 MkII end-to-end (see the summary at the top), bench-tested against a single MkII and confirmed by an owner on an original IC-7300. The full build plan — how IWC is carved out of YWC, what's kept, what's rebuilt, and the phased CI-V roadmap — lives in [docs/design/iwc-clone-split-plan.md](docs/design/iwc-clone-split-plan.md).

## Release notes

**Only the version you are installing is listed here.** Earlier releases are in
[RELEASE_HISTORY.md](RELEASE_HISTORY.md); their installers are still on the
[releases page](https://github.com/mm5agm/Icom_Web_Control/releases).


### v1.4.0 (2026-10-07)

> **A full release.** Download `Icom_Web_Control_Setup.exe` below and install it over the top — nothing to uninstall first, and your settings, memories, calibration and voice phrases are all kept.

**The headline is pop-out windows**: the panels you want to keep an eye on, on whatever screen you want them, at whatever size.

- **Pop-out windows for a second monitor.** The CW Reader, CW Send, RTTY Tuner, Twin PBT and DX Spots list each have a **↗** button that opens them in a browser window of their own. Drag that window to a second monitor, size it how you like, and the panel keeps working in it — a pop-out holds IWC open by itself, exactly as the main page does, so the app will not shut down just because the main tab is not the one you are looking at. **Reattach** brings the panel back into the page, and it comes back at the size you had made the window rather than snapping back to the old dialog size. User Manual, [§5.18](USER_MANUAL.md#518-pop-out-windows).

- **The CW Reader and RTTY Tuner get out of the way when the mode changes.** The reader closes itself outside CW, and the tuner outside RTTY, DATA and SSB — but only once the new mode has held for **two seconds**, so spinning through the band stack or passing through another mode on the way does not shut them. A panel that has been popped out **pauses instead of closing**, because closing somebody's second-monitor window out from under them is not a favour. User Manual, [§18](USER_MANUAL.md#18-cw-reader), [§20](USER_MANUAL.md#20-rtty).

- **The RTTY Tuner can be resized, and shrunk to almost nothing.** Drag it to whatever size suits, and **Tones only** hides the controls and the status line so the crossed ellipse alone is left — small enough to tuck into a corner of the screen and still tune by. User Manual, [§20](USER_MANUAL.md#20-rtty).

- **CW Send refuses to send in the wrong mode.** Ask it to send while the transmit VFO is in SSB and it says so plainly, **keeps the line you typed** in the box rather than throwing it away, and offers to switch the VFO to CW for you. User Manual, [§19.1](USER_MANUAL.md#191-sending-a-line).

- **The spectrum panel is no longer a fixed 280 pixels tall.** On a 4K monitor it was a stripe. Its control bar now has a **Height** setting from 50% to 300%, and there is a grip along the bottom edge that can be dragged — or driven with the arrow keys — to anything from 40 to 1000 pixels. The height is remembered per VFO. User Manual, [§5.4](USER_MANUAL.md#54-spectrum-display).

- **Movable windows now sit on top of the page, and the one you last touched is in front.** Dragging the CW reader or the RTTY tuner over the meters or the spectrum used to let parts of what was underneath show through it, and when two of them overlapped the one further down the page always won regardless of which you were using. Both are fixed: every movable window is drawn above the page, and clicking or opening one brings it to the front.

- **Fixed: folding the VFOs away with the Scope off stranded the page.** Fold the VFO panels, switch the **Scope** off, and you were left with no VFO panels and no button to bring them back — the **VFOs** button lives in the spectrum panel's header, which the Scope switch collapses along with everything else in it. The only way out was turning the Scope back on. The panels now return when the scope goes away, and reloading with the Scope already off starts with them showing.

- **Fixed: clicking a RTTY segment on 20, 15 or 10 m set RTTY-R instead of RTTY.** Reversed RTTY hands the radio's own decoder mark and space the wrong way round, so it decoded nothing at all on a signal that was tuned perfectly well. Twelve band-plan entries carried the wrong mode, across all four regions.

- **Fixed: the DX Spots list opened where you could not see it.** On the main page its remembered position was below the bottom of the window, and the page does not scroll that far — so pressing the button appeared to do nothing whatsoever. It now opens across the top of the window, and a position remembered from a larger screen is brought back into view. A click on the gap between the **All bands** switch and its label now flips the switch, too.

- **Fixed: the CW Reader's Tune switch was never remembered** and came back off on every page load. It now stays as you left it.

- **Fixed: the Twin PBT dialog went stale.** It showed where the radio's knobs were when it opened and never looked again, so turning them at the radio left the dialog describing something that was no longer true. It re-reads whenever you come back to it.

- **Fixed: a page left open but untouched could have the app shut down underneath it.** The About page, say, left open in a tab — after a quiet spell it stopped counting as connected, IWC shut itself down, and the next click gave *connection refused*. Every page now keeps its connection alive the same way, with longer timeouts at both ends.

- **Fixed: the spectrum's sliders and resize grip were hard to see.** The Range, Speed and Bright sliders had a faint track that all but vanished on the dark panel, and the keyboard focus ring on the resize grip disappeared against the VFO card beneath it. The sliders are now drawn like the Notch slider, and the grip is lighter, with a larger mark and an amber focus ring.

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
