# Icom Web Control

![Status](https://img.shields.io/badge/Status-pre--alpha%20(not%20yet%20functional)-orange?style=flat-square)
![Licence](https://img.shields.io/badge/Licence-GPL--3.0-blue?style=flat-square)

> **Pre-alpha — this does not control a radio yet.** I'm building Icom Web Control (**IWC**) as a sibling to my [Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control) (YWC) project, for Icom CI-V transceivers. The two are deliberately separate applications with separate repositories — YWC stays Yaesu-only, IWC stays Icom-only.

## What this is

IWC is a web-based control panel and panadapter for Icom transceivers, cloned from YWC and re-fitted for Icom's CI-V protocol. The plumbing YWC already got right — the real-time SignalR pipeline, the meter gauges, the spectrum display, the settings and rigctld bridge, and the voice control — is being kept; the Yaesu CAT layer is being replaced with a fresh CI-V layer behind a clean radio-control seam.

**Voice control is a first-class requirement, not an add-on** — several of the operators I build for are partially sighted, so hands-free operation matters from day one.

## First target radio: Icom IC-7300 MkII

- CI-V over USB Type-C (default address `B6`)
- Single receiver, HF + 6 m (+ 4 m on European versions)
- Spectrum scope streamed **over CI-V** (`27 00`, 475 points) — so no external SDR is needed, unlike some setups. The MkII's rear LAN port offers a faster scope feed later.

Other Icom CI-V radios (IC-705, IC-7610, IC-9700, …) share the same protocol family and can follow once the IC-7300 MkII works end-to-end.

## Status & plan

Nothing here controls a radio yet. The full build plan — how IWC is carved out of YWC, what's kept, what's rebuilt, and the phased CI-V roadmap — lives in [docs/design/iwc-clone-split-plan.md](docs/design/iwc-clone-split-plan.md).

## 📖 Why This Application Exists

I'm building this for the same reason I built [Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control) — I can't see my radio's controls without a magnifying glass. On top of that, Icom has tucked a lot of the IC-7300's controls away on menu pages that you reach through the touchscreen, and I find getting to the page I want a bit hit-and-miss — sometimes I land on it, most times I don't. Could just be fat-finger syndrome. Accessibility is a first-class goal: support for partially sighted users through NVDA and Windows Narrator, and **voice control** so the radio can be operated hands-free. As a ham who uses WSJT-X, JTAlert, and Log4OM, I like being able to start them from the app rather than opening each one separately. It carries over YWC's memory channel banks and the functions to read and save them — you don't need to save to the transceiver unless you specifically want them on it (taking the radio to another location, for example). Please read the settings carefully, as you can overwrite the transceiver's memories.

Tablet testing has been limited — feedback from tablet users is particularly welcome.

## 🌱 Why Sponsorship Matters

I'm retired and maintain this project on a limited income, funding all development tools personally. AI-assisted coding has been invaluable for building features quickly, but it isn't free.

If this project has helped you, please consider sponsoring it. Even small contributions make a real difference and help keep the development tools running.

## ⚠️ Windows Security Warnings on First Install

*(This applies once installable builds of IWC are published — there are none yet. It's the same situation as YWC, so it's documented here ready for when the first installer lands.)*

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

Once IWC goes public and starts publishing releases, it will include the same in-app update check as YWC — a banner the first time you run it after a new release lands. Until then, **any one of these will let you know when the first builds appear**:

- **GitHub release notifications** (most reliable, free, no spam):
  1. Make sure you're signed in to GitHub
  2. Visit https://github.com/mm5agm/Icom_Web_Control
  3. Click the **Watch** dropdown at the top-right of the page
  4. Choose **Custom** and tick only **Releases**
  5. Save — you'll get one email per release, and nothing in between

- **RSS / Atom feed** — if you use a feed reader (Feedly, NewsBlur, Inoreader, Thunderbird, etc.), subscribe to https://github.com/mm5agm/Icom_Web_Control/releases.atom — new releases appear in your reader without any account or email signup.

## ⚠️ Warning

This software will interact with radio hardware. When it reaches a testable state I will use only the official Icom CI-V commands as documented, but you will use it entirely at your own risk. Always verify transmit frequencies, power levels, and settings before use.

## Licence

GPL-3.0, the same as YWC. See [LICENSE](LICENSE).

---

*Colin Campbell, MM5AGM*
