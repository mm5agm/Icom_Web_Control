# Voice Control

> **This document used to describe an Amazon Alexa setup procedure (Echo device → Cloudflare tunnel → IWC). That approach has been retired in favour of a much simpler local solution.**
>
> Voice control in IWC now uses **Windows' built-in speech recognition (SAPI 5 / `System.Speech`)** running locally on the same PC as IWC. A microphone connected to the PC is the only hardware requirement — no Echo, no Alexa Skill, no domain, no Cloudflare account, no public endpoint. Recognised audio never leaves your computer.
>
> Voice control has been part of IWC since its first release, **v1.0.0** (2026-08-01), and has been extended in most releases since. See [README.md](README.md#release-notes) for what each release changed.

## Where to find the current documentation

- **[USER_MANUAL.md §17 Voice Control](USER_MANUAL.md#17-voice-control)** — full description of what voice does, the supported command set, how to enable it, the en-GB Windows speech-pack install step, and troubleshooting.
- **[USER_MANUAL.md §5.16 Voice Announcements](USER_MANUAL.md#516-voice-announcements)** is the separate feature that makes IWC *speak to you* (band/mode/TX cues). Voice Control (§17) is *you speaking to IWC*. The §17 introduction includes a callout disambiguating the two.

## What happened to the Alexa walkthrough?

The original 220-line setup procedure (covering Amazon Developer Console, Cloudflare DNS migration, Alexa Skill JSON, `cloudflared` install, signature verification, etc.) predates the carve, so it is preserved on the `feature/alexa-voice-control` branch of **[Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control/blob/feature/alexa-voice-control/VOICE_CONTROL.md)**, not here. There is no such branch in this repository.

The proof of concept on that branch did work end-to-end including signature verification — a real Echo device successfully drove a local IWC over a Cloudflare tunnel — but the setup commitment (well over an hour of fiddly configuration per user) was too high for the audience. The local SAPI 5 approach takes about two clicks in IWC Settings, runs offline, and is the supported voice-control path going forward.

The branch is retained for reference in case any future work needs to revisit the Alexa route or borrow patterns from the signature-verification code there.
