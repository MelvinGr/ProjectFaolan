# Combat: the BehaviorControlCenter ack protocol (armed + unarmed)

Status 2026-06-12: armed and unarmed swings work in-client; equip-state decides which protocol the client
speaks. Commits `226b16f`, `a115c40`. Golden tests: `BehaviorAckTests` (byte-exact against
`Captures/Stihetfv_soldier-conqueror_login_combat_armed-and-unarmed.pcapng`). Wider combat-loop status (NPC
aggro/death/XP/loot) is tracked as Gitea issues under the `area:combat` label, plus `docs/re/quests-and-combat.md`.

## The Interact request (0x642CD3D6 upload)

`[bodyLen u32][subop][0000C350][instance][seq u16 = 0003][behaviorId u32][argsLen u32][args][tail]`

- `behaviorId` = the `ActionType`: **0x0F NormalAttack** (armed swing), **0x11 UnarmedAttack** (empty main
  hand; behavior TypeIds 1089xx), 0x09 equip move (see `docs/subsystems/inventory.md`), 0x2E weapon switch, 0x16 talk,
  0x12 use object, etc.
- args protobuf `{TypeId, Mode, Category, Field6, ...}` - **Mode is a monotonic per-session action
  counter** across ALL BCC interactions.
- Attack tails end with the **direction-ability id** (769094/769095/769099 = Normal Attack Left/Right/
  Front - the 0x2052-granted abilities); the swing target rides `args.SpellTarget.TargetHandle`.

## The ack rules (capture-proven)

1. **Every behavior request gets an echo-ack**: the request echoed with `seq+1` and **field3 spliced into
   the args, value = the request's own Mode** (NOT a server counter - the earlier "monotonic phase counter"
   read was Mode itself incrementing), tail echoed + 12 zero bytes appended
   (`BehaviorAckBody.BuildBehaviorEchoAckBody`).
2. **Air swings (no target) get exactly that and nothing else** - armed and unarmed alike.
3. **Targeted ARMED hits** additionally get the result-block ack variant (the 81-byte block with the
   damage slot and the `0x06 000000CB` constant - `SendNormalAttackResponse`), then `DamageResultIIR
   (0xFEC4158A)` + the target's vitals application + `EnergyIIR (0x40749B8A)` stamina.
