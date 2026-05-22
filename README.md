
# Yaesu Web Control

> I would appreciate feedback and bug/layout reports. I have only tested on the FTdx101MP and the spectrum display with the SDRplay RSP1B.

This is a continuation of my FTdx101_WebApp with more Yaesu transceivers added and more controls.

**Supported transceivers:**

| Transceiver | Power | Receivers | Notes |
|-------------|-------|-----------|-------|
| FTdx101MP | 200 W | Dual | All features supported |
| FTdx101D | 100 W | Dual | All features supported |
| FTdx10 | 100 W | Single | Two VFOs; no rear-panel IF output for spectrum |
| FT-710 | 100 W | Single | Two VFOs; no rear-panel IF output for spectrum |
| FTDX3000 | 100 W | Single | Two VFOs; no memory tag (MT) command |

## Main Page
![Yaesu Web Control Main Page](pictures/DevelopScreen.png)

## Calibration Page
![Calibration Page](pictures/Calibration.png)

## ⚠️ Warning

This software interacts with radio hardware. I have used only the official Yaesu CAT commands as per the manual, however, you use entirely at your own risk. Please read the licence. Always verify transmit frequencies, power levels, and settings before use.

---

## 📖 Why This Application Exists
I wrote this application because I can't see the FTdx101MP controls without using a magnifying glass. I've added support for partially sighted users by utilising NVDA and windows narrator. As a ham who uses WSJT-X, JTAlert, and Log4OM, I thought it would be nice to add buttons to start them from the app as it saves openning up the individual programs. I've added memory channel banks and functions to read and save etc. You don't need to save to the transceiver unless you specifically want them on it, taking your transceiver to another location for example. Please read the settings carefully as you can overwrite the transceivers memories.  

Tablet testing has been limited — feedback from tablet users is particularly welcome.

---

## 💬 Community

Join the discussion group for announcements, bug reports, and feedback:

