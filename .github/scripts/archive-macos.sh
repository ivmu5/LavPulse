#!/usr/bin/env bash
set -euo pipefail

# Catalyst can use an app bundle name different from AssemblyName, and may
# leave both single-architecture intermediates and the merged universal app.
# Select by bundle identity and actual executable architectures.
app=""
while IFS= read -r -d '' candidate; do
  plist="$candidate/Contents/Info.plist"
  [[ -f "$plist" ]] || continue
  identifier="$(/usr/libexec/PlistBuddy -c 'Print CFBundleIdentifier' "$plist" 2>/dev/null)" || continue
  [[ "$identifier" == com.laveyana.lavpulse.mixer ]] || continue
  name="$(/usr/libexec/PlistBuddy -c 'Print CFBundleExecutable' "$plist")" || continue
  executable="$candidate/Contents/MacOS/$name"
  [[ -f "$executable" ]] || continue
  echo "Candidate: $candidate ($(lipo -archs "$executable" 2>/dev/null || true))"
  if lipo "$executable" -verify_arch x86_64 arm64; then
    app="$candidate"
    break
  fi
done < <(find samples/LavPulse/bin -type d -name '*.app' -prune -print0)
if [[ -z "$app" ]]; then
  echo "::error::No universal LavPulse Mac Catalyst bundle with x86_64 and arm64 was found."
  find samples/LavPulse/bin -type d -name '*.app' -print
  exit 1
fi
mkdir -p artifacts
archive="artifacts/LavPulse-$RELEASE_TAG-macos-unsigned.zip"
ditto -c -k --sequesterRsrc --keepParent "$app" "$archive"
unzip -tq "$archive"
