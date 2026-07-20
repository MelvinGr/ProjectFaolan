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
`CombatService.OnNpcKilled` `MarkDead`s the world entity (+ XP + kill quests) and plays the death anim via the
retail NPC-death pair — a **death-pose `MoveTaskIIR`** (0x96C46740, movement-type 0x06, freezes the corpse in
place) + a **death-behavior `BehaviorControlCenterIIR`** (0x642CD3D6, behaviorId 41877) on the dying handle
(`WorldDeath.BuildDeathPose`/`BuildDeathBcc`, #204/#349, see below). The
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

Death trigger (#204 NPC death anim + #214 player release screen). A `health=0` StatUpdate alone does NOT drive
the client's death state (verified in-client 2026-06-17: NPCs despawned with no anim; the player release screen
never showed). **NPC death and player death use DIFFERENT triggers** — decoded from
`NecroL80_..._casting-combat` (NPC) vs `AssassinL23_..._resurect`/`Stihetfv_..._beach` (player):

- **NPC death** (#204/#349) = a **death-pose `MoveTaskIIR`** (0x96C46740, movement-type 0x06 — freezes the
  corpse where it fell) followed by a **death-behavior `BehaviorControlCenterIIR`** (0x642CD3D6) on the dying
  handle. In `NecroL80_..._casting-combat` **every** NPC kill carries exactly this pair and **NO
  `SimpleCharKilledBy`** (validated by `Captures/scripts/decode_npc_death.py`). The death is identified by the
  BccIIR **envelope** — form **0x0004**, slot **0x0000001F**, a 13-byte protobuf `08 <behaviorId> 18 <variant>
  28 08 30 00 60 <f12>`; the **behaviorId is creature-specific** (that creature's own death animation — 41877
  for the common humanoid mobs, 17036 for the F023 creature) and the field-3 `<variant>` byte varies per kill
  (a hit-direction/ragdoll selector). Built by `WorldDeath.BuildDeathPose` + `BuildDeathBcc(handle,
  NpcDeathVariant)` (byte-exact goldens), sent by `CombatService.OnNpcKilled` after the killing-blow vitals. We
  send the common humanoid death (behaviorId 41877, `NpcDeathVariant` = 0x05); per-creature death behaviorIds
  need per-NPC behavior data we don't yet carry (follow-up).
- **Player death** (#214) uses `SimpleCharKilledByUpdateIIR_t` (**0xB066AFC2**) — the "you were killed by X"
  record that drives the local release/resurrect UI, ABSENT from NPC kills. Record:
  `[subop][0000C350][dyingHandle][00][01][0000C350][killerEndpoint]`, built by `WorldDeath.BuildKilledBy`
  (byte-exact golden). `WorldSimulation.SendDeathScreen` sends it (along with the death-pose + death-Bcc) for the
  player BEFORE `DispatchValidResurrectionSpots`.

**Player death/resurrect — COMPLETE + verified in-client, #214 CLOSED 2026-06-20.** Full flow: release window
(`CharacterStats.bcc_state=29`) → **instant** respawn (the `killerEndpoint` MUST be the actual mob, not the
player's own handle, or the client's ResurrectView goes into the 15s wave-respawn path — `ResolveKiller` +
`WorldEntity.LastAttackerHandle`, commit 8a570b5) → revive (BccIIR 29→1 + vitals) → **relocate to the res point
with no loading screen** via the revive `IntendedTeleport`(0x10D27BC0)+`n3Teleport`(0x5A32F0D7) carrying the
position + the LIVE `Zone.PlayfieldToken` (`TeleportCodec.BuildReviveDestination`, `RelocateToRespawnPoint`,
commit 763aeb0). Trail: agent memory `protocol_death_resurrect`.
(The corpse object `TombStoneFullUpdateIIR_c` 0x0D80E7C9 is decoded but not used — the corpse lingers as the dying
creature, then a separate loot bag spawns, #246.)

**Resurrection spots (`DispatchValidResurrectionSpots`, s2c 0x2024) — body fully decoded, #372.** The body is a
reference position + a **list** of the points a player may revive at, built by `ResurrectionSpotsBody`:

```
[refPos X,Y,Z][u32 (count+1) * 0x3F1]  then count × 41-byte records:
  [spotPos X,Y,Z][u32 spotType][u32 mapId][u32 0][u32 0][u32 0][u32 spotId][u32 restorationLevel][u8 0x00]
```

Three fields once carried as "undecoded constants" are real, and all three were invisible while we only ever sent
the **one** spot of the death case — at count 1 the hardcoded `0x7E2` *is* the correct prefix and a single
end-of-body `0x00` lands exactly where the per-record one goes
[V3: DispatchValidResurrectionSpotsTests.ValidResurrectionSpotsBody_CountPrefixIsListFramed]:

- `0x7E2` is the **list count**, `(count+1) * 0x3F1` — the same list framing the guild lists use. The four values
  across the captures are exactly 2/3/4/8 × 0x3F1 for the 1/2/3/7-spot frames.
- the trailing `0x00` is **per-record** (stride 41), not one byte at the end of the body.
- the `0x0A` tail is **`RestorationLevel`** — the percent of max the point revives you at (#375). Observed
  0x0A/0x32/0x64 = 10/50/100, and for all 21 spot ids in the captures the wire value equals the RDB
  `RestorationLevel` we import for that point, along with its map and position, to the byte
  [V3: DispatchValidResurrectionSpotsTests.ValidResurrectionSpotsBody_EmitsThePointsRestorationLevel].

The list is ordered **nearest-first** from the reference position (the client renders it in wire order). Proven
rather than guessed: the three 7-spot frames carry the *same seven points in three different orders*, each
ascending by distance from that frame's own reference position; ascending distance holds in 23/23 multi-spot
frames where ordering by id holds in only 16/23
[V3: DispatchValidResurrectionSpotsTests.ValidResurrectionSpotsBody_TwoSpots_MatchCapture_OrderedNearestFirstWhateverTheInputOrder]. `spotType` (0xC7A0 login / 0xC79E create-flow / 0xC79C once)
stays undecoded and is **not** a property of the point — point 790836 appears under both 0xC79C and 0xC7A0.

**Discovery = a banner + a re-send (#372), verified in-client.** Walking near an undiscovered point sends TWO
things in the same frame [V2: Hoxxling_mage-heraldofxotli_login_gameplay.tshark.json frame 7862]:

1. a **`FormatFeedbackIIR` FIFO banner** — the toast the player sees (`FormatFeedbackCodec.BuildRespawnPointDiscoveredXml`,
   `typeFlags` = FIFO banner, footer 1, same framing as the area-name banner);
2. the **spots re-send** above, with the new point now in the list, so the release screen has it next death.

The banner is sent with the **live** position to a **living** player and does not open the release screen (that is
`SimpleCharKilledByUpdateIIR`'s job). `WorldSimulation.DiscoverNearbyRespawnPoints` drives both.

The banner XML identifies its string by **`id=`**, not the `token=` every other feedback message uses:

```xml
<remoteformat id="218327236" category="100" key="KsATGOK`H%9l@@z[1mo," knubot="0">
  <parameter value="7;&lt;localized id=&quot;784237&quot; category=&quot;270&quot; ... &gt;" />
</remoteformat>
```

⚠️ **A scan for `remoteformat token="…"` cannot find this message, and its absence is not evidence it doesn't
exist.** #372 was stuck for exactly that reason: a token scan came back empty, the empty result was read as "retail
sends no toast", and a whole theory (the client toasts itself off the re-send, like `NewLevelIIR`) got built to
explain an artifact of the search. The server bytes were byte-perfect against the capture and the client still
showed nothing. When enumerating feedback, match `<remoteformat ` and read whichever of `id=`/`token=` is present —
across the captures 175 frames use `token=` and 8 use `id=` (message ids 218327236, 64771509, 23).

**No name crosses the wire**: the parameter is a nested `<localized>` naming the point by its own id under LDB
category 270 ("Water's Edge", "Secluded Cove", …), and the client resolves it — a point's name-string id IS its
point id. The nested `key` is the per-point key of that string. It is **optional**: a keyless banner renders
correctly in-client (verified on Conall's Valley 3711341, a point no capture covers), which matters because
captures only ever discover 784237/784238 — there is no key for the other 536 and no way to derive one. Feedback
keys are per-string CONSTANTS, never per-send nonces: all 16 message ids/tokens across the captures carry exactly
one key each [V3: FormatFeedbackCodecTests.BuildRespawnPointDiscoveredXml_MatchesCapture].

Discovery radius is **not** capture-derivable: the two captured discoveries fire at 13.7 and 11.6 units, but that
is where the player had already got to when the server noticed — a floor, not the value. There is no radius in the
RDB either, so `RespawnDiscoveryRange` is a feel call (30). Both crossing tests (discovery + area banners) sample
on the **base tick**; they previously rode the 1 Hz regen accumulator, which made them fire late and read in-client
as a too-small radius.

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
- ⚠️ `MoveTaskCodec` bodies (`BuildStationary`/`BuildPatrol`/...) carry their **own** 4-byte length prefix,
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

**The data tail has exactly ONE form: 54 bytes** (`SelfBuffCodec.BuildBuffDataLong`). Across all 103
`Captures/*.tshark.json` the only s2c `MagicBuffIIR` tails are **54** bytes (×1412) and **0** (×578 — the rank-1
end twin). A 50-byte tail occurs **zero** times. `[V2: Captures/*.tshark.json — every s2c body whose u32 length is
followed by 6b87e316; scan reproduced by Faolan.Tests/SelfBuffCodecTests]` The client's
`MagicBuffIIR_c::ReadSubClass` reads 4 bytes past the end of a 50-byte tail and **rejects the whole record** —
silently, apart from the client log (`ReadSubClass for IIR 1804067606 (MagicBuffIIR_c) failed! datasize left in
stream=4, raw=76`) — so the buff simply never renders.

⚠️ Both of these bit us at once (#274, fixed 2026-07-16, in-client verified): the cast path passed `CastSpell`'s
**bare base id** where `ApplySpell` wanted the `(base<<8)|rank` **wire** id, *and* the shared path emitted a
50-byte tail that never existed in retail (it originated as a hand-built blob in our own code, and its golden test
asserted the codec against that same blob, so it could never fail). Server-side the lifecycle looked perfect —
correct durations, clean expiry, removal twins on the wire — while the client rendered nothing. **A buff bug that
looks correct in the server log is almost always one of these two.**

**Mount buff — fully captured** (`NecroL80_mage-necromancer_login_mount_move_dismount`, the only 2
ApplySpell packets in it): mount up = self ApplySpell **`0x31EB1500`** + a **54-byte** payload
(`…0031eb15…` at [22..26]); dismount = **`0x31EB1501`** (base|rank 01) with an **empty** payload. So the mount
**buff** apply/remove is a real oracle now — distinct from the demol80 `0x3BAD7D` summon. The mount **entity
spawn** is still gated on the mid-session-spawn blocker (#26), so the buff renders but the horse model does not.

**Buff persistence across relog (#274).** Self-buffs survive a logout **with their remaining time**. The live
`WorldEntity.ActiveBuffs` is savegame state, so `PlayerPersistenceService.SavePlayerState` (logout + the 5-min
autosave) snapshots each self-buff to the `CharacterBuff` table — `BuffId`, the effect `Nonce`, and an absolute
`ExpiresAt` — and `RestoreBuffs` re-applies them in the post-`0x205F` world-entry flush
(beside `RestoreMount`). Restore rebuilds the `ApplySpell` payload from those columns via `SelfBuffCodec` (not a
stored blob) with the duration recomputed to the time left, so the client counts down the **remaining** seconds and
the tick expires it on schedule. Only self-buffs persist (NPC-cast debuffs carry a volatile caster handle + a DoT
and are dropped); buffs owned by a dedicated re-applier — the login shield and the mount ride buff — are excluded
at save time so they aren't double-applied on entry (`GameServerListener.ShouldPersistBuff`).

**Shield of the Risen (#221) — RESOLVED + in-client verified.** `SendShieldOfTheRisen`
(`GameServerListener.Buffs.cs`) applies the newbie-protection self-buff from `FlushPendingPost205FFollowUp`, which
is re-armed on each teleport/changemap re-entry, so it re-asserts on login AND every playfield change. The real
ids are bases **`4061768` (1h)** + **`3666828` (10h)**, both named "Shield of the Risen" in `Spells` with real
durations (`NecroL80_..._conans-castle-exit` rec#165/#736) — the old `0x000B9000` claim was WRONG (that id is the
"Exhausted" debuff). It uses `SelfBuffCodec.BuildBuffDataLong` — the 54-byte tail that is the only form retail
sends (#274) — and retail shows exactly **one at a time**, picked by playfield type: the 1h on an instanced
playfield, the 10h in the shared open world (we select by `Map.InstanceMode`, tracked in
`Session.ActiveShieldSpellBase`). The client renders it TOP-of-screen (protection banner) and auto-generates the
"You are protected …" line from the ApplySpell itself. It clears three ways: the **10h/1h timer** (the generic
`TickBuffExpiry`, like any timed buff), a manual `CancelBuff`, or — the newbie-protection rule — the **first
aggressive action**.

**Shield breaks on the first aggressive action.** `BreakShieldOnAggression` (`GameServerListener.Buffs.cs`) drops
the shield the instant the player commits an aggressive action — an offensive cast (target ≠ self), a ground AoE,
or a melee/unarmed swing — matching retail's "protected until you attack." For a *cast* it fires at **cast begin**
(the aggression is known upfront from the target/ground-cast), not when the cast time elapses. It sends the
**`LostGraceEffect`** on-screen feedback line (byte-exact `FormatFeedbackCodec.BuildLostGraceEffectXml`, verified
against `NecroL80_..._conans-castle-exit` rec#508 where the loss happens mid-combat) **before** the rank-1 removal
twin, then drops the live buff and clears `Session.ActiveShieldSpellBase`. A self-buff cast never breaks it (the
helper no-ops when no shield is tracked). A pure **playfield swap** of the shield does NOT send `LostGraceEffect`
(rec#206/#1750 swap the 1h↔10h silently) — the toast means the grace was genuinely lost. The **timer-expiry**
path deliberately does not send `LostGraceEffect` (no capture oracle for the 10h duration).

## Cross-player action visibility (#48 + spell effects)
A player's combat/cast packets are framed to the **actor** only, so without relaying they are invisible to
other players in range. Retail relays them: the `Hoxxling …_other_playersvisible` capture shows s2c packets for
other-player handles (the `0x01316xxx` char-id range, distinct from the `0x000xxxxx` NPC range) — the **same
body** the actor's own client receives, only the frame receiver differs. Two relays run off the interest
`Known` set (so a still-loading viewer is never sent an action for a handle it hasn't built — the `#245`
invariant):
- **The cast/attack GESTURE** — the `BehaviorControlCenter` animation (`#48`, `BroadcastBehaviorToViewers`),
  fired for `NormalAttack` / `UnarmedAttack` / `CastSpell` in `GameServerListener.Inbound.cs`.
- **The spell EFFECT** — the `ApplySpell` (`0x6B87E316`) itself (`BroadcastSpellToViewers`, from
  `SendMagicBuffIir`), so the buff/effect that lands renders on nearby clients, not just the gesture. The capture
  streams both other-player **self-buffs** (e.g. player `0x01316e68`) and **offensive casts** (player
  `0x01316cee` on NPC targets) to the observer, framed to the observer's endpoint. Verbatim-relay + Known-gating
  locked by `CrossPlayerSpellRelayTests` against the captured body. **2-client in-client confirmation pending.**

## Spell cast FSM (#236)
A player spell cast is a `BehaviorControlCenter` (`0x642CD3D6`) `CastSpell` action (`ActionType 4`), handled in
`GameServerListener.Inbound.cs`. The real spell id rides the action **trailer** (`[u32 from][u32 spellId]`), not
the protobuf `SpellTarget.SpellId` (that field is the constant `0xC350` type-word). The BCC **state word** (the
`u16` before `actionType`) is `3` = begin, `2` = **cancel** — capture-derived (`actions-described-in-chat`:
begin×2 / cancel×1; every cast in the ice-strike/casilda/casting captures is begin=3).

- **Timing:** begin → echo-ack (renders the cast bar) → hold the spell's `CastTime` (`Task.RunAfter`) →
  complete-ack (form-`0x0a`) → resolve the effect. `CastTime 0` resolves immediately.
- **Interrupt / move-cancel (#347):** each begin captures a per-session `ActiveCastToken` (`NetworkClientSession`).
  The client sends its own `CastSpell` **state `2`** cancel when the player moves mid-cast (for both a locked cast
  and a ground-target AoE); the server also bumps the token from the move handler when it sees a real translation
  (`GameServerListener.Inbound.cs` `CharDCMoveIIR`, position delta > `CastMoveCancelEpsilonSq` so turning-in-place
  to aim doesn't self-cancel). Either way the deferred resolve sees a mismatched token and aborts (no complete-ack,
  no damage). The **client cast bar** is stopped by answering the cancel with retail's **state `0x0007` abort BCC**
  (`BehaviorAckBody.BuildCastInterruptAbort` / `CombatService.SendCastInterruptAbort`) — the begin's args (held as
  `Session.PendingCastArgs`) re-emitted at state `0x0007` with the cancel packet's tail; this drops the bar, cancels
  the animation and pops the client's "Interrupted" floating text (client-generated — no separate feedback packet).
  Byte-exact against `NecroL80_..._cast-interupt` (begin idx988 + cancel idx1006 → abort idx1013), golden-tested in
  `BehaviorAckTests`. Echoing the cancel back (the old behaviour) left the bar running.
- **Ground-targeted AoE:** a spell aimed at a reticle POSITION (no locked entity) carries the target Vector3 in
  the trailer (`[u8 kind=0][f32 X/Y/Z BE]…`) and has **no** `SpellTarget`; `CastGroundTarget.TryParse` reads it
  (an entity cast sends `kind=1` and a garbage position). `CombatService.ResolveGroundAoe` then damages every
  attackable NPC (Disposition ≠ Friendly) within `GroundAoeRadiusUnits` of the reticle — on empty ground it is a
  no-op like retail. **[V0]** the AoE radius is a single interim constant (no per-spell radius in the RDB spell
  record) and the channel's per-tick DoT is resolved as one hit on completion — both are Gitea follow-ups.

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
