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

## NPC death lifecycle (loot bag -> corpse fade -> respawn)

A killed NPC no longer instant-pops. The killing blow pushes the `health=0` vitals (`ApplyNpcDamage`), then
`CombatService.OnNpcKilled` `MarkDead`s the world entity (+ XP + kill quests) and plays the death anim via the
retail NPC-death pair — a **death-pose `MoveTaskIIR`** (0x96C46740, movement-type 0x06, freezes the corpse in
place) + a **death-behavior `BehaviorControlCenterIIR`** (0x642CD3D6, behaviorId 41877) on the dying handle
(`DeathSequence.Pose`/`DeathSequence.Bcc`, #204/#349, see below). The
world tick (`WorldState.Tick`) then drives the corpse sequence (`WorldEntity.TickCorpse` → `CorpseStage`) and,
independently, the respawn timer.

⭐ **The bag is not the corpse, and the body does not pop.** Measured over 275 NPC deaths in 134 captures
(`--report corpse-timing`): the loot bag is a **separate dynel spawned in the SAME packet burst as the death**,
and the body lies **beside** it for another half-minute.
[V1: Faolan.Importer/Cli/ReportCli.CorpseTiming.cs]

| stage | when | what goes out |
|---|---|---|
| `CorpseStage.Drop` | **0.00 s** median (n=100 matched kills) | `LootBagFullUpdateIIR` for a **new** handle, at the corpse **+0.8 u on Z** (91 of 100 exactly `(dx,dz)=(0.0,+0.8)`) |
| `CorpseStage.Fade` | **30.72 s** median (n=229) | `n3ToClientFade` on the CORPSE handle, `fadeSeconds` **2.0** — invariant for `EntityClass.Character` across all 229; a loot bag's is 3.0 |
| `CorpseStage.Quit` | **33.18 s** median (n=228) | `n3ToClientQuit` on the corpse handle |
| bag expiry | **300 s** unlooted (4 bags, 299.8–300.2) | the bag's own `n3ToClientFade` + consumed-visual (`BroadcastEntityFade`) |

⚠️ The quit is measured **from the fade**, not as "the first quit after the death". A dead entity is still
interest-managed — walk away from a body and it quits, walk back and it re-spawns — so the first quit is often
that one. Reading it that way leaves the median at 33.18 s but drops the minimum to 6.96 s, which is the tell.

1. **loot drop** (`NpcCorpseLootDropped` → `LootBroadcaster.DropCorpseLoot`) rolls the drop and spawns a
   **SEPARATE** loot bag (its own handle, via `SpawnLootBagAt` — the standalone-bag path, decoupled in #246). The
   bag lives/loots/expires independently (a `ForLoot` entity, tick-expired) and SURVIVES the NPC's respawn. (Old
   `MakeCorpseLootable` reused the NPC handle in place — removed; it pinned the respawn to the death spot, #246.)
   A kill with nothing to give leaves no bag at all.
2. **corpse teardown** (`NpcCorpseFaded` → `BroadcastCorpseFade`, then `NpcCorpseQuit` →
   `BroadcastEntityDespawn`) fades and then drops the BODY, long after the bag landed.
3. **respawn** (`RespawnDelaySeconds`, `NpcRoute.DefaultRespawnDelaySeconds`): revives the NPC at its spawn
   point and re-renders it (`NpcRespawned`, which re-adds the handle to the viewers' Known set, #246/#245).
   **EXCEPT** `NpcRoute.NoRespawn` creatures (summons, ambient pool members): they leave the world
   (`NpcCorpseRemoved`) when the corpse sequence ends instead of waiting out a timer they never use.

⭐ **A respawn comes back on a NEW entity handle**, which is why it takes a moment to see. A handle is stable for
as long as an entity *lives* — the same one re-spawns at identical coords across interest churn — so the natural
test, `death(H)` then `spawn(H)`, looks right and finds nothing at all. The revived creature is a new entity with a
new id. The signal is the same **creature** reappearing **at the dead one's spawn point**:

| creature | playfield | observed intervals | |
|---|---|---|---|
| Brutal Pirate | City of Tortage (4020) | 149.9, 149.9, 149.9, 150.0, 150.2, 150.2, 150.2 (+ one at 157.3) | 7 agreeing kills across **2 unrelated captures** → **150 s** |
| Wild Cow | Poitain (2070) | 119.5, 120.0 | 2 kills, 2 captures → **120 s** |
| Wild Pig | Poitain (2070) | 119.9 (and one at 192.9 the client was out of range for) | one clean reading, agrees with Wild Cow |

⭐ **These are timers, and the proof needs no assumption about streaming range** (`--report respawn-interval`).
A spawn body arrives when the client is told, so a constant interval *could* in principle be one moment of the
player returning — except that the kills behind the 150 s cluster are **62 s apart inside one capture**. No single
delivery event can land at a fixed offset from each of several deaths spread over a minute, and the same cluster
also spans two sessions, which share nothing. (A player-position "was the client watching" gate was built first and
thrown away: judged against a fixed 100 u radius it rejected every clean 150.0 s pirate and accepted a wandering
Panther.)
[V1: Faolan.Importer/Cli/ReportCli.RespawnInterval.cs]

⛔ **What SELECTS 120 vs 150 is not decidable from the corpus, and not for want of harvesting.** The two measured
creatures are a beast in Poitain and a humanoid in Tortage, so creature identity and playfield are perfectly
confounded; nine attributes survive the cross-tab (map, level, health, community, superfamily, npc_type, normalset,
race, monster_data) and only `faction_id`/`profession_type` are eliminated, by being unset on both. Of the 251
kills that matched nothing, 151 never see that creature again in the dump and 89 more only see a re-streamed
handle. **One capture would decide it: kill a Panther in City of Tortage and stand there three minutes** — a beast
on the humanoid's map. Until then `NpcTemplate` has no column and `NpcRoute.DefaultRespawnDelaySeconds` takes
the best-attested cluster (**150 s**) for everything — Gitea #855.

⚠️ **Do not re-derive this from "the client watched for N seconds and nothing came back."** That bound cannot tell
*no respawn* from *the client was out of range when it happened*, and it is wrong by a factor of five here: the
same capture that shows `Brutal Pirate 0xaa88` respawning at 150.0 s also contains a 334 s window in which it
"never returned". Only observed intervals count.
`[V0: what governs a creature's respawn interval — creature vs playfield are confounded in the corpus]`

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
  BccIIR **envelope** — form **0x0004**, slot **0x0000001F**, a protobuf `08 <behaviorId> 18 <counter>
  28 08 30 00 60 <f12>`; the **behaviorId is creature-specific** (that creature's own death animation, harvested
  per `NpcTemplate.DeathBehaviorId`). Built by `DeathSequence.Pose` + `DeathSequence.Bcc` (byte-exact goldens),
  sent by `CombatService.OnNpcKilled` after the killing-blow vitals.

  **Three of the four args fields were misread, and the 272 captured death records settle all three (#742).**
  Only `field5 = 8` and `field6 = 0` really are constants — 272 of 272.

  - ⚠️ **field3 is not a "death variant"**, it is the dying entity's own **behaviour counter**, which is what the
    protobuf schema has called it all along (`CommandqueueServerId`). It takes **40 distinct values from 2 to
    373**, and against the same actor's previous BCC record of *any* kind it is **+1 on 265 of 267**. The
    progression is visible inside one kill: an NPC's two swings carry 5 and 6, the abort of the second carries 6
    again, and its death carries 7. The old constants (5 for an NPC, 3 for a player) were just the value the two
    golden captures happened to be at. `CombatService` now passes the NPC's real counter —
    `WorldEntity.AttackSwingCounter`, the same one its swing acks already number themselves with.
  - ⚠️ **field12 is the death animation's LENGTH in centiseconds, and it is the CREATURE's** (#742/#781). It was
    hardcoded at 240 for everything, which gave a beast's 1.0 s death animation 2.4× its own timing. Three of the
    four animations are invariant across every captured record of them — humanoid **240** (166/166),
    quadruped/beast **100** (32/32), the environmental one **300** (2/2) — so for those the animation and the
    creature say the same thing.

    ⭐ **The demon/large-caster animation `17036` is where they come apart, and it settles the field.** It takes
    eight values over 52 records; resolving every actor against its own capture's spawns
    (`--report death-timing`) gives **22 creatures, 22 of which carry ONE value and none of which carries two**:

    | 120 | 160 | 176 | 220 | 240 | 280 | 360 | 592 | 816 |
    |---|---|---|---|---|---|---|---|---|
    | Moose | Wild Cow, Wild Pig | Striped Boar, Wild Boar | Necrotic Bomb | Harvester | ten casters/crawlers | Bat Demon, Corruptor | Enslaved Demon | Valley Yeti |

    Two FAMILIES landing on one number each — both boars 176, cow and pig 160 — is not what noise looks like.
    Harvested per creature into `NpcTemplate.DeathActivationTime` (75 templates), with
    `DeathSequence.ActivationTimeFor` demoted to the fallback for an un-harvested one.

    ⚠️ A **zero** field12 is dropped rather than harvested: 20 of the 186 humanoid records carry one, on five
    creatures, three of which carry nothing else — so a zero is some other state of the record, not a length.
    Those three keep NULL and fall back to the animation's 240.
  - ⭐ **There is a FOURTH death animation, `128031`**, and it is PLAYER-only: two records, both on one
    character, both with field12 300. One of the two lands 8 s before that session's own annotation *"got
    incinerated by lava"* — inside the normal annotation lag — while the plain *"died"* annotation two minutes
    earlier maps to an ordinary 41877 death. So an environmental death appears to play its own animation. Two
    records is not enough to wire a rule on, and nothing selects it.
    [V0: what selects the 128031 death animation]
- **Player death** (#214) uses `SimpleCharKilledByUpdateIIR_t` (**0xB066AFC2**) — the "you were killed by X"
  record that drives the local release/resurrect UI, ABSENT from NPC kills. Record:
  `[subop][0000C350][dyingHandle][00][01][0000C350][killerEndpoint]`, built by `SimpleCharKilledByUpdateCodec`
  (byte-exact golden). `PlayerDeathService.SendDeathScreen` sends it (along with the death-pose + death-Bcc) for the
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

**Self-resurrection — the OFFER pair (`DispatchResurrectRequest`, s2c 0x2018), #890.** A resurrection is not
pushed onto a player: it is OFFERED and answered. The server sends

```
DispatchResurrectRequest [ID32 resurrector][u32 spellTemplate][u8 castSpellOnResurrect]   (ResurrectRequestCodec)
```

and the client replies on the GameCharAgent tier with `ResurrectMe(resurrectSpellID)` to accept or
`CancelResurrectionRequest(resurrectRequesterID, resurrectSpellID)` to decline. The spell id is a **token that comes
back** — which is why it need not resolve in any catalog for the exchange to work, and the one in the capture
(4841463) resolves in neither our tables nor this client's RDB.

The three field names are the client's own: its dispatcher (`FUN_004c0160`) reads them in order and asserts "stream
bad after `'i_cResurrectorID'` / `'i_nSpellTemplate'` / `'i_castSpellOnResurrect'`", then dispatches through vtable
slot `0x64` == `(0x18 + 1) * 4`.

⚠️ **The offer is armed while the player is ALIVE.** The corpus's single request is sent the instant a
Self-Resurrection Potion is used, by a player at full health who never died in that session — because that is what
the item does. Its tooltip is the specification: *"an option to resurrect themselves at the location of their death,
with 100% of their Health, Stamina and Mana restored. The potion must be consumed before the death occurs to have an
effect… The option to self-resurrect will persist until it is used."* So there is **no prompt at use time** (checked
in-client), and the death side of the exchange is not captured at all.

⭐ **The spell says how much it restores, and that is a whole content family.** `53183 AddSelfResurrectionSpell`
carries the spell to cast (field 88) and the restore percentage (field 188), lifted into
`Spells.SelfResurrectionSpell` / `SelfResurrectionPercent` by `RdbSpellImporter`. Sixteen spells carry it, and the
percentages are exactly the item tiers: the Potion 100, and Minor / Major / Exceptional 25 / 50 / 75 for both the
Elixirs of Resurgence and the Phoenix Bloods — plus the class abilities `Cheat The Reaper` (25),
`Undying Glory of Xotli` (25) and `Set's Life Spark` (100).

`WorldSimulation` holds the armed offer keyed by CHARACTER, not by connection, so it survives the zone change that
tears the world connection down — an option lost by walking through a door would contradict "persist until it is
used". It is not persisted, so it does not survive a server restart, the same narrowing the consumable cooldowns
have (#764). Taking it revives **in place**, at the position the player fell, restoring the offer's percentage of
all three pools.

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
`SimpleCharKilledByUpdateIIR`'s job). `PlayerDeathService.DiscoverNearbyRespawnPoints` drives both.

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

## The swing's own reach (#696)

The target handle a swing carries is the **client's**, so the server is the only thing that can bound a melee
swing. The division of labour is not symmetric with spells: the client range-checks a *cast* itself and simply
never sends an out-of-range one, but it always sends the swing (in-client, 2026-08-07: *"spells are checked
client side, but melee you can always do"*). Same shape as #271 — the client enforces what its own data tells
it, the server has to own what the client does not.

`CombatTargeting.ResolveSwingReach(world, attacker, targetHandle, out distance)` is that bound, and it is the
one number the cleave uses (`SwingReach` **is** `DefaultCleaveReach`): two constants would let a swing land on
a primary the splash could not have touched. It returns `InReach`, `TooFar`, or `TargetNotInZone`; the caller
answers both refusals as an **air swing** — the plain echo-ack a targetless swing already gets — rather than
dropping the request, because a silently refused action leaves the client waiting (the #271 lesson).

`TargetNotInZone` refuses too. You cannot hit what the attacker's zone cannot place, and passing it only bought
a hit-react and a floating damage number for a hit `ApplyNpcDamage` then refuses anyway (#695). The two
refusals are logged differently on purpose: too far is the player's doing, not-in-zone is a targeting/interest
bug on our side.

⚠️ **Every melee path has to go through it**, which is the part that took two attempts:

- the armed swing (`SendNormalAttackResponse`),
- the **unarmed** swing (`SendUnarmedAttackResponse`) — this had no check at all, and it is the only caller of
  `SendSwingDamage`, so a bare-handed player could kill anything selectable at any distance,
- the **combo finisher**, which is a separate op in the inbound handler and used to run whatever the swing
  decided — so a refused swing still delivered the combo's spell. `SendNormalAttackResponse` now returns
  whether the swing landed and the finisher rides that. (The combo's *step* tracking still advances on a miss:
  the client counts the step it pressed either way, and desyncing the two ends would be worse.)

⚠️ **The first cut of this gate shipped doing nothing**, and it is worth knowing why. It resolved the target
through `TryGetZone(MapId)`, which only ever finds the **default** instance — and it was tested on Tortage
Island (map 4010), which is `MapInstanceMode.Solo`. The lookup missed, the unresolvable branch fell open, and
every swing passed. The session log then read as *"the check runs and measures a small distance"*, which was
diagnosed as a stale-position problem; the log actually shows a swing landing for 40 on a mob standing
untouched at its own spawn **21.3 u** away, with its first move broadcast arriving only afterwards (the hit is
what aggroed it, #695) — the signature of a guard that never resolved its target at all.
`MeleeReachTests` now pins the instanced case directly, including that map 4010 really is Solo.

Reach remains **provisional** for the reason above; it is also where a weapon-dependent reach would go if one
is ever recovered — a polearm and a dagger plainly do not share it, and nothing shipped tells us what either
should be. [V3: MeleeReachTests.OutOfReach_InAPrivateInstance_IsRefused]

## Directional combat: the shield rose (#20)

AoC's signature melee is DIRECTIONAL — the attacker picks an arc, and the defender spreads a finite guard
across the same arcs, so the swing is worth more into whichever arc is thinnest.

**The arcs.** Three, named by the client itself: `ShieldDirection_e` / `ShieldDirection_n`, the UI strings
`Combat_Shield_{All,Left,Mid,Right}` and the LDB lines `ShieldLeft%1%` / `ShieldUp%1%` / `ShieldRight%1%` — so
"Mid" is the overhead arc. The rose is drawn by `CombatRoseView_c` (vtable `0x020a216c`), fed by
`TargetingModule_t`.

**The attack direction** is one of FIVE zones, and the three arcs above are the guards that cover them — a lower
zone is guarded by the arc on its own side, which is the client's own fold (see "The swing's DIRECTION is on the
wire" below). A PLAIN swing states its zone as the LAST word of the `BehaviorControlCenterIIR` request tail: the
`Normal Attack` ability id, LDB-named `769094` *Upper Left*, `769095` *Upper Right*, `769099` *Overhead*, plus
`769096` / `769097` (*Lower* Left/Right), which carry slot 0 in the catalog because the captured characters were
below the level that unlocks them. `CombatShieldRose.DirectionFor` maps the id to the zone and `ArcOf` the zone
to its guard [V3: CombatShieldRoseTests.DirectionFor_MapsTheFiveDirectionAbilities].

⚠️ **A swing is always directed** — a *combo step* simply states its zone somewhere else. It names no ability
(word 5 is absent on 107 of the 515 captured player swings) yet every one of those results still carries a
direction code, because the player reached that step by pressing a direction key and the catalog says which:
`Combo.Actions` is the key sequence (`UL,UR,UP`), so step *n*'s zone is its *n*-th token. `ComboSwing.Direction`
carries it into both the mitigation and the ack
[V3: ComboServiceTests.EachComboStep_CarriesTheZoneItsKeyNames]. Only a combo with no directional grammar at all
(a reactive/magic marker, a `Thrust` step) has no zone to state, and that case alone falls back.

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

### The swing's DIRECTION is on the wire, and the ack states it (#673)

A swing states its direction in each hit result's own type word, and split by actor the corpus shows a mob does
this even though it names no direction *ability*:

| | word 5 (the ability) | the result type |
|---|---|---|
| **NPC-actor** (2,691 swings) | never present | `0xCB` ×896, `0xC9` ×545, `0xCA` ×511, `0xCD` ×384, `0xCC` ×373 |
| **player-actor** (515 swings) | present on all 515 | `0xCB` ×275, `0xC9` ×167, `0xCA` ×145 |

**Five zones, three guards.** The wire alphabet is `SwingDirection` — Upper Left, Upper Right, Overhead, Lower
Left, Lower Right → `0xC9`..`0xCD` — and the rose that *guards* them is three arcs wide. That is not a modelling
compromise; it is retail's shape, and the collapse is the client's own: `FUN_005edd90` rewrites `0xCC` → `0xC9`
and `0xCD` → `0xCA`, folding each lower zone onto the upper one **on its own side**. So `CombatShieldRose.ArcOf`
maps Lower Left → `Left`, Lower Right → `Right`, Overhead → `Mid`.

That same fold is retail's progression rule — a character swings in three directions and unlocks the two lower
zones at level 40 (`0x28`, the threshold the fold is gated on) — which is why every captured *player* swing
carries only the three, while mobs, not being gated, use all five. The full derivation of the five-code alphabet,
and the two further client sources that pin it, is in
[`docs/re/behaviorcontrolcenter-grammar.md`](../re/behaviorcontrolcenter-grammar.md).

We used to draw the arc **twice**: `CombatShieldRose` rolled one for the mitigation while
`BehaviorAcks.HitResultBlock` wrote a constant `0xCB`, so the packet said *overhead* on every swing regardless of
what the player actually took. `WorldSimulation` now draws one **zone** and spends it on both — the mitigation
sees `ArcOf(direction)`, the ack `SwingDirectionCode(direction)`.

**The draw is measured, not a placeholder.** Collapsed onto the three guard arcs the corpus's mob swings are
Left 918 (33.9 %) / Mid 896 (33.1 %) / Right 895 (33.0 %) — χ² 0.37 on 2 df, i.e. uniform. The skew visible in
the raw five-way counts is the *height* axis the rose does not model: within the two side arcs, 1,056 upper /
757 lower = **58.2 % upper** (~7σ off even). `RollSwingDirection` is exactly that: a uniform arc, then the
height. Why retail leans upper is unexplained — and it is not a per-mob repertoire, since 55 of the 56 templates
with ≥ 8 captured swings use all five zones.

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

**An NPC's swing is the same mechanic as a player's.** Nothing about it is visible on the mob: the shields drawn
over a defender are a **UI overlay** of the rose, not a model or animation state, so there is no
left-swing/right-swing pose to look for on either side. A mob names no direction *ability* — none of ~63k
NPC-actor BCC records carries word 5, because a player's direction rides on an ability id and a mob has no
ability to name — but it does state the direction in each hit result's type word, which is the correction #673
made to this paragraph.

So the **selector** is no longer the class's placeholder. `CombatShieldRose.RollSwingDirection` draws a zone from
the measured mob distribution: uniform over the three guard arcs (χ² 0.37 on 2 df over 2,709 swings), then upper
at 58.2 % within a side arc. What is *not* modelled is any per-mob tendency — a mob that favours an arc, or aims
at the player's weakest guard the way a player aims at the mob's. The corpus gives no evidence of either: 55 of
the 56 well-sampled templates use all five zones.
[V3: CombatShieldRoseTests.RollSwingDirection_IsUniformOverTheThreeGuardArcs]

The player's guards therefore do work in PvE, matching the live game's "in general, outside of a few battles,
you can leave them alone for PvE" — and the default `111` spread is the table's neutral entry, so leaving them
alone costs exactly nothing.

Applied on the melee path only (`NpcCombatService.ApplyNpcAttack`, after armour), through the same table as a
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

**Two follow-up measurements, one of which killed the obvious objection.**

*The world-entry burst is not what makes the radius impossible.* The natural complaint about the table above is
that a capture's `t+0` burst is a world **build** — it states the playfield the player just loaded into, so of
course it reaches 1,834 u. Excluding every capture's entry burst moves the numbers almost not at all: 243 bursts
instead of 293, median 211.8 instead of 225.4, and the max only drops from 1,834.5 to **1,757.5**. Mid-session
streaming reaches just as far. The radius is impossible on its own merits.

*38.6 % of bulk despawns are a **refresh**, not interest loss.* Of the 210 despawn bursts, **81** have most of
what they dropped re-spawned within 30 s — often within a single second, at the same distances. So the "cycle"
#529 named is the cell being **re-sent**, not the objects leaving and re-entering range: the Stone Bricks
respawned 62 u farther away because the player kept walking between the drop and the resend, and the two
distances describe the walk rather than any interest boundary.

That also settles what reproducing this would buy. Re-sending a cell's whole contents on every boundary
crossing is visible churn — objects blink — with no gameplay consequence, and our radius model is already the
gentler behaviour. The measurement is worth having; imitating it is a fidelity-versus-flicker call, not an
obvious win. `--report dynel-cycle` prints all of the above.
`InterestManager` is still radius-based — changing it is open work on #529.

### The pass a standing player never got (#778)

Every interest pass was driven by the PLAYER — `BroadcastPlayerMove` (they moved), `BroadcastPlayerEntry` (they
arrived), `RefreshQuestVisibility` (their quests changed). Nothing was driven by the **subject** moving, and
`BroadcastNpcMove` only sends to viewers who already hold the handle. So for a player standing still, an NPC
that walked out of the interest range kept its last-rendered position for ever (no `n3ToClientQuit`), and one that
walked in was invisible until they next moved.

⚠️ **It cannot hang off the NPC's own move effect**, which is the obvious fix. A ROUTE walker emits no per-step
effect at all: since #716 the beat is issued to the client once and walked *there*, and the server only advances
its model. The entities most in need of this are exactly the ones with no move to hook — which is also why #703
turned a rare problem into a routine one, a single Old Tarantia beat running up to 98 waypoints.

So the world tick re-runs each connected player's own pass on a 1 s accumulator
(`WorldSimulation.InterestSweepIntervalSeconds` → `WorldBroadcaster.ResolveInterest`). `moveBody: null` makes it
Spawn/Despawn only — the movement itself already rides `BroadcastNpcMove` or the issued patrol — and the mover
arguments are left null because they drive world-OBJECT streaming, and a static object cannot walk into range.
A subject walking at ~2.2 u/s covers two units per sweep, so the granularity is far finer than the boundary it
is testing, and the cost is one distance check per entity in the player's zone.
[V3: StandingPlayerInterestTests.AnNpcThatWalkedOutOfRange_IsDespawnedForAStandingPlayer]

⛔ **And a radius has no memory, which the sweep turned from a curiosity into churn.** A walker whose beat runs
along the interest edge crosses it twice a lap, and each crossing is a whole `FA015807` spawn record one way and
a despawn the other — for an NPC that never actually goes anywhere. Measured rather than argued: an NPC dithering
three units either side of the line resolved **6 interest events** over six laps.

`InterestManager.DespawnHysteresis` is the band that stops it — spawned at the range, despawned only 15 u past
it. ⚠️ It is **ours, not retail's**: the range it widens is itself unmeasured (#856), and retail's own answer to
this family is a grid with hysteresis rather than a radius with a band (#529). It is sized against the thing that
churns — a 2.2 u/s walker spends about seven seconds in the band before a despawn lands.

⚠️ It is a **despawn-side rule only**, and that is the invariant to preserve: an entity in the band is kept if the
viewer already holds it and ignored if it does not. Spawning into the band would simply make the effective range
165 u and move the churn outward.
[V3: StandingPlayerInterestTests.AWalkerPacingTheBoundary_IsNotRespawnedEveryLap]
[V3: StandingPlayerInterestTests.AnUnknownNpcInsideTheHysteresisBand_IsNotSpawned]

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
`EnergyCodecTests`. Wired into `NpcCombatService.ApplyNpcAttack` via `IWorldOutbound.SendNpcAttackAnimation`.
The animation/result params (`typeId`/`swingCounter`/`f5`/`f11`/`f12`/`resultType`/`resultK*`) are not
semantically decoded - capture-typical defaults for the live send.

## Hostile-COLOURED is not hostile-BEHAVED (#822)

The Town Crier attacked the player, and the data underneath was right: template 14572 is `cCategory = 1`
unanimously — **62 votes** across two captures and two characters — and he **never fights**. He spawns and
despawns as the player walks past him for a whole session with no attack, no hate list, no threat. Retail
colours him red and leaves him alone; our code read one flag as both.

So `WorldEntity.Hostile` (the colour, and the right to fight back) and `WorldEntity.Aggressive` (whether
walking past is enough) are separate now.

⚠️ **Two discriminators were tried and failed first**, which is why the rule is a conjunction:

* **Perception** — the crier has NULL `SightSkill`/`HearingSkill`/`SmellingSkill`, a distinctive 7% of hostiles.
  But that 7% also holds the Crow gang, the Wharf Rats and the Nemedian Scouts, which are genuinely aggressive.
* **Combat capability alone** — 102 hostile templates carry no weapon, and most of them are animals, which fight
  perfectly well without one.

⭐ **`NpcWeapon.Initiates` is armed-OR-not-humanoid, and it was validated on the exact population that killed
perception.** Of the 18 hostile templates with no senses at all:

| | |
|---|---|
| **armed → stays aggressive** (14) | Crow Harrier / Nightblade / Roofhugger / Stalker, Nemedian Archer + 3× Scout, 5× Wharf Rat, Wulfhere |
| **unarmed** (5) | Aelfrith, Ambassador Nidas, Chancellor Publius, Dexitheus, Town Crier |

Every mob armed, every civilian not. `Frozen Hatred` and `Parasitic Soul` are unarmed too and stay aggressive,
because they are not humanoid.

Across all **352** hostile templates the rule selects **10** (`--report npc-aggression`): the four level-80 named
city NPCs, two Dockworkers, two Slaves of Acheron, the Town Crier, and one uncertain case — `Vanir Acolyte`,
level 20. Nine of ten are unmistakably civilians.

⚠️ **It gates INITIATION only.** Retaliation stays on `Hostile`, so a passive NPC still fights back when
attacked — this can make something unprovoked, never inert. And `Aggressive` **defaults to true**, so a mint site
that never considers it keeps the old behaviour rather than silently pacifying its NPC.

⚠️ The humanoid signal is whether the spawn carries a **morph table**, which is harvest-dependent: a humanoid
whose morphs were never captured reads as an animal and stays aggressive. That errs toward leaving mobs alone
rather than pacifying them.

⚠️ A trap met on the way, worth repeating: **three** templates share the name *"Red Hand Sentry"* (2271 and 2279
hostile, 9551 friendly). Comparing by NAME gives a completely wrong picture of the dispositions.

## Where an NPC's hostility comes from (#256/#715/#731/#840/#841)

`NpcStatCore.Disposition` is retail's own `cCategory` — the third word of the `DispatchUpdateAggroStatus` reply,
`0` friendly (green) / `1` hostile (red) / `2` neutral (yellow). It drives both the overhead name colour and, in
our sim, whether the NPC aggros on sight (`WorldEntity.Hostile` is `Disposition == 1`).

**It is server-authored: there is no client-side table to mine.** The client ASKS, per target, at runtime — a
client that could derive hostility from its own RDB would not need to. (`data/FactionRelations.xml`, RDB
1010701/1, was chased for this and is player REPUTATION in a different id space - now decoded in its own right,
see [factions.md](factions.md); and retail's own spawns omit
`NpcStats.faction_id` entirely.) So the only way to learn a template's disposition is to capture retail
answering for it, and **targeting the NPC is enough** — no combat, no kill, no quest state.

The importer harvests it in `NpcDispositionModule`, and how it aggregates matters. `NpcDispositionTiers` owns the
order — **three observations and one inference**, strongest evidence first:

1. **A template that was polled gets its own answer.** 406 of the 414 polled templates vote **unanimously**, and
   the answers line up with the client's colour table exactly — traders/villagers/citizens green, mobs red,
   **guards yellow**.
2. **A template caught SWINGING at something is hostile** (#840). Same corpus, a different record — an
   observation, not an inference — and it is ranked above the faction roll-up for the same reason its own poll is:
   its own behaviour beats an average over its faction-mates.
3. **A template still unanswered falls back to its FACTION's majority** (`NpcStats.community`), which is what
   gives the harvest reach beyond the NPCs actually targeted.
4. **The mob rule** (#841): `CarsGroup == 3` **and** a real, non-civilian community → hostile. The only inference
   in the chain, and it fires only when the corpus has said nothing about the creature *or* its whole faction.
5. **Nothing → NULL**, which the sim treats as friendly, the client's own default for an NPC it was never told
   about.

### Why tier 4 exists, and why it is scoped the way it is

The captured tiers reach as far as the corpus does and no further: 154 types over **645 placements** came out of
them with no answer, and a NULL is served friendly — so the Tortage beach crocodiles stood in the world green and
passive (#840). Both new captures and a hand-maintained override were ruled out, so what was left had to be a
rule. ⚠️ And a rule here is exactly what went wrong before: `Hostile = Community > 0` was replaced on 2026-07-11
*because* it aggroed every Citizen, vendor and Gate Guard — community 1 is the CIVILIAN/default faction.

So it was measured rather than argued. `--report disposition-rule` scores every candidate against the 430
templates retail was captured answering for directly — ground truth no tier of the chain contributed to:

| rule | hostile found | NEUTRAL→hostile | FRIENDLY→hostile | accuracy |
|---|---:|---:|---:|---:|
| always friendly (the default it replaces) | 0 / 102 | 0 | 0 | 76.3% |
| `Community > 0` (the pre-2026-07-11 rule) | 86 / 102 | 84 | **159** | 39.8% |
| `Community` not in {null, 1} | 84 / 102 | 58 | 91 | 61.2% |
| `CarsGroup == 3` alone | 96 / 102 | 5 | 0 | 97.4% |
| **shipped:** both | 84 / 102 | 2 | **0** | 95.3% |

The old rule lands **below the friendly default** it was traded for, and its 159 friendly-side errors are the
2026-07-11 regression quantified. ⭐ The collision group alone scores a point higher than what ships and is
deliberately not what ships: on the population that actually reaches the tier the two answer the **same 118
templates**, so the community half costs nothing today while halving the neutral-side error. The slice they
disagree on — the mob collision group in the civilian faction or in none — is where the passive wildlife sits, the
rabbits, hens, deer and badgers retail answers NEUTRAL for. A rule that makes a rabbit bite is a visible defect;
leaving a creature friendly is the status quo the tier improves on, so the tie breaks conservative.

`CarsGroup` (spawn proto field 5) is **not** decoded as an aggression field — it is the collision/response group —
and it is used as a CORRELATE, consulted only where there is no observation. It separates the two populations
cleanly all the same: groups 0/1 hold the named townsfolk (Villager, Serving Wench, Armorsmith, Tanner Chael),
group 3 the mobs (Wolf, Bear, Warg, Vanir Berserker, Mercenary Archer, Pict Ripper Jaw).

Result: **645 placements over 154 types down to 102 over 61**, and the residue is Refugees, Commoners, a Target
Dummy and named story NPCs — the population the friendly default is actually right for. `ZonePopulationLoader`
warns per zone with the count and the names, because a world quietly missing its monsters looks exactly like a
world that has none.

### Turning a PEACEFUL NPC hostile mid-session — the collision-group restate (#839)

Everything above decides what a creature is when the world loads. A creature that turns on the player *during*
play — Saddur, whose dialogue fires the `Fight` scry event — needs one record more, and it is the one that makes
the flip real to the client:

```
StatIIR (0xF98E10B3)  [class 0xC350][npc handle][env 0x00][stat 0x41A CarsGroup][3]
```

⚠️ Without it the flip reaches the server model and **nothing tells the client**. Every other part already
worked: the NPC swung (8 damage landed), the hate list arrived addressed to the player, and the client's own
`RequestAggroStatus` re-poll was answered `cCategory 1` — and the player still saw a peaceful nameplate and could
not hit back. The reason is that Saddur spawned in `CarsGroup` **1**, the townsfolk group; an ordinary mob is
already in group 3, which is why no other aggro path needs this.

⭐ The corpus dates it exactly. Over all 134 captures **every character `CarsGroup` record carrying 3 is one of
the three captured Saddur fights** — three captures, three handles, no fourth occurrence — and in all three the
order is identical:

```
SendScryEvent "Fight"  →  CarsGroup := 3  →  HateListUpdate  →  the client re-polls  →  CombatMode 4, MoveTask, swings
```

So the restate is the FIRST thing retail sends on the flip, ahead of everything the ordinary aggro burst carries.
`NpcBroadcaster.BroadcastNpcTurnedHostile` sends it, byte-exact against all three fights
(`NpcTurnsHostileTests`). Two details are copied from the capture rather than reasoned out and are marked as such
in the code: it is a **character** record carrying the **world-object `env` byte** (0x00 — the same "restate the
dynel-layer copy first" shape `SkillPointTests` pins on Runspeed), and retail sends it **twice**, as two packets
in one segment, in all three captures.

⭐ **That second copy is not about the flip.** Retail sends a whole family of stats as an exact back-to-back pair
every time (`--report duplicate-records`, over 30,125 `StatIIR` records): `WaypointType` 336 pairs / 336 singles,
`SoulFragments` 56/56, `GuildID` 38/38, `MovementMultiplier` 35/35 — and `CarsGroup == 3` itself 3/3. A doubled
count *equal* to the single count is one "first" and one "repeat" per event, i.e. always a pair. So the doubling
is a property of retail's stat sender across a set of stats rather than something specific to turning an NPC
hostile, and copying it here is copying a general behaviour.
`[V0: why retail's stat sender emits a pair for this family of stats.]`

That retail treats the group as *live* state is also independent evidence for the tier-4 reading above: the value
it moves a creature to when it starts fighting is the value the rule keys on. `NpcStatCore.MobCarsGroup` is the
single constant both use.

⚠️ It was faction-first for everything until #715, on the reasoning that per-NPC `cCategory` is "noisy". It is
not — the noise was the name collision of #731 (38 templates share `Halen van Dummy`, spanning four factions, and
votes were routed faction-ward *through the name*). A faction roll-up also cannot express the guard/citizen split,
since guards share a community with the citizens they protect, and it is outright wrong where a faction holds two
populations: community 346 polls four templates unanimously neutral and two unanimously hostile.

⚠️ **Handles are recycled** — 91 of them carry two different templates within one capture — so the extractor
replays `handle -> template` as it walks the packets and attributes each poll to the most recent spawn *before*
it. Collapsing that into one map first silently gave four `Crocodile Pict` templates another mob's votes.

### ⭐ The broadcast rule (#272)

**A record addressed to a SUBJECT and describing world-visible state goes to every player who can see that
subject — not only to the client whose action caused it.** A record about a player's own state, or a reply to
their own request, does not.

This class of bug is invisible in single-client testing *and* in a single-client capture, which is why it needs a
standing measurement rather than a convention. The oracle is
**`login_2-clients_group-trade-friend`** — the only capture with two game connections. With one client, a
broadcast and a private reply look identical; with two, a record reaching both streams IS a broadcast.
`--report broadcast-check` re-measures it:

| verdict | records |
|---|---|
| **BROADCAST** (every subject reached both clients) | `SimpleCharFullUpdate` 35/35, `MoveTask` 12/12, `BehaviorControlCenter` 11/11, `StatUpdate` 9/9, `LookAt` 8/8, `CharDCMove` 6/6, `DoorFullUpdate` 5/5, `MagicBuff` 5/5, `Stop` 4/4, `StatDamage` 3/3, `ClimbingDynelFullUpdate` 3/3, `AddToGroup` 2/2, `RemoveFromGroup` 2/2, `LockedToChanged` 2/2 |
| **per-client** (every subject reached one) | `FullPlayerCharacter` 0/2, `Feedback` 0/2, `FormatFeedback` 0/2, `UpdateInventory` 0/2, `CoreClaim` 0/1 |
| **MIXED** | `SimpleDynelFullUpdate` 56/70, `N3ToClientQuit` 2/44, `GameActionList` 5/6, `CharInPlay` 3/5, `StatIIR` 3/4, `Regen` 1/2 |

⚠️ **Do not automate off the type.** An IIR type is not one message: a `BehaviorControlCenterIIR` carries an
entity's animation (broadcast) *and* the ack of a client's own request (not), and the two players in that capture
never fought — so its 11/11 says only that these eleven were the first kind. The record's **role** decides. The
MIXED rows are the proof: `N3ToClientQuit` is 2/44 because a despawn is per-viewer *by interest*, which is
correct, not a bug.

**How to apply.** Use `WorldBroadcaster.BroadcastEntityRecords` for records about an NPC/world entity, and
`BroadcastBehaviorToViewers` to relay a player's own behaviour to everyone watching. Reach for them when the
record says what an entity **did**; leave a per-client send when it answers what a client **asked**.

Fixed under this rule so far: the NPC death sequence (drop-target, death pose, death BCC — previously sent to
the killer alone, so a creature someone else killed stayed standing for you), and the search / hide / rest player
stances (previously the actor only, so a rogue entering the shadows was still standing upright to everyone else).

### The death pose flushes what the chase never sent (#272)

⚠️ A creature killed **mid-chase** used to jump onto its killer and only then play its death animation, and only
when killed *from a distance* — a melee kill never showed it.

The tick throttles a chase into ~6u segments (`MoveBroadcastSegment`) and flushes the remainder in its own
`EndMove` when the NPC arrives. **Death is the one way a move ends without going through that**, so a mob still
running at you carries up to 6u the client was never told about — and the death pose states the *true* position.
Kill it in melee and the chase had already stopped and flushed, which is why the bug looked range-dependent.

⭐ Retail keeps the two in agreement rather than posing somewhere else: over the whole corpus **250 of 267 death
poses sit at distance 0.00** from the end of that handle's preceding MoveTask (median 0.00). So the pose is right
and the client was simply behind — `WorldSimulation.FlushPendingMove` sends the missing segment before
`DeathSequence.Pose`, and `WorldEntity.TakePendingMoveSegment` re-anchors so a second call cannot re-send the same
ground. Gated by `PendingMoveFlushTests`.

## NPC chase / aggro movement (#190, 2026-06-15)

A hostile NPC aggros a player two ways: proximity — a player inside `NpcAggroRange` (20u) — or **being hit**,
which engages it with its attacker for `NpcIrritationForgetSeconds` regardless of distance
(`WorldState.RecentAttackerOf`, off the attacker + timestamp `CombatService.ApplyNpcDamage` stamps on the NPC).
The damage half applies none of the proximity gates (range, line of sight, the vertical band): those answer
"would this mob notice you", and a mob that has been hit already has. The leash below still bounds the chase.

⚠️ Both halves matter for HEALTH, not just movement. A disengaged NPC full-heals (the anti-whittle rule below),
so while damage did not aggro, any mob attacked from outside 20u reset to full on the next tick and only ever
took the most recent hit — in-client that reads as "the first hit is fine, everything following is not" (#695).

Once engaged, `WorldState.Tick` steps the NPC toward
the target (horizontal, `NpcMoveSpeed` 7u/s, keeping terrain Y) and `NpcBroadcaster.BroadcastNpcMove`
sends a per-tick **MoveTask transform** (`MoveTaskCodec.BuildBody`, the 61-byte `0x02` "orient" form:
origin->this-tick position, facing travel direction) so nearby viewers watch it move. In melee range
(`NpcMeleeRange` 3u) it swings (the attack-animation ack above). **Leash:** past `NpcLeashRange` (45u) from
spawn it disengages *immediately*, walks home (ignoring re-aggro, `NpcRoute.Returning`), and heals on arrival.
Losing the target out in the field also sends it home, but only after `NpcChaseDeaggroDelaySeconds` of standing
its ground (`NpcRoute.TargetLostAt`), so stepping out of aggro range and straight back in is one engagement.
WORKS in-client (it moves + leashes + stops on de-aggro).

### ⭐ Each attacker chases its own SLOT, and the ring is retail's (#908)

Reported in-client: five `Tortage Pirate`s on one player settled **0.03u-0.46u apart** — every one of the ten
pairs inside half a unit. The chase walked all of them to `NpcContactRange` of the target's *exact* position with
no per-attacker offset, so N attackers solved for one sphere and stacked inside it.

The geometry is **measured, not designed** — `--report melee-crowd`, 25,638 NPC-NPC pairs across 75 captures. The
method matters and is worth reusing:

- ⚠️ It does **not** go through attack records. Its sibling `ranged-standoff` established that a mob's `Combat:*`
  BCC carries an empty target list, so "who is attacking whom" is not recoverable that way. It does not need to
  be — the question is about positions, and those are streamed.
- ⚠️ **`CharDCMove` is the PLAYER's movement and only the player's** — measured, not assumed: 30,769 records over
  the corpus, **not one** naming an NPC handle. An NPC moves by `MoveTask`, so a report that tracks NPC positions
  from `CharDCMove` is silently reading each mob's *spawn* position — where it stood when it streamed in, not
  where it fought. (`ranged-standoff` has that defect; noted there.)
- ⚠️ Proximity is not aggro, so the crowd is cut to mobs that have moved recently — a chase is mobs walking, a
  city square is mobs standing.

**Is it a ring, or do they just pack in?** On a circle of radius R with N evenly-spaced occupants neighbours sit
`2·R·sin(π/N)` apart, so dividing the observed closest pair by that factor recovers the radius each crowd size
implies. Packing would show the implied radius falling away with N; a ring holds it flat. It is flat:

| crowd | closest walking pair (median) | implied R |
|---:|---:|---:|
| 4 | 1.49u | 1.05 |
| 5 | 1.21u | 1.03 |
| 6 | 1.06u | 1.06 |
| 7 | 0.83u | 0.96 |
| 8 | 0.83u | 1.08 |

So attackers take a **slot on a ring around the target**. `AttackerSlotRadius` is `NpcContactRange` (1.5u),
bracketed between the corpus's two estimates that lean opposite ways — the implied R above *under*-estimates
(even spacing is assumed, real spacing is not) while the measured distance from a crowd member to the player
(median 2.95u, p10 1.57u) *over*-estimates (bystanders). What confirms the pick is that it reproduces retail's
spacing at this radius: **2.03u for three attackers against retail's 2.11, 1.33 for five against 1.21, 0.84 for
seven against 0.83**.

⭐ **The angle is a golden-angle sequence, not a hash.** Handles are allocated sequentially, so a wave of spawns
gets consecutive handles — the input an ordinary scrambling hash handles *worst*. One was tried first and put two
of five consecutive attackers 0.42u apart, inside the stack this was meant to fix. `handle * (2³²/φ)` read as a
fraction of a turn keeps any prefix of the sequence as evenly spread as it can be. The target's handle is added
as a **rotation of the whole ring**, so a creature does not take the same angle against everything it ever
fights while the attackers' relative spacing is untouched.

⚠️ The walk aims at the slot; the **range check still measures the target**. They differ by up to the ring
radius, so reusing the slot distance would let a mob swing from out of range on one side and refuse a legitimate
swing on the other. Gated by `AttackerSlotTests`, which compares each crowd size against the retail median above.

⭐ **Three AI clocks, and all but one are retail's own** (#804, from the client's tuning sheet RDB 1000516 —
`docs/rdb/client-tweak-table.md`): `AIDefaults_MaximumChaseRadius` 45 → `NpcLeashRange`,
`AIDefaults_IrritationForgetPeriod` 4 → `NpcIrritationForgetSeconds`, `AIDefaults_ChaseDeaggroDelayTime` 5 →
`NpcChaseDeaggroDelaySeconds`. The anti-whittle gate below is the exception and is **ours** — retail's sheet
states no such rule. They used to be one constant of 10 doing all three jobs, which is why none of the retail
numbers could be adopted until it was split.

A **patroller's** territory is its spawn *plus its whole beat* (`NpcRoute.BeyondLeash`), measured to the
route's **polyline** rather than to its waypoints — the walk goes down the middle of each leg, and a sparsely
sampled beat (the single-leg animal wanders on map 2070 run up to 228u end to end) has its entire middle
further than a leash-radius from both ends. Measuring to the vertices alone therefore read 28 of the 1,073 stored
beats as "dragged too far from home" ~40u in (the leash was still 40u when this was measured), so those NPCs ping-ponged off their own route forever and never reached the
far end of it (#716).

**Anti-whittle:** a disengaged NPC with its health down restores to full, so it cannot be worn down from
outside its aggro range across separate approaches. The trigger is `NpcAntiWhittleSeconds` (10s) since the last
hit — **not** "no player currently in aggro range", which is a different question and, while damage did not
aggro, fired between every pair of ranged hits. It is deliberately LONGER than either AI clock: it has to cover
the gap between two casts of a slow spell (Smite is a 2s cast on a 1s recast) plus the walk into range, and at
4s or 5s a full heal would land mid-fight.

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
0x2CCE 9441→9462; 0x2C47 11118→11138; 0x2C48 12141→12187 - perfect correlation).

### ⭐ The values are BCC STATE ids, and all of them decode (#905)

The client calls stat `0x14` **"BCC State"**, and it means it literally: the values are **state ids out of the
client's own behaviour trees** (RDB 1010203, plain-text XML —
`--report bcc-trees --report-arg <ids>`). Every value the corpus carries now has a name:

| id | name | trees | id | name | trees |
|---:|---|---:|---:|---|---:|
| 1 | `Idle` | 28 | 29 | `Limbo` | 24 |
| 2 | `Movement` | 11 | 39 | `Spellweaving` | Character |
| 4 | `Fighting` | 22 | 41 | `Drunk` | Character |
| 9 | `Sitting` | 16 | 69 | `Evade` | 3 |
| | | | 72 | `Talking_idle` | Character |

Three corrections fall straight out:

- ⚠️ **`1` is `Idle`, not "the player is in combat"** — which this page and the enum both used to say. The
  player's captured 1 is the client sitting in its idle state.
- ⚠️ **`0` is not a state at all.** No shipped tree declares id 0, so pushing 0 to sheathe — inferred from "idle
  entities carry no stat-20 record" — was sending a value retail has no meaning for. The sheathe is **`1` (Idle)**.
- `29` is `Limbo`, the client's own name for death; `9` is `Sitting` (#355's `/sit`); and `39` is `Spellweaving`,
  the state [`combos.md`](combos.md) already keys the `Spellweaving Remover` criterion off (`!= 39`) without
  knowing what the number was.

⚠️ State ids **collide across trees** — 11 is `LoopingEmote` in Animal/Character and `SneakHiding` in seven
others — so a value only resolves against the tree the entity actually runs. That is why the lookup names the
tree and why there is no flat published id→name dump for states (there is one for *substates*, which do not
collide).

Implemented as a world-sim state transition: `WorldEntity.InCombat` flips in `WorldState.Tick` when a hostile NPC
acquires/loses a target (emitting `NpcEnterCombat`/`NpcExitCombat` effects), and `NpcBroadcaster.BroadcastNpcCombatMode`
pushes the stat (4 `Fighting` on enter, 1 `Idle` on exit) to every viewer in range that already sees the NPC (mirrors `BroadcastNpcMove`).
Reset on death/respawn so a respawned mob re-draws cleanly. Builder `VitalsPacket.BuildStatBlockBody`, byte-exact
against frame 6424 (`CharacterStatBlockTests.BuildStatBlockBody_NpcCombatMode_MatchesCapturedFrame`); transition
logic covered by `WorldStateTests.Tick_NpcEngagesAndDisengages_EmitsCombatModeEffects`. The exit value is now
`1` (`Idle`) rather than the inferred `0`, which no shipped tree declares — see the state table above.
**VERIFIED in-client — the NPC swings with its weapon drawn (#218 CLOSED).**

⭐ **But the send was a ONE-SHOT, and retail's is not (#905).** `BroadcastNpcCombatMode` fires on the engage
transition and reaches only viewers who already `Known` the handle, so a mob that aggroed before the player could
see it never drew its weapon *for that player* — and there was no retry for the rest of the fight. Measured over
one five-pirate fight: the server sent 1,315 `StatUpdateIIR` frames and **exactly one** went to an NPC — handle
0x65, the single pirate the player had selected, which transitioned later via the retaliation path when its
handle was already known. The other four aggroed by proximity on the tick the player arrived, and are exactly the
four that rendered sheathed. One-to-one with what was on screen.

Retail restates it: over all 138 dumps (10,373 well-formed `StatUpdateIIR` frames) there are **593 stat-20
records across 43 captures**, and per (capture, handle) **25% are sent more than once — one of them 28 times**.
So an already-fighting NPC now re-asserts its state to a viewer at the moment that viewer confirms it has built
the dynel (`HandleWorldEntrySpawnPoll`, the same structural gate the #225 idle assert uses).

## Level-up feedback (#209)
On a ding (XP-driven or the GM `.level` set) retail sends, in order: a burst of **`AttributeChanged`/`Increased`**
on-screen feedback lines — **one per primary attribute that rose** (the 5 category-615 ids 804/806/808/810/814) —
then a `0x96B8DC59` vitals push with the new Health/Stamina/Mana maxima. There is **no** "You reached level N"
banner in any capture, and the Level number is never pushed as a standalone `0x36` stat (the client tracks it from
the snapshot/quest stream). The attribute lines are `FormatFeedbackIIR` payloads — see `docs/subsystems/on-screen-feedback.md`
and `AttributeChangedCodec` (golden `AttributeChangedCodecTests`).
Both paths are the same code: `LevelUpService` owns the burst, and combat only decides how much XP a kill is worth
(`CombatResolver.ResolveKillXp`) before handing it to `LevelUpService.ApplyXp`; the GM `.level` set enters at
`LevelUpService.SetLevel`. Which attributes rise / by how much is provisional (`Progression.AttributeGrowth`, flat
+1/primary) — no per-class growth tables exist in captures.

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
- **`0x200A DispatchLearnSpell` / `0x200B DispatchForgetSpell`** — one spell entering or leaving the spellbook.
  Body = a bare big-endian `u32` spell id (`i_nSpellID`), invariant across all 68 captured grants.
  `SendLearnSpell`/`SendForgetSpell`, one `SpellBookChangeCodec` for both (decode `CaptureSpellImporterTests`,
  golden `SpellBookChangeTests`).

  ⭐ **The FORGET half is client-recovered, not capture-derived (#915).** No capture carries one — retail only
  sends a forget when something un-teaches a spell, which no captured session does — but the body needs no
  capture: the client's two dispatch readers are the same function twice, `FUN_004bf2b0` (learn) and
  `FUN_004bf3a0` (forget), each making a single call to `FUN_0048f900` (a 4-byte stream read byte-swapped to host
  order = one big-endian `u32`) before handing the value to adjacent vtable slots `+0x2c` / `+0x30`. Both
  "stream bad after `'i_nSpellID'`" diagnostics name the same single field. [V1: docs/re/aoc-client-rpc-methods.json]

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
at save time so they aren't double-applied on entry (`BuffService.ShouldPersistBuff`).

**Shield of the Risen (#221) — RESOLVED + in-client verified.** `SendShieldOfTheRisen`
(`BuffService`) applies the newbie-protection self-buff from `RestoreEntryBuffs` in the world-entry
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

**Shield breaks on the first aggressive action.** `BreakShieldOnAggression` (`BuffService`) drops
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
- **The behaviour GESTURE** — the `BehaviorControlCenter` animation (`#48`, `BroadcastBehaviorToViewers`) in
  `GameServerListener.Inbound.cs`. ⭐ **Relayed by DEFAULT since #912**, which is a change of shape, not of
  contents. It used to be a five-id allowlist that grew once per discovery — #48 the combat behaviours, #428 the
  mounted jump, #403's sibling `Equip` — each time with the same symptom (an action visible only to the actor).
  `/sit` was the next instance: it arrives as `BehaviorId.Transition`, which was not in the list.

  `--report bcc-relay` settles it off the corpus. Split every s2c BCC by whether its actor is the **recipient**
  (the recipient's own handle being the modal actor over that capture's c2s records) and retail relays a foreign
  actor's behaviour on **19 of the 23 ids the corpus carries** — `Transition` 1,233 relayed records across 81
  captures against 719 echo-acks, `Emote` 24,764 across 88. Five of nineteen was never going to converge by
  discovery.

  ⚠️ Exactly one id is excluded: **`Mount`**, 100 s2c records every one an echo-ack, over 25 captures that all
  carried foreign BCC actors — *a miss needs that control*, because a zero is otherwise just "nobody else did it
  nearby". It has a mechanism too: the viewer's mount visual is the ride buff + `StatUpd(Mount, MountSpeed)` that
  `MountService.SeatAndPersist` already broadcasts, so relaying the BCC would double-send it.

  ⚠️ `Search` (n=4), `Climbing` (4) and `Tradeskill` (2) are zero-relay as well and are deliberately **not**
  excluded: at that n a zero is not a rule, and the base rate among ids with a real opportunity is 19 of 19.

  ⚠️ The corpus is single-client, so "relayed" here means *the client is asked to render this behaviour for a
  handle that is not its own* — mostly NPCs. It does not by itself prove retail relays a **player's** instance to
  another player. What it does establish is the client-side half, which is the one that matters: nothing
  downstream of the record knows whether the foreign handle is an NPC or a player.
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

**The whole feature is one content switch.** `Content:KillStreaks` (default `true`, retail's behaviour) gates
`CombatService.AnnounceKillChain`, which is where a chain is *started* — with it off nothing is tracked, so the
tick's closing `0` has nothing to close either and no `ChainKillIIR` is ever sent.

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

### A cast is TWO s2c behaviour records, and the cooldown rule has two exemptions (#768/#769)

Both tickets were opened off a count of s2c `0x642cd3d6` bodies in the 45 packets after a cast, and both counts
conflate other actors' behaviour with the caster's. Re-measured over **all 687 captured casts**, attributing each
record to a cast by `(actor, args.1, args.2)` and each cast to its spell by the `Magic:Attack` **tail's u64 low
word** — which *is* the spell id, and is the only place a cast states one:

| phase sequence | casts |
|---|---:|
| `0x04` → `0x0A` (begin ack, complete) | 598 |
| `0x04` → `0x07` (begin ack, **abort**) | 46 |
| nothing at all | 25 |
| `0x05` alone | 7 |
| `0x04` → `0x0A` → `0x0F` (a commit after the complete) | 6 |
| `0x04` → `0x0E` → `0x0A` (a **delay** record mid-cast) | 4 |

**645 of 687 casts are exactly two records**, which is what we send: `SendBehaviorEchoAck` then the form-`0x0a`
completion, and `CastInterruptAbort` for the `0x07` variant. The "7 to 14 per cast" of #769 was every entity in a
busy live playfield — an NPC's `Combat:Unarmed` swings, a `Limbo/Death`, and the ambient `BccAction` burst a dozen
unrelated NPCs share one timestamp for.

**The cooldown (#768) has the same shape.** Once each `DispatchLockTemplate` is attributed to a cast rather than a
window, retail locks **every completed cast of a spell that has a recast** — 102/102 buffs, and 100/101 nukes —
with two exemptions. `SpellCooldownPolicy` is that rule:

- **A recast of 1.0 s or less does not lock.** `Fires of Gehenna (Rank 1)` (recast 1.0 s) was cast **93** times
  and locked **zero** times; the shortest recast retail *was* seen to lock is 2.0 s, over eight distinct spells,
  and nothing in the corpus sits between the two. ⭐ This also re-reads the observation #767 took as evidence that
  the recast runs from the cast BEGIN — `Smite (Rank 1)`, a 2.0 s cast with a 1.0 s recast, re-requested 2.43 s
  after the begin. That is not a timer starting early, it is a button that was **never greyed**.
- **A pet-POINT summon does not lock.** The necromancer's eleven `Undead Minion:` / `Deathless Acolyte:` /
  `Dread Archmage:` spells all cost pet points and all state a 22.0 s recast, and across 28 captured casts retail
  sent no lock at all — while `Ancient Soul`, which summons too but costs no points, locks on 7 of 7. The
  discriminator is `SummonPointCost`, not "is a summon".

⚠️ **The body carries the WHOLE recast, not what is left of it.** 40 of the 43 spells the corpus locks read
exactly `RecastCentiseconds / 100`: `Flesh to Worms` is a 1.2 s cast with a 6.0 s recast and every lock reads
6.00. We used to subtract the elapsed cast time, which got `Hellfire Breath` (a 2.8 s cast with a 2.0 s recast)
wrong in both directions at once — it sent **nothing**, because the recast had already elapsed by the time the
cast finished, where retail sends a full 2.00. (The partial values `LockTemplateCodec` documents are the RESTORE
shape — a relog re-stating time left on a running lock — not the cast shape.)

## Starting gear (#220)
A brand-new beach character is seeded its slave outfit in `CreateFlowService.SeedStarterEquipment`
(`WorldEntryService.cs`), from the `StartingData` constants harvested from the `*create*` captures
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

## A cast's damage is the SPELL's, not the level's (#395)

`CombatResolver.ResolveSpellDamage` was `20 + level*5` and ignored its `spellId` outright, so every spell a
character owned hit for the same number and the only thing that could separate two of them was the caster's gear.
The per-spell power was never missing from the data — it is the **health magnitude the spell's own RDB template
states**, the same rows `SpellStatEffect` already carried for heals, with the sign the other way round:

| spell | stored on stat 27 | at the vitals ÷100 scale |
|---|---|---|
| `Ice Strike (Rank 2)` | `-4300 .. -5000`, ramping to `-7100 .. -8260` over levels 20-34 | 43-50 → 71-82 |
| `Ice Strike (Rank 6)` | `-16300 .. -18950`, ramping over levels 80-100 | **163-189.5** |

⭐ **The client itself renders the second row verbatim** — `--report spell-tooltip --report-arg 144315@80` prints
*"163-189.5 points of Health"* from our own decode — so the number is retail's, not a curve fitted to it. **2,149**
shipped spells carry one, which is what makes this per-spell rather than per-rank-of-one-spell.

`CombatService.StatedHit` resolves it (level BAND, then the ramp inside the band, then a roll across the stated
range like a weapon's min..max) and hands it to `ResolveSpellDamage` as the base term. Magic Damage (#399) still
adds flat on top and the spell-line feat boost (#590) still scales the finished number — the stated hit replaces
the level curve, it does not bypass the rest of the pipeline.

⚠️ **The placeholder stays** for a spell that states nothing, and that is still most casts in practice — an
unarmed swing's ability id, an NPC cast, any spell with no template row. A stated hit of 0 means "this spell says
nothing", not "this spell does nothing".

⚠️ **A PERIODIC row is the DoT engine's**, and excluding it from the instant path was a fix rather than a
narrowing: the period is what separates two mechanics stated on one stat (`Daggamalt`'s 405 HP at once against its
67.5 HP every second), **726** shipped spells state both, and every one of them was having its ticks applied as
extra instant magnitude at the moment it landed. The ticking half is `Spell.DotDelta` / `DotIntervalMs`.

### ⭐ The stated hits reproduce retail at Tortage levels, to within 1%

`StatDamageIIR` names the spell that caused it (`spellBaseId`, the third tail word), so the corpus can be joined
to the catalog per SPELL rather than per damage school. Two spells carry enough player→NPC hits to have a median,
and both land on the number the template states for the caster's own level — **unmitigated**:

| spell | ramp | our mid-roll at the caster's level | retail median | n |
|---|---|---:|---:|---:|
| `Flesh to Worms (Rank 1)` | 480→1530 over levels 2-24 | **720** (level 5-6) | **723** | 233 |
| `Ice Strike (Rank 1)` | 3330→4100 over levels 7-19 | **3780** (level 9-10) | **3752** | 201 |

Both casters' levels are independently known from the same sessions, so the level is not fitted. The stated
minimum shows up too: `Flesh to Worms`'s smallest observed hit is **489** against the 480 its level-2 endpoint
states.

⚠️ **The same join says the model is INCOMPLETE at level 80.** Five spells cast by the level-80 necromancer all
land at a consistent **≈4.7×** their stated hit — `Ice Strike (Rank 6)` 4.1, `Pestilential Blast (Rank 6)` 4.7,
`Gangrenous Stench` 4.9, `Deathly Blizzard (Rank 5)` 5.0, `Flesh to Worms (Rank 5)` 5.2 — against base
magnitudes that differ by 3×. A constant *additive* term would not do that (the implied addend ranges 23k-66k);
a per-CASTER multiplier does, and the spread is what a roll across each spell's own stated range produces. So
there is a gear/level-driven multiplier on top of the stated hit that we model as a flat addend
(`ResolveMagicDamage`), and it is worth ~4.7× to a geared level-80 and ~1.0× to a Tortage character. Open work
tracked in Gitea #924.

What a 2026-09-03 pass established about that multiplier, so it is not re-derived:

* ⛔ **The client never computes a spell's hit.** The magic-damage accessor `FUN_01118ce0` has exactly four
  callers, and all four are the character sheet's per-school "Magic Damage" rows (stats `0x36C`–`0x36F` / `0x9E`,
  rendered through `%s` with the LDB-634 labels). No tooltip branch reads it, so a tooltip on a geared character
  shows the raw stated hit — the multiplier is server-side and the client cannot state it.
* **Its inputs are not on the wire either.** Across every level-80 capture the only caster stat pushed is
  `MagicRating` (`0x39B`, 317.00); `SpellDamageMultiplier` (`0x218`), `BaseSpellDamage` (`0x411`),
  `MagicDamageModifier` (`0x35D`), the school ratings and the attributes never travel, and the snapshot sheet is a
  template (Intelligence 10 at level 80).
* **RDB field 49 `Spell Damage Coefficient` is not a multiplicative coefficient**: it runs 65 / 61 / 26 / 23 / 20
  on Ice Strike R6 / Deathly Blizzard R5 / Gangrenous Stench / Pestilential Blast R6 / Flesh to Worms R5 while the
  observed ratio runs the other way (4.1 → 5.25). Read additively — `stated + R × coeff` — it narrows the implied
  addend from a 3× spread to 849…1148 display units of `R`, which is suggestive of "a share of the caster's magic
  damage" and no more: one caster, so `R` cannot be separated from anything else.
* **The 4.7× medians are mixtures.** Ice Strike's 105 hits cluster at ~45k / ~68k / ~88k (2.5× / 3.9× / 5.0× the
  stated mid, with a ~2× tail at 130k–210k); Deathly Blizzard's sit in one band. Per-target populations, not one
  ratio.
* ⭐ **`DPSHit` (53013) spells are deterministic at 80** — `Curse of Blood` lands 764.80 on 46 of 79 hits and
  `Glacial Tomb` 585.72 on 38 of 56 — and read as `Value × K`: 3.00 × 254.93 (unholy) and 2.50 × 234.29 (cold), i.e.
  one constant per SCHOOL that is not any pushed stat. Whichever server-side number that is, it is the same one
  the multiplier above is made of, and a second level-80 caster is what would separate it from the level.
* ⭐ **And the multiplier FACTORS** (2026-09-03, second pass). The unholy-to-cold ratio is **1.088** on the
  deterministic DPSHit pair (254.93 / 234.29) and **1.082** on the direct-damage medians (unholy mean 4.93×,
  cold mean 4.55×, across five spells whose bases differ 3×); and direct-to-DPSHit is **1.93** for unholy and
  **1.94** for cold. Two families, two schools, and the same two numbers come out of each cut — so what the
  server applies is a product of a per-SCHOOL caster term (the same one in both families, ≈1.085 apart between
  unholy and cold on this character) and a per-FAMILY term (direct ≈1.94× what a DPSHit element gets, whatever
  the school). That is the shape a second caster has to reproduce: the 1.94 should hold and the 1.085 should move
  with gear. Read additively instead (`Value + coeff × R`, coefficient 100 on both DPSHit spells) the pair gives
  `R` = 464.80 unholy / 335.72 cold, which is the same 1.38 spread — the two readings are not separable on one
  caster, and neither is modelled.

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

⚠️ **`Spell.DotDelta` is a captured `StatDamageIIR` delta, so it is stored in WIRE units and must be converted
before it is spent.** Everything it meets in the simulation is display: `EntityVitals.Health` is the ÷100 vitals
value, and the melee curve is converted at its point of use for the same reason (#572). `WorldSimulation.DotEffect`
does the conversion once, where a spell row becomes a live effect, so the health application and the wire cannot
disagree; the stored column stays wire so its captured provenance stays readable. Unconverted, a DoT hit 100×
too hard — and harder than the melee bug did, because melee is a per-level CURVE (bounded by the mob's level)
while a DoT is a flat constant that ignores both level and pool: `Rend` at −54/1.5s emptied a level-1 player's
88-point pool in three ticks, and the corpus reaches −258,352 (`Chill (Rank 5)`) (#693).

⭐ **What that delta does at the other end: it is the ONLY field that moves a health bar.** The client's apply is
`StatDamageIIR_t` vtable slot 2, `FUN_008d5960`, and its whole stat write is one line —

```
SetStat(statId, GetStat(statId) + record.delta)      // 008d5b2d..008d5b5d
```

— run unconditionally once the carrier resolves to a `CoreCharacter` (the `0xC879` branch does the same
subtraction on a `Destructible`, which is a harvest node chipping down). Three consequences, each of which was
guessed the other way at some point:

* **`statValue` is never read.** The client recomputes from what it already holds. It is still stated because
  retail states it — and because it is the only thing that pins the delta's scale.
* **`spellBaseId` is read in exactly one place**, `if (deathReason == 10)`, and nothing downstream depends on it.
  ⛔ A record naming spell 0 is **not** voided; the `GetMagicSpell failed to load spell with id 0` seen in a client
  log beside one comes from one of the ~80 other callers of that function. A health record still names the real
  ability, because retail does — see `StatDamageSpellIdTests` — but that is fidelity, not a client requirement.
* **The delta is on the ×100 vitals scale**, like the stat it lands in. The corpus pins the two fields to each
  other rather than to an absolute: `statValue` equals the same handle's `StatUpdateIIR` stat 27 **exactly** (diff
  0 across twelve handles, up to 92 records each) and moves by exactly `delta` across 1,015 consecutive pairs.

⛔ **That last one is #902.** A display-scale delta against a wire-scale pool moved a bar by a hundredth of the
damage: a 12-damage hit took a 20,200-wire bar (202 HP) to 20,188, redrawn as 202. Damage landed, the character
died, and the bar never moved. `RegenIIR` had the ×100 right, so the only correction the client ever received was
upward and the bar climbed **past its own max** — the second symptom, and the same root cause. Scaled once in
`CombatService.SendStatDamage`, beside the health it has to agree with; gated by `StatDamageWireScaleTests`.

⚠️ `StatUpdateIIR`'s own apply (`FUN_007a00e0`) is a plain `SetStat` loop over every `(statId, value)` pair, with
**no vitals filter and no own-handle special case** — worth stating because the opposite was assumed from a client
log that showed our stat 27 arriving and no `CMsg_StatChange` following it. `CMsg_StatChange` is a higher layer;
the absence of a line there is not evidence that the setter was skipped.

`deathReason` — the word after the two zeros, at `+0x40` — turns out not to be padding either: the client writes it
straight to stat `0x152` (`CharacterStatId.DeathReason`), and it also drives a virtual call on the target when
non-zero. The corpus carries 0 (×2,201), 5 (×127) and 10 (×52) on health records, so the codec's earlier "0 in
every captured record" was measured on a population that excluded health.

**A DoT's cadence is measured, not read.** The record carries no period, so consecutive ticks of one spell on
one target are one tick train and the gap between them *is* that spell's interval — harvested by the
`dot-effects` importer module into `Spell.DotIntervalMs`. 55 of the 84 DoT spells in the corpus have one;
values are round half-seconds from 500 ms to 11 s, peaking at 1 s (16 spells) and 2 s (14). A spell with no
measured interval ticks at the 2 s fallback, which is the median of the measured set.

The other 29 are not unmeasured because nobody looked — the estimator REFUSES them (fewer than three usable gaps,
or gaps too ragged to establish a cadence), and refusing is the right outcome: a wrong interval silently misprices
every tick of that spell, while no interval falls back to a documented default. Re-running the module widens this
as the corpus grows, so **new combat captures are what moves it, not new code** (#524).

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

⚠️ **The known limit is the HARMONIC case, and it is not fixable from gaps** (#524). A spell whose ticks are mostly
unseen has no short gaps left to reveal its fundamental, so its gaps concentrate on a *multiple* of the true
period — indistinguishable from a genuinely slower tick. `Aura: Fire Weave` is the one train in the corpus that
looks like this: 17 gaps clustering at 0.92 / 1.23 / 1.53 / 1.84, spaced by roughly 0.3 s.

**A harmonic/GCD estimator was tested against a null and rejected.** Sweeping candidate periods is degenerate on
its own — the smaller the candidate, the better it fits, because every gap is within *p*/2 of some multiple — so
the residual has to be read against chance: residuals of unrelated data are ~U(−*p*/2, *p*/2), i.e.
rms = *p*/√12. Against that null the best fit these 17 gaps admit is a ratio of **0.656** (at 155 ms), while the
identical sweep over random gaps spanning the same range gives a median best of **0.701** and reaches **0.577**
in 5% of trials. So the observed fit sits inside the noise: **the gaps contain no recoverable fundamental**, and a
harmonic estimator would be fitting the sweep's own degeneracy. The measured 1500 ms stands as the only supported
value — the 500 ms an earlier hand scan recorded is not supported by these gaps either, nothing sitting near it.

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

### Healing: the INSTANT half of a spell's effect (#702)

A spell's `SpellStatEffects` rows carry two different things, told apart by the record's KIND: the **lasting**
stat deltas `BuffStatEffects` owns (+62 Hit Rating for 20 minutes) and the **instant** pool moves
`SpellPoolEffects` owns (a heal). Only the first was ever applied, which meant healing did not exist — 52
class-castable spells and every health potion in the catalog showed an icon and moved no bar.

| kind | | unit |
| --- | --- | --- |
| `Hit` 53002 · `DPSHit` 53013 · `InterpolatedHit` 53219 · `MagicHit` 53256 | a flat pool move | the ×100 vitals scale |
| `HitPercentage` 53007 | a percent of the pool | a plain percent |

⭐ **`HitPercentage` states its own unit through a carrier's name.** The dev spell `System Spell: Fire AOE damage
25% of Life` carries **−25** — so that kind is a percent of the pool rather than a value at the stat's display
scale, which is what tells the two families apart without a fit.

The stat says which pool (27 health, 506 mana, 504 stamina — all three ×100 by the client's own divisor table),
and the sign says heal versus damage. `Lifestrike`'s five ranks run 3400 → 33700, i.e. 34 → 337 HP.

⚠️ **It fires ONCE, and that is the whole difficulty.** These rows sit beside the lasting deltas on the same
spell, and the buff paths that touch a spell repeatedly — the relog restore, the expiry sweep, a re-apply that
refreshes a duration — must not carry them. So it hangs off `ApplySpell` ("a spell just landed on someone") and
`UseConsumable`, never off `SendMagicBuffIir`, which all of those go through.

⚠️ **`-100000000` is a drain SENTINEL, not an amount** (`Demonic Puppet` carries it); applied literally it would
kill anything it touched.

⚠️ **`MagicHit` carries no stat at all.** Its key list is `[39 Value, 71]`, so it can never name a pool. It used
to appear in the table under stat 1 `HealthMax` with the `Venomstrike`/`Banestrike` rank ladders, which read as a
max-health drain — that "stat" was key 39's own value, read positionally. See the key-addressing note below.

A consumable's heal sits behind the same "already active → no-op" guard the buff does, which is capture-derived
retail behaviour and, since all four pool-moving potions carry a 60 s duration, amounts to a 60-second potion
cooldown.

### ⚠️ A stat effect is addressed BY KEY, and 1,191 rows were not (#701)

`SpellStatEffects` is `(SpellId, StatId, EffectKind, Amount)`, and it was filled by the POSITIONAL pair view -
payload words 0 and 1, *whatever keys they belong to*. That is only `(stat, amount)` for the twelve kinds whose
key list opens with key `0`. For the other 41 it silently filed another field's value as a stat id:

| kind | key list | what its "StatId" really was |
| --- | --- | --- |
| `StartParticleEffect` 53238 | `[209, 86, 138, 28, 29, 30]` | the BYTE LENGTH of a string (key 209 is text) |
| `NanoResistance` 53162 | `[152, 39]` | key 152 — reported as `Team` / `State` / `SellModifier` |
| `SetSpellLineRecastTime` 53077 | `[1, 39]` | a spell LINE |
| `MagicHit` 53256 | `[39, 71]` | key 39's own VALUE |
| `Set` 53026 | `[55, 0, 39]` | key 55 — its stat is one slot along |

**The magnitude is the first key AFTER the stat in the type's own key list**, which is one rule that gets all
twelve right: key `39 Value` on `Modify`, key `2` on `Hit`, key `168` on `InterpolatedHit`, key `174` (the ramp's
low endpoint) on `InterpolatedModify`, key `70` on `AddAbsorb`, and key `39` again on `Set`. It is also exactly
what the positional read was doing by accident wherever the stat comes first — so the change altered **no value
that was already right**.

Measured over the shipped catalog: **1,191 rows removed**, **427 ADDED** — the recovery half matters as much,
because a stat that is not at slot 0 was invisible before (`Set` 155, `SpellLineStatModifier` 149,
`SpellStatModifier` 123). Nine rows also changed KIND: the `Yuggoth Rising` records that read as "kinds"
`1000, 2000 … 9000` were an unregistered element being read positionally, and are `Modify` with the same amounts.

⚠️ **A second bug the same fix exposed.** The delete was scoped to the spells that still HAVE effects, so a spell
whose list became empty kept its old rows for ever — a change that REMOVES bad data would have left it in place.
It is scoped to every record PARSED now, which is the same lesson `RdbFeatImporter`'s effect sync carries.
[V3: SpellPoolEffectTests.EveryStoredRowsKind_ActuallyCarriesAStat]

### A spell can CRIT, and the client's own catalog says which and how hard (#633)

The spell path had no critical hit at all: `ResolveOffensiveCast` computed a number and applied it. Two RDB spell
fields answer both halves of that, and both are stats the client's tooltip renderer divides by 100
(`StatDisplayScale.Percent1`), so both are **hundredths of a percent**:

| field | client's name | records | modal value |
| --- | --- | ---: | --- |
| `147` | `SpellCriticalChance` | 1,224 | **250** = 2.50% |
| `129` | `SpellCriticalDamage` | 1,228 | **5000** = +50.00% |

⭐ **That damage figure corroborates a constant this server had already assumed on its own authority.**
`CombatResolver.BaseCritDamagePercent` is 150 — a crit hits for 1.5× — and the file says outright that it is
provisional and "waiting on an in-client combat log". The client's own spell table states +50% on 607 of the 1,228
records that state anything at all. Two independent directions, the same number.
[V3: SpellAreaAndChannelTests.TheModalSpellCritDamage_IsTheBaseCritThisServerAlreadyAssumed]

The caster's own Critical Rating adds to both on the same curve a swing uses, and the chance takes the same
`MaxCritChancePercent` ceiling. Like a melee crit it is **not** a distinct wire flag — retail conveys a crit by the
larger number alone (the `type` field is the attacker's damage school), so nothing new goes out.

⚠️ **A spell whose record states no chance does not crit**, rather than falling back to the melee base. That base
exists because everyone crits sometimes with a weapon; extending it to the 20,278 records that state nothing would
give every one of them a 5% crit on no evidence, when the whole value of the field is the client saying WHICH
spells crit. `Fires of Gehenna` and `Shockstrike` do; most of the catalog does not.

⚠️ Roughly half the carriers hold a bare `1` on one or both columns, and it is deliberately **not** special-cased.
Read literally, a 1 is a 0.01% chance of a crit worth +0.01% — a spell that does not meaningfully crit, which is
self-consistent and is what a heal or a utility spell should look like. Special-casing it would be inventing a
sentinel the client does not have.

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

- a **level-GAP penalty** - see below; modelled since #488.
- **ranged mobs deal ~0.2 of same-level melee** even at gap 0 - see below; modelled since #671.

### A SHOT is a different record from a swing (#676)

A ranged NPC's attack is `Combat:Ranged` (`BehaviorId` 0x10), not `Combat:Melee` (0x0F), and it differs from a
swing in three ways. Until #676 our archers sent the melee ack - a sword swing played for a bow, and only the
opening half of the action.

| | melee 0x0F | ranged 0x10 |
|---|---|---|
| records per attack | **one**, seq 0x0004 (1,957 of 2,117) | **two**: seq 0x0004 BEGIN then 0x000A COMPLETE (212 / 196 of 424) |
| result-block tail | 81 bytes with one result | **82** - the same block plus one trailing `0x00` |
| `bcc_action_id` | ~0xBEEC (48876), per-NPC jitter of a few | 77813-77817, modal 77817 |
| combo rate | 427 of 1,504 acks (28.4%) | 57 of 418 (13.6%) |

The two records of one shot carry the **same** swing counter, the same combo id and a byte-identical tail, which
is what identifies them as one attack rather than two. `Combat:Unarmed` (0x11) is the melee shape and carries no
combo at all - 0 of 769.

⚠️ The complete may also carry `anim_random` (CommandMessage field 4). It is on only 26 of the 196 captured
completes, so we omit it: that is sending a subset of what retail sends, not inventing a value.

⚠️ The tail itself needed no new decode - #536/#661 had already accounted for all 3,687 captured attack tails
across all three behaviours, **including** the ranged trailing byte. What was missing was only the builder.

[V3: BehaviorAnimationsTests.ANpcShot_MatchesCapture - rebuilds both records of one captured shot byte-exact]

### The level-GAP penalty is a cliff, not a slope (#488)

A mob well below the player barely scratches them, and the drop happens over **two levels**:

| gap (player level − mob level) | measured `x model` | evidence |
|---|---|---|
| 0–1 | 0.77 – 1.57 | 11 beach templates, 700+ hits |
| 5 | 0.96, 0.99, 1.35 | 43 hits, 3 templates at level 75 |
| **6** | **0.42** (11 hits), 0.76 (1) | template 15441 at level 74 |
| 7 | 0.11, 0.13 | 14 hits, 15437/15441 at level 73 |
| 8–10 | 0.10 – 0.14 | 26 hits, 15436/15441 at level 70–72 |
| 20–21 | 0.11 | 9 hits, Striped Boar at level 59/60 |
| 27+ | 0.03 – 0.11 | drifts slightly lower; one floor covers it |

`CombatResolver.LevelGapPercent` reproduces that as `100 / 42 / 11` percent at gap `≤5 / 6 / ≥7`, and
`ResolveNpcAttackDamage` takes the DEFENDER's level for it.

The clincher is one template against one player: `Hired Bodyguard` (15441) versus the same level-80 necromancer
deals 729 at level 70, 732 at 71, 938 at 73, 3125 at 74 and 7402 at 75. Ten-fold, across five of the attacker's
own levels, with the defender, its gear and the attacker's template all held fixed. That is also what rules
**armour** out — a level-normalised mitigation term cannot differ 8× over three levels of attacker — and the
same fight carries the client's combat log, so the wire numbers are confirmed against 7–14 and 67–92 displayed.

⚠️ **Measured, not recovered.** Retail decides this server-side and the client carries nothing to read: RDB
1000516 has only `TWK_TargetPerLevelArmor` / `TWK_TargetPerLevelProtection` / `TWK_Consequence_XPGainDecayRate`,
and it is undocumented publicly. The two plateaus are well sampled; the step between them rests on one
template's 12 hits.

⚠️ **The mirror case is deliberately NOT modelled.** A mob *above* the player measures 5.15 and 6.99 at gap −19,
which points the right way but is one hit each — and a higher-level mob already hits harder through `level × 100`.

⚠️ **Named BOSSES sit ~3× above this table** and ranged mobs a fifth below it. Both are per-archetype and
independent of the gap, so neither is evidence against the curve. The boss factor is measured but **not
modelled** — see below.

Still thin: caster NPCs above level ~10, and any capture pinning the player's own Critical Damage Rating -
Gitea #479.

### The "elite" outliers were mostly a MEASUREMENT FAULT (#671), and the rest were the LEVEL BAND (#479)

Four attackers read 5–27× the curve and looked like an elite archetype. Two of them were an artifact of the
report, and finding that is what the elite question actually turned on. The cluster that survived that scrub
then turned out not to be elite either — see the level-band table below.

`--report npc-damage` took **the capture's MAXIMUM player level** as "the player". A capture is not one player:
any level-80 stranger who walks past is spawned into it, and the max then stamped `pc lvl 80` onto every mob in
the file. Attributed instead by the hit's own **victim handle**:

| attacker | before | after |
|---|---|---|
| `Wild Boar` L25 | gap 55, **19.58×** | gap 1, **2.15×** |
| `Vanir Sentinel` L26 | gap 54, **14.65×** | gap 0, **1.61×** |

Both ordinary. The "bimodal within one template" reading of `Vanir Sentinel` goes with it — its 25 hits are one
mode at 4191 with a single stray 127, not two mechanics (`--report npc-damage` now splits an attacker's hits at
their largest ratio gap, so that question is answered by the report rather than by eye).

#### ⛔ …and then BOTH rows turned out not to be NPC→player damage at all (#479)

A second measurement fault, one layer under the first. The NPC→player direction was gated on the target being
**not a known NPC** — and an *uncaptured* entity satisfies that as easily as the player does. It is the same
defect the SPELL table carried until `b1b6b6be`, left in the melee half.

Gated instead on the target being a **real player** (a handle that received a `FullPlayerCharacter` snapshot):

| attacker | hits before | hits after |
|---|---:|---:|
| `Wild Boar` L25, median 5385 | 9 | **0** |
| `Vanir Sentinel` L26, median 4191 | 25 | **1** |
| `Tortage Pirate` L5 / L6 | 4 / 19 | **0 / 0** |
| `Brutal Pirate` L5 | 128 | 83 (`x model` 0.95 → **1.04**) |

⚠️ **The bimodal sentence above is exactly inverted.** `Vanir Sentinel`'s "single stray 127" is the ONE hit that
was aimed at the player; the 24 at ~4191 were never in this direction. The same fix removes two player→NPC rows
built the same way (L25 13 hits, L26 32 hits) and a level-11 pair reading 84,653.

So neither template is elite, ordinary, or bimodal — they are not evidence about NPC→player damage in either
direction, and `Wild Boar` should not be cited as an elite outlier anywhere.

⚠️ **And the whole `x model` column had been 100× wrong since #572.** That change moved
`ResolveNpcAttackDamage`'s return to display units while captured damage is a WIRE number, so a level-1
`Pillager` read as "139× the model" instead of 1.39×. `CombatResolver.NpcAttackDamageWire` is the wire-unit
entry point the report scores against; the display value is that ÷ the vitals scale.

Of the other two, `Gaud Guardsman` (23.97×) is a **city guard**: 304,944 health and `Normalset = 32`, exactly
like `Tarantia City Guard`, `Poitain Guard` and every other town guard. It is not an ordinary combat NPC.

**What survives is not a boss row — it is the TOP OF THE LEVEL RANGE** (2026-08-29, #479). This section used to
end on "a ~3× boss factor, blocked on the client's own rank tag, which is Gitea #508's column". That tag now
exists (`NpcStatCore.NpcType`, solved in #797) and it **refutes** the boss reading rather than confirming it:

| attacker | level | `NpcType` | `Normalset` | hits | `x model` |
|---:|---:|---:|---:|---:|---:|
| `Wharf Rat Thug` | 78 | **4** (no title) | — | 18 | 2.80 |
| `Wharf Rat Thug` | 79 | **4** (no title) | — | 13 | 3.00 |
| `Ravenous Crocodile Follower` | 80 | **4** (no title) | — | 32 | 2.72 |
| `Boros` | 80 | **4** (no title) | — | 19 | 2.74 |
| `Argus the Thief` | 79 | 2 (`MiniBoss`) | 3 | 20 | 3.53 |
| `Eerika of the Maidens` | 80 | 2 (`MiniBoss`) | 3 | 18 | 3.41 |

⭐ **Four of the six carry no nameplate rank at all**, and `Wharf Rat Thug` is plainly trash. An ordinary mob in
the band hits like the "bosses" do, so what the factor is about is the BAND. The two that really are `MiniBoss`
sit about 1.25× above the untitled ones — which is the size an elite premium would be, and is two templates.

And the comparison is clean rather than a gap artifact: **both bands sit where the gap penalty does nothing**
(gap 0–2 up here, gap 5 at level 75), so only the base curve is in play.

| attacker | level | `NpcType` | hits | `x model` |
|---:|---:|---:|---:|---:|
| `Hired Bodyguard` | 74 | 4 | 11 | 1.01 |
| `Zingaran Operative` | 75 | 4 | 10 | 0.96 |
| `Hired Bodyguard` | 75 | 4 | 19 | 0.99 |
| `Zingaran Stockpiler` | 75 | 4 | 14 | 1.35 |

So the per-level line holds to 75 and is 2.8× low from 78. **It is now shipped** as
`CombatResolver.TopBandPercent` — 280 % on levels **78–80 only**, the untitled median (2.72–3.00 over 82 hits)
rather than the median of all six, because under-hitting is the cheap direction to be wrong in.

⚠️ **76 and 77 deliberately keep the old line.** The corpus has no NPC→player hit at either, so the knee sits
somewhere in (75, 78] and placing it would be inventing. That is also what #479's remaining request narrows to:
**two levels**, not "high-level combat".

**Health was tested as an alternative BASE and rejected.** An elite's pool is what makes it an elite, so
`damage = k × the attacker's own max health` would explain "elites hit harder" with no archetype column at all.
Over the rows with ≥ 8 hits at |gap| ≤ 2 the two predictors are near-equivalent, which is not enough to move the
base of the damage model. ⚠️ The exact figures move as the corpus grows and as the direction gate above tightens
(the health spread now reads 0.54–5.37 % over 25 rows, median 2.06 %), so read them off a run rather than from
here. `--report npc-damage` prints the head-to-head so a wider corpus can re-decide it.

### A mob that SHOOTS deals a fifth of one that swings (#671)

Independent of the level gap, and visible at gap 0-1 where the per-level curve itself is verified. Measured by
joining `--report npc-damage`'s BY ATTACKER rows to each attacker's **main-hand weapon class**
(`Items.SearchCategory`, the client's own catalog value - never a name match, #244):

| weapon class | templates | hits | median `x model` |
|---|---:|---:|---:|
| **bow (30017)** | 7 | 127 | **0.36** |
| 1h edge (30011) | 16 | 488 | 1.01 |
| dagger (30015) | 10 | 122 | 1.02 |
| 1h blunt (30012) | 7 | 63 | 1.15 |
| 2h blunt (30014) | 3 | 75 | 1.00 |
| polearm (30016) | 3 | 289 | 1.39 |
| none (animals) | 19 | 388 | 1.15 |

The factor is taken from the three **well-sampled** bow templates rather than the median of seven rows of wildly
different weight: `Poacher Marksman` 8758 at 0.17 (31 hits), `Poacher Marksman` 13961 at 0.18 (18) and
`Bat Pict Archer` 937 at 0.21 (70) - 119 hits, three templates, two zones, all at gap 0-1.
`CombatResolver` uses **19 %**.

Every melee class sits at ~1.0, which is what makes this a *ranged* factor rather than a per-weapon damage
model. A weapon roll would not produce it: the bow's own catalog damage (819-5733) is **higher** than the 1h
edge's (505-2864) - the opposite of what is measured.

`NpcWeapon.IsRanged` decides it from the spawn's `Pos 6` item, and `WorldEntity.RangedAttacker` carries it to the
tick. All 258 captured bow placements are at `Pos 6` and none anywhere else, so the main hand alone decides.
Crossbow (30018) and thrown (30019) count too - neither appears on an NPC in the corpus, but they are the same
kind of weapon and the measurement is "a mob that shoots you deals a fifth", not "item class 30017 does".

⚠️ Applied to the mob's SWING only, not to its spell: the factor was measured on `DamageResultIIR` melee hits,
and it is the arrow that lands soft. ⚠️ And it cannot hold at the bottom of the range - a level-3 archer's fifth
is 0.57 of a display point, and the floor of 1 keeps a landed hit from reading as a miss.

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
(Gitea #479). One pcap plus one log gives both sides of
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

## The rating quad is `400 × level − 300` (#685)

`AttackRating` (0x398), `DefenseRating` (0x39A), `MagicRating` (0x39B) and `MagicalResist` (0x39D) are one
number, and it is a **formula** rather than a table: level 1 is 100 and every level adds 400, so level 80 is
31,700. `CombatRatingCurve.For(level)`.

Measured over the whole corpus by sweeping every captured `StatIIR` (`0xF98E10B3`) carrying one of the four ids:

| | |
|---|---|
| records | 2,100 (525 of each id — they always move together) |
| captures | 31 |
| characters | 13, across 6 classes |
| distinct values | 8 — 100, 500, 900, 1300, 1700, 8900, 9300, 31700 |
| levels | 1–5, 23, 24, 80 |
| records off the line | **0** |

The low end is a line rather than five coincidences because three beach captures walk a single character 1 → 5 a
level at a time, and the per-character progressions are monotonic throughout. It is class-independent — a
soldier-guardian, a rogue-assassin, a necromancer, a demonologist and a Herald of Xotli all sit on it.
[V3: CombatRatingCurveTests.MatchesEveryCapturedValue]

⚠️ **This is the BASE**, the same way `ClassAttributeBase` is. Gear and buffs plainly move real combat ratings in
retail, but no capture isolates one — every character measured was wearing what it was wearing — so a future
contribution adds *on top* of this rather than replacing it.

⚠️ **It is not a spellweave record.** These four were modelled as the necromancer weave's "fire stages" with the
level-80 caster's 31,700 mistaken for a burst constant; 20 of the 31 captures that carry them contain no weaving
at all. See [`combos.md`](combos.md).

`SnapshotStatSheet` authors the quad from the character's level, which is what took the four off
`SnapshotStatSheetAuthoringTests`' not-modelled list.

[V0: what makes retail RE-send the quad. It arrives in bursts of four with irregular 5–35 s gaps — 32 bursts in
one 15-minute capture — mostly alone in their own packets, so it is event-driven rather than a timer; a stat-sheet
recompute is the obvious candidate. The value is settled, the cadence is not.]


## A live buff survives a ZONE CHANGE, because its entity does not (#907)

`PlayerRegistry` is keyed by **connection**, and a map change tears the world connection down
(`ClientDisconnected` → `RemovePlayer`). So the re-entry builds a brand-new `WorldEntity` and everything the old
one held that is not persisted is dropped.

⚠️ For a debuff that is not cosmetic. With no `ActiveBuff` entry left there is nothing for `TickBuffExpiry` to
expire, so its removal twin is never sent — and the client keeps its own icon list across a map change, so the
icon sits at **0s for ever**. The reported symptom is exactly that shape: one stuck `Butcher`, the one live at
the instant of the teleport, with every earlier one having expired normally.

⭐ **Retail carries live effects across the change rather than ending them at the door.** In
`xxx_mage-necromancer_login_play3`, `RequestACGTeleport` (frame 4732) is followed at 4749 by five apply-form
`MagicBuffIIR` records re-stating the character's live buffs on the far side. So carrying is the retail-shaped
fix; ending them at the boundary would not be.

`CarriedBuffHold` (World/, owned by `TeleportService`) holds them by **character id** — the same reason
`PlayerDeathService`'s self-resurrection offer is keyed that way: the state has to outlive the connection that
owned it. The tear-down carries only on `Session.IsTeleportingOut`, so a real logout still ends its buffs and
the client rebuilds its icon list from the next login's snapshot; the world entry restores them with `TryAdd`,
so an entry burst that re-applies a buff of its own wins over the staler carried copy. Not persisted — a server
restart drops them, the same narrowing the resurrection offer has.

⛔ Already-expired buffs are dropped rather than carried: the tick removes an expired buff and sends its twin, so
one still present at tear-down is one the tick has not reached yet, and carrying a dead one would re-arm an icon
the client has already been told about.

## An observer learns a streamed-in entity's buffs from the SPAWN (#677)

There is no fan-out to design. The spawn's post-IIR carries a count-framed `BuffRecords` list, decoded and
round-tripping, and it is populated on real spawns: **1,064 of 17,717** parsed spawn bodies carry at least one,
**1,471** records in all. So retail attaches an entity's active buffs to the record that introduces it.

Each record is `[spellId][kind][sourceHandle][stamp][timerTotal][timerRemaining][word6][casterLevel]
[targetClass][targetHandle][5-word trailer]`, and two of those fields close the design questions #677 opened:

- **`TimerRemaining`** — the ticket reasoned that an observer arriving mid-buff has no apply event to count down
  from, so an *absolute* expiry stamp on the server's uptime clock "earns its place", and that the observer's
  client would need `ServerTime_c` live from a `DispatchPong` reply. None of that is required: the record states
  the remaining time directly. It is a real remainder, not the total under another name — it is `<= TimerTotal`
  in every captured record and differs from it in many.
- **`SourceHandle` / `TargetHandle`** — the attribution a separate fan-out would have had to carry somewhere.

What is left of #677 is therefore only the server-side half it also named: NPC-cast buffs are not tracked across
the streaming boundary, so today there is nothing to put in the list. The WIRE side needs no decode.
[V3: SpawnBuffRecordTests.RetailAttachesBuffsToTheSPAWN_NotAFanOutAfterIt]

## An AREA is a chain, not a blast (#772/#776)

A spell's `SpellAreaEffects` rows say "apply spell `EffectSpellId` to up to `MaxTargets` entities within
`RadiusMetres`", optionally on a `PeriodCentiseconds` cadence. The row names **another spell**, so the mechanic
is recursive by construction, and `Gangrenous Stench` is the whole of it in three records:

| record | duration | what it states |
| --- | --- | --- |
| `221070` the CASTABLE | 25 s | two areas, both 7 m / 12 targets, applying `221069` — one at cast (period 0) and one every 4 s |
| `221069` the EFFECT | 9.5 s | one area, 4 m, applying `221068` every 3 s |
| `221068` | 1 s | the damage record at the end of the chain |

The client's tooltip states both levels ("Area Of Effect: … 7.00m radius up to 12 targets" and "Effect over
time: … 4.00m radius every 3 seconds"), so the tiers are the client's own reading, not ours.

### A chain edge is not always an area (#783)

`SpellAreaEffects` carries a **second kind** of link, and the row's `Kind` is what separates them:

| kind | means | area columns |
| --- | --- | --- |
| `AreaFormula` 53087 / `FilteredAreaFormula` 53088 / `ConeFormula` 53259 / … | apply the effect to everyone in a radius | radius, target cap, period |
| `CastNano` 53051 | **also run** the effect, on the same subject | all zero |

`Gangrenous Stench`'s `221069` states one of each: the 4 m / 3 s area above, **and** a `CastNano` to `3963058`
*Physical Wrack*, whose four `-1000` rows are the "-10% Slashing / Piercing / Crushing / Poison" the in-client
tooltip shows. Until #783 the `CastNano` half was read only for ITEM templates (`Item.OnUseSpell`) and dropped on
spells, so `3963058` was imported and **unreachable** — no `Spells.EffectSpell` and no area row pointed at it, and
the spell's headline effect did nothing. 26 spells reference it and 6 could reach it; the other 20 all reach it
this way. The catalog is 10,676 chain rows.

⚠️ A consumer branches on the **kind**, never on the radius: read as an area a chain edge sweeps a 0 m circle,
which is silently nobody. `SpellAreaCatalog` splits on the kind before it splits on the period.

**Applying an area is two jobs, and only the first existed.** `CombatService.ApplySpellArea` dealt the effect
spell's damage to everyone in range and stopped there, so the effect record never LANDED on anybody:

- no damage-over-time on the victim (`221069` is a 9.5 s record);
- none of its stat effects (`Modify 840 = -5%` damage, the three `TempChangeSkill` invulnerabilities);
- and the chain stopped dead, because nothing was holding `221069` for its own 4 m / 3 s area to fire from.

So `BuffTickService.ApplyAreaEffect` owns the whole operation: `ApplySpellArea` answers WHICH entities were
hit, and each of them then gets the effect itself — the `ApplySpell` carrier so the client draws the icon on
the mob, plus a tracked `ActiveBuff` carrying its duration and DoT.

⚠️ **The second job is worthless without a tick behind it.** `TickBuffExpiry`, `TickDots` and `TickAuras` all
walk `Players`, so a buff sitting on a MOB had nothing to expire it, bleed it or emit its areas — which is the
never-expiring orphan icon #274 fixed on the player side, one entity over. `TickNpcBuffs` is that half: it
services a small registry of the non-player entities currently carrying a buff (written where one lands,
pruned as they empty, die or despawn), and

- expires with the retail end twin, so the icon goes;
- bleeds the DoT through the same `ApplyNpcDamage` a cast uses, so a tick that finishes the mob runs the whole
  kill lifecycle and credits the player who applied the debuff;
- emits the effect's own areas, **centred on the mob** — which is what makes the plague spread from whatever it
  is sitting on rather than from the caster.

⚠️ **The aura stamp is keyed by the HOLDER as well as by (buff, area).** Two entities carrying the same effect
otherwise share one stamp and each suppresses the other's cadence — with mobs in the picture that stalls a
spread at one victim however many it had reached.

⚠️ **Everything is attributed to `ActiveBuff.CasterHandle`'s player, all the way down the chain**, even where
the emitter is an infected mob: a bleed that lands the killing blow has to name a killer the XP/loot lifecycle
can pay. If that player has gone, the buff is dropped rather than left ticking for nobody.
[V3: NpcBuffTickTests.AnInfectedMob_EmitsTheEffectsOwnArea_CentredOnItself]

### Walking the chain (#784)

#783 imported the `CastNano` edge and stopped there, so `3963058` was reachable in the database and nowhere
else. Both places a spell LANDS now walk it, on the **same subject** the carrier landed on:

| landing | walker |
| --- | --- |
| a cast, a self-buff, a spell effect on the player | `BuffService.ApplyChainedSpells` (off `ApplySpell`) |
| an area's effect landing on a mob | `WorldSimulation.ApplyEffectToEntity` |

⚠️ **The walk needs a termination guard, and not defensively — the shipped catalog does not terminate
without one.** Over the 10,676 chain rows the graph contains **56 cycles**
(`145503 → 4940266 → 4220203 → 117806 → 145503` is one) and reaches a depth of **12**. `SpellChainWalk` is the
guard, and its two bounds answer different questions:

- the **visited set** terminates the walk, and is also the right semantics — the same buff landing twice on one
  subject is a re-apply, which `SendMagicBuffIir` already treats as a replace rather than a stack (#274);
- **`MaxApplications`** (32) bounds the fan-out. Closure sizes are a median of 2 and a p90 of 10, but the
  largest is 78 (spell `3475257`), and one landing does not get to emit 78 `ApplySpell` carriers. Truncation is
  logged naming the carrier rather than silently accepted.

Three things the walk deliberately does:

- **a zero-duration carrier still chains.** "No `ApplySpell`" is a statement about the CARRIER's icon (#767),
  not about whether it landed — an instant nuke chaining a 30 s debuff is the ordinary shape.
- **a victim already carrying the effect does not re-run it.** An aura re-emits its area every period; nothing
  new landed there, so re-walking would re-assert the chained debuff every tick.
- **a carrier with a separate `EffectSpell` walks BOTH records' edges.** They are two records with two element
  lists, and 232 carriers of that shape state edges of their own; the visited set collapses the 22 that chain
  straight to their own effect spell.

⚠️ **A chain edge's PERIOD is not honoured.** 486 rows carry a non-zero `0x2d6`, which on an area element is
the aura cadence — but on a chain edge it fits "re-run every N" and "run once after N" equally well, and
nothing has been read that decides. The edge runs ONCE when the carrier lands, which is the reading that cannot
over-apply; open in **#786**.
[V3: SpellChainWalkTests.AnEffectLandingOnAMob_AlsoLandsTheSpellItsChainEdgeNames]

## A chain walk applies at most ONE member of a stacking line (#813)

`SpellAreaEffects.Kind = 53051` is #784's *"when this lands, also run X"*, and two shapes in the data are not
that. A fatality put **all ten ranks** of `Furious Inspiration` on the killer's bar at once for 15 seconds, and
the walk's visit set was the only reason it stopped at ten rather than looping.

| shape | what it is | population |
|---|---|---|
| **rank LADDER** | each rank additionally chains the rank BELOW it, so entering anywhere applies everything under it | 116 edges / 62 carriers |
| **rank TABLE** | a nameless, duration-less line header carrying one edge per rank (seq 0 → rank 10 … seq 9 → rank 1) | 105 carriers |
| a real chain | the target is in a DIFFERENT stacking line — #784's actual mechanic | 10,432 edges |

⭐ Both collapse under one rule, because **a stacking line is a mutual-exclusion group**: a walk that applies two
members of one line has produced something the client cannot represent. `SpellChainWalk.TryEnter` claims the
target's `GuiStackingLine` alongside its id.

⚠️ **Keying it on the CARRIER's own line is not enough**, which is the trap here: the table's header (299074) is
in line **0**, so it shares no line with its targets and the ladder rule alone leaves all ten edges running.
Only claiming the *targets'* lines catches it.

### ⭐⭐ Which member wins IS a decode — the criterion run selects it (#861)

That was filed as an open pick: the walk took arrival order, i.e. the table's sequence 0, which for line 44235 is
rank 10. Level scaling was refuted as a rival (97 of the 105 tables have every target at the same level, so a
level has nothing to choose between). What nobody had looked at was the elements' own **criteria** — the
`[id][value][operator]` run every GameAction element carries — because the importer never carried them on these
rows. `SpellAreaEffectCriterion` does now, and the answer was on the wire all along.

`Furious Inspiration` (299074), verbatim (`--report rank-tables --report-arg 299074`):

```
  seq  0 -> 299064  Furious Inspiration (10)   requires 299064 '(10)' OR 299073 '(9)'
  seq  1 -> 299073  Furious Inspiration (9)    requires 299072 '(8)'
  seq  2 -> 299072  Furious Inspiration (8)    requires 299071 '(7)'
  ...
  seq  8 -> 299066  Furious Inspiration (2)    requires 299065 '(1)'
  seq  9 -> 299065  Furious Inspiration (1)    (no gate - always permitted)
```

The operator is `MustHavePassiveAbilityRunning` (91), whose operand is a **spell**, and each edge requires the
rank BELOW it. Read in the order the record stores them — highest rank first — and taking the first that passes,
the table is a **one-rank-per-proc upgrade ladder**: nothing running gives rank 1, rank 5 running gives rank 6.

⭐ **The top edge ORs in ITSELF, and nothing else in the table does.** That single asymmetry is what makes a
subject at the cap *refresh* rather than fall back to rank 1, and it is very hard to read as anything else.
42 of the 65 tables carry it.

⚠️ **Two mechanisms, and the criterion is what says which** — a global rule would have got one of them wrong:

| shape | the gate names | tables |
|---|---|---:|
| **upgrade ladder** | another member of the same table (the rank below) | **43** |
| **trained-rank lookup** | a spell OUTSIDE the table — the passive rank the character holds | **4** |
| mixed | both | 4 |
| ungated | nothing at all — arrival order really is all there is | 14 |

The four lookups are `Rampaging Horde` and friends, whose ten edges each require a different rank of the trained
ability. ⛔ Note that *is* "the rank you trained", the rule #813 ruled out for line 44235 — it was ruled out
correctly for that table and is right for these four. The data decides per table; a server-side rule cannot.

40 of the 43 ladders gate **every** edge on the next one in the record, so the record's own order is the ladder.
The 14 ungated tables keep arrival order, which is the honest residue rather than a claim.

`BuffService.ApplyChainedSpells` evaluates the run through `CriteriaEvaluator` before `SpellChainWalk.TryEnter`,
so the stacking-line rule still admits exactly one member — it is now the member the ladder selects rather than
whichever edge happened to be listed first.
[V3: RankTableLadderTests.EachProc_UpgradesByExactlyOneRank]

## Terrain line-of-sight after the bake fix (#832/#833)

`TerrainService.HasTerrainLineOfSight` blocks a ray when the sampled terrain exceeds the eye-line by
`OcclusionMargin` (1.5 u). It reads the same baked grid the loot drop does — and before #832 that grid was a
per-playfield surface with the playfield's *trees and buildings baked in*, so **43% of the walkable positions
measured on map 4010 read 10–60 m above the player**: a phantom canopy standing between an NPC and its target,
suppressing aggro on open beach.

#832 rebuilt the bake as the lower envelope of the tilemap group. #833 asked whether that actually changed
anything, since the claim was inference from the shared grid rather than an observation. Measured
(`--report terrain-los --report-arg 4010`, 95 NPC spawns × 8 directions):

| range | clear | terrain | collision |
|---|---|---|---|
| 6 u | 97.9% | 0.1% | 2.0% |
| 12 u | 92.4% | 1.1% | 6.6% |
| 15 u | 85.9% | 3.0% | 11.1% |
| **20 u** (`NpcAggroRange`) | **73.0%** | **9.7%** | **17.2%** |

⭐ **The phantom canopy is gone.** The spawn-to-ground deltas now run min −0.17 / median 0 / max **1.46 u**, and
of the 42 spawns sitting below ground *none* is more than the 1.5 u eye height below — against the old 10–60 m.
Terrain contributes ~1% of blocked rays out to 12 u and is not the dominant blocker at any range.

⚠️ **What still climbs with range is COLLISION, not terrain** — 17.2% against 9.7% at the aggro boundary. So if
aggro is still short on 4010 the remaining suppression is real geometry (huts, rocks) plus whatever the range and
vertical gates do, and terrain is no longer the thing to look at. That is the input #577 needs.

⚠️ Not an in-client observation. This measures the grid the gate reads, over real spawn positions; it does not
watch a mob acquire.

### What the gate does now (#577)

`TerrainService.Trace` answers in the order `NearestPlayerInRange` asks: the **terrain half** first, then the
**collision half**, each failing OPEN when its asset is missing.

- **Terrain half** — 24 ground samples along the sightline, both ends lifted by `EyeHeight` (1.5 u); a sample
  occludes when it rises more than `OcclusionMargin` (1.5 u) above the eye-line, i.e. a hump ≥ 3 u above the
  straight ground line between the two. **An endpoint whose eye is under the heightmap's surface takes the terrain
  half out of the test**: the grid does not describe a cave, a cellar or a floor beneath a roof the bake kept, and
  from such a point every sample reads as rock overhead — which occluded at *every* range including point-blank and
  was the melee-only aggro for the 7 of 122 City of Tortage (4020) spawns that sit 1.5–60 u under their grid. The
  endpoint is read with the loot drop's neighbourhood sample (`HeightNear`, 2 u), so a creature beside a rock whose
  nearest cell is the rock's top is not mistaken for a buried one. The collision mesh, which does model the cave
  wall and the building, still gates such a ray.
- **Collision half** — three parallel rays, not one: eye to eye, plus one `ShoulderHalfWidth` (0.5 u) to either
  side in the ground plane, applied to both ends; occluded only when **all three** are. A single ray was defeated
  by anything narrower than a body — a fifth of 4020's collision actors are posts, barrels and pillars — and a
  post between a mob and a player is a thing it sees past, not cover. The centre ray decides the common (clear)
  case at the cost of one cast; the shoulder rays are only cast when it hit something.
  [V3: TerrainServiceTests.A_Post_Between_Them_Is_Seen_Past] [V3: TerrainServiceTests.A_Wall_Wider_Than_The_Shoulders_Blocks_ByCollision]
  [V3: TerrainServiceTests.An_Npc_Under_The_Surface_Is_Not_Blocked_By_The_Roof_Over_It]

⭐ **The heightmap half was not mis-sampling.** The "29% at 10 u / 55% at 20 u on a beach" figures on the ticket
were measured on the pre-#832 per-playfield bake (the phantom canopy above); on the envelope bake
`--report terrain-los --report-arg 4010:20:profile` dumps every terrain-blocked ray's ground profile, and each one
is a real 3–4 u dune or rock hump above *both* eye-lines (4010), or a terrace ledge with the target 4–6 u below on
the next terrace (4020) — geometrically occluded, with the 1.5 u margin already lenient. Two things had inflated
the column instead: rays whose target sits outside `NpcAggroVerticalRange` (6 u), which the tier-1 vertical gate
culls *before* line-of-sight is asked — 23 of 74 terrain-blocked rays at 20 u on 4010, 89 of 142 on 4020 — and the
under-surface spawns. The report now tabulates the vertical gate as its own column so the terrain/collision columns
only count rays the game would actually LOS-test.

Measured on the same bake, gate before → after (`--report terrain-los`, ground-to-ground rays; "vertical" is the
tier-1 band, not line-of-sight):

| map | range | clear | terrain | collision | vertical |
|---|---|---|---|---|---|
| 4010 (95 spawns) | 10 u | 92.9% → **93.4%** | 0.0% → 0.0% | 3.3% → **2.8%** | 3.8% |
| 4010 | 15 u | 78.0% → **78.6%** | 1.8% → 1.8% | 5.0% → **4.5%** | 15.1% |
| 4010 | 20 u | 62.4% → **63.2%** | 6.7% → 6.7% | 6.2% → **5.4%** | 24.7% |
| 4020 on-datum (77 of 122) | 10 u | 79.7% → **80.0%** | 1.9% → 1.9% | 4.4% → **4.1%** | 14.0% |
| 4020 on-datum | 15 u | 66.6% → **66.9%** | 5.2% → 5.2% | 5.0% → **4.7%** | 23.2% |
| 4020 on-datum | 20 u | 55.0% → **55.7%** | 8.6% → 8.6% | 3.6% → **2.9%** | 32.8% |
| 4020 all spawns | 20 u | 41.5% → **44.3%** | 9.5% → **7.2%** | 2.3% → 1.8% | 46.7% |

Of the rays inside the vertical band, the gate clears 97% at 10 u and 84% at 20 u on 4010. The three-ray test
removes 13% of the collision blocks at 0.5 u (24% at 1.0 u, measured and not taken — past a half-shoulder the
outer rays look *around* something wider than the target); the rest is huts and rock faces. The under-surface rule
shows only in the all-spawns 4020 row (terrain 9.5% → 7.2%): a spawn a few units under the grid whose targets are
still inside the band. The dominant column on both maps is now the vertical band, and on this report that is mostly
the sampling — a 20 u circle around a beach spawn reaches sea-stack tops and cliff faces no player can stand on.

⚠️ Still not an in-client observation.
