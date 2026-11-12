# Icom Web Control - release history

Every version's notes are kept here, newest first, so the README stays short. The
installers are all still on the
[releases page](https://github.com/mm5agm/Icom_Web_Control/releases).

This file exists because most of those release pages carry no notes of their own -
the older ones say only "see README.md", or nothing at all - so for most versions
this is the only copy. It is the operator-facing record, moved out of the README
unchanged, and it is not [CHANGELOG.md](CHANGELOG.md), which is a closed archive in
a different and much older format.

---

### v1.7.0-pre7 (2026-11-12)

> **A pre-release.** On Windows, download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings are all kept. The Download link at the top of the README still points at v1.6.0, which is the current full release, so you have to come to the releases page for this build. The Docker image and Mac apps are rebuilt with the same changes.

- **Refine settles the scope's noise floor** so weak signals stand out. Each sweep the radio sends has a rough floor; Refine averages the last few, from **Raw** (as the radio sends them) to **5**. The default, **3**, follows a new signal in about two-thirds of a second, and **5** in about a second. Tuning doesn't smear the trace. On an IC-7300 MkII on 20m FT8, signals stood about half as high again above the floor at 3, and twice as high at 5. It is a slider beside Bright on Classic, and a **REFINE** list beside the span on Radio, its pop-out spectrum and RTTY Contest; all of them share one setting, kept on the server. [§5.4](USER_MANUAL.md#54-spectrum-display)
- Everything listed for v1.7.0-pre1 to pre6 below still needs testing.

### v1.7.0-pre6 (2026-11-11)

> **A pre-release.** On Windows, download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings are all kept. The Download link at the top of the README still points at v1.6.0, which is the current full release, so you have to come to the releases page for this build. The Docker image and Mac apps are rebuilt with the same changes.

- **The log says why a radio isn't found (#128).** When connecting gets no answer, the log now gives the port, the speed, the CI-V address that was asked and the bytes that came back, on the first miss and then about once a minute. Find my radio logs one line for each port and speed it tries. Before this the log showed only "Connecting..." lines.
- **Find my radio asks more slowly on a port that echoes (#128).** If bytes come back from a port but nothing answers, Find my radio asks again one question at a time. With CI-V USB Echo Back on, an IC-7300MK2 was seen throwing away anything that arrived while it was answering. A port that sends nothing back is skipped as before, so Find my radio takes no longer.
- Everything listed for v1.7.0-pre1 to pre5 below still needs testing.

### v1.7.0-pre5 (2026-11-10)

> **A pre-release.** On Windows, download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings are all kept. The Download link at the top of the README still points at v1.6.0, which is the current full release, so you have to come to the releases page for this build. The Docker image and Mac apps are rebuilt with the same changes.

- **IWC finds radios that ignore the broadcast ID question (#128).** An original IC-7300 was seen answering nothing on the CI-V broadcast address while answering at its own address, 94, so IWC said "No radio answered" although other programs worked on the same COM port. If nothing answers on broadcast, connecting and Find my radio now ask at each model's default address (`B6`, `94`, `88`), and if there's still no identity reply, read the frequency there and take the model from the address that answered. All of these only read from the radio. Not yet tried on a radio that needs it: if you had "No radio answered" before, please try this build and say on #128 how it went.
- **Settings search finds Dry run,** the box in the Test this pack window that stops voice commands reaching the radio.
- Everything listed for v1.7.0-pre1 to pre4 below still needs testing.

### v1.7.0-pre4 (2026-11-06)

> **A pre-release, and the first with Linux, Raspberry Pi and Mac builds.** On Windows, download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings are all kept. The Download link at the top of the README still points at v1.6.0, which is the current full release, so you have to come to the releases page for this build.
>
> **Off Windows these are for testing, and they have not yet been run against a real radio.** Follow [the step-by-step instructions](https://github.com/mm5agm/Icom_Web_Control/blob/develop/docs/pi-docker-testing.md), which start with setting USB SEND and USB Keying to OFF on the radio.

- **Docker image for Linux and Raspberry Pi** (amd64 and arm64): `ghcr.io/mm5agm/icom_web_control:v1.7.0-pre4`, or `:testing` for the newest pre-release. A 64-bit OS is needed on a Pi.
- **Mac apps** in this release's assets: `macos-arm64` for Apple silicon, `macos-x64` for Intel. They are unsigned. There is no Dock or menu bar icon, and the app quits with its last browser tab.
- **Radio control only off Windows.** The Voice, CW and RTTY audio pages and Remote Audio are not in these builds, and neither is Find my radio.
- **Off Windows, IWC opens no radio port until you confirm in Settings that the USB lines are off.** Opening a port there raises DTR and RTS and software can't stop it, so a radio set to key from those lines would transmit. After opening, IWC asks whether the radio is transmitting and reads the items back. If the radio is transmitting, or an item isn't OFF, or an answer doesn't come, it closes the port and says why. The IC-7100's manual lists no such items, so have an antenna or dummy load connected and watch the radio the first time.
- **Nothing changes on Windows** from this work. The panel doesn't appear there and Find my radio stays.
- Everything listed for v1.7.0-pre1, pre2 and pre3 below still needs testing.

### v1.7.0-pre3 (2026-11-05)

> **A pre-release, for Windows.** Download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings, memories, calibration and voice phrases are all kept. The Download link at the top of the README still points at v1.6.0, which is the current full release, so you have to come to the releases page for this build. Linux, macOS and Docker are not in this build.
>
> **What needs testing:** everything listed for v1.7.0-pre1 and pre2 below, and now the voice confirm step. Use the Test this pack window in Settings with Dry run ticked, so nothing reaches the radio.

- **Voice commands that transmit now wait for "confirm".** Testing showed that a score can't keep a wrong word off the air: "transmission" scored 0.92 as "transmit on". So "transmit on", "tune antenna" and any custom macro that keys the radio now ask *"Are you sure?"*, and only go ahead if you say "confirm" within 6 seconds. Any other command, or saying nothing, drops it and the radio stays on receive. Both need a score of 0.85 or better. "Stop transmitting" still works straight away.
- **"Key transmitter" is no longer a voice phrase,** including in phrases you have already saved. "Transmit on", "start transmitting" and "transmit now" remain.
- **The test window's Trace is easier to read.** No more "Heard heard", and a refused or unrecognised phrase no longer shows the previous phrase and score beside it.

### v1.7.0-pre2 (2026-11-05)

> **A pre-release, for Windows.** Download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings, memories, calibration and voice phrases are all kept. The Download link at the top of the README still points at v1.6.0, which is the current full release, so you have to come to the releases page for this build.
>
> **What needs testing:** everything listed for v1.7.0-pre1 below, and now the voice test window:
>
> - **IC-7300 and IC-7300 MkII owners:** does everything still work as it did in v1.6.0?
> - **IC-7100 owners:** choose "IC-7100 (under test)" in Settings. Section 15.9 of the manual has the setup. Try connecting, tuning, band and mode changes, meters and reading memories. **Stay on receive. Don't press transmit, Tune or the CW keys on an IC-7100**, because those commands have never reached a real one.
> - **Voice control:** in Settings > Voice Control > Test this pack, leave Dry run ticked, hold Hold to speak and say a command. The Trace shows what was heard and how sure the recogniser was. Does "transmit on" need a clear phrase, without the other commands getting harder to use?

**This build fixes the voice test window. Everything else is as v1.7.0-pre1.**

- **Test this pack works.** Hold to speak did nothing, in every earlier version as well, so no phrase could be tried out there.
- **A dry run stays dry when you let go.** Letting go of the button used to end the dry run straight away, but the recogniser carries on with what it is still hearing. A command finishing just after you let go would have gone to the radio for real. The dead button meant this could not happen until now, and it is fixed before the button came back to life.
- **If the speech recogniser fails to start, the window says so** in the Trace, rather than claiming to be listening.

---

### v1.7.0-pre1 (2026-11-05)

> **A pre-release, for Windows.** Download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings, memories, calibration and voice phrases are all kept. The Download link at the top of the README still points at v1.6.0, which is the current full release, so you have to come to the releases page for this build.
>
> **What needs testing:**
>
> - **IC-7300 and IC-7300 MkII owners:** does everything still work as it did in v1.6.0? A lot changed underneath, so please say if anything has got worse.
> - **IC-7100 owners:** connecting, tuning, band and mode changes, meters and reading memories. **Stay on receive. Don't press transmit, Tune or the CW keys on an IC-7100, and don't use voice commands that transmit.** Those commands have never reached a real IC-7100.
> - **Voice control:** with Dry run on, do the commands that transmit now need a clear phrase, without getting in the way of the others?

**The headline is the IC-7100, under test.**

- **IC-7100 added to Settings, as "IC-7100 (under test)".** It is written from Icom's manual and has only run against a simulated radio. Nothing has been tried on a real IC-7100, so this build is a first look, not support for that radio. The Settings page lists what to check on the radio, and section 15.9 of the manual explains the rest.
- **What it does on an IC-7100:** its bands and modes including 2m and 70cm, the controls it has, and power, preamp and attenuator that follow the radio on each band. Controls it lacks are hidden. It reads the radio's memory channels (495 in 5 banks) but can't write them yet.
- **What it can't do:** the IC-7100 sends no band scope over CI-V, so the Spectrum panels stay empty. RTTY Contest and the other views built around the scope are still offered for now; sorting those out is later work.
- **Windows only.** This build is for Windows. Linux, macOS and Docker builds are being worked on and are not part of this release.

**Also in this build:**

- **Voice commands that transmit need a much clearer match.** Saying "transmit on", starting a tune, or a macro that keys the radio now needs far more confidence than other commands, so a misheard word is much less likely to put you on the air. If the match isn't clear enough you'll hear "Not transmitting, not heard clearly". Commands that stop transmitting are as easy as before. Dry run follows the same rule. The new level is a first estimate and hasn't been tried on the air, so tell me if it refuses too often.
- **A search box on the Settings page.** It finds a setting by its name, by words in its help text, or by a word you might try for it, and takes you straight to it. Each setting also has its own link.

---

### v1.6.0 (2026-11-03)

> **A full release.** Download `Icom_Web_Control_Setup.exe` below and install it over the top — nothing to uninstall first, and your settings, memories, calibration and voice phrases are all kept. It includes everything in the v1.6.0 pre-releases, whose notes are in [RELEASE_HISTORY.md](RELEASE_HISTORY.md). Like v1.5.0 it is unsigned, and Edge or Defender may flag it; the README's Windows Security section says what to do.

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

### v1.6.0-pre9 (2026-11-02)

> **A pre-release.** Download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings, memories, calibration and voice phrases are all kept. The ⬇ Download link at the top of this README still points at v1.5.0, which is the current full release, so you have to go to the releases page for this build. It includes everything in v1.6.0-pre8, pre7, pre6, pre5, pre4, pre3, pre2 and pre1 below.
>
> **What needs testing:** on RTTY Contest, the theme list and the MODE list.

- **RTTY Contest has its own theme list**, beside LAYOUT. It used to take the Radio panel's theme; it still starts from it, but a theme picked here is kept for this page alone. [§5.21](USER_MANUAL.md#521-rtty-contest).
- **The MODE list on RTTY Contest is labelled**, and turns amber when the radio is in a mode outside the four RTTY ones, such as CW from the last time it was used, so it is clear where to change it.

### v1.6.0-pre8 (2026-11-02)

> **A pre-release.** Download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings, memories, calibration and voice phrases are all kept. The ⬇ Download link at the top of this README still points at v1.5.0, which is the current full release, so you have to go to the releases page for this build. It includes everything in v1.6.0-pre7, pre6, pre5, pre4, pre3, pre2 and pre1 below.
>
> **What needs testing:** on RTTY Contest, **MEM** and **MONI**, swapping the dial and the decoded text, and popping each out. IWC switching the radio on when it starts.

- **From Yaesu Web Control v2.5.4-pre13 and pre14, as Bruce VK2RT asked:** on RTTY Contest, a **MEM** key beside **BANDS** lists your memories, and a click tunes the VFO on screen; the list scrolls when there are more than fit. **MONI**, under INNER on the tuning dial, switches the radio's monitor on and off and sets its level. It is kept in the browser, on unless you turn it off, and put back each time the page opens. It doesn't transmit. User Manual, [§5.21](USER_MANUAL.md#521-rtty-contest).
- **The tuning dial and the decoded text share one place** under the tuner, with a **VFO WHEEL** heading. **DECODE** and **VFO WHEEL** swap them, each has its own **POP OUT**, and there is no DOCK left on the page. The dial speeds up when dragged fast and runs on when let go mid-spin. Its box reaches the bottom of the window. The tuner's key reads **RTTY TUNER**.
- **Radio power at start and exit.** Settings has **Turn the radio on when Icom Web Control starts** (on by default) and **Turn the radio off when Icom Web Control closes** (off by default).

### v1.6.0-pre7 (2026-10-31)

> **A pre-release.** Download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings, memories, calibration and voice phrases are all kept. The ⬇ Download link at the top of this README still points at v1.5.0, which is the current full release, so you have to go to the releases page for this build. It includes everything in v1.6.0-pre6, pre5, pre4, pre3, pre2 and pre1 below.
>
> **What needs testing:** on RTTY Contest, the **MENU** key, a **WIDTH** you set being kept, **AUTO** beside WIDTH, the tuning dial, **FIXED** turning a page at the edge, and the tuner starting in the exact RTTY mode chosen in Settings.

- **More from Bruce VK2RT's testing on RTTY Contest.** A **MENU** key beside **VIEWS** opens Settings, the setup pages, the manual and About in a new tab. **FIXED** holds the spectrum until the passband reaches the edge of the screen. User Manual, [§5.21](USER_MANUAL.md#521-rtty-contest).
- **From Yaesu Web Control v2.5.4-pre8:** a **WIDTH** you set while the tuner is open is kept for that mode and not undone when it closes, and **AUTO** beside WIDTH goes back to the tuner's own width. IWC keeps the tuner's last Mark and Shift so every window starts from them. A shortcut can open IWC straight on a page, e.g. `Icom_Web_Control.exe rtty-contest`.
- **From Yaesu Web Control v2.5.4-pre9:** a tuning dial on RTTY Contest, under PWR and WIDTH: the outer ring tunes VFO A in big steps and the inner knob in small ones, each with its own step list. Tune off the edge of a FIXED spectrum and the passband comes back near the other edge, so you carry on tuning the same way.
- **The tuner starts in the exact RTTY mode you chose**, the other sideband included (RTTY-U to RTTY-L, DATA-U to DATA-L). [§20.9](USER_MANUAL.md#209-what-the-tuner-changes-on-your-radio).

### v1.6.0-pre6 (2026-10-29)

> **A pre-release.** Download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings, memories, calibration and voice phrases are all kept. The ⬇ Download link at the top of this README still points at v1.5.0, which is the current full release, so you have to go to the releases page for this build. It includes everything in v1.6.0-pre5, pre4, pre3, pre2 and pre1 below.
>
> **What needs testing:** the CW Reader's **Save** button; the RTTY Contest view; and on the Radio panel, popping the CW or RTTY window out and pressing its key again.

- **Save in the CW Reader.** A **Save** button writes the text in the panel to a `.txt` file headed with the UTC time. In Chrome and Edge on the radio's PC it opens a Save dialog in the folder you used last time. For a whole session, IWC already writes every session to a transcript file as it decodes, in `%APPDATA%\MM5AGM\Icom Web Control\CW Transcripts\`. Asked for by diane903 ([Discussion #86](https://github.com/mm5agm/Icom_Web_Control/discussions/86)). User Manual, [§18.5](USER_MANUAL.md#185-the-panel-controls), [§18.9](USER_MANUAL.md#189-transcripts).
- **RTTY Contest, a GUI for RTTY contesting.** Only what a contest RTTY operator uses, on one screen: the frequency, band keys that land on each band's RTTY spot, the radio's scope over its waterfall, and the RTTY tuner docked at the side with its decoded text. Each part pops out into a window of its own, and named layouts put every window back on its own monitor. Receive only. Pick it from **Views**, or go to `/rtty-contest`. User Manual, [§5.21](USER_MANUAL.md#521-rtty-contest).
- **Choose your RTTY mode.** Settings → **RTTY mode** is RTTY-L, RTTY-U, DATA-L or DATA-U, and the tuner puts the radio in it and holds it there while it is open. [§6.8](USER_MANUAL.md#68-rtty-tuner).
- **RTTY fixes from testing:** RTTY-R had the tones the wrong way up; **Auto** no longer changes the radio's RTTY menu shift; **From radio** and **Auto** stay in the tuner heading with **Tones only**; the scope's arms no longer lean when on tune.
- **Radio panel:** a popped-out CW or RTTY window is no longer drawn a second time on the panel when you press its key; the key brings the window to the front. The duplicate ⇱ inside those windows has gone (use **⇱ WINDOW**), the panel close **×** is red, and the version is shown. User Manual, [§5.20](USER_MANUAL.md#520-radio-panel).

### v1.6.0-pre5 (2026-10-26)

> **A pre-release.** Download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings, memories, calibration and voice phrases are all kept. The ⬇ Download link at the top of this README still points at v1.5.0, which is the current full release, so you have to go to the releases page for this build. It includes everything in v1.6.0-pre4, pre3, pre2 and pre1 below.
>
> **What needs testing:** the **Views** list, and IWC starting in the view you used last; and on an IC-7300MK2, saving the radio's menu to a file and loading it back.

- **One Views list on every page.** Classic, Experimental and Radio are now picked from a **Views** list in the top bar, the same on all three, with the one you are on highlighted. IWC starts in the view you opened last. User Manual, [§5.1](USER_MANUAL.md#51-top-bar).
- **A Menu on the Experimental UI.** Settings, Memories, the manual and the other pages are under **Menu**, each opening in a new tab so the workspace keeps running behind it. The Radio panel's **MENU** key lists the same pages. On the Classic page they stay as links along the top bar, which a screen reader now announces as **Site pages**.
- **Save the radio's own menu to a file, and load it back (IC-7300MK2).** In Settings, **Radio Menu**: **Save radio menu to file** reads all 270 items of the radio's SET menu into an `.ini` file that reads in Notepad. **Change radio settings…** changes items one at a time. **Load this one** shows what a saved file would change and sends only the items you tick, reading each one back. Items that affect transmitting start unticked, and the CI-V, network, date and time items are never loaded. This is not the same as **Backup & Restore**, which saves IWC's own settings; the manual has a table of the differences. User Manual, [§6.6](USER_MANUAL.md#66-backup--restore).
- **Export full backup saves into a Backups folder**, `%APPDATA%\MM5AGM\Icom Web Control\Backups`, and the page has **Open that folder** and **Download a copy**.
- **Fixed: a radio reply that started with an extra FE byte was thrown away.** IWC read the extra byte as the address, decided the reply was not for it, and the command timed out. It now ignores any extra FEs before a reply.

### v1.6.0-pre4 (2026-10-23)

> **A pre-release.** Download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings, memories, calibration and voice phrases are all kept. The ⬇ Download link at the top of this README still points at v1.5.0, which is the current full release, so you have to go to the releases page for this build. It includes everything in v1.6.0-pre3, pre2 and pre1 below.
>
> **What needs testing:** the pop-out spectrum window: clicking and turning the mouse wheel on it, and the watch pane with X-Watch on and the VFOs on different bands.

- **The pop-out spectrum window tunes like the Radio panel.** Click it to tune there, and turn the mouse wheel to move the dial by the panel's **STEP** a notch. A line and a readout follow the mouse and show the frequency a click would send. Each pane has the panel's **SCOPE** settings beside **HOLD**, the window can be resized from its corner, and the text is larger and readable in every theme. User Manual, [§5.20](USER_MANUAL.md#520-radio-panel).
- **The cross-band watch shows its trace without the waterfall.** A watch VFO on another band only gets one scope sweep per X-Watch peek, so its waterfall was a near-empty box. With X-Watch on and the VFOs on different bands, that pane now shows its trace on its own, and in the pop-out window the other pane takes the room. While it waits for the next peek it says **Waiting for data** rather than that it is out of range. The Radio panel's main dial now shows **A** or **B** for the VFO it tunes.
- **The other VFO's passband has its real width.** The radio only reports the IF width of the VFO in use, so the other VFO's passband was a hairline until you selected it. IWC now learns each filter slot's width per mode, reads it during each cross-band peek, and, with X-Watch on and both VFOs on one band, borrows the receiver once for about a tenth of a second when it can't know it any other way. User Manual, [§5.20](USER_MANUAL.md#520-radio-panel).
- **The Radio panel's FILTER key shows the slot and its width**, and the labels under the keys are drawn in each key's own colour, so they can be read on the dark keys.
- **A corner grip scales the Radio panel.** Drag it to any size; the size picker lists it as a percentage and keeps it.
- **The manual explains the passband shading**: where the shaded passband sits for each mode, and why it can sit above or below the dial. User Manual, [§5.20](USER_MANUAL.md#520-radio-panel).
- **Fixed: in USB-D and LSB-D the Radio panel drew the passband centred on the dial**, where the pop-out window drew it on the correct side. Both now agree.
- **Fixed: the readout under the mouse went stale while the dial moved.** With the mouse held still over the spectrum, the readout now keeps showing the frequency under it as the trace moves, on the Radio panel and in the pop-out window.
- **Fixed: a pop-out window on a screen too short for it never kept its size**, and opened at the default size every time, which didn't fit either. It now keeps the size you gave it.

### v1.6.0-pre3 (2026-10-22)

> **A pre-release.** Download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings, memories, calibration and voice phrases are all kept. The ⬇ Download link at the top of this README still points at v1.5.0, which is the current full release, so you have to go to the releases page for this build. It includes everything in v1.6.0-pre2 and v1.6.0-pre1 below.
>
> **What needs testing:** the X-Watch key with pseudo-dual on, especially switching it off and on during a QSO.

- **An X-Watch key switches cross-band watch, so the peek no longer chops a QSO.** With pseudo-dual on and VFO B on another band, IWC briefly retunes the receiver every few seconds to refresh the watch panel, and your audio dips each time. Switching that off used to mean a trip to Settings. Now there is an **X-Watch** key beside the **Listening** badge on the Classic page, on whichever panel you are hearing, and an **X-WATCH** key on the Radio panel. One click stops the peek within about a second, and another starts it again. The key shows the interval in use, such as **X-Watch 15 s**, or **X-Watch Off**. User Manual, [§5.4](USER_MANUAL.md#54-spectrum-display) and [§5.20](USER_MANUAL.md#520-radio-panel).
- **The peek interval is on the X-Watch key, and both settings have left Settings.** The arrow beside **X-Watch** (or the **X-WATCH** list on the Radio panel) offers 5 s to 60 s. **Allow cross-band watch** and **Cross-band peek interval** are no longer in Settings, and the values you had are kept.
- **With X-Watch off, the useless watch panel is hidden.** With cross-band watch off and the VFOs on different bands, the watch panel could only say *This VFO is outside the scope span*. Now only the VFO you are hearing is shown, on the Classic page, the Radio panel and its pop-out spectrum window. The watch panel comes back when you switch X-Watch on or put both VFOs on one band, and your own choice of view is kept. Same-band watch is unaffected.
- **Fixed: cross-band watch never ran from the Radio panel.** The watch spectrum on the Radio panel stayed at *off-screen* unless a Classic page happened to be open as well. It now refreshes from the Radio panel and its spectrum window too.

### v1.6.0-pre2 (2026-10-20)

> **A pre-release.** Download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings, memories, calibration and voice phrases are all kept. The ⬇ Download link at the top of this README still points at v1.5.0, which is the current full release, so you have to go to the releases page for this build. It includes everything in v1.6.0-pre1 below.
>
> **What needs testing:** voice control from the Radio panel.

- **The Radio panel has a VOICE key for voice control.** Until now the only way to use voice control was the mic button on the Classic page. With voice control switched on, the Radio panel has a **VOICE** key next to **MONI**. Hold it down while you speak a command and let go when you have finished; the command works on the selected VFO. While it listens the key lights and shows **LISTENING**, then shows what it heard. User Manual, [§5.20](USER_MANUAL.md#520-radio-panel).

### v1.6.0-pre1 (2026-10-20)

> **A pre-release.** Download `Icom_Web_Control_Setup.exe` from this release and install it over the top. You don't need to uninstall first, and your settings, memories, calibration and voice phrases are all kept. The ⬇ Download link at the top of this README still points at v1.5.0, which is the current full release, so you have to go to the releases page for this build.
>
> **What needs testing:** the Experimental UI's new panels on your own radio. Please report anything that works on the Classic page but not here, and say which page you were on.

- **The Experimental UI gets Fabio Valente's (CR7CDC) dark look from Yaesu Web Control.** In v1.5.0, **Experimental UI** showed the light Classic controls in dock panels. It is now dark, and the panels are rebuilt the way Fabio laid them out for YWC:
  - The toolbar is a strip across the top.
  - **Levels** has POWER, MIC, PROC and MONI keys.
  - **VFO Actions** has A↔B, B→A, A→B, Split and +5k.
  - **Buttons** has ATU, Tune, FM Rep, VOX and the keys that open the CW, RTTY, DX and Voice panels.
  - **Linear Meters** shows PWR, SWR, COMP, ALC, IDD and VDD as bars beside the Spectrum.
  - **VFO A** and **VFO B** are three-column cards, each with a bar S-meter.

  They are still the Classic page's own controls underneath, so they behave as they do there, and the Classic page itself is unchanged. If you rearranged **Default** in v1.5.0, it starts again from the new arrangement. User Manual, [§5.19](USER_MANUAL.md#519-experimental-ui).
- **Fixed: the VOX and PROC buttons did not follow the radio.** Switching VOX or the speech processor on at the radio, or from another page, left IWC's buttons showing the old state. They now follow the radio, on Classic and in the Experimental UI.
- **The Windows Security section of this README explains the Defender warning.** Edge and Defender flagged v1.5.0 as `Trojan:Win32/Wacatac.B!ml`, which is a false positive, and it has been reported to Microsoft. That section now explains how to check the download and keep it. This build is unsigned in the same way and may be flagged too.

### v1.5.0 (2026-10-18)

> **A full release.** Download `Icom_Web_Control_Setup.exe` below and install it over the top — nothing to uninstall first, and your settings, memories, calibration and voice phrases are all kept.

**The headline is three ways to run the radio.** Classic is unchanged and is still where IWC opens; the Experimental UI and the Radio panel are new, and the top bar of each reaches the other two.

- **The Radio panel.** **Radio** in the top bar opens the IC-7300 laid out like the front of a transceiver, at `/radio`: meter and keys down the left, VFO-A and VFO-B over the radio's own band scope, the main dial and the knobs, and windows for CW, RTTY, memories, DX spots, meters and receive setup that drag, resize, tear off and pop out onto a second screen. **M▸VFO** and **VFO▸M** work with the radio's own 99 memory channels, the CW keyer's speed and pitch step with the mouse wheel, and there are six colour themes, including Night (red) for operating in the dark. It is the panel from Yaesu Web Control, shared between the two apps. User Manual, [§5.20](USER_MANUAL.md#520-radio-panel).

- **The Experimental UI.** The main page as a workspace of panels — Toolbar, Clarifier, Meters, Spectrum, VFO A and VFO B — that you can drag, dock side by side or as tabs, resize, hide and pop out, with named layouts saved in IWC. It is the Classic page's own controls rearranged, not a copy, so everything behaves as it does there. The workspace is Fabio Valente's (CR7CDC), from Yaesu Web Control. User Manual, [§5.19](USER_MANUAL.md#519-experimental-ui).

- **The RTTY panel decodes.** What was the RTTY Tuner is now one **RTTY** panel: the crossed-ellipse tuner beside a **Decode** pane printing the text. **Auto** listens for four seconds and reports the shift and speed it actually measured — not the nearest entry on the radio's short list — and refuses rather than guesses when what it heard does not add up; when it is confident it tunes the signal in. The panel sets the radio up for RTTY and puts your mode and filter back when you close it, and **RTTY Contest Mode** in Settings leaves just the tuner for use beside a logger. **Send** is greyed out and does not transmit. User Manual, [§20](USER_MANUAL.md#20-rtty).

- **CW Auto, and a CW Reader that prints less junk.** **Auto** in the CW Reader moves the dial so the station you are hearing sits on your pitch and sets the filter for the speed it is sending, through Reader Mode so **Stop** puts your filter back. The reader also splits characters the sender ran together, judges word gaps at the current speed, and no longer prints the stray E, I and T that noise produces between overs. User Manual, [§18.7](USER_MANUAL.md#auto-tune-the-station-in-for-you).

- **Keyboard shortcuts on the main page.** Single keys for tuning, bands, modes, IF width, filter slot, span and the panels, on Classic and the Experimental UI — the same keys as Yaesu Web Control. Press **?** for the list. User Manual, [§13](USER_MANUAL.md#13-keyboard-shortcuts).

- **"Set it up" buttons.** When the CW Reader has no audio device, or the DX Spots list has no cluster, the message now has a button that opens a small window for just that setting, instead of sending you off to find it in Settings. User Manual, [§18.3](USER_MANUAL.md#183-choosing-the-audio-device), [§5.17](USER_MANUAL.md#517-dx-spots-list).

- **A test log you can send me.** **Diagnostics → Start fresh test log**, do the test, then **Download test log**: you get just that test's part of the log, ready to drag into a GitHub issue. User Manual, [§11](USER_MANUAL.md#11-diagnostics).

- **Fixed: closing a pop-out could shut IWC down with the Radio panel still open.** IWC only counted pages that sent a regular "still here" signal, and the Radio panel did not send one — so with the Radio panel open and one pop-out window, closing the pop-out stopped the app 30 seconds later. Every open page now counts.

- **Fixed: the MIC gain slider did not follow a change made elsewhere.** Moving it on the Radio panel, or in another tab, left the Classic and Experimental sliders where they were. They now follow. The IC-7300's own MIC knob still does not reach IWC: nothing reads the radio's mic gain yet.

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

### v1.3.0 (2026-09-25)

> **A full release.** Download `Icom_Web_Control_Setup.exe` below and install it over the top — nothing to uninstall first, and your settings, memories, calibration and voice phrases are all kept. Everything that was in the v1.2.0-pre1 pre-release is in this build as well, so if you were waiting for the finished version, this is it.

**The headline is the RTTY Tuner**, and it is here in time for the CQ WW RTTY contest this weekend.

- **RTTY Tuner — the crossed ellipse, in the browser.** A **RTTY Tune** button on the main panel opens the classic crossed-ellipse tuning scope: two narrow filters listen to the radio's receive audio, one on the mark tone and one on the space tone, and each draws its own line. On tune you get a clean upright cross; off tune the arms lean and open out. It is a tuning aid and nothing else — it does not transmit, and it does not decode. **You already have a decoder**: the IC-7300 decodes RTTY on its own screen under **MENU » RTTY DECODE**, with no PC involved, and MMTTY and fldigi are tuned exactly the same way. What this gives you is the ability to put a signal on its tones from the browser without leaning over to watch the radio's own display. The scope itself is shared code with my Yaesu app, so both draw an identical figure from identical audio. User Manual, [§20](USER_MANUAL.md#20-rtty).

- **Mark and Shift stay in step with the radio, both ways round.** While the tuner is open it re-reads **SET » Function » RTTY Mark Frequency** and **RTTY Shift Width** every few seconds, so changing the shift on the radio changes the tuner with it. Change it in the tuner instead and the radio's menu follows, so its built-in decoder does not have to be set a second time by hand. The shifts the tuner offers — 170, 200 and 425 Hz — are exactly the ones the radio has, so a shift always goes across; mark is the looser of the two, since the menu holds 1275/1615/2125 Hz only, and a mark off those three is used by the tuner and left off the radio, with the status line saying which of the two it managed. **It follows, but it does not argue:** the moment you type your own Mark or Shift it stops replacing your figure, for the rest of the session and across a reload. A utility station on 425 Hz shift while the radio's menu still says 170 is the ordinary case, and a tuner that dragged you back every four seconds would be useless for exactly the signals this scope is best at.

- **There is no speed setting, and you have not missed it.** Amateur RTTY is 45.45 baud and utilities run 50 or 75, so it is a fair question — but two narrow filters listening for two tones do not care how fast the tones are being keyed. The radio does not offer speed either: its decoder is fixed at 45.45 baud and there is no CI-V command for it.

- **The app can be told to keep running with no browser open.** It has always shut itself down about 30 seconds after the last tab disconnected, which is right when closing the browser is how you finish — and wrong whenever something other than a browser is the reason it is up: WSJT-X or Log4OM driving the radio over the rigctld bridge, a DX cluster feed left collecting spots, or a shack PC you drive over RDP. Until now the only answer was to leave a tab open somewhere and remember not to close it. **Settings → Web Server** now has a switch. It is **on by default**, so nothing changes unless you go looking for it, and it takes effect immediately — no restart.

- **When the radio refuses to send scope data, the app now says so where you are actually looking.** IWC already knew exactly which radio setting was to blame — on the original IC-7300 it is nearly always the CI-V USB baud rate — but it said so only as text painted on the spectrum panel, which a screen reader cannot reach and which cannot be copied into a bug report. The **About** diagnostics block, the very thing the manual asks you to paste, could say no more than "no sweep has ever arrived": a mystery, and it produced one. The About block, the **Diagnostics** page panel and the spectrum badge now all name the cause and the menu item to change. Reported as [#47](https://github.com/mm5agm/Icom_Web_Control/issues/47). User Manual, [§11](USER_MANUAL.md#11-diagnostics).

- **The band scope recovers on its own when the radio stops sending waveform.** The trace could freeze and stay frozen until the app was restarted. IWC now notices the waveform stream has stopped and asks the radio for it again. The underlying reason the radio stops is still not known — if you see the trace freeze, please say what you were doing at the time.

- **A blank band scope now says why, where you are looking.** Opening the radio's own **SET** menu stops it sending scope data, which is the radio working as designed and not a fault — but IWC simply showed an empty panel. It now tells you plainly that the SET menu is what has stopped it, rather than leaving you to work out whether something is broken.

- **The automatic mode change when tuning can be switched off.** Clicking the spectrum or a DX spot sets the mode the band plan gives for that frequency, which is helpful until it is not: **RTTY has no band-plan segment of its own**, so 17m RTTY sits inside the FT8 segment and a click there answers with DATA-U and takes you straight out of RTTY. Contest weekends and 40m SSB around 7.050 have the same problem. **Settings → §6.1**, *Change mode automatically when tuning from the band plan*. Picking a named segment from a VFO band dropdown still sets that segment's mode either way. User Manual, [§5.4](USER_MANUAL.md#54-spectrum-display).

- **Fixed: the Radio & CAT dropdowns on the Settings page were sized to the page rather than to their contents**, so a long COM port description was cut off.

**Also in this release, first published in v1.2.0-pre1** and described in full under [that entry](#v120-pre1-2026-09-22): **Find my radio** and the COM-port list, so a first-time setup no longer asks you to know which port your radio is on; the **original IC-7300 menu checklist** on the Settings page, including the CI-V baud item that catches everybody; a **tuning step you can set**, 1 Hz to 1 MHz per VFO, set four different ways; **memory ordering** on the Memories page, which is how you choose which 99 go to the radio; and an **update banner that tells you what is in the release** and that keeps pre-release testers informed.

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

---

## Fixes up to v1.6.0

The README's *Fixed since the last release* table, moved here unchanged at v1.6.0.

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
