#!/usr/bin/env bash
set -euo pipefail

tag="$RELEASE_TAG"
case "$ANDROID_SIGNING" in
  debug-key|release-key) ;;
  *) echo "Invalid Android signing status"; exit 1 ;;
esac
cd release-assets
assets=(
  "LavPulse-$tag-windows-x64.zip"
  "LavPulse-$tag-android-$ANDROID_SIGNING.apk"
  "LavPulse-$tag-ios-simulator-x64.zip"
  "LavPulse-$tag-macos-unsigned.zip"
)
for asset in "${assets[@]}"; do
  test -s "$asset"
done
sha256sum "${assets[@]}" > SHA256SUMS.txt
cd ..

# A failed upload leaves a draft and a tag reserved for this exact commit.
# A retry must never overwrite a published release or another commit's tag.
git fetch origin --tags
if git rev-parse --verify "refs/tags/$tag" >/dev/null 2>&1; then
  test "$(git rev-list -n 1 "$tag")" = "$GITHUB_SHA" || {
    echo "::error::Version already reserved for another commit. Rerun all jobs."
    exit 1
  }
else
  git tag "$tag" "$GITHUB_SHA"
  git push origin "refs/tags/$tag"
fi

if draft="$(gh release view "$tag" --json isDraft --jq .isDraft 2>/dev/null)"; then
  if [[ "$draft" == false ]]; then
    echo "Release $tag already published; no changes."
    exit 0
  fi
else
  notes="Windows x64: portable ZIP. Android: APK. macOS: unsigned Mac Catalyst app. iOS: x64 simulator only (not for physical iPhones)."
  if [[ "$ANDROID_SIGNING" == debug-key ]]; then
    notes="$notes Android uses an ephemeral CI debug key; updates over other signed installations may fail. Configure the persistent Android signing secrets for stable releases."
  fi
  gh release create "$tag" --verify-tag --draft --title "LavPulse $tag" --generate-notes --notes "$notes"
fi

# A retry may have changed the signing configuration. Remove only the obsolete
# Android package from this draft so the published assets match SHA256SUMS.txt.
if [[ "$ANDROID_SIGNING" == release-key ]]; then obsolete="LavPulse-$tag-android-debug-key.apk"
else obsolete="LavPulse-$tag-android-release-key.apk"
fi
if gh release view "$tag" --json assets --jq '.assets[].name' | grep -Fxq "$obsolete"; then
  gh release delete-asset "$tag" "$obsolete" --yes
fi

# Upload only to a draft. No partial release is exposed if any upload fails.
paths=()
for asset in "${assets[@]}" SHA256SUMS.txt; do
  paths+=("release-assets/$asset")
done
gh release upload "$tag" "${paths[@]}" --clobber
if [[ "$ANDROID_SIGNING" == debug-key ]]; then
  gh release edit "$tag" --draft=false --prerelease --latest=false
else
  gh release edit "$tag" --draft=false --prerelease=false --latest
fi
