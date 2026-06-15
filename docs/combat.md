# Combat: the BehaviorControlCenter ack protocol (armed + unarmed)

Status 2026-06-12: armed and unarmed swings work in-client; equip-state decides which protocol the client
speaks. Commits `226b16f`, `a115c40`. Golden tests: `BehaviorAckTests` (byte-exact against
`Captures/account-stricken998-character-combat_armed-and-unarmed.pcapng`). Wider combat-loop status (NPC
aggro/death/XP/loot) lives in TODO.md §Combat and `docs/archive/quests-and-combat.md`.

## The Interact request (0x642CD3D6 upload)

`[bodyLen u32][subop][0000C350][instance][seq u16 = 0003][behaviorId u32][argsLen u32][args][tail]`

- `behaviorId` = the `ActionType`: **0x0F NormalAttack** (armed swing), **0x11 UnarmedAttack** (empty main
  hand; behavior TypeIds 1089xx), 0x09 equip move (see `docs/inventory.md`), 0x2E weapon switch, 0x16 talk,
  0x12 use object, etc.
- args protobuf `{TypeId, Mode, Category, Field6, ...}` - **Mode is a monotonic per-session action
  counter** across ALL BCC interactions.
- Attack tails end with the **direction-ability id** (769094/769095/769099 = Normal Attack Left/Right/
  Front - the 0x2052-granted abilities); the swing target rides `args.SpellTarget.TargetHandle`.

## The ack rules (capture-proven)

1. **Every behavior request gets an echo-ack**: the request echoed with `seq+1` and **field3 spliced into
   the args, value = the request's own Mode** (NOT a server counter - the earlier "monotonic phase counter"
   read was Mode itself incrementing), tail echoed + 12 zero bytes appended
   (`CombatService.BuildBehaviorEchoAckBody`).
2. **Air swings (no target) get exactly that and nothing else** - armed and unarmed alike.
3. **Targeted ARMED hits** additionally get the result-block ack variant (the 81-byte block with the
   damage slot and the `0x06 000000CB` constant - `SendNormalAttackResponse`), then `DamageResultIIR
   (0xFEC4158A)` + the target's vitals application + `EnergyIIR (0x40749B8A)` stamina.
4. **Targeted UNARMED damage was not captured** (the capture's unarmed swings hit air); the server reuses
   the armed damage path as an extension - revisit if a targeted-unarmed capture lands.
5. Equip moves (0x09) ack with the identity-block variant + a 0x201C record - `docs/inventory.md`.

The `B2 90 80 5C 29 E6 27 CA` 8-byte sequence is the standard null-identity filler wherever an item
identity quad would go (behavior acks, the 0xF result block, 0x2052 grants).

## NPC death lifecycle (corpse linger -> loot bag -> respawn)

A killed NPC no longer instant-pops. The killing blow pushes the `health=0` vitals (`ApplyNpcDamage`), then
`CombatService.OnNpcKilled` only `MarkDead`s the world entity (+ XP + kill quests) - it does NOT despawn or
swap the corpse. The world tick (`WorldState.Tick`) drives two independent timers off the death:
1. **corpse linger** (`WorldEntity.CorpseLingerSeconds`, ~3s): the corpse stays the dying creature so the
   client plays its death animation off the health=0 it just got. When it elapses the tick emits
   `NpcCorpseLootable` -> `WorldBroadcaster.ConvertCorpseToLoot` turns the corpse into a lootable bag IN PLACE
   (reusing the handle - `MakeCorpseLootable`). Every kill leaves a lootable corpse; the drop ROLL only decides
   whether it carries items/cash (empty corpses are fine, matching retail). Falls back to a plain despawn only
   when the map has no loot-bag template.
2. **respawn** (`RespawnDelaySeconds`, ~15s): revives the NPC at its spawn point and re-renders it
   (`NpcRespawned`), clearing any unlooted corpse contents.

Open: the death animation currently relies on the client reacting to the `health=0` StatUpdate during the
linger - a dedicated death-state/anim IIR is not decoded (see #204; candidates noted there).

## Melee cleave (#18)

A swing damages an ARC in retail, not just the selected target. `CombatService.ApplyMeleeCleave` (called
after the primary hit on both the armed and unarmed paths) splashes the same damage to the hostile, living
NPCs clustered around the target - `CombatTargeting.CleaveTargets` selects them (target-centered: other
entities within `DefaultCleaveRadius` of the primary, excluding the primary/players/non-hostile/dead, nearest
first, capped at `DefaultMaxCleaveTargets`). Each secondary gets its own `DamageResultIIR` + the full
damage/kill lifecycle. This is the flat "everyone in the cluster takes the swing" half; the directional arc +
per-direction shield mitigation is #20 (decode #1 = the swing tail direction-ability id, done). The cleave is
TARGET-centered rather than a true player-facing cone because the rotation->forward convention isn't decoded;
radius + cap are provisional, tuned in-client.

## NPC counter-attack animation ack (decoded 2026-06-15)

When an NPC swings at the player, retail sends a second `BehaviorControlCenterIIR` (0x642CD3D6) whose
top-level **actor = the NPC handle** (the player's own attack ack puts the player there). It is structurally
the targeted-hit attack ack with the roles SWAPPED: `args.SpellTarget.handle` and the result-block target
both name the **player** the NPC hit, and the args carry **no Mode field** (the player-only field-2 - the
clean NPC-vs-player tell). `seq` is the constant `0x0004` (server-initiated, not echoing a client seq).
Sending it plays the NPC's swing + the player's hit-react; the authoritative HP move still rides the
world-tick `StatUpdate` (the ack's damage is just the floating number, so the two don't double-apply).

Decode: `Captures/decode_combat_npc_ack.py` (98/98 frames of the strickenbla capture). Builder
`CombatPacketCodec.BuildNpcAttackAnimationAckBody`, byte-exact against frame 6472 in
`CombatPacketCodecTests`. Wired into `WorldSimulation.ApplyNpcAttack` via `IWorldOutbound.SendNpcAttackAnimation`.
The animation/result params (`typeId`/`swingCounter`/`f5`/`f11`/`f12`/`resultType`/`resultK*`) are not
semantically decoded - capture-typical defaults for the live send; **needs in-client verification** that the
swing actually animates.

## Known gaps (TODO.md §Combat)
- HP/stamina auto-regen not implemented. NPC death animation: corpse now lingers then becomes a loot bag
  (above, #204); a decoded death-state IIR is still open.