- **Groups.io:** [Yaesu-Web-Control](https://groups.io/g/Yaesu-Web-Control/topics)

---

## 🌱 Why Sponsorship Matters

I’m retired and maintain this project on a limited income, funding all development tools personally. AI‑assisted coding has been invaluable for building features quickly, but it isn’t free. 

If this project has helped you, please consider sponsoring it. Even small contributions make a real difference and help keep the development tools running.


---

## Important - .NET 10 is now built into this app so there is no need to download and install it.

---

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

---

## 📡 Spectrum Display

The application includes a real-time spectrum display and waterfall, intended for use with a Software Defined Radio (SDR) connected to the transceivers 9 MHz IF output on the rear panel if it has one.

**Supported SDR devices:**

- **SDRplay RSP1 (and other RSP series)** — supported via the SDRplay API v3. The SDRplay API must be installed separately from [sdrplay.com](https://www.sdrplay.com/downloads/). This is the only SDR the author has tested.
- **RTL-SDR, Airspy, and HackRF** — supported via the bundled SoapySDR driver interface. No separate SoapySDR installation is required — the necessary drivers are included in the installer. *These devices have not been tested by the author — feedback from users is very welcome.*

**Features:**
- Variable span: 250 kHz, 500 kHz, 1 MHz, or 2 MHz
- Click anywhere on the spectrum to tune VFO A to that frequency
- Mouse wheel over the spectrum tunes VFO A up/down in 1 kHz steps
- Frequency axis labels automatically track VFO A

---

## Release Notes

## 2026-05-22 - v1.5.0

### Added

- **FT-710 and FTDX3000 support** — the app now supports the FT-710 and FTDX3000 in addition to the FTdx101MP, FTdx101D, and FTdx10. Select your radio in Settings. The FTDX3000 supports split operation; the memory tag (MT) command is not available on that model.
- **Split frequency and Swap VFO** — a Split button enables split TX/RX operation (transmit on VFO B while receiving on VFO A). A Swap button exchanges the VFO A and VFO B frequencies in one click.
- **Clarifier** — the clarifier (RIT/XIT) offset is now displayed and controllable from the main panel.
- **Radio Memories panel** — a new collapsible Memories panel on the main page shows a summary of your stored memories. Click Edit to open the full Memories editor.
- **Memories page** — a dedicated page for managing radio memory channels: add, edit, and delete entries, import all channels from the radio, and export to a JSON file for backup.
- **Save to Mem buttons** — each VFO panel has a Save to Mem button that saves the current frequency and mode to a memory channel in one click.
- **Memory Banks** — on the Memories page you can save the current set of memories as a named bank (e.g. "Contest", "Daily"), then load or delete banks. Useful for switching between different operating setups without re-entering frequencies.
- **Viewport-too-narrow warning** — a dismissible banner appears when the browser window is narrower than the minimum supported width, with a suggestion to zoom out. It hides automatically when the window is widened.

### Fixed

- **Memory import returning 0 channels** — the import used the recall command (`MR{ch}0;`) instead of the read command (`MR{ch};`). The radio silently ignored the recall form, so all 100 channels imported blank. All channels now import correctly.
- **isFtdx10 ReferenceError** — a JavaScript error fired when toggling VFO-B visibility on non-FTdx10 models if the VFO-B script ran before the model variable was set. Fixed.
- **Memories panel drag handler hijacking Edit link clicks** — clicking the Edit navigation link in the memories panel was sometimes intercepted by the drag handler. Fixed.
- **Memories frequency input** — the memories editor was expecting raw Hz values; it now accepts MHz (e.g. 14.074) matching the rest of the UI.
- **Delete-all memories** — deleting all memories left a stale count in the panel header. Fixed.

### Changed

- **App renamed to Yaesu Web Control** — the application was previously named FTdx101_WebApp. It is now Yaesu Web Control throughout the UI, documentation, and file paths. Settings stored under `%APPDATA%\MM5AGM\Yaesu Web Control\` are migrated automatically on first run.

---

## 2026-05-17 - v1.4.0

### Added

- **Roofing filters per model (Settings)** — the Settings page now shows the correct roofing filter information for each radio. The FTdx101MP comes fully loaded with all five filters as standard (12 kHz, 3 kHz, 1.2 kHz, 600 Hz, 300 Hz) — no configuration needed. The FTdx101D has 12 kHz, 3 kHz, and 600 Hz as standard, with checkboxes to tick the optional 1.2 kHz and 300 Hz filters if installed. The FTdx10 section explains that its roofing filter is selected automatically by the radio based on DSP bandwidth and mode, with informational checkboxes for the optional YF-130CN (1.2 kHz) and YF-130CW (300 Hz) filters.
- **VFO-B show/hide toggle** — the **VFO-B** button in the toolbar now works: click it to collapse or reveal the VFO B panel. The last state is remembered across sessions.
- **IF Width Reset button** — a **Reset** button next to the IF Width dropdown (for both VFO A and VFO B) resets IF Width to the widest bandwidth in one click, matching the Zero button that already exists for IF Shift. *(Subsequently removed — the dropdown already provides direct access to every option including the default.)*
- **FTdx10 IF Width options** — the FTdx10 now shows the correct IF Width options (400 Hz – 3.4 kHz, 16 steps), replacing the FTdx101 values that were shown previously.

### Fixed

- **Mouse wheel tuning without clicking a digit** — wheeling the mouse over the VFO frequency display no longer requires clicking a digit first. Wheeling now automatically selects the 1 kHz digit and begins tuning. Previously, wheeling without a prior click was silently ignored (felt like a lockup).
- **Frequency keyboard locale bug** — on European locales where `.` is a thousands separator, NVDA would read "28.000000 megahertz" as "28 million megahertz". The announcement now strips trailing zeros (e.g. "28 megahertz" or "14.074 megahertz").
- **Segment dropdown double-announcement** — hovering the band segment dropdown caused NVDA to announce the selected option twice (once from the live region, once from NVDA's own select handling). The live region no longer duplicates the selected option text for dropdowns.
- **TX-only meters not announcing a value** — hovering the VDD, IDD, or Compression meter canvases before the radio had transmitted would announce the meter name only, with no reading. A "—" placeholder is now shown until the first real reading arrives.
- **PA Temperature showing stale value on startup** — the temperature meter previously displayed the persisted value from the previous session on startup, which could appear unrealistically high if the radio had been warm. It now shows "—" until the first live reading arrives from the radio.
- **Roofing filter dropdown direction** — the roofing filter now lists options narrow-to-wide (300 Hz → 12 kHz) to match the IF Width dropdown direction.
- **FTdx10 roofing filter removed from VFO panels** — the FTdx10 selects its roofing filter automatically based on mode and DSP bandwidth; there is no CAT command to control it. The dropdown has been removed from the VFO panels for FTdx10 users.
- **Navigation bar inaccessible to screen readers on non-main pages** — the navigation bar was hidden from the accessibility tree on every page (Settings, User Manual, Diagnostics, etc.), making it impossible for NVDA or Narrator users to navigate between pages. It is now only hidden on the main control panel page, where the omission is intentional.

---

## 2026-05-12 - v1.3.2

### Fixed

- **FTdx10 Settings badge** — the Current Configuration panel on the Settings page was showing an incorrect configuration for the FTdx10. It now correctly shows "100W · Single RX". The FTdx10 has two VFOs (used for split operation and easy frequency switching) but only a single receiver — it cannot receive on two frequencies simultaneously.

---

## 2026-05-12 - v1.3.1

### Fixed

- **FTdx10 VFO B panel** — the FTdx10 has VFO A and VFO B (used for split operation and memory), but only a single receiver — it cannot receive on two frequencies simultaneously. The VFO B panel is shown so that split TX/RX and memory operation are accessible.

---

## 2026-05-12 - v1.3.0

### Added

- **Accessibility Labels editor** — a new **Accessibility Labels** page (available from the navigation bar) provides a web-based editor for all screen reader labels. Labels are grouped into sections (Band Buttons, Meters, VFO Controls, Frequency Keyboard, Radio Controls, Spectrum Display, Navigation) and can be edited and saved without touching any files. Changes take effect automatically when you switch back to the main page. A **Reset to Defaults** button restores all labels in one click.
- **Spectrum display labels** — the RF spectrum canvas and the four span buttons (250k, 500k, 1M, 2M) are now included in the Accessibility Labels editor.
- **Navigation bar label** — the application home link in the navigation bar is now included in the Accessibility Labels editor.

### Improved

- **NVDA meter announcements** — meter gauges are now hidden from NVDA's accessibility tree (`aria-hidden`). An ARIA live region takes over all meter announcements. When you hover over a meter, NVDA announces the meter name (from your saved label) followed by the current reading — for example, *"Amplifier supply voltage meter: 50.2 V"*. This fixes a long-standing bug where canvas-gauges was re-injecting its own `title` attribute at 10 Hz, overriding any label the user had saved.
- **No announcements on startup** — the main control panel now uses `role="application"`, which prevents NVDA from reading the page in browse mode on load. The navigation bar is hidden from the accessibility tree, so the list of page links is no longer announced when the app opens.
- **Label changes take effect without F5** — after saving labels on the Accessibility Labels page, switching back to the main page automatically reloads the labels without a manual refresh.
- **Frequency keyboard button** — the keyboard open button now uses a numeric (⑁) icon for clearer visual identification.

### Fixed

- **Attenuator (ATT)** — the CAT command format was wrong. The FTdx101 uses a single-digit code (0–3) but the app was sending and parsing the dB values (00/06/12/18) directly. ATT changes now work correctly in both directions.
- **IF Width** — the `SH` command format was wrong (missing leading zero and incorrect digit count). IF Width changes and restores on startup now work correctly.
- **IF Shift** — the `IS` command format was wrong (the FTdx101 uses a sign character and absolute Hz value, not a 0–9999 linear scale). IF Shift changes and restores on startup now work correctly.
- **Label saves not taking effect** — the browser was caching `labels.json` responses. The fetch now uses `cache: no-cache` to ensure the latest saved labels are always loaded.

---

## 2026-05-11 - v1.2.3

### Added

- **On-screen frequency keyboard** — a keyboard icon button (🖮) now appears next to the MHz label on each VFO panel. Click it to open a floating number pad for typing in a frequency directly. The keyboard pre-fills with the current VFO frequency, supports cursor movement, backspace, and clear, and validates the entry before sending it to the radio. The keyboard is draggable, resizable, and remembers its position and size across sessions. All keys have accessible labels for screen readers.
- **Auto-shutdown when browser is closed** — when the last browser tab is closed, the app waits 30 seconds and then exits automatically. Reopening the page within those 30 seconds cancels the shutdown.
- **Version number in navbar** — the current app version is now shown in the top-left corner of every page.

### Fixed

- **AppVersion display** — the version was showing as "vunknown" due to disabled assembly attribute generation. Now reads from a simple constant that is updated alongside the installer version.

---

## 2026-05-11 - v1.2.2

### Fixed

- **Installer no longer requires .NET 10** — the app is self-contained and bundles its own runtime. The installer was incorrectly blocking installation on machines without a system-wide .NET 10 installation.

---

## 2026-05-10 - v1.2.1

### Fixed

- **Calibration data location** — calibration.user.json was being written to the wrong AppData subfolder (`MM5AGM\FTdx101\WebApp\` instead of `MM5AGM\Yaesu Web Control\`). It now lands in the correct folder alongside appsettings.user.json and radio_state.json.
- **Labels file** — labels.json is now copied to `%APPDATA%\MM5AGM\Yaesu Web Control\` on first run so users can easily locate and edit it.

---

## 2026-05-10 - v1.2.0

### Added

- **FTdx10 support** — the app now works with the Yaesu FTdx10 as well as the FTdx101MP and FTdx101D. Select FTdx10 in Settings to enable it.
  - VFO B panel and its toggle button are hidden automatically (the FTdx10 has one VFO).
  - Power slider limited to 100 W.
  - SDR Settings page shows a notice that the FTdx10 has no rear-panel IF output.
- **Band button keyboard navigation** — Tab moves focus into the band group; Left/Right arrow keys move between bands and switch immediately. Correct `radiogroup` ARIA semantics applied.
- **User manual** — comprehensive built-in user manual covering all features, external application setup, meter calibration, diagnostics, and accessibility.
- **Diagnostics page** — live meter readings table (raw 0–255 values, CAT command, last-updated time) and a scrollable SignalR event log with per-property filtering, pause, clear, and save-to-file controls.

### Fixed

- **SWR calibration** — corrected to use the reflection-coefficient formula so SWR readings now scale accurately from raw CAT values.
- **Band button screen reader support** — NVDA and Windows Narrator now consistently announce the full band name (e.g., "20 metres, radio button") when hovering over or focusing a band button. Previously NVDA would sometimes read the short label ("20m") or nothing.
- **Accessible labels** — removed abbreviations that caused screen readers to mispronounce meter names (e.g., "PA" expanded to "Power Amplifier" by NVDA).

### Changed

- **SDR Settings** — description updated to clarify that the FTdx10 has no IF tap, and that an antenna-connected SDR will show absolute RF frequencies rather than a VFO-centred view.

---

## 2026-04-22 - v1.1.0

### Fixed

- **AF Gain slider** — no longer jumps back to its previous position after release. The slider now sends the CAT command directly to the radio instead of only updating internal state.

### Added

- **IF Shift zero-reset button** — a Zero button next to each VFO's IF Shift slider resets it to centre instantly.

### Changed

- **IF Width and IF Shift** values are now persisted across restarts and restored to the radio on startup.
- **Slider appearance** — Power, MIC Gain, and AF Gain sliders now use the native browser appearance for a cleaner, more consistent look.
- **Auto Notch / Man Notch dropdowns** widened so the full option text is visible without the dropdown arrow overlapping it.

---

## 2026-04-21 - v1.0.0

### Added

- **Band segment dropdown** — each VFO now has a Segment selector (CW / FT8 / SSB / RTTY)
  that tunes directly to the standard frequency for that segment on the current band.
  UK and USA band plans are selectable in Settings. 60m shows named channels.
  Last-used segment per band is remembered across sessions.
- **Noise Blanker (NB)** — ON/OFF control added to both VFO panels alongside NR.
- **Manual Notch frequency slider** — continuously adjustable 10–3200 Hz slider
  added below the Manual Notch on/off control for both VFOs.
- **Spectrum crosshair** — hover over the spectrum to see the exact RF frequency
  at the cursor position.

### Changed

- CAT initialisation sequence trimmed from ~100 commands to 43, reducing startup time.
- Band plan (UK/USA) setting added to the Settings page.

---

## 2026-04-17 - v0.9.0 RC1

This is a release candidate for what may be the final major release. Please test and report any issues via the Groups.io group.

### Added

- **Spectrum display and waterfall** — real-time spectrum and scrolling waterfall via SDRplay RSP1 (or any SoapySDR-compatible device) connected to the FTdx101MP 9 MHz IF output.
  - Variable span: 250 kHz, 500 kHz, 1 MHz, or 2 MHz
  - Click on the spectrum to tune VFO A to that frequency
  - Mouse wheel over the spectrum tunes VFO A up/down in 1 kHz steps
  - Frequency axis labels track VFO A in real time
  - Centre frequency displayed at the top of the spectrum

### Changed

- Layout compacted throughout to fit on a single screen without scrolling
- Mic Gain slider moved alongside Power slider
- AF Gain slider moved alongside Roofing Filter for both VFO A and VFO B
- Copyright notice moved into the navigation bar
- Application buttons row and navigation bar made more compact

---

## 2026-04-10 - v0.7.7

### Changed

- Meter gauges repositioned above the VFO panels


## 2026-04-06 - v0.7.6

### Changed

- Minor fixes and improvements
- Ctrl + F goes to full screen, ESC to get back to normal
- Updated main page screenshot to reflect new VFO controls layout.

## 2026-04-06 - v0.7.5

### Added

- **VFO controls panel** — new two-column controls section alongside the band buttons for both VFO A and VFO B:
  - **AGC** — OFF / FAST / MID / SLOW / AUTO
  - **IPO/AMP** — IPO / AMP1 / AMP2
  - **ATT** (Attenuator) — OFF / 6 dB / 12 dB / 18 dB
  - **NR** (Noise Reduction) — OFF / NR1 / NR2
  - **Auto Notch** — OFF / ON
  - **Man Notch** (Manual Notch) — OFF / ON
- All six controls are **fully two-way**: changes made on the radio front panel are reflected in the app automatically via CAT AI mode.
- Control values are **persisted** and restored on startup.
- **Buy Me a Coffee** donate button added to the toolbar (PayPal).

### Changed

- Frequency display moved below the S-meter/band buttons row to free up horizontal space for the new controls panel.
- VFO controls layout uses a compact two-column grid with bold labels and values.
- Selects return to normal appearance immediately after a value is changed (no lingering highlight).

## 2026-04-06 - v0.7.4

### Changed

- Minor fixes and improvements


## 2026-04-05 - v0.7.3

### Changed

- Add groups.io community link to README


## 2026-04-05 - v0.7.2

### Changed

- Refactor frontend: consolidate SignalR handlers and add orchestrators layer
- Release script works


## 2026-04-01 - Major Rewrite Foundation

This release marks a near-complete rewrite of the application.

### Changed

- Front-end architecture migrated to ES module-based structure.
- Gauge rendering moved to class/factory modules for clearer extension points.
- UI behavior split into focused modules to reduce monolithic script complexity.

### Improved

- Clearer separation between CAT polling, UI rendering, and calibration logic.
- Better maintainability for adding new controls and gauges.
- Lower risk of regressions when updating individual UI features.

## 2026-04-03 - Meter and Calibration Updates

### Added

- New gauges: Compression, IDD, and VDD.
- Full multi-gauge calibration editor page with per-gauge cards.
- Per-gauge Save buttons in addition to global Save Calibration.
- TX control button on the Meter Calibration page.

### Changed

- Lower-row gauge order updated to: SWR, Power, Compression, ALC, Temp, IDD, VDD.
- Calibration schema normalized to use `Radio` point values consistently.
- Calibration storage routing now supports:
	- Development save target: `wwwroot/calibration.default.json`
	- User save target: `%APPDATA%\\MM5AGM\\FTdx101\\WebApp\\calibration.user.json`

### Fixed

- IDD meter polling corrected to dedicated CAT command path.
- Power display rounding now uses integer output (no decimal noise).
- Gauge title/value width stability improved to prevent label width jumping.
- Compression/ALC behavior aligned to TX state to reduce idle-mode jumping.
- AF Gain confirmation tolerance and timeout adjusted to reduce false revert alerts.


