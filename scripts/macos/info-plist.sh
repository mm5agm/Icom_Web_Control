# Info.plist writer for the macOS .app. There is no menu bar icon or Dock icon
# (LSUIElement): the app is its browser tabs, and it quits when the last one closes.
#
# shellcheck shell=bash

APP_NAME="${APP_NAME:-Icom Web Control}"
BUNDLE_ID="${BUNDLE_ID:-com.mm5agm.icomwebcontrol}"
EXEC_NAME="${EXEC_NAME:-Icom_Web_Control}"

write_info_plist() {
  local plist="$1"
  local version="$2"
  local include_icon="${3:-0}"
  local executable="${4:-$EXEC_NAME}"
  cat >"$plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleDevelopmentRegion</key>
	<string>en</string>
	<key>CFBundleDisplayName</key>
	<string>${APP_NAME}</string>
	<key>CFBundleExecutable</key>
	<string>${executable}</string>
	<key>CFBundleIdentifier</key>
	<string>${BUNDLE_ID}</string>
	<key>CFBundleInfoDictionaryVersion</key>
	<string>6.0</string>
	<key>CFBundleName</key>
	<string>${APP_NAME}</string>
	<key>CFBundlePackageType</key>
	<string>APPL</string>
	<key>CFBundleShortVersionString</key>
	<string>${version}</string>
	<key>CFBundleVersion</key>
	<string>${version}</string>
	<key>LSMinimumSystemVersion</key>
	<string>13.0</string>
	<key>LSUIElement</key>
	<true/>
	<key>NSHighResolutionCapable</key>
	<true/>
EOF
  if [[ "$include_icon" == "1" ]]; then
    cat >>"$plist" <<EOF
	<key>CFBundleIconFile</key>
	<string>AppIcon</string>
EOF
  fi
  cat >>"$plist" <<EOF
</dict>
</plist>
EOF
}
