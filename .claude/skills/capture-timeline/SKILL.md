---
name: capture-timeline
description: Build a detailed, chronological timeline of what happened in an Age of Conan packet capture - login handoff, world entry, quest chain, dialogue, combat, loot, chat - with every id resolved to a name from faolan.db and the client LDB. Use when asked to analyse/summarise/explain a capture or dump, to say what a session did, to check how much of a capture our tooling can decode, or when handed a .pcapng / .tshark.json.
---

# Capture timeline

Turn a capture into the story of the session, with ids resolved rather than dumped as hex. An
unresolved id in the output is a **real gap in our own data** — so the timeline doubles as a coverage
check on the catalogs, and that is usually the more valuable half.

## 1. Dump the capture (skip if a `.tshark.json` already exists)

```zsh
# The plugin generates its opcode names from Opcodes.cs, so rebuild it first or names come out STALE.
cmake -S Faolan.Wireshark -B Faolan.Wireshark/build && cmake --build Faolan.Wireshark/build -j \
  && cmake --build Faolan.Wireshark/build --target install-faolan-plugin

tools/capture/tshark_dump.sh <data-repo>/Captures/<name>.pcapng
```

Captures live in the private ProjectFaolan-Data repo, not this one. The dump is the canonical
TCP-reassembled form and includes the compressed download packets.

## 2. Generate the timeline

```zsh
python3 tools/capture/timeline.py <data-repo>/Captures/<name>.tshark.json
```

Sections: `summary`, `handoff`, `timeline`, `quests`, `dialogue`, `chat`, `combat`, `coverage`.
Use `--section NAME` (repeatable) to narrow, `--all-packets` for every packet rather than the marker
events, `--db PATH` to point at a different database.

**Read `coverage` first.** It lists what this capture contains that our data cannot name — unresolved
IIR types, `Ox####` placeholder methods, item templates missing from `Items`, abilities missing from
`Abilities`, quests/goals missing from the definitions. Anything listed there is a finding.

## 3. Resolve the text the script deliberately does not

Dialogue, quest titles and objective lines are **encrypted in the client LDB**, and the per-string key
arrives on the wire. The script prints `LDB[category][id]`; two importer reports do the decryption by
joining that ciphertext to keys held in our own tables:

```zsh
RDB="/path/to/Age of Conan/RDB"   # see the client_rdb_install_path memory for this machine
dotnet run --project Faolan.Importer -- --report ldb-quest-text --report-arg 12369,10287 --RdbDir "$RDB"
dotnet run --project Faolan.Importer -- --report ldb-dialogue   --report-arg Turach          --RdbDir "$RDB"
```

`ldb-dialogue` prints each screen as `cat 50002 id NNNN: <text>` in answer-path order — match those
ids against the `dialogue` section's `[NNNN]` to reconstruct the exact conversation that was had.
A screen the wire used but the report does not list is a **conversation-coverage gap**: our tree was
built from captures, so a branch nobody ever walked has no rows. Importing the capture may fill it.

## 4. Report it

Lead with the narrative (what the player did, in order), then the coverage findings. State
percentages you measured rather than impressions — e.g. "wire dialogue resolvable 40/54 screens".
Per CLAUDE.md, file a Gitea issue for each genuine gap; do not leave them only in the write-up.

## Gotchas this workflow exists to encode

- **Abilities are NOT in `Spells`.** `DispatchAddSpecialAbility` / `PerformSpecialAction` carry
  `Abilities.Id` (and the c2s body is the action-bar **slot**). Querying `Spells` returns nothing and
  reads exactly like a missing catalog row. The near-miss tables are `Spells` / `Abilities` /
  `ClassSpells` / `CharacterSpells` / `CharacterAbility` — check which catalog before reporting a gap.
- **Damage is display-scaled ×100.** A wire value of 945 renders in-client as 9.45. Raw numbers look
  absurd for a level-5 character and invite a wrong conclusion.
- **Parsing `Opcodes.cs` must not require a trailing comma.** The last member of an enum has none, so a
  comma-anchored regex silently leaves that id unresolved — which is how `N3ToClientQuitIIR` (283
  packets in one capture) once got written up as an unnamed opcode.
- **A repeated record type is easy to truncate.** A `QuestFullUpdate` carries one category-50304
  objective record *per goal*; reading only the first drops the rest. Use `findall`, not `search`, and
  when a "constant" equals `n * 0x3F1` it is a list count, not a constant.
- **Session-volatile handles are not identity.** Entity handles, shop ids and conversation ids are
  per-session; the stable key is the NPC/vendor NAME or the character id.
- **Don't trust a single capture for a grammar.** Before concluding a field is fixed, scan the whole
  corpus for it on raw bytes — `Iir`-tagged counts can miss records batched inside another packet:

```zsh
python3 - <<'PY'
import json, glob, collections
hits = collections.Counter()
for f in sorted(glob.glob('<data-repo>/Captures/*.tshark.json')):
    for p in json.load(open(f))['Packets']:
        b = p['BodyHex'] or ''
        hits[b.count('<the record hash in hex>')] += 1   # occurrences per packet, not packets
print(hits)
PY
```
