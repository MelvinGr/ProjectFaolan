# Combat: the BehaviorControlCenter ack protocol (armed + unarmed)

Status 2026-06-12: armed and unarmed swings work in-client; equip-state decides which protocol the client
speaks. Commits `226b16f`, `a115c40`. Golden tests: `BehaviorAcksTests` (byte-exact against
`Captures/Stihetfv_soldier-conqueror_login_combat_armed-and-unarmed.pcapng`). Wider combat-loop status (NPC
aggro/death/XP/loot) is tracked as Gitea issues under the `area:combat` label, plus `docs/re/quests-and-combat.md`.

## The Interact request (0x642CD3D6 upload)

`[bodyLen u32][subop][0000C350][instance][seq u16 = 0003][behaviorId u32][argsLen u32][args][tail]`

- `behaviorId` names a COMMAND in the client's own classmap — `BehaviorId` carries all 34 under the client's
  names, and the full grammar (including the one phase that carries an extra word here) is
  [behaviorcontrolcenter-grammar.md](../re/behaviorcontrolcenter-grammar.md). The ones this page is about:
  **0x0F Combat:Melee** (armed swing), **0x11 Combat:Unarmed** (empty main hand; behavior TypeIds 1089xx),
  0x09 Equip (see `docs/subsystems/inventory.md`), 0x2E SwapAlternateEquipment, 0x16 UseCharacter (talk),
  0x12 UseDynel. ⚠️ "NormalAttack" and "UnarmedAttack" were OUR names for the first two, corrected in #536;
  `CombatService`'s `SendNormalAttackResponse` still keeps the old spelling.
- args protobuf `{TypeId, Mode, Category, Field6, ...}` - **Mode is a monotonic per-session action
  counter** across ALL BCC interactions.
- Attack tails end with the **direction-ability id** (769094/769095/769099 = Normal Attack Left/Right/
  Front - the 0x2052-granted abilities); the swing target rides `args.SpellTarget.TargetHandle`.

## The ack rules (capture-proven)

1. **Every behavior request gets an echo-ack**: the request echoed with `seq+1` and **field3 spliced into
   the args, value = the request's own Mode** (NOT a server counter - the earlier "monotonic phase counter"
   read was Mode itself incrementing), tail echoed + 12 zero bytes appended
   (`BehaviorAcks.EchoAck`).
2. **Air swings (no target) get exactly that and nothing else** - armed and unarmed alike.
3. **Targeted ARMED hits** additionally get the result-block ack variant (the 81-byte block with the
   damage slot and the `0x06 000000CB` constant - `SendNormalAttackResponse`), then `DamageResultIIR
   (0xFEC4158A)` + the target's vitals application + `EnergyIIR (0x40749B8A)` stamina.
