#!/usr/bin/env bash
# Copies the package into a Unity project's Assets folder in the layout the
# Asset Store expects, so the Asset Store Publishing Tools can upload it:
#
#   AssetStore~/export.sh /path/to/UnityProject
#
# Result: Assets/Hibira/Realtime/{Runtime,Samples/Lobby,Documentation,...}
# Remove the git (UPM) copy of the package from that project first, or the
# two Hibira.Realtime assemblies clash.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
project="${1:?usage: export.sh /path/to/UnityProject}"
[ -d "$project/Assets" ] || { echo "$project has no Assets folder" >&2; exit 1; }
out="$project/Assets/Hibira/Realtime"
rm -rf "$out"
mkdir -p "$out/Samples" "$out/Documentation"
cp -R "$here/Runtime" "$here/Runtime.meta" "$out/"
cp -R "$here/Samples~/Lobby" "$out/Samples/Lobby"
cp "$here/Documentation~/"*.md "$out/Documentation/"
cp "$here/README.md" "$out/Documentation/README.md"
cp "$here/CHANGELOG.md" "$here/LICENSE" "$out/"
echo "Copied to $out. In Unity: Tools > Asset Store > Uploader, pick the package, choose the Assets/Hibira folder and upload."
