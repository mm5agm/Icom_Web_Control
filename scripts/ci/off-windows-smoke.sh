#!/usr/bin/env bash
# Starts the Linux or macOS build with the simulated radio, so no serial port is
# opened, and checks that every page and everything it links to loads. It only
# reads: nothing is pressed or saved, and the simulated radio can't transmit.
# Filenames are case-sensitive on Linux, which is what the link check is for.
set -u

export IWC_USE_STUB_RADIO=1
export IWC_NO_BROWSER=1
work="$(mktemp -d)"
export XDG_CONFIG_HOME="$work"
base=http://localhost:8080
log="$work/console.log"

dotnet build Icom_Web_Control.csproj --framework net10.0 --configuration Release --nologo --verbosity quiet || exit 1

(cd bin/Release/net10.0 && exec dotnet Icom_Web_Control.dll) >"$log" 2>&1 &
app=$!
trap 'kill "$app" 2>/dev/null' EXIT

up=0
for _ in $(seq 1 60); do
    if curl -fs -o /dev/null "$base/api/status/init"; then up=1; break; fi
    if ! kill -0 "$app" 2>/dev/null; then break; fi
    sleep 1
done
if [ "$up" != 1 ]; then
    echo "The app did not start:"
    cat "$log"
    exit 1
fi

fail=0
seen="$work/seen.txt"
: >"$seen"
for page in / /radio /flexui /Settings /Memories /About /Diagnostics /Ports /Labels \
            /Calibrations /MeterCalibration /AudioFilter /DxSpots /UserManual \
            /ApplicationSetup /CwReader /CwSend /CwPaddle /RttyTuner /RadioSpectrum \
            /RadioWindow /rtty-contest /rtty-contest/window; do
    code=$(curl -sL -o "$work/page.html" -w '%{http_code}' "$base$page")
    echo "$code $page"
    [ "$code" = 200 ] || fail=1
    grep -oE '(src|href)="/[^"]+\.(js|css)[^"]*"' "$work/page.html" \
        | sed -E 's/^(src|href)="//; s/"$//' >>"$seen"
    if [ "$page" = /Settings ] && grep -q '/api/voice' "$work/page.html"; then
        echo "Settings still calls the voice API, which isn't in this build"
        fail=1
    fi
done
for asset in $(sort -u "$seen"); do
    code=$(curl -s -o /dev/null -w '%{http_code}' "$base$asset")
    if [ "$code" != 200 ]; then
        echo "$code $asset"
        fail=1
    fi
done
echo "Checked $(sort -u "$seen" | wc -l) scripts and stylesheets."

# No port is saved here, so the page must ask for one and a confirmation must fail.
curl -s "$base/Settings" -o "$work/settings.html"
if ! grep -q 'id="usbLinesPanel"' "$work/settings.html"; then
    echo "Settings doesn't show the USB lines check"
    fail=1
fi
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' \
    -d '{"port":"","model":"IC-7300MK2"}' "$base/api/cat/usb-lines-confirm")
echo "$code usb-lines-confirm with no port"
[ "$code" = 400 ] || fail=1

if [ "$fail" != 0 ]; then
    echo "--- console ---"
    tail -n 40 "$log"
fi
exit "$fail"
