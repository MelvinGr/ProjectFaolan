#!/usr/bin/env python3
"""Reads named fixtures out of Faolan.Tests/Assets/fixtures.bin (the FCX2 fixture archive).

The archive is the ONE committed file holding every byte of capture-derived data the test suite reads - the
whole-corpus slice plus the hand-carved named fixtures that used to be loose Assets/*.bin and Golden/*.bin files.
Faolan.Importer/Captures/FixtureArchiveWriter.cs owns the format; this is the Python side, for the decode scripts
that want one of those blobs.

    from fixtures import named
    tails = named("assets/BeachTailsTemplate.bin")

Run it directly to list the keys, or to write one out:
    python3 tools/capture/fixtures.py
    python3 tools/capture/fixtures.py golden/SpawnNpcCasilda.bin /tmp/casilda.bin
"""
import json
import os
import struct
import sys
import zlib

MAGIC = b"FCX2"


def archive_path():
    # Repo-relative, resolved from THIS file (tools/capture/) so it works from any cwd.
    return os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "Faolan.Tests", "Assets", "fixtures.bin")


def _load(path=None):
    path = path or archive_path()
    with open(path, "rb") as f:
        raw = f.read()

    if raw[:4] != MAGIC:
        raise ValueError(f"{path} is not a fixture archive (bad magic {raw[:4]!r})")

    manifest_length = struct.unpack_from("<I", raw, 4)[0]
    manifest = json.loads(zlib.decompressobj(-15).decompress(raw[8:8 + manifest_length]))
    payload = zlib.decompressobj(-15).decompress(raw[8 + manifest_length:])

    # The named entries sit after every corpus entry, in manifest order.
    offset = sum(e["n"] for e in manifest["corpus"])
    out = {}
    for e in manifest["named"]:
        out[e["k"]] = payload[offset:offset + e["n"]]
        offset += e["n"]

    return out


_cache = None


def named(key, path=None):
    global _cache
    if _cache is None:
        _cache = _load(path)

    if key not in _cache:
        raise KeyError(f"no fixture '{key}' in the archive; have: {', '.join(sorted(_cache))}")

    return _cache[key]


def keys(path=None):
    global _cache
    if _cache is None:
        _cache = _load(path)

    return sorted(_cache)


if __name__ == "__main__":
    if len(sys.argv) == 1:
        for k in keys():
            print(f"{len(named(k)):>9}  {k}")
    elif len(sys.argv) == 3:
        with open(sys.argv[2], "wb") as f:
            f.write(named(sys.argv[1]))
        print(f"{sys.argv[1]} -> {sys.argv[2]}")
    else:
        print(__doc__)
        sys.exit(2)
