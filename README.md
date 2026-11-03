# Icom Web Control

![Status](https://img.shields.io/badge/Status-released-brightgreen?style=flat-square)
![Licence](https://img.shields.io/badge/Licence-GPL--3.0-blue?style=flat-square)
![Latest release](https://img.shields.io/badge/Download-v1.6.0-brightgreen?style=flat-square)
![Downloads](https://img.shields.io/github/downloads/mm5agm/Icom_Web_Control/latest/Icom_Web_Control_Setup.exe?label=Downloads&style=flat-square)

> **v1.5.0 — current release.** IWC controls the **Icom IC-7300** and **IC-7300 MkII** end-to-end: frequency/mode, S-meter and Po/SWR/ALC, PTT, band/VFO/split, RF power, the RX DSP panel, the CI-V spectrum scope, ATU, voice control, a CW reader and sender, a RTTY tuning scope, and a rigctld bridge for WSJT-X. The two radios speak near-identical CI-V and IWC drives both the same way — set the CI-V address to `94` for the original, `B6` for the MkII. Development and bench testing has been on a single IC-7300 MkII, and an owner of the original IC-7300 has confirmed v1.0.6 working on his radio. That is still only two radios between them, so if anything behaves unexpectedly — on either — please report it. I'm building Icom Web Control (**IWC**) as a sibling to my [Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control) (YWC) project, for Icom CI-V transceivers. The two are deliberately separate applications with separate repositories — YWC stays Yaesu-only, IWC stays Icom-only.
>
> **[⬇ Download the latest installer](https://github.com/mm5agm/Icom_Web_Control/releases/latest)**

## 📦 Releases and pre-releases

Some entries on the [releases page](https://github.com/mm5agm/Icom_Web_Control/releases) are marked **Pre-release**. Those are my ongoing work — bug fixes and improvements published as I finish them, rather than held back for weeks until there is enough for a numbered release.

They have had less testing than the full releases. If you want the quiet life, use the **⬇ Download** link above: GitHub's `latest` always points at the newest *full* release and skips pre-releases entirely.

If something has been biting you and a pre-release says it is fixed, or you want the newest features and do not mind the odd rough edge, go ahead and install it — and please tell me how you get on. That feedback is what turns a pre-release into a full one.

**Pre-releases never nag you — if you are on a full release.** IWC's in-app update banner will only ever offer you another full release, so if you don't go looking for a pre-release you will never be told one exists, and trying one is always a deliberate trip to the releases page. There is no setting that changes this and nothing to switch on by accident. **If you are already running a pre-release**, the banner does tell you when a newer pre-release — or the finished version — arrives, because being told is the point of testing one. (GitHub's own **Watch → Releases** notifications are a separate thing and *do* include pre-releases — see [Staying informed about updates](#staying-informed-about-updates) below.)

## ✨ Added since the last release

**New in v1.6.0:**

- **RTTY Contest, a GUI for RTTY contesting** *(still being tested)*. Only what a contest RTTY operator uses, on one screen: the frequency, band keys that land on each band's RTTY spot, the radio's scope over its waterfall, and the RTTY tuner docked at the side with its decoded text. A tuning dial, **MEM** for your memories, **MONI**, **FIXED** to hold the spectrum still, its own theme list and a **MODE** list that turns amber when the radio is outside the RTTY modes. Each part pops out into a window of its own, and named layouts put every window back on its own monitor. It is the page from Yaesu Web Control, shared between the two apps, and much of it came from Bruce VK2RT's testing there. Receive only: transmit from your logger, or from MMTTY. Pick it from **Views**, or go to `/rtty-contest`. [§5.21](USER_MANUAL.md#521-rtty-contest).
- **CW Paddle: send CW from your own paddle** *(new and still being tested)*. The paddle goes into a USB adapter that shows up as a MIDI device: a **HaliKey MIDI**, or an **ESP32-S3 board** that you program from the page with one button. The keyer and sidetone run in the browser, and IWC keys the radio by DTR on a COM port. What you send shows in the CW Send log beside what you type. It is for operating the radio remotely, it starts in dry run every time so you can practise without transmitting, and **HTTPS** in Settings lets a browser on another computer use the adapter. It has been tested in Yaesu Web Control on an FTdx101MP, but has not yet keyed an IC-7300, so please tell me how it goes. [§21](USER_MANUAL.md#21-cw-paddle).
- **Choose your RTTY mode.** Settings → **RTTY mode** is RTTY-L, RTTY-U, DATA-L or DATA-U, and the tuner puts the radio in that exact mode and holds it there while it is open. Choose a DATA mode for AFSK. [§6.8](USER_MANUAL.md#68-rtty-tuner).
- **RTTY fixes from testing.** In RTTY-R the tuner had the tones the wrong way up; **Auto** no longer changes the radio's RTTY menu shift, which the radio transmits FSK on; **From radio** and **Auto** stay in the tuner heading with **Tones only**; and the scope's arms no longer lean when the signal is on tune. [§20](USER_MANUAL.md#20-rtty).
- **Radio power.** IWC switches the radio on when it starts, if it is off, and can switch it off when IWC closes. Both are in Settings; the first is on by default, the second off.
- **Save in the CW Reader.** Writes the text in the panel to a `.txt` file. Asked for by diane903 ([Discussion #86](https://github.com/mm5agm/Icom_Web_Control/discussions/86)). [§18.5](USER_MANUAL.md#185-the-panel-controls).
- **X-Watch key.** Cross-band watch is switched from a key beside the **Listening** badge and on the Radio panel, with the peek interval on the key, so the peek no longer chops a QSO. [§5.4](USER_MANUAL.md#54-spectrum-display).
- **The Radio panel** has a **VOICE** key for voice control, a corner grip that scales it to any size, a main dial that spins like a weighted knob, and a pop-out spectrum window that tunes on a click or the mouse wheel. The other VFO's passband now has its real width. [§5.20](USER_MANUAL.md#520-radio-panel).
- **One Views list on every page**, and IWC starts in the view you used last. The Experimental UI has a **Menu**, and Fabio Valente's (CR7CDC) dark look from Yaesu Web Control. [§5.1](USER_MANUAL.md#51-top-bar), [§5.19](USER_MANUAL.md#519-experimental-ui).
- **Save the radio's own menu to a file, and load it back (IC-7300MK2).** In Settings, **Radio Menu**. [§6.6](USER_MANUAL.md#66-backup--restore).
- **Open IWC on the page you want** from a shortcut, e.g. `Icom_Web_Control.exe rtty-contest`.

What was new in v1.5.0 (the Radio panel, the Experimental UI and an RTTY panel that decodes) is in its [release notes](RELEASE_HISTORY.md#v150-2026-10-18).

![The Radio panel](pictures/Radio_GUI.png)

Previous releases: **pop-out windows** for a second monitor arrived in v1.4.0 — full detail in the [v1.4.0 notes](RELEASE_HISTORY.md#v140-2026-10-07); the **RTTY Tuner** and the self-recovering band scope in the [v1.3.0 notes](RELEASE_HISTORY.md#v130-2026-09-25); **Find my radio**, the IC-7300 menu checklist, the settable tuning step and memory ordering in the [v1.2.0-pre1 notes](RELEASE_HISTORY.md#v120-pre1-2026-09-22).

## 🔧 Fixed since the last release

One line per fix, newest first, with the build that has it. A pre-release installs exactly like a release and carries everything before it; each one is written up under [Release notes](#release-notes). *Not yet in a build* means the fix is in the code and will be in the next pre-release or release.

| Fixed | Issue | In build |
|---|---|---|
| A radio reply that started with an extra FE byte was thrown away: IWC read the extra byte as the address, decided the reply was not for it, and the command timed out. It now ignores any extra FEs before a reply. | — | v1.6.0 |
| In RTTY-R the tuner had the tones the wrong way up, **From radio** showed the wrong mark, and a click on RTTY Contest landed a shift away. | — | v1.6.0 |
| **Auto** in the RTTY tuner could change the radio's RTTY menu shift, which the radio transmits FSK on. It now leaves the menu alone and says when the station's shift is not the menu's. | — | v1.6.0 |
| Cross-band watch never ran from the Radio panel: its watch spectrum stayed *off-screen* unless a Classic page was open as well. | — | v1.6.0 |
| A popped-out CW or RTTY window was drawn a second time on the Radio panel when you pressed its key. The key now brings the window to the front. | — | v1.6.0 |
| In USB-D and LSB-D the Radio panel drew the passband centred on the dial, where the pop-out window drew it on the correct side. Both now agree. | — | v1.6.0 |
| The readout of the frequency under the mouse went stale while the dial moved. | — | v1.6.0 |
| A pop-out window on a screen too short for it never kept its size. | — | v1.6.0 |
| The VOX and PROC buttons did not follow the radio when switched at the radio or from another page. | — | v1.6.0 |
| Closing a pop-out window could shut IWC down with the Radio panel still open: only pages that sent a regular "still here" signal counted as open, and the Radio panel and its pop-outs never sent one. Every open page now counts. | — | v1.5.0 |
| The MIC gain slider on the Classic and Experimental pages did not move when the mic gain was changed on the Radio panel or in another tab. It now follows. | — | v1.5.0 |
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

**`v1.5.0` is the current release**, and `v1.0.0` was the first — IWC controls an IC-7300 or IC-7300 MkII end-to-end (see the summary at the top), bench-tested against a single MkII and confirmed by an owner on an original IC-7300. The full build plan — how IWC is carved out of YWC, what's kept, what's rebuilt, and the phased CI-V roadmap — lives in [docs/design/iwc-clone-split-plan.md](docs/design/iwc-clone-split-plan.md).

## Release notes

**Only the version you are installing is listed here.** Earlier releases are in
[RELEASE_HISTORY.md](RELEASE_HISTORY.md); their installers are still on the
[releases page](https://github.com/mm5agm/Icom_Web_Control/releases).


### v1.6.0 (2026-11-03)

> **A full release.** Download `Icom_Web_Control_Setup.exe` below and install it over the top — nothing to uninstall first, and your settings, memories, calibration and voice phrases are all kept. It includes everything in the v1.6.0 pre-releases, whose notes are in [RELEASE_HISTORY.md](RELEASE_HISTORY.md). Like v1.5.0 it is unsigned, and Edge or Defender may flag it; the Windows Security section below says what to do.

**The headline is RTTY Contest and the CW Paddle.**

- **RTTY Contest, a GUI for RTTY contesting** *(still being tested)*. Only what a contest RTTY operator uses, on one screen: the frequency, band keys that land on each band's RTTY spot, the radio's scope over its waterfall, and the RTTY tuner docked at the side with its decoded text. **MODE** holds the radio in the RTTY mode you choose and turns amber when it is in another. A tuning dial (outer ring for big steps, inner knob for small ones) shares a place with the decoded text. **MEM** lists your memories, **MONI** sets the monitor, **WIDTH** and **AUTO** set the filter, and **FIXED** holds the spectrum still and turns a page at the edge. **MENU** opens Settings and the other pages in a new tab, and the page has its own theme list. Each part pops out into a window of its own, and named layouts put every window back on its own monitor. It is the page from Yaesu Web Control, shared between the two apps, and much of it came from Bruce VK2RT's testing there. Receive only: transmit from your logger, or from MMTTY. User Manual, [§5.21](USER_MANUAL.md#521-rtty-contest).

- **CW Paddle** *(new and still being tested)*. Send CW from your own paddle when you operate the radio remotely. The paddle goes into a USB adapter that shows up as a MIDI device: a **HaliKey MIDI**, or an **ESP32-S3 board** that you program from the page with one button. The keyer and sidetone run in the browser, and IWC keys the radio by DTR: through the CI-V port on an original IC-7300, or the second USB port on an IC-7300MK2. It starts in dry run every time, so you can practise without transmitting; choosing a key line can put the radio in transmit at once. What you send shows in the CW Send log, on lines tagged *paddle*. **Enable HTTPS** in Settings lets a browser on another computer use the adapter. Tested in Yaesu Web Control on an FTdx101MP; it has not yet keyed an IC-7300, so please tell me how it goes. At the radio, plug the paddle into its KEY jack as usual. User Manual, [§21](USER_MANUAL.md#21-cw-paddle).

- **RTTY.** Settings → **RTTY mode** chooses RTTY-L, RTTY-U, DATA-L or DATA-U, and the tuner goes to that exact mode and holds it. In RTTY-R the tones are the right way up now; **Auto** never changes the radio's RTTY menu shift; **From radio** and **Auto** stay in the heading with **Tones only**; and the scope's arms straighten at the same dial setting. IWC keeps the tuner's last Mark and Shift. User Manual, [§20](USER_MANUAL.md#20-rtty).

- **Radio power at start and exit.** **Turn the radio on when Icom Web Control starts** (on by default) and **Turn the radio off when Icom Web Control closes** (off by default), in Settings.

- **Save in the CW Reader.** A **Save** button writes the text in the panel to a `.txt` file headed with the UTC time. Asked for by diane903 ([Discussion #86](https://github.com/mm5agm/Icom_Web_Control/discussions/86)). User Manual, [§18.5](USER_MANUAL.md#185-the-panel-controls).

- **X-Watch.** An **X-Watch** key beside the **Listening** badge, and an **X-WATCH** key on the Radio panel, switch cross-band watch on and off, so the peek no longer chops a QSO, and carry the peek interval. With X-Watch off the useless watch panel is hidden, and with it on and the VFOs on different bands the watch pane shows its trace without the empty waterfall. User Manual, [§5.4](USER_MANUAL.md#54-spectrum-display).

- **Radio panel.** A **VOICE** key for voice control. A corner grip scales the panel to any size, and the main dial spins like a weighted knob (the idea is from the tuning dial in Brian Clark's IC-7300-Scope). The pop-out spectrum window tunes on a click or the mouse wheel and has the **SCOPE** settings on each pane. The other VFO's passband has its real width, and the **FILTER** key shows the slot and its width. User Manual, [§5.20](USER_MANUAL.md#520-radio-panel).

- **Views and menus.** One **Views** list on every page, and IWC starts in the view you used last. The Experimental UI has a **Menu**, and Fabio Valente's (CR7CDC) dark look and panels from Yaesu Web Control. A shortcut can open IWC straight on a page, e.g. `Icom_Web_Control.exe rtty-contest`. User Manual, [§5.1](USER_MANUAL.md#51-top-bar), [§5.19](USER_MANUAL.md#519-experimental-ui).

- **The radio's own menu to a file, and back (IC-7300MK2).** Settings → **Radio Menu** saves all 270 items of the radio's SET menu to an `.ini` file and loads a file back, sending only the items you tick. Items that affect transmitting start unticked. **Export full backup** saves into a Backups folder. User Manual, [§6.6](USER_MANUAL.md#66-backup--restore).

- **Fixed:** a radio reply with an extra FE byte in front was thrown away; cross-band watch never ran from the Radio panel; a popped-out CW or RTTY window was drawn twice; in USB-D and LSB-D the Radio panel drew the passband centred on the dial; the readout under the mouse went stale while the dial moved; a pop-out window on a short screen never kept its size; the VOX and PROC buttons did not follow the radio.


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

Because the installer is not code-signed, Windows and third-party antivirus tools treat it as an unknown, newly downloaded executable and may warn about it or block it. This is expected — the file is not malware. The installer is built by GitHub Actions directly from the source in this repository, and GitHub shows a SHA-256 digest beside each file on the release page — run `Get-FileHash Icom_Web_Control_Setup.exe` in PowerShell and the two should match. Follow whichever of these matches what you see:

**Microsoft Edge or Defender blocks the download, or Defender quarantines it, and says it contains a virus or "This program is dangerous and executes commands from an attacker"**
This is Defender's cloud/machine-learning classifier (the detection name ends in `!ml` — with v1.5.0 it was `Trojan:Win32/Wacatac.B!ml` on the installer itself, caught by Edge while it was still downloading). It is a false positive that hits many unsigned .NET applications, and it can appear on a build that scanned clean the day before. To recover:

- **If Edge blocked the download:** open Downloads (**Ctrl+J**), click **…** on the Icom Web Control entry, choose **Keep**, then **Show more → Keep anyway**.
- **If Defender quarantined it:** open **Windows Security → Virus & threat protection → Protection history**, click the entry, choose **Actions → Allow on device**, then download the installer again (or simply start IWC again if it was the installed program that was taken). Choose *Allow*, not just *Restore* — Restore puts the file back but Defender may take it again at the next scan.

Don't switch Defender off — allowing this one file is enough. If this happens to you, please post the detection name shown in Protection history in a [Discussion](https://github.com/mm5agm/Icom_Web_Control/discussions) or an issue; I report each one to Microsoft as a false positive, and that is what makes it stop happening for the next person.

**"Windows protected your PC" (SmartScreen) or an Unknown Publisher prompt when you run the installer**
Click **More info → Run anyway**.

**Norton (or other antivirus) flags the file as malware**
This is the same false positive, caused by the executable being unsigned and newly downloaded. In Norton, go to **Security → History**, find the quarantined file, and choose **Restore & Exclude** (or the equivalent Allow option in your antivirus).

**Right-click → Properties → Unblock**
Windows marks files downloaded from the internet as untrusted. Before running the installer, right-click the file, choose **Properties**, and if you see an **Unblock** checkbox at the bottom of the General tab, tick it and click OK.

**"This app can't run on your PC" — Smart App Control**
If Smart App Control is enabled it will block unsigned apps entirely. Go to **Settings → Privacy & Security → Windows Security → App & Browser Control → Smart App Control** and switch it to **Off**, then restart your PC and try again.

The screenshot below shows the Smart App Control setting:

![Smart App Control Screenshot](pictures/SmartAppControl.png)

These are one-time steps — once the app is installed you won't see them again.

## Privacy

Icom Web Control runs on your own PC and does not send your data anywhere. It makes only these network connections:

- Between your browser and the PC it runs on, on your own network, for the web interface.
- From your browser to GitHub (api.github.com), to see whether a newer release exists. It reads the release list; nothing about you or your station is sent. See [GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).
- To a DX cluster, only if you turn DX spots on and enter a cluster yourself. It logs in with the callsign you enter, as every DX cluster requires.

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
