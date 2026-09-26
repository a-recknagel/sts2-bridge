#!/bin/bash
# Fetch and build the pinned sts2-cli substrate into research/combat_parity/.work/ (gitignored).
#
# sts2-cli's own setup.sh copies ten DLLs out of the installed game, IL-patches the copy of sts2.dll,
# refreshes its localization tables from the .pck, and builds. It only reads from the install.
# Needs: git, a .NET 9 runtime (plus any SDK >= 9), network for the clone and one NuGet package.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
CLI_URL=https://github.com/wuhao21/sts2-cli
CLI_REV=084d1aa   # "Merge pull request #93 from wuhao21/update/sts2-v0.111.0", 2026-09-07
CLI="$HERE/.work/sts2-cli"

if [ ! -d "$CLI/.git" ]; then
    mkdir -p "$HERE/.work"
    git clone -q "$CLI_URL" "$CLI"
fi
git -C "$CLI" checkout -q "$CLI_REV"
# Godot members sts2.dll's combat code calls that the stubs lack (a monster's SetVisible, Mathf.Log, ...); without
# them that fight's turn loop dies with MissingMethodException. See sts2-cli-stubs.patch.
git -C "$CLI" apply --reverse --check "$HERE/sts2-cli-stubs.patch" 2>/dev/null || git -C "$CLI" apply "$HERE/sts2-cli-stubs.patch"
(cd "$CLI" && ./setup.sh "$@")
dotnet build -v q "$HERE/ReplayCheck/ReplayCheck.csproj"
dotnet build -v q "$HERE/CombatWorker/CombatWorker.csproj"
