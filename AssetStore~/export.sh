#!/usr/bin/env bash
# Copies the package into a Unity project's Assets folder in the layout the
# Asset Store expects, so the Asset Store Publishing Tools can upload it:
#
#   AssetStore~/export.sh /path/to/UnityProject
#
# Result: Assets/Hibira/Realtime/{Runtime,Samples/Lobby,Documentation,...},
# with the same .meta files (and GUIDs) as the .unitypackage that
# AssetStore~/build.py and the asset-store workflow make.
# Remove the git (UPM) copy of the package from that project first, or the
# two Hibira.Realtime assemblies clash.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
project="${1:?usage: export.sh /path/to/UnityProject}"
python3 "$here/build.py" --project "$project"
echo "In Unity: Tools > Asset Store > Uploader, pick the package, choose the Assets/Hibira folder and upload."
