# Icom Web Control

![Status](https://img.shields.io/badge/Status-released-brightgreen?style=flat-square)
![Licence](https://img.shields.io/badge/Licence-GPL--3.0-blue?style=flat-square)
![Latest release](https://img.shields.io/badge/Download-v1.0.3-brightgreen?style=flat-square)
![Downloads](https://img.shields.io/github/downloads/mm5agm/Icom_Web_Control/latest/Icom_Web_Control_Setup.exe?label=Downloads&style=flat-square)

> **v1.0.3 — current release.** IWC controls an **Icom IC-7300 MkII** end-to-end: frequency/mode, S-meter and Po/SWR/ALC, PTT, band/VFO/split, RF power, the RX DSP panel, the CI-V spectrum scope, ATU, voice control, and a rigctld bridge for WSJT-X. It has been developed and tested against a single IC-7300 MkII by one operator, so if anything behaves unexpectedly please report it. I'm building Icom Web Control (**IWC**) as a sibling to my [Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control) (YWC) project, for Icom CI-V transceivers. The two are deliberately separate applications with separate repositories — YWC stays Yaesu-only, IWC stays Icom-only.
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

**`v1.0.3` is the current release**, and `v1.0.0` was the first — IWC controls an IC-7300 MkII end-to-end (see the summary at the top), tested against a single radio. The full build plan — how IWC is carved out of YWC, what's kept, what's rebuilt, and the phased CI-V roadmap — lives in [docs/design/iwc-clone-split-plan.md](docs/design/iwc-clone-split-plan.md).

## Release notes

### v1.0.4 (2026-08-07) — pre-release

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
