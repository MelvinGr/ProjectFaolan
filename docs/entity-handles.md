# Entity ids & the handle-range constraint

How players, NPCs, and dropped objects are identified on the wire and in the world model — and the one rule
that bites if you ignore it: **a mid-session entity handle must fall in the low range the client tracks, or the
entity silently never renders.**

## The two kinds of identifier

| | what it is | scope | who keys off it |
|---|---|---|---|
| **persistent id** | `Character.Id` (DB key); for NPCs/vendors/conversations the stable key is the **name** | survives sessions | persistence, save/load, kill-quest target match |
| **wire handle** | the value spawn/move/combat/despawn packets address an entity by (`WorldEntity.Handle`); for a player it is also the GameCharAgent endpoint instance | session / playfield | the live world model + the client's render/poll tables |

Rule (CLAUDE.md): **session-volatile handles must never be persistence keys.** Persistence keys off
`Character.Id` (players) or the **name** (NPCs, vendors, conversations, shops).

## Players: handle == ClientInstance == Character.Id (on purpose)

For a player these are one value. Login resolves the character *by* the client instance
(`GetCharacterForAccount(accountId, clientInstance)`) and asserts `resolvedCharacter.Id == clientInstance`;
`WorldSimulation.PlayerWorldEntity` seeds `WorldEntity.Handle = ClientInstance` (`WorldSimulation.cs:124`). DB
character ids are the **real AoC ids** (e.g. `20016761` = `0x01313E79`, `134407637` = `0x08029D15`), not small
autoincrements, so the persistent id already sits in a client-tracked range. Retail unifies these too — the
player's `FA015807` spawn handle, the combat `DamageResult`/vitals target, and the RPC endpoint instance are all
the same `0x0802xxxx`-class value across the captures, and our player-spawn + combat goldens are byte-exact
against that. See memory `player_handle_equals_character_id`.

Do **not** split them for the player: the client self-correlates the char-id-keyed UI (GameCharAgent endpoint)
with the in-world dynel addressed by combat/vitals. Assigning the player a separate synthetic handle risks the
client not binding the two, and diverges from the captures we mirror.

## NPCs / loot / dynels: session-allocated handles in the low tracked range

These get runtime handles unrelated to any persistent id, and the **range matters**. Observed (from captures
and `WorldBroadcaster.cs:43-51`):

| entity | handle range |
|---|---|
| mid-session dynels (mount, interest NPCs) | `0x000286xx` |
| world-burst NPCs | `0x000818xx` |
| our loot / summon allocator (`_nextLootHandle`) | starts `0x00090000` — just above the burst NPCs, below the char-id range |
| player / char-id | `~0x0131xxxx` … `0x0802xxxx` (`0x02000000+`) |

**The failure mode:** the client only polls/tracks handles inside this band. A handle far above it — the old
`0x40000000`, or even `0x0F000000` — is never polled (`client → 0x2020`), so the server never reaches
`SpawnCheck 0x2008`, and the entity **never renders**. That is exactly why `_nextLootHandle` starts at
`0x00090000` and not an arbitrary large number.

**Necessary but not sufficient.** Being in-range only makes a mid-session spawn *eligible*. Our pushed
mid-session spawns still don't render because the client refuses to poll for entities it didn't learn about
during the world-entry load — a load-vs-play gate, not a range problem. Full writeup:
`docs/mid-session-spawn-blocker.md`, memory `protocol_midsession_spawn_blocked`.

## Don't confuse with the playfield-instance token

Separate namespace: each `Zone` mints a **playfield-instance token** (`WorldState.cs:28`, starts `0x00A00000`;
beach replay overrides it with the burst's own, e.g. `0x01ea985c`) that is stamped into every despawn so the
client scopes the removal to the right playfield. It is *not* an entity handle. See memory
`protocol_despawn_n3toclientquit`.

## Practical rules

- Player world entity: seed `Handle` from `ClientInstance` (== `Character.Id`). Never mint a separate one.
- A second world entity for the *same* character (future pet / controllable corpse): give it a **fresh
  allocator handle** in the low range — never reuse the char id for two live dynels.
- New NPC/loot/object handles: draw from the low-range allocator (`0x00090000+`), never a high constant.
- Anything persisted: key off `Character.Id` or the entity **name**, never the wire handle.
