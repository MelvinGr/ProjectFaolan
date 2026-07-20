# Captures/ and the data repo

`Captures/` is a **symlink**, not a directory. The 349 MB of retail packet traces live in
[ProjectFaolan-Data](https://git.groenendaal.io/MelvinGr/ProjectFaolan-Data) along with the other large
binaries, so this repo stays small.

```sh
cd ~/Development                     # wherever this repo's PARENT lives
git clone https://git.groenendaal.io/MelvinGr/ProjectFaolan-Data.git
cd "C#/Project Faolan"               # back into this repo
ln -s ../../ProjectFaolan-Data/Captures Captures
```

Adjust the `../..` to however deep this repo sits below the clone. `Captures` is gitignored, so the
symlink is yours and never committed.

## Why a symlink and not a submodule

Every path in the code and docs — `TestPaths.CapturesDir`, ~100 doc citations of individual
`*.tshark.json` files, the importer's `--captures` default — resolves at `<repo>/Captures/...`. The
symlink keeps all of them working untouched. A submodule would too, but only if the data repo held the
captures at its root, and it also holds `Ghidra/` and `Terrain/`.

## What is NOT in the data repo

The **tooling** that reads the captures stays here, because the test suite depends on it:

| was | now |
|---|---|
| `Captures/scripts/` | `tools/capture/` — the `decode_*.py` dissectors + `tshark_dump.sh` |
| `Captures/ghidra/` | `tools/ghidra/` — the RE `.java` scripts and their decode artifacts |
| `Captures/frida/` | `tools/frida/` — the live-client Frida scripts |
| `Captures/re/`, `Captures/reference/` | `docs/re/` — two decode artifacts |

## Terrain

`Terrain/` is in the data repo too, but it needs **no symlink** — it is a config path. Point
`TerrainDir` in `faolan.json` at the clone, or leave it unset to run without ground-snap and
line-of-sight (`TerrainService` logs that it is disabled and carries on).

## ⚠️ Without the captures, the suite FAILS

That is deliberate. Nine corpus gates assert they saw data — `"no snapshot bodies found in the capture
corpus"`, `"this gate would be vacuous"` — rather than passing on an empty directory. A vacuous pass
would hide a broken wire model, which is the exact failure those gates exist to catch.
