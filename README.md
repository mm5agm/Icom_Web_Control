# Icom Web Control

![Status](https://img.shields.io/badge/Status-released-brightgreen?style=flat-square)
![Licence](https://img.shields.io/badge/Licence-GPL--3.0-blue?style=flat-square)
![Latest release](https://img.shields.io/badge/Download-v1.0.0-brightgreen?style=flat-square)
![Downloads](https://img.shields.io/github/downloads/mm5agm/Icom_Web_Control/latest/Icom_Web_Control_Setup.exe?label=Downloads&style=flat-square)

> **v1.0.0 — first public release.** IWC controls an **Icom IC-7300 MkII** end-to-end: frequency/mode, S-meter and Po/SWR/ALC, PTT, band/VFO/split, RF power, the RX DSP panel, the CI-V spectrum scope, ATU, voice control, and a rigctld bridge for WSJT-X. It has been developed and tested against a single IC-7300 MkII by one operator, so if anything behaves unexpectedly please report it. I'm building Icom Web Control (**IWC**) as a sibling to my [Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control) (YWC) project, for Icom CI-V transceivers. The two are deliberately separate applications with separate repositories — YWC stays Yaesu-only, IWC stays Icom-only.
>
> **[⬇ Download the latest installer](https://github.com/mm5agm/Icom_Web_Control/releases/latest)**

## 📦 Releases and pre-releases

Some entries on the [releases page](https://github.com/mm5agm/Icom_Web_Control/releases) are marked **Pre-release**. Those are my ongoing work — bug fixes and improvements published as I finish them, rather than held back for weeks until there is enough for a numbered release.

They have had less testing than the full releases. If you want the quiet life, use the **⬇ Download** link above: GitHub's `latest` always points at the newest *full* release and skips pre-releases entirely.

If something has been biting you and a pre-release says it is fixed, or you want the newest features and do not mind the odd rough edge, go ahead and install it — and please tell me how you get on. That feedback is what turns a pre-release into a full one.

**Pre-releases never nag you.** IWC's in-app update banner only ever announces a full release, so if you don't go looking for a pre-release you will never be told one exists. Trying one is always a deliberate trip to the releases page. (GitHub's own **Watch → Releases** notifications are a separate thing and *do* include pre-releases — see [Staying informed about updates](#staying-informed-about-updates) below.)

## What this is

IWC is a web-based control panel and panadapter for Icom transceivers, cloned from YWC and re-fitted for Icom's CI-V protocol. The plumbing YWC already got right — the real-time SignalR pipeline, the meter gauges, the spectrum display, the settings and rigctld bridge, and the voice control — is being kept; the Yaesu CAT layer is being replaced with a fresh CI-V layer behind a clean radio-control seam.

**Voice control is a first-class requirement, not an add-on** — several of the operators I build for are partially sighted, so hands-free operation matters from day one.

## First target radios: Icom IC-7300 and IC-7300 MkII

Both are single-receiver HF + 6 m (+ 4 m on European versions) transceivers that speak Icom's CI-V protocol and stream a spectrum scope **over CI-V** (`27 00`) — so no external SDR is needed, unlike some setups.

- **IC-7300 MkII** — the primary bench radio, tested end-to-end. CI-V over USB Type-C, default address `B6`, 475-point scope. Its rear LAN port offers a faster scope feed later.
- **IC-7300** (original) — near-identical over CI-V, default address `94`, CI-V over USB Type-B. Expected to work but not yet bench-tested; if you have one, please try it and tell me how it goes (set the CI-V address to `94` in Settings).

Other Icom CI-V radios (IC-705, IC-7610, IC-9700, …) share the same protocol family and will be added if there is a user that will do testing of the radio functions- not programming

## Status & plan

**`v1.0.0` is the first public release** — it controls an IC-7300 MkII end-to-end (see the summary at the top), tested against a single radio. The full build plan — how IWC is carved out of YWC, what's kept, what's rebuilt, and the phased CI-V roadmap — lives in [docs/design/iwc-clone-split-plan.md](docs/design/iwc-clone-split-plan.md).

## Release notes

### v1.0.2 (unreleased) — pre-release

Band-plan accuracy and a visible start-up. Nothing here changes how the radio is driven.

- **Band buttons and the toolbar now use *your* band plan.** IWC had two sets of band edges and only one of them knew about your region: the display used a single hard-coded worldwide table, so a UK operator on 3.900 MHz was told "80m" even though that is outside the Region 1 allocation. Both now resolve against the **Band Plan** you chose in Settings.
  - **Behaviour change to expect:** on frequencies outside your region's allocation the band button no longer lights up normally — it turns **red**, on whichever band you were nearest to. Region 1 operators will see this above 3.800, above 7.200, and below 1.810 MHz, where the old table let those frequencies pass as in-band. That is the correct answer, not a fault.
  - DX-spot filtering and the spectrum's band shading are unaffected — they deliberately use worldwide envelopes.
- **The Segment dropdown now shows where you actually are.** It tracks the live frequency wherever it comes from (spectrum click, front-panel knob, on-screen keyboard), and it is bounded by the band edges: previously the highest segment kept claiming your frequency however far above the band you tuned. Out of band it reads **OOB** on red, and selecting it can no longer tune the radio.
- **A proper start-up screen.** The "Initialising" overlay was never actually styled — it rendered as a strip at the top of a half-built page. It is now a full-screen panel that stays up until the spectrum appears, so the layout stops rearranging itself under you. A **Continue anyway** button is there if you ever need it.
- **The spectrum appears sooner** when you open IWC in a new tab or reload the page. The panel was waiting for a periodic status broadcast that could be up to 29 sweeps away; a browser that connects now gets one on the next sweep.
- **The update banner is now guaranteed to ignore pre-releases.** It already only asked GitHub for the newest *full* release, but that was an unwritten assumption; it is now documented, guarded in code, and written into the project's rules so it can't drift. Nothing changes for you — pre-releases stay something you go and fetch on purpose.

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

## 📖 Why This Application Exists

I'm building this for the same reason I built [Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control) — I can't see my radio's controls without a magnifying glass. On top of that, Icom has tucked a lot of the IC-7300's controls away on menu pages that you reach through the touchscreen, and I find getting to the page I want a bit hit-and-miss — sometimes I land on it, most times I don't. Could just be fat-finger syndrome. Accessibility is a first-class goal: support for partially sighted users through NVDA and Windows Narrator, and **voice control** so the radio can be operated hands-free. As a ham who uses WSJT-X, JTAlert, and Log4OM, I like being able to start them from the app rather than opening each one separately. It carries over YWC's memory channel banks and the functions to read and save them — you don't need to save to the transceiver unless you specifically want them on it (taking the radio to another location, for example). Please read the settings carefully, as you can overwrite the transceiver's memories.

Tablet testing has been limited — feedback from tablet users is particularly welcome.

## 🌱 Why Sponsorship Matters

I'm retired and maintain this project on a limited income, funding all development tools personally. AI-assisted coding has been invaluable for building features quickly, but it isn't free.

If this project has helped you, please consider sponsoring it. Even small contributions make a real difference and help keep the development tools running.

## 🎨 Skins and appearance

IWC's look is being built around swappable **skins** — a planned feature that will let you restyle the whole panel (layout, controls and colours), including a front-panel replica of the radio. If there's a skin or a look you'd like to see, I'd love to hear about it. Please post your suggestions in [Discussions](https://github.com/mm5agm/Icom_Web_Control/discussions) so other users can add to them, or send them to me directly.

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

IWC carries over YWC's in-app update check — a banner appears the first time you run it after a new **full** release lands. Pre-releases are never announced in the app. To also be notified outside the app, **any one of these works** (note that both of these *do* fire for pre-releases, unlike the in-app banner):

- **GitHub release notifications** (most reliable, free, no spam):
  1. Make sure you're signed in to GitHub
  2. Visit https://github.com/mm5agm/Icom_Web_Control
  3. Click the **Watch** dropdown at the top-right of the page
  4. Choose **Custom** and tick only **Releases**
  5. Save — you'll get one email per release, and nothing in between

- **RSS / Atom feed** — if you use a feed reader (Feedly, NewsBlur, Inoreader, Thunderbird, etc.), subscribe to https://github.com/mm5agm/Icom_Web_Control/releases.atom — new releases appear in your reader without any account or email signup.

## ⚠️ Warning

**IWC keys your transmitter.** It has been developed and tested against a single IC-7300 MkII by one operator — yours may be the second radio it has ever seen. It uses only the official Icom CI-V commands as documented, but no software is bug-free and **you use it entirely at your own risk.**

- **Test into a dummy load first.** Confirm transmit, power, and mode behave before you put a signal on air.
- **Always verify transmit frequency, power level, and mode** before transmitting — do not assume the app and the radio agree.
- **If Voice control is enabled it keys the radio.** A misheard command can start a transmission; keep an eye (and ear) on the radio's TX state.
- **Memory operations can overwrite your transceiver's memories.** Read the Settings and the memory functions carefully before saving to the radio.

If something looks wrong, stop transmitting and check the radio's own display. Please report anything you find on the [GitHub issues page](https://github.com/mm5agm/Icom_Web_Control/issues).

## Licence

GPL-3.0, the same as YWC. See [LICENSE](LICENSE).

---

*Colin Campbell, MM5AGM*
