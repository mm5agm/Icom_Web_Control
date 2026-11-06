# Testing Icom Web Control on a Raspberry Pi, Linux or a Mac

These builds are for testing. They are radio control only: the Voice, CW and RTTY
audio pages and Remote Audio are not in them yet. They come with pre-releases, not
full releases.

## Before anything else: the USB lines

On Linux and macOS, opening a USB serial port raises its DTR and RTS lines. Nothing
in software can stop that. If the radio has USB SEND or USB Keying set to use either
line, it goes into transmit as soon as the port opens, and stays there.

So set these to **OFF** on the radio before you start. The path to them is under
Menu > SET > Connectors on the radios that have them.

| Radio | Items that must be OFF |
| --- | --- |
| IC-7300 and IC-7300MK2 | USB SEND, USB Keying (CW), USB Keying (RTTY) |
| IC-7100 | Its manual lists no such items. Have an antenna or dummy load connected and watch the radio the first time. |

Icom Web Control won't open the port until you confirm this in Settings. After it
opens, it asks the radio whether it is transmitting and reads those items back. If the
radio is transmitting, or an item isn't OFF, or an item can't be read, it closes the
port and tells you why. The check can only happen after the port has opened, so it
can't replace setting the items yourself.

## Raspberry Pi or Linux PC, with Docker

The Pi needs a 64-bit OS, and Docker with the compose plugin.

1. Plug in the radio and turn it on. Find its port:

   ```
   ls -l /dev/serial/by-id/ /dev/ttyUSB* /dev/ttyACM*
   ```

   A radio with two USB serial ports shows two. CI-V is normally the first (ttyUSB0).

2. Get the compose file and start it, using your port:

   ```
   mkdir -p ~/iwc && cd ~/iwc
   curl -fsSLO https://raw.githubusercontent.com/mm5agm/Icom_Web_Control/develop/docker-compose.yml
   export IWC_SERIAL_DEVICE=/dev/ttyUSB0
   docker compose pull
   docker compose up -d --no-build
   ```

3. From any computer on your network, open `http://<address of the Pi>:8081`.

4. In Settings choose the serial port and your radio, save, then read the USB lines
   panel and press the button if the items are OFF.

To watch what it is doing: `docker logs -f iwc`. Settings and logs are in `./data/iwc`.
To stop it: `docker compose down`.

If the port won't open and the log says permission denied, check the number from
`getent group dialout` and start it with `IWC_DIALOUT_GID` set to that number.

The host port is 8081 so that this can run beside Yaesu Web Control. Set
`IWC_HTTP_PORT` to use another.

## Mac

Docker on a Mac can't reach USB, so use the DMG from the pre-release's assets:
`macos-arm64` for Apple silicon, `macos-x64` for Intel.

1. Open the DMG and drag Icom Web Control to Applications.
2. The app isn't signed, so macOS blocks it the first time. In Terminal:

   ```
   xattr -dr com.apple.quarantine "/Applications/Icom Web Control.app"
   ```

3. Open it. Your browser opens the app's page. There is no Dock or menu bar icon, and
   it quits when you close its last browser tab.
4. In Settings choose the port (the `/dev/cu.` one) and your radio, save, then read
   the USB lines panel and press the button if the items are OFF.

If you have turned off quitting with the last tab in Settings, quit it from Activity
Monitor.

## What to report

- Which radio, which computer and which build (the version is on the About page).
- Whether the frequency and mode follow the radio, and whether changing them works.
- Anything in the log: `docker logs iwc`, or the logs folder under
  `~/Library/Application Support/MM5AGM/Icom Web Control` on a Mac.
