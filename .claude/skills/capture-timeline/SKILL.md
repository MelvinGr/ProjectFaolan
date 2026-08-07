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
dotnet run --project Faolan.Importer -- --report timeline \
  --report-arg <data-repo>/Captures/<name>.tshark.json --Content:RdbDir "<client>/RDB"
```

`./run.sh --timeline <dump>` is the same thing with the local paths already filled in.

ONE flat chronological stream, one line per event: time, frame, tier, direction, the resolved
description **and** the raw body — so a message the decoder cannot describe still shows its bytes
rather than an empty column. There are no sections and no brief/verbose modes: reading a session as it
happened is the job, and an event and its consequence must not land under different headings.

Per-frame movement/heartbeat traffic is suppressed with a printed count of what was dropped, never
silently.

**Read the trailing `#` lines first.** They list the IIR types and methods the capture used that
`Opcodes.cs` does not name. Anything there is a finding — and so is an id printed raw mid-timeline (a
template, spell or ability that did not resolve).

## 3. Deeper text than the session itself used

Dialogue, quest titles and objective lines are **encrypted in the client LDB**. The timeline already
decrypts them INLINE using the key the wire itself carries, so conversations read as English in the
stream — including screens our own `ConversationScreens` table has never captured. Without `--Content:RdbDir`
it degrades to printing `LDB[category][id]`.

Reach for these two reports when you want a whole NPC's tree or a quest's full journal text, rather
than only the lines this session happened to use:

```zsh
RDB="/path/to/Age of Conan/RDB"   # see the client_rdb_install_path memory for this machine
dotnet run --project Faolan.Importer -- --report ldb-quest-text --report-arg 12369,10287 --Content:RdbDir "$RDB"
dotnet run --project Faolan.Importer -- --report ldb-dialogue   --report-arg Turach          --Content:RdbDir "$RDB"
```

A screen the wire used but `ldb-dialogue` does not list is a **conversation-coverage gap**: our tree was
built from captures, so a branch nobody ever walked has no rows. Importing the capture may fill it.

## 4. Report it

Lead with the narrative (what the player did, in order), then the coverage findings. State percentages
you measured rather than impressions. Per CLAUDE.md, file a Gitea issue for each genuine gap; do not
leave them only in the write-up.

## Resolving entity handles

Lines name their entities: `Panther 0x3a6da <- the player 0x1317cf8`. Handles are **session-volatile**,
so they cannot be looked up — they are resolved through the capture itself, by matching each spawn
body's POSITION against `NpcSpawns` / `WorldObjects` on that playfield.

Three classes are excluded rather than matched, or they take the name of whatever they stood near: the
**player**, summoned **pets** (one matched a "Brutal Pirate" 22u away), and **other players** (a
character id sits far above the entity-handle range; one standing inside 2u of a vendor came out
labelled "Trader"). A match prints its distance (`Bat Demon (+10u)`) when it is not near-exact — an
**ambush** spawns where it was triggered rather than at its home point, so that offset is a finding.

⚠️ **Position also tells you WHO acted.** Harvest-node animations keep firing for other players in a
retail capture; correlate the player's own `CharDCMoveIIR` position (raw body, BIG-endian floats at
offset 18) against the node before crediting an interaction to them. Two "extra" brick pickups in the
necromancer capture happened 29u and 77u from the player — other people's.

## Adding a decode

If a message shows only hex and it matters, add a **codec** in `Faolan.Core.Network/Codecs/` with
`Build`/`Parse` and a golden test off the captured bytes, then call it from the report. Do not decode
inline. The whole reason the timeline moved out of a Python script is that a second implementation of
the wire grammar drifts — and it did, four wrong offsets before anyone noticed by eye.

⚠️ **Every IIR codec's body starts at the SUB-OPCODE — hand it the record with the u32 carrier length
already stripped.** Passing the wrong slice does not fail cleanly: it reads four bytes out of step and
either runs off the end or returns plausible values (that is how a `StatDamageIIR` once reported
`Flags +0.02` where the capture said `HealthCurrent -37.25`).

There used to be a second family that read a u32 LENGTH first, and the two were indistinguishable from a
`Parse(in ReadOnlySpan<byte>)` signature. That split is gone (#530) and now GATED (#531): a codec must
either open by consuming its own opcode word, or declare `OpcodeIsInTheFrame` — which the c2s codecs
(`DeleteItemCodec`, `MoveInventoryItemCodec`) do, because their sub-opcode rides in the RPC frame and their
body genuinely starts at the entity class. So the answer is always in the codec's own type; you never have
to guess, and a new codec that leaves it unsaid fails the build.

## Gotchas this workflow exists to encode

- **Opcodes are PER INTERFACE.** Every interface numbers its first method `0x2000`, so `PassBlob`,
  `InitiateAuthentication` and `DispatchChallenge` are the same number. Only the ENDPOINT class says
  which interface a packet is on; keying off the opcode alone made the universe handshake decode as an
  IIR whose "type" was the ASCII of an account name.
- **Abilities are NOT in `Spells`.** `DispatchAddSpecialAbility` / `PerformSpecialAction` carry
  `Abilities.Id` (and the c2s body is the action-bar **slot**). Querying `Spells` returns nothing and
  reads exactly like a missing catalog row.
- **Damage and stat deltas are display-scaled ×100.** A wire value of 945 renders in-client as 9.45.
- **A repeated record type is easy to truncate.** A `QuestFullUpdate` carries one category-50304
  objective record *per goal*; reading only the first drops the rest. And when a "constant" equals
  `n * 0x3F1` it is a list count, not a constant.
- **Don't trust a single capture for a grammar.** Before concluding a field is fixed, scan the whole
  corpus for it on raw bytes — `Iir`-tagged counts can miss records batched inside another packet.
