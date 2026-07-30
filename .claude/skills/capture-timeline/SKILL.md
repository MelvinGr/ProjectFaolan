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

Sections: `summary`, `handoff`, `timeline`, `detailed`, `quests`, `dialogue`, `chat`, `combat`,
`coverage`. Use `--section NAME` (repeatable) to narrow, `--db PATH` for a different database.

**`--detailed` is the one to reach for when reading a session as it happened.** The sectioned default
splits a session across eight headings, so an event and its consequence land under different ones;
`--detailed` is the same data as ONE flat chronological stream, one line per event, each carrying its
time, frame number, tier, direction, resolved description AND its raw body — so a packet the decoder
cannot describe still shows its bytes instead of an empty column. It suppresses only per-frame
movement/heartbeat traffic and prints a count of what it dropped; `--all-packets` keeps everything.

It is what makes a multi-step mechanic legible. The quarry ambush reads straight off it: the brick
pickup, the dynel's state sequence, then the Bat Demon spawning 3s later already hating the player.

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

## Resolving entity handles

Combat/despawn lines name their entities: `Panther 0x3a6da <- the player 0x1317cf8`. Handles are
**session-volatile**, so they cannot be looked up — they are resolved through the capture itself, by
matching each spawn body's POSITION against `NpcSpawns` / `WorldObjects` on that playfield.

Two things are excluded rather than matched, or they take the name of whatever they stood near: the
**player**, and summoned **pets** (a pet matched a "Brutal Pirate" 22 units away). A match is printed
with its distance (`Bat Demon (+10u)`) whenever it is not near-exact — an **ambush** spawns where it
was triggered rather than at its home point, so that offset is a finding, not noise.

**Target selection** is `LookAtIIR` (c2s), rendered as `the player 0x… selects Turach 0x27929`; a zero
target at offset 21 is the player CLEARING their selection, printed as `deselects`.

⚠️ Handles at or above `0x1000000` are CHARACTER ids — other players — and are never position-matched.
One stood within 2u of a vendor and came out labelled "Trader" before that rule existed.

⚠️ **Position also tells you WHO acted.** Harvest-node animations keep firing for other players in a
retail capture; correlate the player's own `CharDCMoveIIR` position (raw body, BIG-endian floats at
offset 18) against the node before attributing an interaction to them. Two "extra" brick pickups in
the necromancer capture happened 29u and 77u from the player — other people's.
