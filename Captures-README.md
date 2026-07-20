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

`Terrain/` is in the data repo too, but it needs **no symlink** — it is a config path, and
`faolan.json` now carries it explicitly:

```json
"TerrainDir": "../../ProjectFaolan-Data/Terrain"
```

Relative to the repo root, which is the server's required CWD. Adjust it if your clone sits
elsewhere. On startup the server says which it took:

```
Terrain: baked terrain files from '...' (ground-snap + line-of-sight on).
```

⚠️ If the directory is missing, `TerrainService` logs that ground-snap and line-of-sight are
**disabled** and carries on. That is a real behaviour change that only shows up in one log line, so
check for that line rather than assuming terrain is loaded. The key was previously absent from
`faolan.json` entirely, falling back to a bare `Terrain` — which is why it is spelled out now.

## Do you actually need the captures?

**No.** All 1046 tests read a committed 12 MB extract of the corpus
(`Faolan.Tests/Assets/corpus-extract.bin`) rather than the captures themselves — the six IIR types the
whole-corpus gates walk, pulled out of the 349 MB and deflated. Regenerate it with the capture repo
present:

```sh
dotnet run --project Faolan.Importer -- --report corpus-extract
```

**The whole suite runs without `Captures/`** — 1046 of 1046. The symlink is only needed to regenerate
the extract, and to run the importer and the `--report` tools against real captures.

⚠️ The extract is DERIVED, so a regeneration against a partial corpus would quietly WEAKEN every gate
while they all still pass. Its manifest pins per-IIR counts and the reader asserts them — a short
extract throws rather than narrowing coverage.

## ⚠️ Without the captures, the suite FAILS

That is deliberate. Nine corpus gates assert they saw data — `"no snapshot bodies found in the capture
corpus"`, `"this gate would be vacuous"` — rather than passing on an empty directory. A vacuous pass
would hide a broken wire model, which is the exact failure those gates exist to catch.
