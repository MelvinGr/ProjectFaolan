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

## Known gaps (TODO.md §Combat)

- Melee is cone/AoE in retail (every target in the arc takes damage); we resolve only the selected target.
  Directional combat + defensive shields is #20 (decode #1 = the swing tail direction-ability id, done).
- NPC counter-attack ANIMATION ack (retail's second BccAck with actor = target handle) - undecoded; the
  world model deals NPC damage on the tick instead.
- HP/stamina auto-regen not implemented. NPC death animation: corpse now lingers then becomes a loot bag
  (above, #204); a decoded death-state IIR is still open.
