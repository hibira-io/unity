#!/usr/bin/env python3
"""Checks that every asset in the package has a .meta file.

Unity ignores files without one in an immutable (git or registry) package,
so a missing .meta silently drops that file for everyone who installs it.

  python3 Tests~/check-meta.py          # check
  python3 Tests~/check-meta.py --write  # create the missing ones

GUIDs are derived from the path, so writing again gives the same files.
"""
import hashlib
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

FOLDER = """fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

MONO = """fileFormatVersion: 2
guid: {guid}
MonoImporter:
  externalObjects: {{}}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {{instanceID: 0}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

ASMDEF = """fileFormatVersion: 2
guid: {guid}
AssemblyDefinitionImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

TEXT = """fileFormatVersion: 2
guid: {guid}
TextScriptImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

MANIFEST = """fileFormatVersion: 2
guid: {guid}
PackageManifestImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

DEFAULT = """fileFormatVersion: 2
guid: {guid}
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

# The jslib is a WebGL-only plugin.
WEBGL_PLUGIN = """fileFormatVersion: 2
guid: {guid}
PluginImporter:
  externalObjects: {{}}
  serializedVersion: 2
  iconMap: {{}}
  executionOrder: {{}}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  - first:
      WebGL: WebGL
    second:
      enabled: 1
      settings: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def template(path, is_dir):
    if is_dir:
        return FOLDER
    name = os.path.basename(path)
    ext = os.path.splitext(name)[1]
    if name == "package.json":
        return MANIFEST
    return {
        ".cs": MONO,
        ".asmdef": ASMDEF,
        ".jslib": WEBGL_PLUGIN,
        ".md": TEXT,
        ".json": TEXT,
        ".txt": TEXT,
    }.get(ext, DEFAULT)


def assets():
    for dirpath, dirnames, filenames in os.walk(ROOT):
        # Unity skips hidden folders and folders ending in ~.
        dirnames[:] = sorted(d for d in dirnames if not d.startswith(".") and not d.endswith("~"))
        for d in dirnames:
            yield os.path.join(dirpath, d), True
        for f in sorted(filenames):
            if f.startswith(".") or f.endswith(".meta"):
                continue
            yield os.path.join(dirpath, f), False


def main():
    write = "--write" in sys.argv
    missing = []
    for path, is_dir in assets():
        meta = path + ".meta"
        if os.path.exists(meta):
            continue
        rel = os.path.relpath(path, ROOT).replace(os.sep, "/")
        missing.append(rel)
        if write:
            guid = hashlib.md5(("hibira-unity:" + rel).encode()).hexdigest()
            with open(meta, "w", newline="\n") as f:
                f.write(template(path, is_dir).format(guid=guid))
    # A .meta without its asset makes Unity warn on import.
    orphans = []
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = [d for d in dirnames if not d.startswith(".") and not d.endswith("~")]
        for f in filenames:
            if f.endswith(".meta") and not os.path.exists(os.path.join(dirpath, f[:-5])):
                orphans.append(os.path.relpath(os.path.join(dirpath, f), ROOT))
    for rel in missing:
        print(("wrote " if write else "missing .meta: ") + rel)
    for rel in orphans:
        print("orphan .meta: " + rel)
    if orphans or (missing and not write):
        sys.exit(1)


if __name__ == "__main__":
    main()
