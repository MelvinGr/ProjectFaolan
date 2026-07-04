# Necro pets / minions — capture decode + MVP (2026-07-05)

From `NecroL80_mage-necromancer_login_summon_pet.pcapng` (necro lvl80 login in a town, player instance
`0x0802E5D4`). Goal: get minions working.

## What the capture shows

- The scene is a TOWN: the nearby entities decoded to `"Guard"` (`0x0001A2C8`–`0x0001A2D2`) and `"Trader"`
  (`0x0001A282`) NPCs, not pets. No `FA015807` spawn in the capture carries `pet_owner_instance = 0x0802E5D4`,
  and no new creature spawns after the world-entry burst — so **NecroL80 did not summon a fresh pet on the wire
  here**; the capture is mostly login + town ambient (guards/traders getting `ApplySpell`/`GameActionList`/BCC).
- `ApplySpell 0x6B87E316` spell **`0x3BAD7D`** is applied to the guard cluster on login. Per the MountCatalog
  note that id is the **mount** channelled-summon, not a necro pet — consistent with this being ambient, not a
  pet summon.
- Player→entity targeting is a 16-byte RPC `[u32 C350][player 0x0802E5D4][u32 C350][target handle]` (e.g. the
  client selecting the Trader). That's generic target selection, reusable for "command pet to attack X".

Conclusion: this capture does not contain a clean necro-pet summon to reverse byte-for-byte. The pet subsystem
(spawn an OWNED, commandable minion via a class summon spell, follow/attack AI, persistence) is a large feature
with no prior code; the retail summon-spell → owned-pet-spawn wire form needs a capture that actually summons.

## MVP shipped this session — `.pet <npc name>`

A minion **works now** via a GM trigger, built on existing infra:
- `WorldBroadcaster.SpawnNpcForPlayer` — mid-session spawn that renders a creature next to the player
  (the #23 mid-session-spawn path).
- `WorldBroadcaster.SetNpcFollow` — companion-follow (#262).

`.pet <name>` spawns a creature and makes it follow → a rendered, following minion. This demonstrates the
minion loop end-to-end; the retail-accurate necro path (class summon spell → `pet_owner`-tagged spawn →
pet bar / commands) is the follow-up (#307).

## Follow-up (#307) to make it retail-accurate

1. Capture an actual necro pet summon (cast in an open area) to get the owned-pet spawn wire form
   (`ProtoCoreCharacterIIR.pet_owner_instance`/`pet_type`/`pet_points` fields 8/9/10 are the owner binding).
2. Wire a class summon spell (via the cast FSM, [[spell_cast_fsm_and_rdb_catalog]]) to spawn the pet with the
   caster as `pet_owner`.
3. Pet command bar: reuse the `[player][target]` selection RPC for attack/follow/stay.
4. Pet persistence + despawn on death/logout.
