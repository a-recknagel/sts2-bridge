#!/bin/bash
# Copy the game's DLLs into lib/ (gitignored), IL-patch the copy of sts2.dll, and build the drivers.
# It only reads from the install. Needs an installed Slay the Spire 2 (v0.111.0 is what the parity fixtures were
# recorded on), a .NET 9 runtime plus any SDK >= 9, and network once for NuGet (Mono.Cecil, for the patcher).
#
#   ./setup.sh                        # Steam's default location
#   ./setup.sh /path/to/game/data     # e.g. .../SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
GAME="${1:-${STS2_GAME_DIR:-}}"
if [ -z "$GAME" ]; then
    case "$(uname -s)" in
        Darwin)
            GAME="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
            [ -d "$GAME" ] || GAME="${GAME%_arm64}_x86_64" ;;
        Linux)
            GAME="$HOME/.steam/steam/steamapps/common/Slay the Spire 2"
            [ -d "$GAME" ] || GAME="$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2" ;;
    esac
fi
[ -d "$GAME" ] || { echo "game directory not found: $GAME (pass it as the first argument)" >&2; exit 1; }

# sts2.dll and what it loads at run time; Harmony and System.IO.Hashing are also compile references.
DLLS=(sts2.dll SmartFormat.dll SmartFormat.ZString.dll Sentry.dll Sentry.Godot.dll Steamworks.NET.dll
      MonoMod.Backports.dll MonoMod.ILHelpers.dll 0Harmony.dll System.IO.Hashing.dll)
mkdir -p "$HERE/lib"
for dll in "${DLLS[@]}"; do
    src="$GAME/$dll"
    [ -f "$src" ] || src="$(find "$GAME" -name "$dll" -print -quit)"
    [ -n "$src" ] || { echo "$dll not found under $GAME" >&2; exit 1; }
    cp "$src" "$HERE/lib/$dll"
done

dotnet run -v q --project "$HERE/dotnet/Patcher" -- "$HERE/lib/sts2.dll"
dotnet build -v q "$HERE/dotnet/ReplayCheck/ReplayCheck.csproj"
dotnet build -v q -c Release "$HERE/dotnet/CombatWorker/CombatWorker.csproj"
