set -euo pipefail
sdk="$(xcrun --sdk iphonesimulator --show-sdk-version)"
runtime="com.apple.CoreSimulator.SimRuntime.iOS-${sdk//./-}"
device="$(xcrun simctl list devices available -j | python3 -c '
import json, sys
devices = json.load(sys.stdin)["devices"].get(sys.argv[1], [])
print(next(device["udid"] for device in devices if device["name"].startswith("iPhone")))
' "$runtime")"
trap 'xcrun simctl shutdown "$device" || true' EXIT
xcrun simctl boot "$device"
xcrun simctl bootstatus "$device" -b
app="$(find samples/LavPulse/bin -type d -path '*/iossimulator-x64/Laveyana.LavPulse.app' -print -quit)"
test -n "$app"
find "$app" -type f -name '*.dylib' -exec codesign --force --sign - {} \;
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"
xcrun simctl install "$device" "$app"
bundle="$(/usr/libexec/PlistBuddy -c 'Print CFBundleIdentifier' "$app/Info.plist")"
launched="$(xcrun simctl launch "$device" "$bundle")"
echo "$launched"
pid="${launched##*: }"
sleep 5
if ! kill -0 "$pid"; then
  echo "::group::Simulator startup log"
  xcrun simctl spawn "$device" log show --last 3m --style compact \
    --predicate "processIdentifier == $pid OR eventMessage CONTAINS[c] '$bundle'" || true
  echo "::endgroup::"
  echo "::group::Crash reports"
  find "$HOME/Library/Logs/DiagnosticReports" -maxdepth 1 -type f -name 'Laveyana.LavPulse*.ips' \
    -exec head -n 100 {} \; || true
  echo "::endgroup::"
  exit 1
fi
