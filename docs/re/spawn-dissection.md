# World-packet dissection: players, NPCs, world objects, inventory, dialogue

Byte-level facts about the world packet families that the DB-driven world entry builds. The verified per-message
index + coverage are [`../protocol/gen/messages.md`](../protocol/gen/messages.md) and
[`../protocol/gen/coverage.md`](../protocol/gen/coverage.md). The deep-dive readers are:

- `FA015807` entity spawn — [fa015807-spawn-reader.md](fa015807-spawn-reader.md) (NPC) and
  [player-spawn-generation.md](player-spawn-generation.md) (player);
- `96C46740` MoveTask — [movetask-grammar.md](movetask-grammar.md);
- `66AEDD50`/`F096251E`/`33A56FB0` world objects — [iir-readers-combat-world.md](iir-readers-combat-world.md)
  (the shared bounds struct) and [iir-subclass-readers.md](iir-subclass-readers.md);
- `737A6DF9` inventory snapshot — [fullplayer-snapshot-reader.md](fullplayer-snapshot-reader.md);
- `642CD3D6` Interact — [behaviorcontrolcenter-grammar.md](behaviorcontrolcenter-grammar.md).

Every one of these families parses into a typed model and rebuilds byte-exact over the capture corpus.

## Client behaviours that constrain construction

1. **The player's entity handle is the client instance**, and the client never polls its own handle.
2. **The client polls only entities near the player** (`0x2020` → SpawnCheck), and the SpawnCheck answer is
   reactive (it answers any polled handle), so an injected spawn needs no manifest entry. A built `FA015807` plus a
   built stationary `96C46740` on a fresh server handle is polled and renders standing still.
3. **A spawn body the client does not accept is dropped silently** — no crash, no `pcDynel is NULL`; the entity
   simply does not render.
4. **Each `FA015807` spawn is paired with a `96C46740` MoveTask** whose body carries the entity's position and
   waypoints, so a spawn whose position disagrees with its paired MoveTask is inconsistent.
5. **Rotation is fragile on the player spawn**: a wrong rotation can stall the loading screen, so a near-zero
   stored quaternion falls back to identity.
6. The client does no reassembly on the uncompressed download; within a tightly-batched burst, a change in TCP
   segmentation can misframe byte-identical packets.

## The identity component — CharacterStats

The identity/stat component (sex/side/race/class/morph scale/life/health/…) is `ProtoCoreCharacterIIR.CharacterStats`
(field 4 of the CharacterIIR), fields `1=sex 2=side 3=race 4=monster_scale 5=monster_data 6=life 7=health …`,
decoded across every player capture (sex/race vary: Strie 3/1, hoxxling/necrol80/demol80 2/3). The generated type
re-encodes byte-identical (`CharacterStatsRoundTripTests`), so the component is built from a typed object rather
than spliced.

Mapping onto DB columns: `race` (field 3) is the `Race` enum; `sex` (field 1) is the `Sex` enum **+ 2**
(Male=2, Female=3); `life`/`health` are **×100** the player-facing `CharacterMaxStats` values. **Class is not in
the spawn** (no 0x12-0x2C value anywhere); it is character-list metadata.

## NPC spawns

**An NPC spawn is the same `FA015807` grammar as the player spawn** — only the field values differ:

```
[0:20]    header: bodyLen, subOp=FA015807, ack=0xC350, handle, fieldA
[20:~66]  transform group 0x2C: 0A vec3(pos LE), 12 quat(rot LE), 18 playfield-varint
[66:293]  pre-IIR scaffold (the SimpleCharFullUpdateIIR_t stat block, see fa015807-spawn-reader.md)
@297      ProtoCoreCharacterIIR (u32 length prefix @293 on every beach NPC):
            f1  name
            f4  CharacterStats   sex/side/race/monster_scale/monster_data/life/health/.../head_mesh/level/radius
            f5  NpcStats         f4=template_id (NPC identity), super_family, community, npc_flags, npc_type
            f7  CharacterBcc     idle/action anim ids
            f23 inventory_entry  worn equipment (humanoids only; absent on animals — Crocodile/Panther)
[iirEnd:] post-IIR stream (see fa015807-spawn-reader.md)
```

**`NpcStats.template_id` (IIR f5.f4, fixed32) is the stable per-type identity** — Crocodile=14049,
Pillager=8756/14040 (two variants), Kalanthes=2704, Casilda=9411. Instances of the same template share
race/sex/monster_data/head_mesh; `monster_scale` carries the per-spawn size jitter (e.g. crocodiles 129–136). So an
NPC spawn is reconstructable from a per-template row (name, the CharacterStats/NpcStats/Bcc field set, equipment
list) + per-instance (handle, position, rotation, monster_scale). Neither the pre-IIR nor the post-IIR carries an
entity handle; both are session-independent.

Spawn handles are session-volatile; a captured spawn matches its DB row by **position**, never by handle.
`SpawnHandleTable` (`Faolan.Core.Network/Codecs/Spawn/SpawnHandleTable.cs`) allocates per-session handles.

## MoveTask framing

The MoveTask does **not** use the `0x2C` transform framing, and its floats are **big-endian** — the opposite of
the spawn transform's LE. Body head is `[u32 bodyLen][96C46740][ack=0xC350][u32 handle][00 00][curPos vec3 BE]`
before the mode byte. A non-patrolling NPC's registration is a constant 31-byte body, so an NPC needs only its
position to register and render standing still; a mover is constructed from {position, waypoint path, speed} —
the data `PatrolPath` holds.

## World objects

The three world-object sub-opcodes share the `FA015807` header + `0x2C` transform group but carry no pre-IIR
block; the object message's u32 length prefix immediately follows the transform:

```
[0:20]   header: bodyLen, subOp, ack, handle, fieldA
[20:~65] transform group 0x2C: 0A vec3(pos), 12 quat(rot), 18 playfield-varint
@msg     [u32 BE msgLen][object message]: f1=id(varint), f2=name(string), f3=nested (e.g. a remoteformat XML
         for interactive props), f5/f8/f12 = trailers
[msgEnd:] the bounds struct + per-class trailer (iir-readers-combat-world.md)
```

Sub-opcode = object class: `66AEDD50` = decor/props (`004709_..._decor_chain_hangingwoman`, "Broken Bottle",
"Acheronian Chest/Gate"), `F096251E` = interactive containers ("Chest [N7FU]", carries the remoteformat in f3),
`33A56FB0` = doors. The object state string in the bounds struct is `object_closed`/`object_open`/`Open` (empty
for decor and zones). `WorldObjectCodec` builds the body from `WorldObject` columns.

## Inventory snapshot entries

The `0x737A6DF9` body is prefix + protobuf root (containers) + tails. Containers are field-6 messages keyed by
kind: Equipped=101, Bag=104, Resource=112, Quest=113; each has f1{f1=kind,f2=owner}, f2=capacity (not occupied
count), f3=flags, f4=repeated item entries. An entry:

```
entry.f1                      = slot
entry.f2(details).f1          = itemId            (per-instance GUID, not from the template)
details.f2 {f1=0,  f2=v}      = flags property
details.f2 {f1=23, f2=v}      = inventory record id (per-instance)
details.f2 {f1=26, f2=v}      = stack count
details.f3(tail).f1/f2/f3     = 3x identity payload {f1..f4 fixed32 = Item.Data1..Data4}
tail.f4 = tail.f5             = Item.Id           (template id)
tail.f7                       = 1
tail.f8                       = archetype          (per template; e.g. Slave Manacles = 0x43555035)
```

`SnapshotItems` constructs this, byte-exact against the captured "Slave Manacles" entry (`SnapshotItemsTests`).

**The item identity quad must be real.** An entry carrying a fabricated `Data1..4` quad stalls the client on
loading; an entry built from a real item's quad and archetype loads, and the client resolves the whole item —
name, slot type, level, stats, vendor price, bind-on-equip — from the template id + quad. The quad and archetype
are harvested from captured inventories (`Faolan.Importer/Extractors/ItemHashHarvester.cs`); an item is buildable
iff its archetype is known. The item model is in [inventory.md](../subsystems/inventory.md).

Equip/unequip arrives as a `0x642CD3D6` Interact whose `BccActionId` is the move direction
(`MoveItemInInventoryDirection`, e.g. 42010 EquippedToInventory / 42011 InventoryToEquipped); the trailing struct
holds big-endian `FromSlot, ToSlot, item-template-handle`.

## NPC conversations

Clicking an NPC sends an `Interact` (`642CD3D6`, `UseCharacter`); the clicked NPC's entity handle is in the args
(`args.SpellTarget.TargetHandle`). Retail replies with interact echoes (`642CD3D6`), an entity-state block, a
`0x2015` "select target", then the conversation window: `4A3D899A` (the dialogue line, e.g. id 12493) +
`F164A4A5` (the `remoteformat` answer list, e.g. id 7615). All are keyed to the NPC handle and embed the player
name/guild in the XML.

Each option click is another `UseCharacter` whose args encode the chosen branch; the server replies with the next
node(s). The dialogue **text is client-side** (localized by id); the server sends only node ids + parameters. A
conversation is therefore a state machine — `(current node, chosen option) -> next node id(s)` — which the importer
harvests into `ConversationNode`/`ConversationEdge` and `ConversationService` drives. Captured trees include the
Kalanthes intro (**12493**, a shared "Stranger" greeting), the strickenbla combat dump's
`12493 -> 12490 -> 20018 -> 20019 -> 20020` (remoteformats `12621/12622/12623`) -> `19919, 26909, 12492, 12494`,
and the Strie-randomActions / DemoL80 trees (`7731/19985/31636`, `21717/22047/35071`, …). See
[knubot-conversation-packets.md](../subsystems/knubot-conversation-packets.md).

## Archive reference: the previous-generation C++ server

`Archive/` holds the previous-generation Project Faolan server, written in C++ (extract it to a scratch dir; paths
below are relative to its `trunk/`). It is not portable to this server, but it is a reference for data and for
"what does retail send here":

- **`trunk/sql/aocdatabase.sql`** — its schema: `player_items` / `npc_items` (equipment slots: neck=0x01,
  head=0x02, tokens=0x03, chest=0x05, main_hand_left=0x06, belt=0x07, off_hand_left=0x08, ring_left=0x09,
  ring_right=0x0a, …), `npcs` (model, map, position; Kalanthes is id 433376, model 2704, map 4010), `playerstats`,
  `classes` (confirms the `Class` enum, e.g. Priest of Mitra = 24 = 0x18), `const_stats` (per level/race/class
  base health/mana/stamina/exp), `const_abilitys` / `p_abilities` (ability templates, same quad shape as items).
- **`trunk/sql/p_items2.sql`** — an item template table `p_items(name, id, level, data0..data4)`; `data1..4` is the
  identity quad above.
- **`StatIds.txt`** — a stat id ↔ name mapping.
- **`WorldServer/WorldServerOpcodes.h`** — opcode names (e.g. add-item = `0x1d`).
- **`WorldServer/CommandHandler.cpp`** — `addItem` builds a 48-byte `item_pack` header followed by
  `id, id, iLevel, unk(0x4f4f4939), data1, data2, data3, data4`, then the quad **repeated**, ending `0x3f1, 0`.