4. **Targeted UNARMED damage was not captured** (the capture's unarmed swings hit air); the server reuses
   the armed damage path as an extension - revisit if a targeted-unarmed capture lands.
5. Equip moves (0x09) ack with the identity-block variant + a 0x201C record - `docs/subsystems/inventory.md`.

The `B2 90 80 5C 29 E6 27 CA` 8-byte sequence is the standard null-identity filler wherever an item
identity quad would go (behavior acks, the 0xF result block, 0x2052 grants).

## NPC death lifecycle (corpse linger -> loot bag -> respawn)

A killed NPC no longer instant-pops. The killing blow pushes the `health=0` vitals (`ApplyNpcDamage`), then
`CombatService.OnNpcKilled` `MarkDead`s the world entity (+ XP + kill quests) and sends the explicit death
trigger `SimpleCharKilledByUpdateIIR` (0xB066AFC2) so the client plays the death anim (#204, see above). The
world tick (`WorldState.Tick`) drives two independent timers off the death:
1. **corpse linger** (`WorldEntity.CorpseLingerSeconds`, ~3s): the corpse stays the dying creature so the
   client plays its death animation. When it elapses the tick emits `NpcCorpseLootable` ->
   `WorldBroadcaster.ConvertCorpseToLoot`, which (decoupled, #246) rolls the drop, spawns a **SEPARATE** loot bag
   (its own handle, via `SpawnLootBagAt` - the standalone-bag path) at the death spot, then
   despawns the corpse. The bag lives/loots/expires independently (a `ForLoot` entity, tick-expired) and SURVIVES
   the NPC's respawn. (Old `MakeCorpseLootable` reused the NPC handle in place - removed; it pinned the respawn to
   the death spot, #246.) Empty drops just despawn the corpse.
2. **respawn** (`RespawnDelaySeconds`, ~15s): revives the NPC at its spawn point and re-renders it
   (`NpcRespawned`, which re-adds the handle to the viewers' Known set, #246/#245). **EXCEPT** summoned NPCs
   (`WorldEntity.NoRespawn`, set in `SpawnNpcForPlayer` — the pet-summon / test-spawn path): once dead they don't
   revive — the post-death timer runs
   as a despawn (`TickPostDeathDespawn` → `LootExpired` fade+remove) so a test mob fades out instead of coming
   back (commit `df8f90d`, a GM QoL Melvin asked for).

`.delete [<handle>]` (GM, `DeleteCommand`): the DURABLE removal — despawns the selected entity live AND deletes its
`NpcSpawns` row(s) (`IDatabaseRepository.DeleteNpcSpawns`) so it never re-renders, unlike a plain live despawn
(`DespawnEntityForPlayer`, which comes back on relog). The tool for scrubbing captured import artifacts (phantom
players baked into the pcap). A
neutral named entity deletes every same-name row on the map; a faction mob only the rows at its spot (commit `576c007`).

Death trigger (#204 NPC death anim + #214 player release screen) — **WIRED 2026-06-19 (commit f5bcb5e).** A
`health=0` StatUpdate alone does NOT drive the client's death state (verified in-client 2026-06-17: NPCs despawned
with no anim; the player release screen never showed). The explicit trigger is `SimpleCharKilledByUpdateIIR_t`
(**0xB066AFC2**) — decoded from `AssassinL23_..._resurect` as the packet right before the death `0x2024`. Record:
`[subop][0000C350][dyingHandle][00][01][0000C350][killerEndpoint]`, built by `WorldDeath.BuildKilledBy`
(byte-exact golden). `CombatService.OnNpcKilled` sends it for the dying NPC (after the killing-blow vitals);
`WorldSimulation.SendDeathScreen` sends it for the player BEFORE `DispatchValidResurrectionSpots`.

**Player death/resurrect — COMPLETE + verified in-client, #214 CLOSED 2026-06-20.** Full flow: release window
(`CharacterStats.bcc_state=29`) → **instant** respawn (the `killerEndpoint` MUST be the actual mob, not the
player's own handle, or the client's ResurrectView goes into the 15s wave-respawn path — `ResolveKiller` +
`WorldEntity.LastAttackerHandle`, commit 8a570b5) → revive (BccIIR 29→1 + vitals) → **relocate to the res point
with no loading screen** via the revive `IntendedTeleport`(0x10D27BC0)+`n3Teleport`(0x5A32F0D7) carrying the
position + the LIVE `Zone.PlayfieldToken` (`TeleportCodec.BuildReviveDestination`, `RelocateToRespawnPoint`,
commit 763aeb0). Trail: agent memory `protocol_death_resurrect`.
(The corpse object `TombStoneFullUpdateIIR_c` 0x0D80E7C9 is decoded but not used — the corpse lingers as the dying
creature, then a separate loot bag spawns, #246.)

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

Decode: `Captures/scripts/decode_combat_npc_ack.py` (98/98 frames of the strickenbla capture). Builder
`CombatPacketCodec.BuildNpcAttackAnimationAckBody`, byte-exact against frame 6472 in
`CombatPacketCodecTests`. Wired into `WorldSimulation.ApplyNpcAttack` via `IWorldOutbound.SendNpcAttackAnimation`.
The animation/result params (`typeId`/`swingCounter`/`f5`/`f11`/`f12`/`resultType`/`resultK*`) are not
semantically decoded - capture-typical defaults for the live send; **needs in-client verification** that the
swing actually animates.

## NPC chase / aggro movement (#190, 2026-06-15)

A hostile NPC with a player inside `NpcAggroRange` (20u) chases it: `WorldState.Tick` steps the NPC toward
the target (horizontal, `NpcMoveSpeed` 7u/s, keeping terrain Y) and `WorldBroadcaster.BroadcastNpcMove`
sends a per-tick **MoveTask transform** (`MoveTaskCodec.BuildBody`, the 61-byte `0x02` "orient" form:
origin->this-tick position, facing travel direction) so nearby viewers watch it move. In melee range
(`NpcMeleeRange` 3u) it swings (the attack-animation ack above). **Leash:** past `NpcLeashRange` (40u) from
spawn it disengages, walks home (ignoring re-aggro, `WorldEntity.Returning`), and heals on arrival; losing
the target out in the field also sends it home. WORKS in-client (it moves + leashes + stops on de-aggro).

On arrival home the NPC now also emits a **StopIIR** (`StopCodec` → `BroadcastNpcStop`, the `NpcStopped`
tick effect) to formally close the move stream (#190), and on engage the broadcaster pushes the NPC's
**aggro** — a `HateListUpdate` + `ThreatLevel` (`HateListCodec`/`ThreatLevelCodec` → `BroadcastNpcAggro`,
#216) — alongside the combat-mode weapon-draw. Both are gated by `NpcAutoAttackEnabled` and await in-client
verification.

**Open polish (walk gait + slight snap).** The 61-byte form has no speed field, so the client uses a default
interpolation (a small teleport/snap) and a **walk** gait. Findings from the MoveTask grammar:
- The task-type byte **@30** (offset incl. the 4-byte length prefix) is the form discriminator: `0x02` orient
  (the 61-byte form we send), `0x04` patrol/segment-list, `0x08` **follow-entity**. NB the FULL recursive grammar
  + authoritative per-mode type names are now decoded (100%, #53) in `docs/re/movetask-grammar.md` (0x04 is the
  recursive `ListMoveTask`, 0x06 = `AnimatedMoveTask`); the labels here are combat-side shorthand.
- `0x08` follow (the chasing mobs send it with id = the player handle) is **target/facing metadata, NOT a
  mover** - wiring it as the chase driver made NPCs face+attack in place without moving. The actual
  locomotion rides the position-transform packets sent alongside it.
- ⚠️ `MoveTaskBuilder` bodies (`BuildStationary`/`BuildPatrol`/...) carry their **own** 4-byte length prefix,
  so they must be framed RAW after the RPC header - wrapping them in a `WriteArrayUInt32Length` length prefix
  double-prefixes and the client drops the packet.
- **Next candidate** for run gait + no snap: the `0x05` "pace" form (len 69) `[05][const 12][f32][vec3][vec3]`
  - a speed float + two positions (a linear move WITH speed). Drive the per-tick step from it instead of the
  61-byte form. Full MoveTask grammar: `docs/re/spawn-dissection-plan.md`.

## NPC weapon-draw / combat stance (#218, decoded 2026-06-16)

NPC swings rendered **empty-handed** (the swing plays but no weapon is in hand) because the client never put the
NPC into **combat stance** - entities sheathe their weapon out of combat and DRAW it on entering combat, and we
never sent the draw. There is **no dedicated weapon-draw IIR**: the trigger is a **stat** in the ordinary
`0x96B8DC59` vitals update - **stat `0x14` (CombatMode) = 4**. In the strickenbla capture every beach melee mob
gets `CombatMode = 4` the tick BEFORE its first swing (handle 0x2C56 frame 6424 → swings 6472; 0x2C59 7341→7424;
0x2CCE 9441→9462; 0x2C47 11118→11138; 0x2C48 12141→12187 - perfect correlation), and the player gets
`CombatMode = 1` when it enters combat. The value is a stance/mode code (melee mobs = 4, player = 1, one
non-swinging entity = 1) not fully decoded, but **4 is what every actual melee swinger sends**; idle entities
carry no stat-20 record at all, so **0 = sheathed**.

Implemented as a world-sim state transition: `WorldEntity.InCombat` flips in `WorldState.Tick` when a hostile NPC
acquires/loses a target (emitting `NpcEnterCombat`/`NpcExitCombat` effects), and `WorldBroadcaster.BroadcastNpcCombatMode`
pushes the stat (4 on enter, 0 on exit) to every viewer in range that already sees the NPC (mirrors `BroadcastNpcMove`).
Reset on death/respawn so a respawned mob re-draws cleanly. Builder `VitalsPacket.BuildStatBlockBody`, byte-exact
against frame 6424 (`CharacterStatBlockTests.BuildStatBlockBody_NpcCombatMode_MatchesCapturedFrame`); transition
logic covered by `WorldStateTests.Tick_NpcEngagesAndDisengages_EmitsCombatModeEffects`. The exit value (0) is an
inference (the capture's mobs died rather than leashing, so a sheathe was never captured). **VERIFIED in-client
— the NPC swings with its weapon drawn (#218 CLOSED).**

## Level-up feedback (#209)
On a ding (XP-driven or the GM `.level` set) retail sends, in order: a burst of **`AttributeChanged`/`Increased`**
on-screen feedback lines — **one per primary attribute that rose** (the 5 category-615 ids 804/806/808/810/814) —
then a `0x96B8DC59` vitals push with the new Health/Stamina/Mana maxima. There is **no** "You reached level N"
banner in any capture, and the Level number is never pushed as a standalone `0x36` stat (the client tracks it from
the snapshot/quest stream). The attribute lines are `FormatFeedbackIIR` payloads — see `docs/subsystems/on-screen-feedback.md`
and `AttributeChangedFeedback` (decode `Captures/scripts/decode_levelup_feedback.py`, golden `AttributeChangedFeedbackTests`).
Wired into both paths: GM `.level` (`SetLevelCommand`) and `CombatService.PushLevelUp`. Which attributes rise / by
how much is provisional (`Combat.AttributeGrowth`, flat +1/primary) — no per-class growth tables exist in captures.

## Live spell / ability grants (#73, #74)
Two distinct mid-session grant packets, both on the GameCharInterface conversation (same class pair), each
byte-exact:
- **`0x2052 DispatchAddSpecialAbility`** — grants a catalog **ability** (the melee/utility kit). Body = the 81-byte
  ability struct. `SendAddSpecialAbility`/`AbilityGrantBody.BuildAddSpecialAbilityBody`, used by login
  `GrantCharacterAbilities` (post-0x2024, from `FlushPendingPost205FFollowUp`).
- **`0x200A DispatchLearnSpell`** — grants a real **spell**. Body = a bare big-endian `u32` spell id (`i_nSpellID`),
  invariant across all 68 captured packets. `SendLearnSpell`/`AbilityGrantBody.BuildLearnSpellBody` (decode
  `Captures/scripts/decode_learn_spell.py`, golden `LearnSpellTests`).

(The GM test commands `.grantability`/`.abilitylist`/`.learnspell` were removed in the "remove 25 GM test commands"
cleanup — 2026-07-05; the underlying wire builders above are still live.)

**Spell granting / persistence (#74).** Spells are learned per-character: the captures show a caster's whole
learned set is re-sent on every world entry (NecroL80 always relearns her 5, DemoL80 his 9), so it is modelled like
the ability re-grant. The set lives in the per-character `CharacterSpell` table (CharacterId, SpellId);
`GrantCharacterSpells` replays it on world entry (BEFORE the FullChar snapshot, via `SendLearnSpell`, so the
client absorbs them silently — see `SendWorldEntry`), and `CaptureSpellImporter.ImportLearnedSpellsAsync` seeds captured characters from the grants in
the pcaps (attributed via the grant's RpcHeader **receiver instance** = the character id; the GameCharAgent→
GameCharInterface class pair disambiguates the 0x200A opcode collisions). Level-up auto-grant is NOT implemented —
captures don't say which spell is learned at which level. **In-client verification pending** (needs-testing on #74).

The sibling ops the RE listed — `DispatchForgetSpell 0x200B`, `RemoveSpecialAbility 0x2053`, `UntrainSingleFeat
0x2011`, `PassiveAbilityEquip/Unequip` — have **zero** packets on this class in any capture, so no byte-exact
oracle; deferred under #73.

## Buffs: ApplySpell self-buffs, the mount buff, the login shield (#26 / #221)
Buffs ride **`0x6B87E316` ApplySpell (MagicBuff)**, the same op as a combat cast. A **self-buff** is
`applyTo == applyFrom == the entity's own handle`:
`[subop][0000C350][applyTo][u16 seq][spellId u32][0000C350][applyFrom][data]`. `ApplySpell`
builds it (via `SendMagicBuffIir`, which registers `applyTo == applyFrom` casts in `WorldEntity.ActiveBuffs` so
the client-initiated `CancelBuff` `0x201E` and the expiry tick can track them). The **wire spell id is `(baseId << 8) | rank`** — apply uses rank `00`,
remove uses rank `01`; the payload embeds the same `baseId` at offset **[22..26]**.

**Mount buff — fully captured** (`NecroL80_mage-necromancer_login_mount_move_dismount`, the only 2
ApplySpell packets in it): mount up = self ApplySpell **`0x31EB1500`** + a **54-byte** payload
(`…0031eb15…` at [22..26]); dismount = **`0x31EB1501`** (base|rank 01) with an **empty** payload. So the mount
**buff** apply/remove is a real oracle now — distinct from the demol80 `0x3BAD7D` summon. The mount **entity
spawn** is still gated on the mid-session-spawn blocker (#26), so the buff renders but the horse model does not.

**Persistent login shield (#221) — CURRENTLY DISABLED.** The `GrantStartingBuffs` call in
`FlushPendingPost205FFollowUp` (which re-applied a class-agnostic shield self-buff to every character, right after
the ability/spell re-grants, and re-armed on each teleport/changemap re-entry) is **commented out as of
2026-06-19** (`GameServerListener.WorldEntry.cs`): the capture-proven id `0x000B9000` renders in-client as the
"Exhausted" DEBUFF and, applied persistently with no working expiry, never clears. It stays off until the
expected duration/clear-point is pinned. The wire grammar is validated against the captured mount buff above, so
the shape is sound, but **"Shield of the Risen" is in no capture** — its exact id/payload is unknown.
**Blocked on #221** (buff expiry) before re-enabling.

## Starting gear (#220)
A brand-new beach character is seeded its slave outfit in `EnsureBeachStarterEquipment`
(`GameServerListener.WorldEntry.cs`), from the `StartingData` constants harvested from the `*create*` captures
(one row, only if absent — an existing equipment row is left as the player left it):
- **Wrist:** Slave Manacles (`StartingData.StartingWristItem` = 3361557, universal).
- **Clothing by sex** (`Character.Sex`): male = bare chest + Red Loincloth (legs, `StartingMaleLegsItem`); female =
  gown (chest) + matching bottom (legs), one of `StartingFemaleOutfits`. Garment colour is randomised per character
  in retail, so one representative set is picked at random per female character.
- **Weapon by class** (main-hand-**left** slot 0x06, `StartingData.StartingWeapon`): Assassin → Marlin Spike,
  Necromancer → Broken Bottle (0x33D0A0), Tempest of Set → Broken Bottle (215711, the other bottle). **Per-class,
  NOT per-archetype** (Conqueror + Guardian are both Soldiers and do
  not share a weapon). Coverage is only the classes the create dumps contain; the rest start unarmed. The Fleshy
  Club (a level-2 char) and Broken Oar (a login capture) are NOT create-flow starters and are excluded. In-client
  verification pending (needs-testing on #220).

## Known gaps (Gitea, `area:combat`)
- HP/stamina auto-regen not implemented. NPC death animation: corpse now lingers then becomes a loot bag
  (above, #204); a decoded death-state IIR is still open.
