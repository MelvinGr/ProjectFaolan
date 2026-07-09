# 05 — Identity & wire codes

The small fixed vocabularies the protocol uses to say *what* and *which* — entity classes, the sex/race/class
codes — and the rule for what may be used as a persistence key.

---

## Entity classes (the typeTag)

The `typeTag` in an IIR record ([`03-iir-carrier.md`](03-iir-carrier.md)) is the subject's object class. Values
(from `EntityClass`):

| Value | Class | Notes |
|---|---|---|
| `0x0000C350` | Character | players / NPCs / creatures — the common case |
| `0x0000C878` | SimpleDynel | static decor / props |
| `0x0000C87A` | LootBag | loot bags / containers |
| `0x0000C879` | Destructible | destructible objects |
| `0x0000C87D` | ClimbingDynel | climbing dynels |
| `0x0000C748` | ZoneLabel | zone / city label objects |

[V1: Captures/ghidra/DumpIirReaders.java]

## Sex

Wire code, **uniform** across the player editor and the NPC tables (there is one client, one encoding):

| Code | Sex |
|---|---|
| `0` | None / unset |
| `2` | Male |
| `3` | Female |

`2`/`3` are the client's own values (`Faolan.Core.Enums.Sex`), not a server remapping. A player spawn built from
DB columns reproduces the captured template byte-for-byte, sex code included.
[V3: PlayerSpawnBuilderTests.BuildFromScratch_ReproducesStrieTemplate]

Race and class travel as their own small codes in the same spawn body; the spawn round-trips through parse, so a
rebuilt spawn with a changed identity still reads back correctly.
[V3: PlayerSpawnBuilderTests.BuildFromScratch_WithOverriddenIdentity_ParsesBackWithNewName]

## Handles vs stable keys

The **handle** in an IIR record is the live entity id for this session only. For a player, handle, ClientInstance
and character id happen to be the same value; for anything else the handle is session-volatile.

Rule, enforced by convention throughout the server:

> A session-volatile value (entity handle, shop id, conversation id) **must never** be a persistence key. The
> stable key is the NPC/vendor **name** (conversations, shops) or the **character id**.

Persisting a handle produces rows that resolve to the wrong entity — or nothing — on the next login, because the
handle is reassigned per session.

## See also

- [`03-iir-carrier.md`](03-iir-carrier.md) — where the typeTag and handle sit in a record.
- [`01-crypto.md`](01-crypto.md) — the credentials the character id keys back to.
- `docs/npc-three-tier-architecture.md` — how a Character-class entity is built from typed columns.
