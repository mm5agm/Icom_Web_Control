# Voice Control

> **This document used to describe an Amazon Alexa setup procedure (Echo device → Cloudflare tunnel → IWC). That approach has been retired in favour of a much simpler local solution.**
>
> Voice control in IWC now uses **Windows' built-in speech recognition (SAPI 5 / `System.Speech`)** running locally on the same PC as IWC. A microphone connected to the PC is the only hardware requirement — no Echo, no Alexa Skill, no domain, no Cloudflare account, no public endpoint. Recognised audio never leaves your computer.
>
> Voice control v1 shipped in **v2.4.0-pre1** (2026-06-24) and is part of the current pre-release v2.4.0-pre2.

## Where to find the current documentation

- **[USER_MANUAL.md §17 Voice Control](USER_MANUAL.md#17-voice-control)** — full description of what voice does, the supported command set, how to enable it, the en-GB Windows speech-pack install step, and troubleshooting.
- **[USER_MANUAL.md §5.16 Voice Announcements](USER_MANUAL.md#516-voice-announcements)** is the separate feature that makes IWC *speak to you* (band/mode/TX cues). Voice Control (§17) is *you speaking to IWC*. The §17 introduction includes a callout disambiguating the two.

## What happened to the Alexa walkthrough?

The original 220-line setup procedure (covering Amazon Developer Console, Cloudflare DNS migration, Alexa Skill JSON, `cloudflared` install, signature verification, etc.) is preserved on the `feature/alexa-voice-control` branch:

```
git checkout feature/alexa-voice-control -- VOICE_CONTROL.md
```

The proof of concept on that branch did work end-to-end including signature verification — a real Echo device successfully drove a local IWC over a Cloudflare tunnel — but the setup commitment (well over an hour of fiddly configuration per user) was too high for the audience. The local SAPI 5 approach takes about two clicks in IWC Settings, runs offline, and is the supported voice-control path going forward.

The branch is retained for reference in case any future work needs to revisit the Alexa route or borrow patterns from the signature-verification code there.
