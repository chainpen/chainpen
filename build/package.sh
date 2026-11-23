#!/usr/bin/env bash
# Builds a self-contained release and packs it with Velopack (FS-800–803).
#
#   build/package.sh <rid> <version>      e.g. build/package.sh osx-arm64 0.1.0
#
# Output: artifacts/releases/<rid>/ — the installer (Setup.exe / .pkg), a portable copy, and the files auto-update
# reads (RELEASES, *.nupkg). macOS builds get only an ad-hoc signature (no paid signing yet). Run on the OS the
# rid is for: Velopack packs Windows on Windows, macOS on macOS, Linux on Linux.
set -euo pipefail

rid="${1:?rid, e.g. osx-arm64}"
version="${2:?version, e.g. 0.1.0}"
root="$(cd "$(dirname "$0")/.." && pwd)"
publish="$root/artifacts/publish/$rid"
releases="$root/artifacts/releases/$rid"

# vpk targets .NET 10; let it run on the .NET 11 runtime the SDK brings.
export DOTNET_ROLL_FORWARD=Major

rm -rf "$publish" "$releases"
dotnet publish "$root/src/Chainpen.App/Chainpen.App.csproj" -c Release -r "$rid" --self-contained \
  -p:Version="$version" -o "$publish"

# The `chainpen` command and MCP server (FS-969, FS-970) ship beside the app, sharing its runtime and libraries.
dotnet publish "$root/src/Chainpen.Cli/Chainpen.Cli.csproj" -c Release -r "$rid" --self-contained \
  -p:Version="$version" -o "$publish"

# Optional release notes: the path to a markdown file of bullet points, shown in the app's "Update ready" panel.
notes=()
if [[ -n "${RELEASE_NOTES:-}" ]]; then notes=(--releaseNotes "$RELEASE_NOTES"); fi

dotnet tool restore
exe="Chainpen.App"
if [[ $rid == win-* ]]; then exe="Chainpen.App.exe"; fi
common=(--packId Chainpen --packVersion "$version" --packDir "$publish" --mainExe "$exe"
        --packTitle Chainpen --outputDir "$releases" --channel "$rid" ${notes[@]+"${notes[@]}"})

case "$rid" in
  osx-*)
    plist="$root/artifacts/publish/Info-$rid.plist"
    sed -e "s/@VERSION@/$version/g" "$root/build/macos/Info.plist" > "$plist"
    # "-" is an ad-hoc signature: free, and required for the app to run on Apple Silicon at all. The .pkg installer
    # stays unsigned: an installer package can't take an ad-hoc signature.
    dotnet vpk pack "${common[@]}" --icon "$root/src/Chainpen.App/Assets/Icon/chainpen.icns" \
      --plist "$plist" --signAppIdentity - ;;
  win-*)
    dotnet vpk pack "${common[@]}" --icon "$root/src/Chainpen.App/Assets/Icon/chainpen.ico" ;;
  linux-*)
    dotnet vpk pack "${common[@]}" --icon "$root/src/Chainpen.App/Assets/Icon/chainpen.png" ;;
  *)
    echo "Unknown rid: $rid" >&2; exit 1 ;;
esac

# vpk can report invalid options and still exit 0, so check it made something.
if ! ls "$releases"/*.nupkg >/dev/null 2>&1; then
  echo "Velopack produced no package for $rid" >&2
  exit 1
fi

echo "Packed $rid $version into $releases"
