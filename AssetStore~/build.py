#!/usr/bin/env python3
"""Builds the Asset Store copy of the package without running Unity.

  python3 AssetStore~/build.py                    # dist/Hibira-Realtime-<version>.unitypackage
  python3 AssetStore~/build.py --out FILE         # write the .unitypackage to FILE
  python3 AssetStore~/build.py --project DIR      # copy into DIR/Assets/Hibira/Realtime instead

Both outputs use the classic Asset Store layout:

  Assets/Hibira/Realtime/{Runtime,Samples/Lobby,Documentation,CHANGELOG.md,Third-Party Notices.txt}

The MIT LICENSE stays out of the Asset Store copy, which the Asset Store EULA
covers; Third-Party Notices.txt says the same source is MIT on GitHub. The
README copied into Documentation drops its image lines (the cover and the CI
badge), which point at files that are not in the package, and its License
section names the EULA instead of linking the LICENSE file.

Files that already have a .meta in this repository (everything under Runtime)
keep it. The rest (samples, docs, changelog, licence, and the folders above
them) get one whose GUID is derived from its path, so every release ships the
same GUIDs and an update replaces files in a project instead of duplicating
them.

A .unitypackage is a gzipped tar with one folder per asset, named by GUID,
holding `asset` (the file; absent for folders), `asset.meta` and `pathname`.
"""
import argparse
import gzip
import hashlib
import importlib.util
import io
import json
import os
import shutil
import sys
import tarfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PREFIX = "Assets/Hibira/Realtime"
NOTICES = "Third-Party Notices.txt"
ASSET_STORE_LICENSE = (
    "This copy is licensed under the Unity Asset Store EULA. The same source is also "
    "published under the MIT License at https://github.com/hibira-io/unity. "
    "See Third-Party Notices.txt."
)


def load_templates():
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("check_meta", os.path.join(ROOT, "Tests~", "check-meta.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.template


template = load_templates()


def sources():
    """Yields (source path, path inside the Unity project) for every file."""
    def tree(src, dest):
        for dirpath, dirnames, filenames in os.walk(src):
            dirnames[:] = sorted(d for d in dirnames if not d.startswith("."))
            rel = os.path.relpath(dirpath, src)
            base = dest if rel == "." else dest + "/" + rel.replace(os.sep, "/")
            for f in sorted(filenames):
                if f.startswith(".") or f.endswith(".meta"):
                    continue
                yield os.path.join(dirpath, f), base + "/" + f

    yield from tree(os.path.join(ROOT, "Runtime"), PREFIX + "/Runtime")
    yield from tree(os.path.join(ROOT, "Samples~", "Lobby"), PREFIX + "/Samples/Lobby")
    for f in sorted(os.listdir(os.path.join(ROOT, "Documentation~"))):
        if f.endswith(".md"):
            yield os.path.join(ROOT, "Documentation~", f), PREFIX + "/Documentation/" + f
    yield os.path.join(ROOT, "README.md"), PREFIX + "/Documentation/README.md"
    yield os.path.join(ROOT, "CHANGELOG.md"), PREFIX + "/CHANGELOG.md"
    yield os.path.join(ROOT, NOTICES), PREFIX + "/" + NOTICES


def read_asset(src, asset_path):
    """The bytes that go into the package for this file."""
    with open(src, "rb") as f:
        data = f.read()
    if asset_path == PREFIX + "/Documentation/README.md":
        lines = data.decode("utf-8").split("\n")
        kept = [l for l in lines if not l.lstrip().startswith(("![", "[!["))]
        text = "\n".join(kept)
        text = text.replace("[MIT](LICENSE).", ASSET_STORE_LICENSE)
        while "\n\n\n" in text:
            text = text.replace("\n\n\n", "\n\n")
        data = text.encode("utf-8")
    return data


def repo_meta(src_path):
    """The .meta checked into the repository for this file or folder, if any."""
    meta = src_path + ".meta"
    if os.path.exists(meta) and os.path.relpath(meta, ROOT).startswith("Runtime"):
        with open(meta, encoding="utf-8") as f:
            return f.read()
    return None


def generated_meta(asset_path, is_dir):
    guid = hashlib.md5(("hibira-unity-assetstore:" + asset_path).encode()).hexdigest()
    return template(asset_path, is_dir).format(guid=guid)


def guid_of(meta):
    for line in meta.splitlines():
        if line.startswith("guid:"):
            return line.split(":", 1)[1].strip()
    raise ValueError("meta without a guid")


def entries():
    """Returns [(asset path, source path or None for a folder, meta text)], folders first."""
    files = list(sources())
    folders = {}
    for src, dest in files:
        parts = dest.split("/")
        for i in range(2, len(parts)):  # skip "Assets" itself
            folder = "/".join(parts[:i])
            if folder in folders:
                continue
            src_dir = None
            if folder == PREFIX + "/Runtime" or folder.startswith(PREFIX + "/Runtime/"):
                src_dir = os.path.join(ROOT, folder[len(PREFIX) + 1:])
            folders[folder] = (src_dir and repo_meta(src_dir)) or generated_meta(folder, True)
    out = [(path, None, meta) for path, meta in sorted(folders.items())]
    for src, dest in files:
        out.append((dest, src, repo_meta(src) or generated_meta(dest, False)))
    guids = {}
    for path, _, meta in out:
        g = guid_of(meta)
        if g in guids:
            raise SystemExit(f"duplicate GUID {g}: {guids[g]} and {path}")
        guids[g] = path
    return out


def add_bytes(tar, name, data):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mode = 0o644
    info.mtime = 0
    tar.addfile(info, io.BytesIO(data))


def write_unitypackage(path):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    raw = io.BytesIO()
    with tarfile.open(fileobj=raw, mode="w", format=tarfile.USTAR_FORMAT) as tar:
        for asset_path, src, meta in entries():
            guid = guid_of(meta)
            if src is not None:
                add_bytes(tar, guid + "/asset", read_asset(src, asset_path))
            add_bytes(tar, guid + "/asset.meta", meta.encode())
            add_bytes(tar, guid + "/pathname", asset_path.encode())
    with open(path, "wb") as f:
        with gzip.GzipFile(fileobj=f, mode="wb", mtime=0, filename="") as gz:
            gz.write(raw.getvalue())


def write_project(project):
    if not os.path.isdir(os.path.join(project, "Assets")):
        raise SystemExit(f"{project} has no Assets folder")
    shutil.rmtree(os.path.join(project, PREFIX), ignore_errors=True)
    for asset_path, src, meta in entries():
        dest = os.path.join(project, asset_path)
        if src is None:
            os.makedirs(dest, exist_ok=True)
        else:
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with open(dest, "wb") as f:
                f.write(read_asset(src, asset_path))
        with open(dest + ".meta", "w", encoding="utf-8", newline="\n") as f:
            f.write(meta)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--out", help="where to write the .unitypackage")
    parser.add_argument("--project", help="copy into this Unity project instead")
    args = parser.parse_args()
    if args.project:
        write_project(args.project)
        print(f"Copied to {os.path.join(args.project, PREFIX)}")
        return
    with open(os.path.join(ROOT, "package.json"), encoding="utf-8") as f:
        version = json.load(f)["version"]
    out = args.out or os.path.join(ROOT, "dist", f"Hibira-Realtime-{version}.unitypackage")
    write_unitypackage(out)
    print(out)


if __name__ == "__main__":
    sys.exit(main())
