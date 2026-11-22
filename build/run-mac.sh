#!/usr/bin/env bash
# Builds the app and runs it on macOS as a real app bundle, Fillsign Dev.app, rather than with `dotnet run`.
#
#   build/run-mac.sh [file ...]      e.g. build/run-mac.sh samples/sample-form.pdf
#
# Run as a bare program, macOS treats Fillsign as a background process: the system's Open panel (Open…, Add pages…)
# shows but won't let anything be chosen, and accessibility and screen-capture tools can't find the window. As a
# bundle with its own identifier (app.fillsign.desktop.dev) it behaves like the installed app, without being mixed
# up with it. The bundle is a Debug build in artifacts/, with only an ad-hoc signature.
set -euo pipefail

if [[ "$(uname)" != "Darwin" ]]; then
  echo "This is for macOS. Elsewhere, use: dotnet run --project src/Fillsign.App" >&2
  exit 1
fi

root="$(cd "$(dirname "$0")/.." && pwd)"
app="$root/artifacts/Fillsign Dev.app"
build="$root/src/Fillsign.App/bin/Debug/net11.0"

dotnet build "$root/src/Fillsign.App/Fillsign.App.csproj" -c Debug -v quiet -nologo

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
rsync -a --delete "$build/" "$app/Contents/MacOS/"
sed -e 's/@VERSION@/0.0.0-dev/g' \
    -e 's#<string>app.fillsign.desktop</string>#<string>app.fillsign.desktop.dev</string>#' \
    -e 's#<string>Fillsign</string>#<string>Fillsign Dev</string>#g' \
    "$root/build/macos/Info.plist" > "$app/Contents/Info.plist"
cp "$root/src/Fillsign.App/Assets/Icon/fillsign.icns" "$app/Contents/Resources/"
codesign --sign - --force --deep "$app" 2>/dev/null

# Files are passed as full paths: the app starts in another folder.
files=()
for file in "$@"; do
  files+=("$(cd "$(dirname "$file")" && pwd)/$(basename "$file")")
done

# A Debug build uses the installed .NET; pass on where it is when that isn't the system location.
env_args=()
if [[ -n "${DOTNET_ROOT:-}" ]]; then env_args=(--env "DOTNET_ROOT=$DOTNET_ROOT"); fi

# The ${a[@]+...} form keeps macOS's bash 3.2 from treating an empty list as unset.
open -n ${env_args[@]+"${env_args[@]}"} "$app" --args ${files[@]+"${files[@]}"}
echo "Started $app"