4. **Targeted UNARMED damage was not captured** (the capture's unarmed swings hit air); the server reuses
   the armed damage path as an extension.
5. Equip moves (0x09) ack with the identity-block variant + a 0x201C record - `docs/subsystems/inventory.md`.

The `B2 90 80 5C 29 E6 27 CA` 8-byte sequence is the standard null-identity filler wherever an item
identity quad would go (behavior acks, the 0xF result block, 0x2052 grants).

## NPC death lifecycle (corpse linger -> loot bag -> respawn)

A killed NPC no longer instant-pops. The killing blow pushes the `health=0` vitals (`ApplyNpcDamage`), then
`CombatService.OnNpcKilled` `MarkDead`s the world entity (+ XP + kill quests) and plays the death anim via the
retail NPC-death pair — a **death-pose `MoveTaskIIR`** (0x96C46740, movement-type 0x06, freezes the corpse in
place) + a **death-behavior `BehaviorControlCenterIIR`** (0x642CD3D6, behaviorId 41877) on the dying handle
(`DeathSequence.Pose`/`DeathSequence.Bcc`, #204/#349, see below). The
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
   back (commit `df8f90d`, a GM QoL).

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
  `SimpleCharKilledBy`** (validated by `DeathSequenceTests`). The death is identified by the
  BccIIR **envelope** — form **0x0004**, slot **0x0000001F**, a 13-byte protobuf `08 <behaviorId> 18 <variant>
  28 08 30 00 60 <f12>`; the **behaviorId is creature-specific** (that creature's own death animation — 41877
  for the common humanoid mobs, 17036 for the F023 creature) and the field-3 `<variant>` byte varies per kill
  (a hit-direction/ragdoll selector). Built by `DeathSequence.Pose` + `DeathSequence.Bcc(handle,
  NpcDeathVariant)` (byte-exact goldens), sent by `CombatService.OnNpcKilled` after the killing-blow vitals. We
  send the common humanoid death (behaviorId 41877, `NpcDeathVariant` = 0x05); per-creature death behaviorIds
  need per-NPC behavior data we don't yet carry.
- **Player death** (#214) uses `SimpleCharKilledByUpdateIIR_t` (**0xB066AFC2**) — the "you were killed by X"
  record that drives the local release/resurrect UI, ABSENT from NPC kills. Record:
  `[subop][0000C350][dyingHandle][00][01][0000C350][killerEndpoint]`, built by `SimpleCharKilledByUpdateCodec`
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
reference position + a **list** of the points a player may revive at, built by `ValidResurrectionSpotsCodec`:

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

1. a **`FormatFeedbackIIR` FIFO banner** — the toast the player sees (`RespawnPointDiscoveredCodec`,
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

A swing damages an area **in front of the attacker**, not just the selected target and not a ball around it.
Funcom's own quickstart: *"Your attacks deal damage in a small area **in front of you**. You'll always do more
damage to your primary target, but you should be mindful of other nearby enemies!"*, and the wiki's *"if you
can get two or more enemies in front of you, your attacks can hit multiple enemies"*.

`CombatService.ApplyMeleeCleave` (called after the primary hit on both the armed and unarmed paths) splashes
to the hostile, living NPCs inside that cone — `CombatTargeting.CleaveTargets` selects them: within
`DefaultCleaveReach` of the **attacker** and inside `DefaultCleaveHalfAngleDegrees` of their facing,
excluding the primary/players/non-hostile/dead, nearest first, capped at `DefaultMaxCleaveTargets`. Each
secondary gets its own `DamageResultIIR` + the full damage/kill lifecycle.

⚠️ This was **TARGET-centered** until 2026-08-05 — a sphere around the selected enemy, chosen so it would need
no rotation convention. Wrong shape, and wrong in a way a player feels: it cleaved into a mob standing behind
the target and away from you, and missed the one beside you the target happened to be far from. The facing it
was avoiding has been decoded for a while (`2·atan2(qy, qw)`,
[`../re/chardcmove-grammar.md`](../re/chardcmove-grammar.md)).
[V3: CombatTargetingTests.TheConeIsAroundTheATTACKER_NotAroundTheTarget]

**Same damage to the secondaries, deliberately.** The quickstart says the primary takes more, and the only
shipped number for it is `Combat_{PvE,PvP}DamageReductionMainOnSplash` = **1** — which the client registers and
never reads (`TweakGlobals`: reads=0 on both, against reads=1 on a control tweak, so they are the server's
numbers), and whose value is a no-op under any non-absurd reading. Inventing a fraction the balance sheet does
not carry would be worse than matching it.

Reach and half-angle are **provisional**: no shipped number states either (the tweak table carries no melee
arc/reach entry — `TWK_Selection_Radius` 10 is targeting, not damage). 8 u and ±60° are sized off
`WorldState.NpcMeleeRange` and the slack the old radius carried.

This is the flat "everyone in front takes the swing" half; the directional arc + per-direction shield
mitigation is the section below. The two are independent — the direction picks which of the *defender's*
guards you meet, the cone picks *who* you meet.

## Directional combat: the shield rose (#20)

AoC's signature melee is DIRECTIONAL — the attacker picks an arc, and the defender spreads a finite guard
across the same arcs, so the swing is worth more into whichever arc is thinnest.

**The arcs.** Three, named by the client itself: `ShieldDirection_e` / `ShieldDirection_n`, the UI strings
`Combat_Shield_{All,Left,Mid,Right}` and the LDB lines `ShieldLeft%1%` / `ShieldUp%1%` / `ShieldRight%1%` — so
"Mid" is the overhead arc. The rose is drawn by `CombatRoseView_c` (vtable `0x020a216c`), fed by
`TargetingModule_t`.

**The attack direction** rides the swing as the LAST word of the `BehaviorControlCenterIIR` request tail — the
`Normal Attack` ability id, LDB-named: `769094` *Upper Left Attack*, `769095` *Upper Right Attack*, `769099`
*Overhead Attack*. `769096` / `769097` (*Lower* Left/Right) exist in the ability catalog with slot 0 and appear
in no capture. `CombatShieldRose.ArcFor` maps them; an ability that is none of the five is undirected and takes
no arc modifier [V3: CombatShieldRoseTests.ArcFor_MapsTheDirectionAbilitiesToTheirArc].

**The defender's guard** is `CombatShieldIIR` (`0xCFFD0519`, `CombatShieldCodec`) — one u32, decimal-packed
hundreds=Left, tens=Mid, units=Right, stored by the client as stat `0xA5`. All 110 captured records spread
exactly **three** shields, and the ten possible spreads all occur
[V3: CombatShieldRoseTests.EveryCapturedDistribution_SpreadsExactlyThreeShields].

**The shift is a single-shield move on a 5–12 s timer, NOT a reaction to where you swing.** 34 of the 39
captured transitions move exactly one shield from one arc to another (4 re-send the same value; the one
3-shield jump, `030 -> 003`, reads as a lossy capture collapsing steps)
[V3: CombatShieldRoseTests.Shift_MovesExactlyOneShieldToADifferentArc]. The intervals have a hard floor at
5.0 s with 34 of 39 inside [5, 12] (median 7.3 s) and the five above consistent with dropped packets. The
"defender AI shifts its guard toward where it is being hit" premise does **not** survive the corpus: over the
26 transitions with a single gaining arc and at least one swing since the previous update, the gaining arc is
the most-attacked arc 9 times — **35%, against 33% by chance**
[V2: Blablabla_soldier-guardian_create_random-actions-combat-levelup-questcomplete.tshark.json].
The opening spread is likewise flat across the ten compositions (71 engagements, chi-square 2.8 against
uniform, 9.3 against a per-shield multinomial), so it is rolled, not derived from the NPC.

**The mitigation is the client's own balance sheet, and it is OURS to apply.** RDB 1000516 carries
`Combat_PvE{0..3}ShieldBonus` = **0.2 / 0 / −0.33 / −0.66** and `Combat_PvP{0..3}ShieldBonus` =
**0.2 / 0 / −0.15 / −0.30**, indexed by the shields covering the struck arc — a *positive* entry is a bonus to
the attacker, so an unguarded arc takes 20% more. Ghidra `TweakGlobals` reports **reads=0** on all eight (they
are registered and never consumed): these are server numbers shipped in the client's tuning table
[V1: docs/rdb/client-tweak-table.md]. ⚠️ This used to cite recast as the same tell. It is not one — the recast
base turned out to be client data the client reads for itself (the spell record's own GameAction element, see
[`../re/spell-recast-source.md`](../re/spell-recast-source.md)); it is the tweak table's `reads=0` that carries
the argument here, not the analogy.

**A PLAYER's rose is the PLAYER'S — it arrives c2s (#620).** The two sides of the same message are owned by
opposite ends. An NPC's rose is the server's; a player's is the client's: it owns stat `0xA5`, the shield
keybinds write it locally, and it uploads each change through `n3EngineClient::SendIIRToServer`. The keybind
handler is `FUN_01117a20` (reached from the five command stubs at `0x00455cb0`..`0x004562b0`), and it has
exactly the two modes the live game documents — move ONE shield between two arcs, or stack all three on one arc
— plus a fourth stub that resets to `111`. It writes stat `0xA5` through `FUN_01117d60` and then builds a
`CombatShieldIIR_c` and sends it, which is the *only* place in the image that message is constructed
[V1: docs/re/shield-rose-client-upload.md]. The corpus can't see this — all 110 records are downloads, because the
captured sessions never touched a shield key — so the direction is established from the client, not the wire.

So the server does **not** roll or shift a rose for a player. `WorldState.Tick` skipping players is correct, not
the gap it looked like. `GameServerListener.HandlePlayerShieldRose` takes the uploaded spread, applies the
client's own overflow clamp (`CombatShieldRose.Sanitize` — over three shields resets to `111`, which is what
`FUN_0113de90` does on receipt), stores it on the player's `WorldEntity` and relays it to the viewers who can
see them. A player enters the world already guarding the retail default `111`, which is also what their own
snapshot stat sheet carries (`SubClassStatBlock`, `ShieldAllocation` `0x6F`).

The spawn scaffold's shield word (`SpawnPreIir.WordStatIds[13]` → stat `0xA5`) is deliberately left at 0: 310 of
the 317 captured PLAYER frames carry 0 there, so retail does not seed a viewer's rose from the spawn either.

**And it does not catch up afterwards, so neither do we (#622).** A viewer that arrives after a defender's rose
was set draws nothing over it until the next change — the obvious fix is to re-send the rose when the viewer
confirms the spawn, and the corpus says retail does not. `--report shield-rose` times all **110**
`CombatShieldIIR` records against their own defender's `SimpleCharFullUpdateIIR`: of the **71** first-roses,
**none** arrives within 2 s of that handle's spawn, and the earliest of them is **2.4 s** — an engage, not a
catch-up. Every other record follows a previous rose at the measured 5–12 s shift cadence, including the four
that re-send an unchanged value (the closest is +6.1 s, inside the cadence). So the hole is retail's, and
inventing a send to plug it would put a packet on the wire that retail never sends.

**Mitigation reads whatever the handle resolves to**, NPC or player, and a player defender switches to the
`Combat_PvP*` column — the gentler one (−0.15/−0.30 where PvE gives −0.33/−0.66), which is why that column
exists at all.

**An arc is a direction the attacker SWINGS, not where the attacker stands (#621).** Funcom's own description
is *"you could instantly stack all of your shielding to your right side if you see an enemy **charging up a
right-side attack**"* — the arc is a property of the incoming swing, which is exactly what the player's three
direction abilities are on our side of it. ⚠️ A geometric reading (arc = the attacker's bearing against the
defender's facing) was implemented and **reverted**: the *"attacks from behind leave characters open to more
damage"* line in the same description is a **separate positional rule**, not the arc split, and the client's
294-entry balance sheet carries no back/flank number to pair with it.

**An NPC's swing is the same mechanic as a player's — the same Left/Mid/Right and only that.** Nothing about it
is visible on the mob: the shields drawn over a defender are a **UI overlay** of the rose, not a model or
animation state, so there is no left-swing/right-swing pose to look for on either side. That is also why the
corpus is silent — not one of ~63k NPC-actor BCC records carries a direction word, while the player's own
swings echo back one for one, because a player's direction rides on an *ability id* and a mob has no ability to
name. The mitigation is server-side either way (the client registers all eight `Combat_*ShieldBonus` tweaks and
reads none), so nothing needs to be on the wire.

That leaves the **selector** as the only unknown, and `CombatShieldRose.SwingArc` picks uniformly over the
three. ⚠️ It is the one placeholder in the class, and what is assumed is the *distribution*, not the mechanic:
`Roll` is uniform because the corpus measured it uniform, this is uniform because nothing observes it. What
would replace it is evidence that a mob favours an arc — its own AI tendency, or aiming at the player's weakest
guard the way a player aims at the mob's.
[V3: CombatShieldRoseTests.ApplyArcMitigation_AMobsSwingIsMitigatedByTheArcThePlayerStacked]

The player's guards therefore do work in PvE, matching the live game's "in general, outside of a few battles,
you can leave them alone for PvE" — and the default `111` spread is the table's neutral entry, so leaving them
alone costs exactly nothing.

Applied on the melee path only (`WorldSimulation.ApplyNpcAttack`, after armour), through the same table as a
player's directed swing. An NPC **cast** takes no arc, for the same reason a player's spell does not: the rose
guards swings.

**Server side.** `CombatShieldRose` (roll / shift / cadence / arc mapping / clamp / mitigation) is the policy;
`WorldEntity.OpenShieldRose` + `TickShieldShift` hold the per-NPC state. `WorldState.Tick` rolls the rose
on the engage transition and shifts it on each entity's own timer, emitting `NpcShieldRose`, which
`WorldBroadcaster.BroadcastShieldRose` turns into the `CombatShieldIIR` for every CONFIRMED viewer (the
heartbeat's gating — the rose is drawn on the dynel; the owner is skipped, having drawn it already).
`CombatService.SendNormalAttackResponse` reads the swing's arc and scales the damage by the table. An NPC's rose
closes on disengage and on death, so each fight opens on a fresh one; a player's is theirs for the session.

## Retail's world-object interest is a GRID, not a radius (#529)

Not combat, but it is measured with the same corpus tooling and there is nowhere better to put it yet.
`--report dynel-cycle` groups every `SimpleDynelFullUpdateIIR` / `n3ToClientQuitIIR` burst in the corpus and
asks two questions of it.

**A single interest radius is impossible.** A radius `R` bounds a spawn burst from above (nothing past `R` may
appear) and a despawn burst from below (nothing inside `R` may vanish), so `R` would have to satisfy
*every* `farthest-spawned ≤ R ≤ every nearest-despawned`. Over 293 spawn and 210 despawn bursts:

| | min | p25 | median | p75 | max |
|---|---:|---:|---:|---:|---:|
| farthest thing SPAWNED | 50.2 | 145.5 | 225.4 | 747.6 | **1834.5** |
| nearest thing DESPAWNED | **3.5** | 61.1 | 79.6 | 116.0 | 501.8 |

Something despawned 3.5 u from the player while things were spawning 1,834 u away. All 293 spawn bursts reach
past the nearest despawn. No radius produces that, which is why #529's Stone Bricks respawned 62 u *farther*
away than they had despawned.

**A ~32-unit cell grid does fit.** If interest is per cell, a burst fires when the player crosses a boundary,
so the player's position at a burst should sit near a multiple of the cell size on one axis. Scored against the
right null — the same statistic over *every position the player walked*, because a player does not stand
uniformly — with distinct positions on both sides:

| cell | median offset at a burst | while walking |
|---:|---:|---:|
| 16 | 1.1 | 2.4 |
| **32** | **1.3** | **5.0** |
| 50 | 6.6 | 7.4 |
| 64 | 4.6 | 9.9 |
| 100 | 14.3 | 14.9 |
| ≥128 | — | no separation |

32 is the strongest and the finest independent one; 16 and 64 fit as its harmonics (every multiple of 32 is a
multiple of 16, and every other 32-boundary is a 64-boundary). Everything at 100 and above is indistinguishable
from the walk.

⚠️ This is a correlation between bursts and grid boundaries, not a read of retail's implementation: it says the
trigger is *positional and quantised at ~32 u*, not how the cells are indexed or how far the interest reaches.
`InterestManager` is still radius-based — changing it is open work on #529.

## NPC counter-attack animation ack (decoded 2026-06-15)

When an NPC swings at the player, retail sends a second `BehaviorControlCenterIIR` (0x642CD3D6) whose
top-level **actor = the NPC handle** (the player's own attack ack puts the player there). It is structurally
the targeted-hit attack ack with the roles SWAPPED: `args.SpellTarget.handle` and the result-block target
both name the **player** the NPC hit, and the args carry **no Mode field** (the player-only field-2 - the
clean NPC-vs-player tell). `seq` is the constant `0x0004` (server-initiated, not echoing a client seq).
Sending it plays the NPC's swing + the player's hit-react; the authoritative HP move still rides the
world-tick `StatUpdate` (the ack's damage is just the floating number, so the two don't double-apply).

Decode: `CombatDamageCodecTests` (98/98 frames of the strickenbla capture). Builder
`BehaviorAnimations.NpcAttackAck`, byte-exact against frame 6472 in
`EnergyCodecTests`. Wired into `WorldSimulation.ApplyNpcAttack` via `IWorldOutbound.SendNpcAttackAnimation`.
The animation/result params (`typeId`/`swingCounter`/`f5`/`f11`/`f12`/`resultType`/`resultK*`) are not
semantically decoded - capture-typical defaults for the live send.

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
**aggro** — a `HateListUpdate` + `ThreatLevel` + `LockedToChanged` (`HateListUpdateCodec` / `ThreatLevelCodec` /
`LockedToChangedCodec` → `BroadcastNpcAggro`, #216/#405) — alongside the combat-mode weapon-draw. All are gated by
`NpcAutoAttackEnabled`.

The three go out in retail's order: `NecroL80_…_login_combat` shows `LockedToChanged` arriving right after a
`HateListUpdate`/`ThreatLevel` pair. Its body is `[npc handle] → [target ref]`, and every one of the 250 captured
instances across 18 dumps is a low NPC handle locking onto a **player**, which is what identifies it as the aggro
moment rather than (as was once assumed) part of a spell-hit bundle.
[V2: NecroL80_mage-necromancer_login_combat.tshark.json]

⚠️ It goes to **viewers**, not just to the target, and that is measured rather than assumed: in
`login_2-clients_group-trade-friend` the same (npc, target) pair is sent on BOTH clients' streams (frames
3146/3147 and 3394/3395) though only one of them is the target. A single-client capture cannot tell those two
routings apart — the two-client one is what settles it.
[V2: login_2-clients_group-trade-friend.tshark.json]

**Walk gait + slight snap.** The 61-byte form has no speed field, so the client uses a default
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
- The `0x05` "pace" form (len 69) is `[05][const 12][f32][vec3][vec3]` — a speed float + two positions (a linear
  move WITH speed). Full MoveTask grammar: `docs/re/spawn-dissection.md`.

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
and `AttributeChangedCodec` (golden `AttributeChangedCodecTests`).
Wired into both paths: GM `.level` (`SetLevelCommand`) and `CombatService.PushLevelUp`. Which attributes rise / by
how much is provisional (`Combat.AttributeGrowth`, flat +1/primary) — no per-class growth tables exist in captures.

## Live spell / ability grants (#73, #74)
Two distinct mid-session grant packets, both on the GameCharInterface conversation (same class pair), each
byte-exact:
- **`0x2052 DispatchAddSpecialAbility`** — grants a catalog **ability** (the melee/utility kit). Body = the 81-byte
  ability struct. `SendAddSpecialAbility`/`AddSpecialAbilityCodec`, used by login
  `GrantCharacterAbilities` (post-0x2024, from `FlushPendingPost205FFollowUp`).

  **The `Abilities` catalog has two tiers (#303), and only one of them can be granted.** An ability is an RDB
  **1000020** record — the same type as an item template, so the type alone can't identify one; the **LDB** does: an
  ability is named in cat **50210** (the ability/spell name category, description in 50211) and is *not* named in
  the item-name categories 50200/50220. That yields **265**, which `RdbLdbAbilityImporter` (module
  `rdb-ldb-abilities`) catalogues. Validated both ways: all 27 captured abilities match the classifier and **none**
  of them carries an item name, while the 12 candidates that do are consumables whose spell-name entry is their
  effect ("Purple Lotus Powder Spray").

  ⚠️ The other **238 are IDENTITY ONLY**. The `0x2052` body is built around the ability's 128-bit **TEA key**, which
  rides the grant itself and is **random** (not derivable from the id — checked), so an ability no capture ever
  granted has a name and nothing else. `Ability.IsGrantable` is the test; `CharacterLoadout.ResolveAbilities` logs and skips a
  key-less row rather than putting a null key on the wire. Recovering the rest needs a capture that grants them —
  the same key blocker as the item catalog (Gitea #415). Pinned by `AbilityCatalogTests`.
- **`0x200A DispatchLearnSpell`** — grants a real **spell**. Body = a bare big-endian `u32` spell id (`i_nSpellID`),
  invariant across all 68 captured packets. `SendLearnSpell`/`LearnSpellCodec` (decode
  `CaptureSpellImporterTests`, golden `LearnSpellTests`).

(The GM test commands `.grantability`/`.abilitylist`/`.learnspell` were removed in the "remove 25 GM test commands"
cleanup — 2026-07-05; the underlying wire builders above are still live.)

**Spell granting / persistence (#74).** Spells are learned per-character: the captures show a caster's whole
learned set is re-sent on every world entry (NecroL80 always relearns her 5, DemoL80 his 9), so it is modelled like
the ability re-grant. The set lives in the per-character `CharacterSpell` table (CharacterId, SpellId);
`GrantCharacterSpells` replays it on world entry (BEFORE the FullChar snapshot, via `SendLearnSpell`, so the
client absorbs them silently — see `SendWorldEntry`), and `CaptureSpellImporter.ImportLearnedSpellsAsync` seeds captured characters from the grants in
the pcaps (attributed via the grant's RpcHeader **receiver instance** = the character id; the GameCharAgent→
GameCharInterface class pair disambiguates the 0x200A opcode collisions). Level-up auto-grant is NOT implemented —
captures don't say which spell is learned at which level.

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

**The data tail has exactly ONE form: 54 bytes** (`MagicBuffDataCodec`). Across all 103
`Captures/*.tshark.json` the only s2c `MagicBuffIIR` tails are **54** bytes (×1412) and **0** (×578 — the rank-1
end twin). A 50-byte tail occurs **zero** times. `[V2: Captures/*.tshark.json — every s2c body whose u32 length is
followed by 6b87e316; scan reproduced by Faolan.Tests/MagicBuffDataCodecTests]` The client's
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
**buff** apply/remove is a real oracle now — distinct from the DemoL80 `0x3BAD7D` summon. The mount **entity
spawn** is still gated on the mid-session-spawn blocker (#26), so the buff renders but the horse model does not.

**The world-entry mount restore is a FRESH apply, and that is what retail does** (#342). The changemap capture
`NecroL80_mage-necromancer_login_leave_king_conan_castle-Dismount` shows the whole sequence: riding out of the
no-mount King Conan castle into a mountable zone, retail emits five adjacent records —

```
FullPlayerCharacterIIR  ->  GameTimeIIR  ->  ride buff 0x31EB1500  ->  Shield of the Risen 0x3DFA4800
                                          ->  StatUpd (Mount 0x00BA = 0x313C9B00, MountSpeed 0x00BB = 100)
```

That entry-restore ride buff is byte-identical to a player-initiated mount cast **except for the per-application
effect id and the trailing nonce** — 6 bytes of 84, both of which vary per application by definition
(`NpcSpellCastCodecTests.MountRideBuff_EntryRestoreMatchesAFreshCast_ExceptThePerApplicationIds`). So there is no
"already active" or silent variant of the record, and the FullChar snapshot carries no active-buff region for one to
live in. Reconstructing the mount silently inside the snapshot would be an invention rather than a mirror of retail.
What retail differs on is **position**: it sends the restore inside the entry stream, immediately after the snapshot
and `GameTimeIIR`, so `RestoreMount` runs at the end of `SendBurstlessWorldEntry` rather than from the
post-`0x205F` flush — the same reasoning that moved `GrantCharacterSpells` there to stop it re-toasting.

**Buff persistence across relog (#274).** Self-buffs survive a logout **with their remaining time**. The live
`WorldEntity.ActiveBuffs` is savegame state, so `PlayerPersistenceService.SavePlayerState` (logout + the 5-min
autosave) snapshots each self-buff to the `CharacterBuff` table — `BuffId`, the effect `Nonce`, and an absolute
`ExpiresAt` — and `RestoreBuffs` re-applies them **inside the world-entry flush** via `RestoreEntryBuffs`, alongside
`RestoreMount` and `SendShieldOfTheRisen` (#423). All three used to run from the post-`0x205F` flush, after the client
had left the loading screen, which reads as a NEW application and fires the client's apply feedback on every login and
every playfield change — #342 confirmed that for the mount, whose feedback was audible. Restore rebuilds the
`ApplySpell` payload from those columns via `MagicBuffDataCodec` (not a
stored blob) with the duration recomputed to the time left, so the client counts down the **remaining** seconds and
the tick expires it on schedule. Only self-buffs persist (NPC-cast debuffs carry a volatile caster handle + a DoT
and are dropped); buffs owned by a dedicated re-applier — the login shield and the mount ride buff — are excluded
at save time so they aren't double-applied on entry (`GameServerListener.ShouldPersistBuff`).

**Shield of the Risen (#221) — RESOLVED + in-client verified.** `SendShieldOfTheRisen`
(`GameServerListener.Buffs.cs`) applies the newbie-protection self-buff from `RestoreEntryBuffs` in the world-entry
flush (#423 — it used to run from `FlushPendingPost205FFollowUp`), so it re-asserts on login AND every playfield
change, silently, in the position the changemap capture shows retail using. The one entry that does **not** get it is
`WorldEntryMode.CreateWorld` — a character's very first arrival is the intro cutscene, where the player has no
control, so the protection banner would paint over a cinematic and the shield would be spent by the first swing of a
fight they have not had yet. Every later login, including the first ordinary one after creation, applies it. The real
ids are bases **`4061768` (1h)** + **`3666828` (10h)**, both named "Shield of the Risen" in `Spells` with real
durations (`NecroL80_..._conans-castle-exit` rec#165/#736) — the old `0x000B9000` claim was WRONG (that id is the
"Exhausted" debuff). It uses `MagicBuffDataCodec` — the 54-byte tail that is the only form retail
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
**`LostGraceEffect`** on-screen feedback line (byte-exact `FeedbackXml.LostGraceEffect`, verified
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
  fired for `BehaviorId.CombatMelee` / `CombatUnarmed` / `MagicAttack` (and the mounted jump's `BccAction`) in
  `GameServerListener.Inbound.cs`.
- **The spell EFFECT** — the `ApplySpell` (`0x6B87E316`) itself (`BroadcastSpellToViewers`, from
  `SendMagicBuffIir`), so the buff/effect that lands renders on nearby clients, not just the gesture.
- **The buff's END** — the rank-1 end twin (`SendBuffRemoval`) relays the same way. A buff other clients were told
  to start has to be told to stop, or their copy never ends: the spellweave trance made that visible (viewers saw
  the caster hold the weave pose forever, because only the caster got the removal), but it applied to every buff. The capture
  streams both other-player **self-buffs** (e.g. player `0x01316e68`) and **offensive casts** (player
  `0x01316cee` on NPC targets) to the observer, framed to the observer's endpoint. Verbatim-relay + Known-gating
  locked by `CrossPlayerSpellRelayTests` against the captured body.

## The kill CHAIN (#446)

Retail announces a kill streak with `ChainKillIIR` (`0x0EED7068`) on the **player's own handle**. The whole
grammar is measured off the corpus by `--report chain-kill` over its **67** records:

| | |
|---|---|
| counts sent | `2` ×32, `3` ×3 — **never 1** |
| every non-zero is closed by a `0` | 32 of 32 |
| that closing delay | median **8.97 s**, spread 7.90–9.22 |
| escalations (`2` → `3`) | 1.36–7.83 s, i.e. inside the window |

So it is a chain of kills with a **9-second window**: the first kill starts one silently, a second inside the
window announces `2`, further kills escalate, and the chain closes with an explicit `0` about nine seconds after
the last kill. `WorldEntity.RegisterKillInChain` / `TryLapseKillChain` hold the state,
`CombatService.AnnounceKillChain` sends the kill side and `WorldSimulation.TickKillChains` the closing `0`.
[V3: KillChainTests.AnAnnouncedChainLapsesExactlyOnce_AndAnUnannouncedOneNeverDoes]

⚠️ **It is NOT paired with the kill counters.** #446 read stat `0x6A` (106) as "remaining of 20" and `1000070`
as "achieved", from a live session where both moved on kills, and expected the IIR to fire with them. Over the
whole corpus **0 of the 67 arrivals coincide with a move of `106`, `1000069` or `1000070`**. Those three are the
persisted daily kill counters the client's own stat-name table names (`DailyPvEKills` / `DailyPvPKills`); the
chain is a live announcement and nothing else. Modelling them is still open.

The body was never the gap either — `ChainKillCodec` has read it byte-exact off the client's own reader
(`FUN_0079c0b0`) since the V1 IIR sweep. What was missing was only *when to send one*.

## Fatalities (#140)

A killing blow makes the server apply a **fatality burst** to the KILLER — two self-buffs, plus anything that keys
off them. Long treated as un-pinnable ("no capture shows a fatality"), which was true only of the **melee**
finishers: the corpus carries the server side of a **caster's** fatality in seven captures.

Worked example, `xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city` fr1267 — one frame, right after the
killing cast completes (`BccIIR seq=0x000A action=4`):

```
ApplySpell 5070797 "Fatality"        self, 0.1s
ApplySpell  791221 "Fatality"        self, 10s
StatDamage
ApplySpell 3666099 "Frozen Hatred"   self, 6s   - only when that buff is up
```

36 bursts across the corpus carry both `Fatality` ids; 21 also carry the Frozen Hatred proc. Every payload is the
ordinary 54-byte self-buff form (`MagicBuffDataCodec`) with magnitude **1** — a fatality needs no new
wire shape, which is why `FatalityService` is three ApplySpell sends and not a bespoke record
(`FatalityBurstTests` locks all three against the captured bytes).

**Frozen Hatred's pet.** The necro spell `4377207` applies buff `4377209` (#434), and its tooltip reads: *"When you
or your group perform a fatality and this buff is active, the Frozen Hatred pet appears and is under your control
for 60 seconds. If you already have a Frozen Hatred pet, the duration of the pet will increase by another 60
seconds. This timer is capped at 5 minutes."* That is implemented as `SummonTimedPetForPlayer` + the world model's
`WorldEntity.DespawnAt`, which the tick fades (`TimedDespawn`) — one field carrying both the lifetime and its
extension, clamped to the cap. The pet is NPC template **14580 "Frozen Hatred"**.

**The roll comes from the client's data.** RDB **1010219** holds 58 `<fatalitygroup criteria="…">` records with 205
real `<fatality fatalitychance="N" minlevel="M" …/>` entries (plus 3 dev rows at 9980%). `fatalitychance` is a
straight **percent**, and its tiers map 1:1 onto `minlevel`:

| minlevel | 1 | 5 | 30 | 40 | 60 | 80 |
|---|---|---|---|---|---|---|
| fatalitychance | 5% | 10% | 15% | 20% | 20% | 30% |

so a character rolls the best tier its level qualifies for — `FatalityService.FatalityChancePercent`, gated by
`FatalityBurstTests`.

**The ANIMATION is client-side — the server's only lever is ORDER.** Three things the corpus establishes:

1. *No packet type is unique to a fatality.* Comparing the s2c packet types in a window around **37 fatal** kills
   against **98 normal** ones: the only difference is the `ApplySpell` (`0x6B87E316`) carrying the Fatality buffs.
   The victim's death record is the ordinary kind — `field1` is always the creature's own death behaviour (41877
   humanoid / 215333 quadruped / 17036 demon) and `field3`/`field12` spread identically on fatal and non-fatal
   kills. So there is no separate "play the finisher" emission to reproduce.
2. **The burst comes BEFORE the victim's death records, 37/37.** The Fatality `ApplySpell`s land 7–20 packets
   ahead of the death-pose MoveTask *and* ahead of the death `BccIIR`, usually in the same frame as the killing
   blow: `xxx_mage-necromancer` fr1267 is burst → victim `StatDamage` → death pose → (fr1270) death BccIIR. Since
   the death record carries no fatality marker, this ordering is the only thing that can tell the client the kill
   *was* a fatality before it picks the corpse's animation — so `CombatService.OnNpcKilled` rolls and sends the
   burst first, then the death records.
3. *The finisher tables are weapon-keyed.* All 59 `<fatalitygroup>` records carry a `criteria` of 23 (1H-Sword
   family), 24 (1H-Axe), 25 (2H-Polearm), 26 (Ranged-Bow), 27 (1H-Blunt + stealth 1hb), 28 (2H-Sword/Axe),
   29 (2H-Blunt), 30 (1H-Dagger + stealth piercing), 32 (Ranged-Crossbow) or 53 (three scripted specials: the Iron
   Tower executioner, the Warmonk unarmed, Conan killing Thoth-Amon). Each names a paired `selftargetanimation` /
   `targetanimation` (e.g. `fighting_fatality_weapon_1he_decap_attacker` / `_victim`) with
   `poptargetonattractor="yes"` — the client snaps the corpse to an attractor and plays both halves itself.
   **[V0]** how a SPELL kill selects its finisher is not fully resolved, but it is NOT this table: 1010219 has no
   caster group, yet every captured fatality is a caster kill. The client's own `Magic/Animation` channel answers
   the "where else" half — on a spell kill it looks the finisher up as an **effectset**, by name:

   ```
   Magic.Animation - Effectset <searching> : fighting_fatality_magic_and_ranged_frost
   ```

   So the spell case is keyed `fighting_fatality_<weapon-class-family>_<damage-school>` (here the `magic_and_ranged`
   family + `frost`) through the same `Effectset <searching>` path every ordinary cast animation uses — not through
   1010219's paired `selftargetanimation`/`targetanimation`.

   **The finisher renders through the EFFECT layer, not the animation layer** — which is why it is invisible to
   `AnimSys`, the channel that names every base/action animation the client plays. One retail caster fatality, whole
   frame (`#244580`, level-6 necromancer, spell kill; the finisher did play on screen):

   | in the frame | line |
   |---|---|
   | the trigger | `RECEIVED IIR: class BehaviorControlCenterIIR_c` |
   | the buffs | `RECEIVED IIR: class MagicBuffIIR_c` ×3 |
   | the buff's stat effect | stat `840` `0 → 20`, stat `536` `10000 → 12000` (+20%) |
   | the finisher lookup | `Magic.Animation - Effectset <searching> : fighting_fatality_magic_and_ranged_frost` |
   | **the finisher itself** | `GameCode.Effects - Starting 7 visual effect [ResourceID 1010207:3667002] on attractor 10 shutoff=1` and `[1010207:3667001] on attractor 35` |
   | the proc | `RECEIVED IIR: class PetIIR_c` + `[Frozen Hatred]::PlayBaseAnim` |
   | the victim | ordinary `combat_death` — `Die_fatality` (movementset actions 3108/2915, `Set:12509`) never ran |

   Both effect resources fire **exactly once** in the whole session, only here, and their ids sit far above the
   session's ordinary effect range — they are the fatality's own content. Meanwhile stat `840`/`536` revert at
   `18:12:10`, exactly **10 s** after the burst, which independently confirms the `791221` 10-second buff above from
   the client side.

   So the shape is: BCC action → two self-buffs (state) → an effectset named
   `fighting_fatality_<class-family>_<school>` → the effectset's resources started on attractors. `AnimSys` naming
   no `*fatality*` animation is not evidence of absence; the base-animation layer is simply not where it lives.
   What is still open is the mapping from the effectset NAME to those resource ids, and whether a melee fatality
   instead uses 1010219's paired `selftargetanimation`/`targetanimation` — open work in Gitea #440.

   (The client's rig also carries a bone named `rig_bone_decapitationhelp`.)

## Spell cast FSM (#236)
A player spell cast is a `BehaviorControlCenter` (`0x642CD3D6`) `Magic:Attack` action (`BehaviorId` 4), handled in
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
  (`BehaviorAcks.CastInterruptAbort` / `CombatService.SendCastInterruptAbort`) — the begin's args (held as
  `Session.PendingCastArgs`) re-emitted at state `0x0007` with the cancel packet's tail; this drops the bar, cancels
  the animation and pops the client's "Interrupted" floating text (client-generated — no separate feedback packet).
  Byte-exact against `NecroL80_..._cast-interupt` (begin idx988 + cancel idx1006 → abort idx1013), golden-tested in
  `BehaviorAcksTests`. Echoing the cancel back (the old behaviour) left the bar running.
- **Ground-targeted AoE:** a spell aimed at a reticle POSITION (no locked entity) carries the target Vector3 in
  the trailer (`[u8 kind=0][f32 X/Y/Z BE]…`) and has **no** `SpellTarget`; `CastGroundTarget.TryParse` reads it
  (an entity cast sends `kind=1` and a garbage position). `CombatService.ResolveGroundAoe` then damages every
  attackable NPC (Disposition ≠ Friendly) within the spell's blast radius of the reticle — on empty ground it is a
  no-op like retail. A CHANNELED one (`ChannelGroundAoe`) spreads the damage over ticks across the channel and
  re-checks the blast area each tick, so an NPC that wanders in is picked up; an interrupt stops it.

  **Both numbers are the client's own** (#349), and neither is in the property run — which is why they read as
  unsourceable for so long:
  - **Blast radius** = `Spell.AreaRadius`, the `Radius` key (11) of the spell's area-TARGETING GameAction element
    (`AreaFormula`, `ConeFormula`, `CoreFilteredAreaFormula`, … — `GameActionTypes.AreaTargetKinds`), one region
    over from the field run. In metres, on 1,023 of the 20,848 spells. ⚠️ NOT the same thing as field 347
    `SplashRadius`, which is the collateral a single-target hit spills onto bystanders.
  - **Channel tick interval** = `Spell.ChannelingIntervalMs`, RDB field 447 `ChannelingInterval` (centiseconds
    ×10), on 516 spells. Corroborated independently: on every spell where a tick interval was also MEASURED off
    the wire (`Spell.DotIntervalMs`, from captured `StatDamageIIR` gaps) the two agree exactly — Deathly Blizzard
    2,500 ms, Waves of Flame 1,000, Resting 500 — with no disagreement (`SpellAreaAndChannelTests`).

  **[V0]** the fallbacks remain for a spell the client states neither for. Deathly Blizzard is exactly that case
  for the radius: it states a channel interval and no area at all, so its blast is server-side and
  `GroundAoeRadiusUnits` (5 m, where the client's own radius distribution centres) still stands in. A `ConeFormula`
  spell's arc (keys 212/213) is not modelled — it currently blasts as a ring of the same reach.

## Starting gear (#220)
A brand-new beach character is seeded its slave outfit in `CreateFlowService.SeedStarterEquipment`
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
  Club (a level-2 char) and Broken Oar (a login capture) are NOT create-flow starters and are excluded.

## Armour mitigation: the client's own pipeline (decoded 2026-07-26)

`FUN_0113ba00` (log channel `GameCode/ArmorMitigation`) is the client's mitigation computation, and it is the
authority for #401. Reading it, with stat ids as `CharacterStatId`:

```
armor        = stat 0x1C0 (Armor)            + stat 0x1C6 (PvP Armor)     when the PvP flag is set
protection   = stat 0x1C3 (Protection)       + stat 0x1CA (PvP Protection) likewise, for the magical branch
attrBonus    = (stat 0x324 (Strength) - 10) * 0.1        physical branch
             = (stat 0x328 (Intelligence) - 10) * 0.1    magical branch (0x32A Wisdom for damage types 6/7)
totalArmor   = (percentBonus * 0.01 + 1.0) * armor + attrBonus * TWK_ArmorAbilityModifier
adjusted     = (2.0 - level / stat 0x3C5) * totalArmor          level = stat 0x36
mitigationMod= (adjusted / (level * TWK_TargetPerLevelArmor)) * 100
roll         = 100.0 - mitigationMod
```

`roll` then indexes a **mitigation table** (`FUN_0145f050(table, 1, armorType, roll)`) which returns a
`(MinMitigation, MaxMitigation)` pair, and the final percentage interpolates between them:
`final = (max - min) * roll * 0.01 + min` (with a sign-flipped branch when `min < 0`). If the table has no entry
the client falls back to a closed form: `final = a / (base + a)` when `a >= base`, else `(a / base) * 0.5`, where
`base = level * TWK_TargetPerLevelArmor`.

Key consequences for our implementation:

- Mitigation is **level-normalised**: the same armour rating mitigates less as the character levels, because the
  denominator is `level * 137.225` (`TWK_TargetPerLevelArmor`, see [`../rdb/client-tweak-table.md`](../rdb/client-tweak-table.md)).
- **Strength adds armour** (Intelligence adds protection), scaled by `TWK_ArmorAbilityModifier` = 2.0 — attributes
  above the baseline 10, at 0.1 per point.
- The physical branch covers damage types 1/2/3/8; types 4/5/10 and 6/7 take the magical branch with
  `TWK_TargetPerLevelProtection` = 46.0625 and their own per-type stats (0x39E/0x39F/0x9D, 0x3A0/0x3A1).
- The `(2.0 - level/stat 0x3C5)` term is an apprentice/mentor adjustment - it is 1.0 when `stat 0x3C5 == level`.

### The mitigation table lookup (`FUN_0145f050`) — record layout decoded, contents not yet located

The lookup walks a `std::vector` (begin/end at table `+0x54`/`+0x58`) of **28-byte records** (stride `0x1C`), and its
own debug line names the fields:

```
[TABLE][MinMitigation: +0x0C][MaxMitigation: +0x10] [Range: +0x14 .. +0x18] [AdjustedRange: ...]
```

| offset | meaning |
|---|---|
| `+0x00` | byte selector — the call site always passes **1** |
| `+0x04` | i32 branch: **1 = physical, 2 = magical** (the caller's damage-kind, not an armour type) |
| `+0x0C` | **MinMitigation** |
| `+0x10` | **MaxMitigation** |
| `+0x14`, `+0x18` | the roll RANGE this row covers, both offset by an adjustment passed in `XMM0` |

A row matches when `+0x00 == selector`, `+0x04 == branch`, and `range.lo + adj <= roll < range.hi + adj`; the caller
then interpolates between Min and Max. Worth noting: the "roll" the caller passes is the **constant 50.0**
(`0x42480000`, which is also what its `Roll:` log prints), so at this call site the roll is not randomised.

### ⚠️ We were breaking this ourselves: stat `0x3C5` (Apprentice Level)

Captured live from the client's own `GameCode/ArmorMitigation` channel (2026-07-26) with the character sheet open,
which is what triggers the computation — the client does NOT run it on incoming damage, so opening the sheet is the
only way to see it:

```
[NecroL80] TotalArmorValue:2157 (Armor:2017, AttributeBonus:140, Strength:70, Penetration:0)
[NecroL80] ApprenticeLevel Adjusted ArmorValue:-168246
[NecroL80] Mitigation Modification:-1532.57
[NecroL80] Roll:50
No mitigation entry found for [Player:1][ArmorType:1][AdjustedRoll:1632.57]
[NecroL80] [Final Mitigation:0.938747]
```

Every number confirms the formula above to two decimals — and exposes a server bug. `ApprenticeLevel` (stat `0x3C5`,
LDB 965) defaults to **1** when unsent, so `(2.0 − level/1)` = **−78** for a level-80 character: the adjusted armour
goes to −168246, the mitigation modification to −1532.57, the adjusted roll lands at 1632.57 — far outside any table
row — and the client falls through to its closed-form fallback, which is where the sheet's 93.9% actually comes from.
Sending `ApprenticeLevel = level` makes the term 1.0 and the numbers land at `adjusted = 2157`,
`mitigationMod = 19.65`, `adjustedRoll = 80.35`, inside the table's range. Fixed by pushing it in the world-entry
stat block.

The trace also pins `AttributeBonus`: `((rawStrength − 10) / 10) × TWK_ArmorAbilityModifier` = `((710 − 10)/10) × 2`
= 140, using the ×10 RAW attribute scale (the sheet's displayed "Strength: 70" is the rounded display value).

And it quantifies #441 independently: the client's own gear armour is **2017**, where our `ItemStatModifiers` model
only **369** — because 9 of the 13 equipped pieces are template-scaled and carry no records.

### The table is EMPTY on this build — so the closed form IS the client's behaviour

With `ApprenticeLevel` fixed, the adjusted roll lands at **80.35** (inside 0..100) and the lookup STILL reports
`No mitigation entry found`, with **zero** `[TABLE]` lines. Since the lookup logs a `[TABLE]` line for every row whose
selector+branch match — on both the range-hit and range-miss paths — zero lines means no row matched
`[Player:1][ArmorType:1]` at all. `ARMOR_MITIGATION` *is* loaded (the client logs `Loaded ARMOR_MITIGATION`), so the
vector the lookup walks is empty. That also explains why a content scan of 1.8 GB of the client's writable memory for
the 28-byte record pattern found nothing.

So on this build the closed-form fallback is not a degraded path — it is what the client always does:

```
target = level * TWK_TargetPerLevelArmor            (137.225)
final  = adjusted >= target ? adjusted / (target + adjusted)
                            : (adjusted / target) * 0.5
```

Verified to seven decimals at both trace points: `(2157 / 10978) * 0.5` = **0.0982419**, exactly the client's
`Final Mitigation`, and the broken-apprentice run's 0.938747 comes out of the same expression with adjusted = −168246.
`CombatResolver.MitigationFraction` implements this, pinned by `CombatResolverTests`.

### The MAGICAL branch, from the same trace

The magical branch logs no intermediates — only the shared `Roll` / `Final Mitigation` — but the same trace carried
two magical lookups, and they solve exactly against `target = level × TWK_TargetPerLevelProtection` (80 × 46.0625 =
**3685**):

| ArmorType | AdjustedRoll | Final Mitigation | ⇒ adjusted | NecroL80's sheet |
|---|---|---|---|---|
| 1 (physical) | 80.3516 | 0.0982419 | 2157 | armour 2017 + 140 |
| 2 (magical) | 90.7191 | 0.0464043 | **342** | Protection Cold / Electrical / Fire = **342** |
| 2 (magical) | 98.5075 | 0.00746269 | **55** | Protection Holy / Unholy = **55** |

Each `adjusted` is EXACTLY a Protection score off her character sheet, and `100 − adjusted/target × 100` reproduces
both logged rolls to the precision the client prints. So `ArmorType` is the branch (1 = physical, 2 = magical), and
the magical curve is the armour curve with a different per-level target — implemented as
`CombatResolver.ProtectionMitigationFraction`.

The attribute term (`TWK_ProtectionAbilityModifier` = 0.5 on Intelligence for damage types 4/5/10, Wisdom for 6/7)
contributed **zero** in both points: the client's stat store carries the BASELINE 10 for those attributes rather than
the sheet's computed totals (#393), so the term is real in the client's code but nil in practice today. It is
implemented anyway, because that is what the client computes.

### Confirmed again on a SECOND character, after the fix

DemoL80 (Demonologist, level 80) on a fresh login with `ApprenticeLevel` now sent — note the adjusted value is no
longer inverted, and all three lookups reproduce the implemented curve exactly:

```
TotalArmorValue:2043 (Armor:1903, AttributeBonus:140, Strength:70, Penetration:0)
ApprenticeLevel Adjusted ArmorValue:2043      <- was garbage before the fix
Mitigation Modification:18.6099
No mitigation entry found for [Player:1][ArmorType:1][AdjustedRoll:81.3901]  -> Final Mitigation 0.0930497
No mitigation entry found for [Player:1][ArmorType:2][AdjustedRoll:92.673]   -> Final Mitigation 0.036635
No mitigation entry found for [Player:1][ArmorType:2][AdjustedRoll:96.7707]  -> Final Mitigation 0.0161465
```

`(2043 / 10978) × 0.5` = 0.0930497 and `2043 / 10978 × 100` = 18.6099; solving the magical rolls back gives
Protection 270 and 119, whose fractions are 0.036635 and 0.0161465. **Six verification points across two characters
now**, all exact — pinned in `CombatResolverTests`.

The level normalisation itself is confirmed at **level 23** (AssassinL23, assassin), where the protection target is
`23 × 46.0625` = 1059.4375 rather than 3685: logged roll 98.7729 / final 0.00613533 solves to protection 13, and
`(13 / 1059.4375) × 0.5` = 0.0061355. Four level-80 points could not distinguish a per-level target from a constant
one; this one can. `ApprenticeLevel` also arrives correctly at a non-80 level (`965=23`).

One modelling caveat found the same way: the client's protection is **not necessarily integral** — AssassinL23's other
group solves to exactly **19.5** (roll 98.1594 → final 0.009203). `CombatResolver`'s `uint` rating therefore rounds,
so a defender with a half point of protection mitigates a hair less than the client would say.

⚠️ The table's DATA has not been located yet, and on this build it does not matter. It is `ARMOR_MITIGATION`, one of the client's 25 named data tables
(`docs/rdb/client-tweak-table.md`), parsed by `ArmorMitigation_c` — but unlike `HitsManaAndStamina_c` it carries no
record-size assert to identify the RDB entry by shape, it is in none of the ~55 RDB types dumped so far, and a
content scan of the live client's 1.8 GB of writable memory for the 28-byte pattern above found nothing. The likely
reason is that the 28-byte struct above is the IN-MEMORY form; the parser may expand or reorder the file records, so
matching the file by this stride was never guaranteed. Next: decode `ArmorMitigation_c`'s own read method, or read
the vector by following the table singleton (`FUN_0113d9a0`) rather than by scanning for a pattern. Gitea #401.

## Where the combat numbers come from (2026-07-28)

The damage constants in `CombatResolver` are labelled **MEASURED** or **PROVISIONAL**, and the class header
explains the method. In short:

```zsh
dotnet run --project Faolan.Importer -- --report npc-damage --captures <data-repo>/Captures
```

joins every captured damage record to the attacker's/target's **spawn level** and prints
hits/min/median/mean/max per level per damage school, in both directions. The sample size sits beside each
number, which is the point - a row built on four hits is visibly not a curve.

**Two packets, two meanings.** `DamageResultIIR` (0xFEC4158A) is the melee HIT - one per swing, 91..133 at
level 1. `StatDamageIIR` (0xE9F24184) is spell hits and DoT TICKS - small and repeating for a bleed, large for
a real cast. ⚠️ The word after `delta` in StatDamage is the damage **school**, not a stat id; censusing it as a
stat finds nothing and reads as "retail has no caster NPCs", which is how the magical branch went missing on a
first pass. Filtering by school instead shows magical casters at levels 1-6.

**A DoT's cadence is measured, not read.** The record carries no period, so consecutive ticks of one spell on
one target are one tick train and the gap between them *is* that spell's interval — harvested by the
`dot-effects` importer module into `Spell.DotIntervalMs`. 53 of the 84 DoT spells in the corpus have one;
values are round half-seconds from 500 ms to 11 s, peaking at 1 s (16 spells) and 2 s (14). A spell with no
measured interval ticks at the 2 s fallback, which is the median of the measured set.

The round-half-second **snap** is corrective, not cosmetic, and that is measured rather than assumed: over the
measured set the raw estimate sits a mean of **−0.05%** from the value it snaps to — no bias in either direction —
and the mean absolute distance falls from 5.3% on spells with 3–4 gaps to 3.3% on spells with 20 or more. That is
the signature of an unbiased estimator of a round interval with sampling noise, so the snap is removing that noise
rather than inventing a value (#657).

⚠️ A tick train is scoped to **one capture**: its key includes the target and caster entity handles, and those
are session-volatile and recycled, so pooling ticks across captures splices unrelated trains. Pooling the
resulting *gaps* is fine — a gap is a duration. Note also that the distortion is one-sided: a dropped or
unobserved tick merges two periods into one long gap and can never produce a short one, so the gaps of a real
DoT cluster at the interval with a long upper tail. That is why `DotIntervalEstimator` accepts on how tightly
gaps *concentrate* around the median rather than on their spread — a quartile bound rejects good data precisely
because a quarter of its gaps are doubled periods.

⚠️ The same one-sidedness defeats the **median itself** once a spell's gaps are stretched: the median drifts into
the tail, and the tight cluster at the bottom is then measured against a number it has nothing to do with. So the
estimator's leading rule is a **tight-cluster** one — the first tight (±5%) cluster walking upwards, provided that
cluster still **contains the shortest observed gap** — with the concentration-around-the-median test answering only
where that declines (#657, #660).

The proviso is what makes the cluster rule safe: a tight cluster with other gaps *below* it is a harmonic, not the
period, so the walk stops once its window leaves the bottom behind. Without it the rule reads 4000 ms off a ragged
`{2.1 2.2 2.4 | 4.0 4.2 4.4 | 8.5 8.8 9.1 | 12.0}` train. It also makes *declining* the common outcome — over the
50 median-accepted spells the cluster rule agrees on 21, declines on 28 and disagrees on 1 — which is correct
conservatism, because a re-application's first gap is an arbitrary fraction of the period and sits below the real
cluster.

⚠️ **That does not make the shortest gap an upper bound on the interval**, and the shortcut must not be
reintroduced: 41 of the 50 accepted spells have an estimate *above* their own shortest gap, precisely because
re-application fragments are common. Only a tight cluster is evidence; a lone short gap is not.

The two Ice Strike ranks are the worked cases and they now agree at 2000 ms. Rank 6 is the *rescue*: 13 of its 38
gaps inside 3.5% of each other at ~2.15 s while its median is 4.20 s, so the median rule rejected it outright.
Rank 1 is the *correction*, and the reason the cluster rule leads rather than merely fills in: 42 of its 93 gaps
sit in that same ~2.15 s cluster, but its median is 2.365 s — only 10% high, yet the wrong side of the 2.25 s
midpoint, so the half-second snap turned a 10% bias into a reported **2500 ms**. It is the one spell in the corpus
where the two rules disagree.

⚠️ **The school NUMBERING was wrong until #487, so any pre-#487 census output names the wrong elements.** Cold
sat at 5 with everything after it one too high, which made a necromancer's unholy nukes read as *Holy* and their
frost as *Siege*. The real order appends Cold after the nine — `Slashing 1 · Piercing 2 · Crushing 3 · Fire 4 ·
Electrical 5 · Holy 6 · Unholy 7 · Poison 8 · Siege 9 · Cold 10` — plus `12` = environmental (fall damage,
[`environmental-damage.md`](environmental-damage.md)) and `23` = a heal, the one value that never carries a
negative delta. See `DamageSchool`, which also records the trap: the client's **PvP** invulnerability block does
list Cold in fifth place, so re-deriving the order from that block reproduces the bug.

**The scale is settled**, which is what allows a damage number to be compared with a health pool. Pairing a hit
with the target's next health update does not work (4 usable pairs in the whole corpus - retail does not push
one per hit). The kill does: total damage dealt to an NPC that **died** equals the health its own spawn
declared, ratio 0.73..1.10. So damage, spawn health and player health share one unit.

That is what exposed the long-standing `5 + 3 * level` NPC swing: 8 damage at level 1 against health pools in
the thousands, ~15x too weak, invisible because nothing in the code compared damage with the health it is
subtracted from.

**The NPC swing line is confirmed at the top of the range** (2026-07-29). A level-80 necromancer fighting in
Poitain gives two unrelated templates at level 75, both within 4% of `level * 100` against a level-80 player -
`Hired Bodyguard` 7324 and `Zingaran Operative` 7200 versus a model of 7500 - and the same fight's combat log
shows the player seeing 67-92, so the wire figures are cross-checked rather than inferred. `level * 100` is no
longer an extrapolation from the beach.

⚠️ **Read `--report npc-damage`'s BY ATTACKER section, not its level buckets.** A level bucket mixes a
304,944-HP `Poitain Guard` with a 4,605-HP `Zingaran Alchemist`, both level 80, and it also mixes attackers
whose level sits at very different distances from the player's. Attributed per template, with the capturing
player's level alongside, two effects separate out that the buckets render as one apparent cliff:

- a **level-GAP penalty** - full damage within ~5 levels of the player, collapsing to ~0.10 by 7, and running
  the other way when the mob is *above* the player. Retail applies it server-side (the client tweak table
  carries no level-difference constant), and we model none of it -
  [#488](https://git.groenendaal.io/MelvinGr/projectfaolan/issues/488).
- **ranged mobs deal ~0.2 of same-level melee** even at gap 0 (`Poacher Marksman` 0.17, `Bat Pict Archer` 0.21).

Still thin: caster NPCs above level ~10, and any capture pinning the player's own Critical Damage Rating -
[#479](https://git.groenendaal.io/MelvinGr/projectfaolan/issues/479).

## The client's own COMBAT LOG - an instrument the packets cannot replace (2026-07-28)

The retail client writes a plain-text combat log on demand. It exists in our build: the exe carries
`logCombatCommand`, `CombatLog-%s.txt`, `CombatLoggingStarted`/`CombatLoggingAlreadyStarted`, and the command's
own name comes from the LDB, where all six language pages give the same word.

```
/logcombat on      # start
/logcombat off     # stop - do this BEFORE quitting, or the file can be left truncated
```

The file lands in the client's install directory as `CombatLog-<date>_<time>.txt`, one time-indexed line per
event, written as English sentences. The templates are LDB page **211711**, which is how we know exactly what it
records without having to run it first:

| template | what it gives us |
|---|---|
| `You hit %s for %u points of %s damage.` | damage + SCHOOL, player -> target |
| `You were attacked by %s for %u points of %s damage.` | damage + school, incoming |
| `You hit %s with a spell for %u points of %s damage.` | spell hits are DISTINGUISHED from melee |
| `%s hit %s for %d points of damage. Critical Hit!` | **crits are labelled** |
| `You absorbed %d points of damage from %s attack` | **mitigation/absorb is visible** |
| `%s absorbed %d points of damage.` | the same, third-person |

⚠️ **Two of those cannot be measured from a packet capture at all**, which is what makes this worth running:

- **Crits.** `DamageResultIIR` carries no crit flag - its two trailing words are always 0 - so on the wire a crit
  is just a larger number and is uncountable. The log labels it. `BaseCritChancePercent` /
  `BaseCritDamagePercent` are marked PROVISIONAL in `CombatResolver` precisely because the corpus cannot see
  them; the log can.
- **Absorb / mitigation.** The wire carries the post-mitigation number only. The log names the absorbed amount
  separately, so the armour formula becomes checkable end to end rather than only at its output.

It also settles a question the corpus left open: the log prints what the player SEES, so logging a fight while
capturing it pins the display-vs-wire scale by direct correspondence instead of by the kill-ratio argument.

**Best value:** run `/logcombat on` for the SAME fight that is being packet-captured
([#479](https://git.groenendaal.io/MelvinGr/projectfaolan/issues/479)). One pcap plus one log gives both sides of
every number.

### Measure damage as a FRACTION of the target, not as a number

`--report npc-damage` prints a PLAYER -> NPC section as a percentage of each target's own health, and that is the
row to tune against:

| level | hits | median % of target | hits to kill |
|---|---|---|---|
| 1 | 176 | 10.4 | 9.6 |
| 2 | 60 | 10.7 | 9.3 |
| 3 | 217 | 3.8 | 26.1 |
| 4 | 24 | 2.5 | 39.7 |
| 5 | 201 | 7.3 | 13.7 |

So retail kills an ordinary mob in roughly **10 hits** early on, tailing to 14-26 by level 5.

⚠️ The absolute medians in the section above it (465..691 at level 1) are NOT that number. A level bucket mixes a
63-health critter, a ~5,000-health elite and a 300,000-health city guard - `NpcSpawn.Health` legitimately spans
9 to 1,108,500, because retail's world has both populations (see `NpcHpAnchors`). Averaging damage across them
produces a figure that matches no actual fight, and reading it as if it did is what briefly made our weapon
damage look 50x too low. Per-target fractions are stable across the same data.

The rows above level 5 in that table (0.0%, "700,000 hits to kill") are exactly the unkillable population showing
itself - which is a useful check that the split is real and not an artefact.
