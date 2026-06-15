# Entity object-class ids (the `0xC350` family)

`0x0000C350` (= 50000) appears all over the wire. This is what it is.

## Two separate "class id" namespaces

The protocol has **two** unrelated id systems — don't confuse them:

1. **RPC conversation class ids** — the endpoint/conversation routing (`SenderClass`/`ReceiverClass`, the
   `ProtoEndpoint.type` of an `RpcHeader`). These are **BKDR-31 hashes of the client class name**: `0xA9ECB95D` =
   hash("GameCharAgent"), `0xB171CE13` = GameAgent, `0x6C67C147` = GameInterface, … (`Faolan.Core/Network/Conan/
   ConanEndpoints.cs`, `docs/aoc-opcode-ids.md`). Big, hash-shaped values.

2. **In-world entity object-class ids** — the subject of this doc. The `type` half of an **entity id**
   `{type, instance}` (= protobuf `ProtoID32`, and the `type` field of a `ProtoEndpoint` that points at a world
   entity). Every IIR record that targets an entity begins `[u32 type][u32 handle]`, where `type` is one of these
   and `handle` is the live per-session instance. These are a small **enumerated registry based at 50000** — NOT
   hashes. Defined in `Faolan.Core/Network/EntityClass.cs`.

## The ids

| id (`uint`) | dec | class | IIR record type |
|---|---|---|---|
| `0xC350` | 50000 | **Character** — players / NPCs / creatures (`n3Char`/creature) | (the per-character records) |
| `0xC748` | 51016 | ZoneLabel — zone/city labels | `0x33A56FB0` |
| `0xC878` | 51320 | SimpleDynel — static decor / props | `0x66AEDD50` |
| `0xC879` | 51321 | Destructible | `0x6BD56736` |
| `0xC87A` | 51322 | LootBag / container | `0xF096251E` |
| `0xC87D` | 51325 | ClimbingDynel | `0x1FD6412B` |

Note the object-CLASS id (`type` in the record header) is distinct from the IIR record-TYPE (the `0x2000`
sub-opcode / message kind) — they map 1:1 per class but are different values on the wire.

## Why `0xC350` (Character) is everywhere

Players, NPCs and creatures are **all "Characters"**, so every record addressed *to* a living entity carries
`0xC350` + its handle: the per-character `StatIIR`/`StatUpdateIIR`/`EnergyIIR`, the `FullPlayerCharacter`
snapshot framing, the bank container (kind 105), the vitals + money packets, the guild-member records, the
ScryScript/quest acks, the spawn-scaffold ack word, … This is also why a from-DB NPC spawn and a player spawn
share the FA015807 grammar — both are Characters.

## Evidence it's the "Character" class id

Ghidra, this session (`MoveTaskIIR_c::PollStatus`, the move-task reader's status poll):

```c
if (*id != 50000)   // 0xC350
    log("MoveTaskIIR_c::PollStatus[%d:%u], id is not of a character!");
```

So the client itself treats `type == 50000` as "this id refers to a character." The captured loot-bag looter
ids were `{type=0, instance=0}` (empty / no-owner) — a real looter would be `{50000, handle}`.

## In code
`Faolan.Core/Network/EntityClass.cs` — the canonical `EntityClass.Character` (+ the world-object classes). It
replaced ~a dozen ad-hoc local constants (`CharacterWireType`, `TypeTagAck`, `VitalsMarker`, `Ack`, `WireType`,
`IirMarker`, `EntityTypeTag`, …) that all held `0x0000C350`. (Unrelated: `0x9C50` = 40016, which shows up inside
the PlayfieldFIIR body, is a *different* value — not an entity class id; semantics TBD.)
