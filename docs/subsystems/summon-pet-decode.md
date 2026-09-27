# Pets and minions

The pet protocol — summon, owner binding, command bar, dismiss — decoded from the necromancer captures, and how our
server runs it. The same subsystem serves social companions and combat pets.

## The summon on the wire

From `NecroL80_mage-necromancer_login_summon_pet.pcapng` (necro lvl80 in a town, player instance `0x0802E5D4`):

- **Frame 1197, C→S `BehaviorControlCenterIIR` (0x642CD3D6, 72 B):** the player activates the summon ability — a
  channelled BCC action. The body carries the player instance as a protobuf varint `d4 cb 8b 40` (= 0x0802E5D4)
  plus the ability/cast fields (`08 c0e80a … 52 09 08 d0 86 03 10 d4cb8b40 …`).
- The server streams the cast (BCC animation updates, `0x4517AA81` position, `0x2009` ack), frames 1198–1217.
- **Frame 1219, S→C `FA015807` spawn, handle `0x0001A3DB` = "Tamed Puma" (473 B, minimal creature):** the pet —
  the same `FA015807` creature spawn every NPC uses. The owner is not a field of the spawn (no `pet_owner` varint).
- The pet's stats follow (`0xF98E10B3` StatIIR ×3, `0x96B8DC59` StatUpdate), frame 1222.

(The guard cluster in the same capture receives `ApplySpell 0x3BAD7D` + `GameActionList` + BCC at frame 221; that
is ambient NPC state, not part of the summon.)

`NecroL80_..._casting-with-chat-notes.pcapng` labels every action with a preceding local-say note. The two
**Deathless Acolyte** casts are the necro combat pets (creatures `0x00023EDC` Arcanist / `0x00023EDF` Magus,
~471 B): the spawn is followed by `StatIIR` + `ApplySpell` (the pet's buff) + `StatUpdate` + `MoveTask`.

## Owner binding — `PetIIR_c` (0x1F4CB0F7)

Reader in `docs/re/iir-readers-conversation-misc.md`: `[u32 mode], mode∈{0,1}→[u32×2], mode==0→[u32×3]`. This is
the packet that tells the client "this creature is your pet", sent right after the spawn: carrier
`[sub][C350][ownerHandle=player][u8 env]` then the mode payload carrying the pet handle (+ the kind triple for mode 0).
Mode-0 (len 41) on each summon (frames 470/679/919 for the 3 pets), mode-1 (len 29) on dismiss (frame 1167). So
ownership is a server signal, not client inference and not a spawn field.

### The kind triple, and `pointCost`

The mode-0 kind triple's middle slot `f2` is the pet's point cost, and the client's pet-point bar counts from it —
stat `0x1C8 PetPoints` appears in zero updates across all 59 necromancer captures, the spawn body carries no field
equal to the cost, and `npc_stats.TemplateId` (which matches retail's exactly: 11736 Blood Arcanist, 11735
Archmagus, 11733 Arcanist, …) only identifies the creature. Measured over the corpus by `--report pet-cost`:

| triple | assigns | costs | creatures |
|---|---:|---|---|
| `(0,1,1)` | 14 | cost 1 ×14 | Blighted One, Corruptor, Harvester, Life-stealer, Mutilator, Necrotic Bomb |
| `(0,2,2)` | 12 | cost 2 ×12 | Arcanist, Magus, Reaper |
| `(0,4,2)` | 36 | cost 4 ×35, cost 1 ×1 | Archmagus, Blood Arcanist, *Demon Avenger* |

`f2` equals the summoning spell's `SummonPointCost` on 61 of 62 resolved assigns. The exception is the report's own
join (creature→cost by name, and several spells name one creature, so `Demon Avenger` picks up a cost-1 spell while
retail assigns 4); the server reads `SummonPointCost` off the spell it actually cast. Sending a constant 2 makes
every pet show as costing 2 (four cost-4 Blood Arcanists read as 8 rather than 16).

`f3` is fitted: 1 on the cost-1 pets, 2 on both the cost-2 and the cost-4 ones, so over the three cost classes the
corpus holds it reads as a coarse tier rather than a second copy of the cost. A cost-3 pet would decide it; the corpus
has none. A social pet carries `(5, 0, 1)`, and a pet with no spell behind it summons at cost 0, the value retail's
own social pets carry. `PetCodec` holds `CombatKind` / `SocialKind` for the fixed slots.

## Commands — `QueuePetCommand` (0x205b)

`[u32 type=0xC350][u32 petHandle][u32 command]`.

The command set is the client's own twelve, recovered from the client rather than the corpus. The client carries
the names as one contiguous string block, and that block's order is the enum's order:

```
Assist  Follow  Defend  AttackTag  CeaseFire
AllAssist  AllFollow  AllDefend  AllAttackTag
DismissFollower  Dismiss  AllDismiss
```

The base is pinned by the corpus: require the three capture-attributed codes to land on semantically matching
names, and exactly one base in the plausible range does it — `0x1EF`.
[V1: docs/re/aoc-client-rpc-methods.json] [V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-3.tshark.json]

| code | name | evidence |
|---|---|---|
| `0x1EF`–`0x1F3` | Assist / Follow / Defend / AttackTag / CeaseFire | per-pet; never used in any capture |
| `0x1F4` | **AllAssist** | `chat-annotated-4`: press 20:11:08, note "set **app** pets to assist" 20:11:10 |
| `0x1F5` | **AllFollow** | the alignment, plus the ability-slot ladder below |
| `0x1F6` | **AllDefend** | "net magus to defend" 05:59:38 → press 05:59:41; "set app pets to defend" → press; "defend again" → press. Three independent notes |
| `0x1F7` | **AllAttackTag** | the alignment, plus the ability-slot ladder below |
| `0x1F8` | **DismissFollower** | "dismissing arcanist" 05:59:13 → press 05:59:16, confirmed by behaviour: `ApplySpell` + `PetIIR` mode-1 + `n3ToClientQuit 0x04B82B2D` |
| `0x1F9`, `0x1FA` | Dismiss, AllDismiss | the per-pet and all-pets spellings of the same act |

The alignment is a derivation, not a fit: no other offset puts an assist-like, a defend-like and a dismiss-like
name on those three codes. The nearest rival (`0x1F4` as `Assist`) matches assist and defend but puts `CeaseFire`
where a pet was captured being despawned. Executable as `PetCommandTests.ExactlyOneBase_SatisfiesAllThreeAnchors`.

A second, independent derivation agrees: the bar's ability slots map to the all-pets codes in descending order —
Assist 264 → `0x1F4`, Follow 263 → `0x1F5`, Defend 262 → `0x1F6`, Attack 261 → `0x1F7`.

The corpus carries only the all-pets codes because the captured character was a necromancer commanding a group, so
every press went through the all-pets bar (hence "set *app* pets to…").

A const-scan of `AgeOfConan.exe` for `0x1F4`–`0x1F8` is a trap: it lands on the client's stat-name registration
table, where `0x1F8` is `Stamina` and `0x1FA` is `Mana` — the same numbers in an unrelated namespace. The string
block above is the right artifact.

## The loop

1. **Summon**: C→S BCC `0x642CD3D6` ability → channel → S→C `FA015807` pet spawn + `StatIIR` + `ApplySpell`
   (pet self-buff) + `StatUpdate` + `MoveTask` + `PetIIR` 0x1F4CB0F7 mode-0 owner-assign `[player][pet]`.
2. **Command**: C→S `QueuePetCommand` 0x205b `[C350][pet][cmd]` — the twelve codes above.
3. **Dismiss**: 0x205b 0x1F8 → S→C `ApplySpell` + `PetIIR` mode-1 + `n3ToClientQuit 0x04B82B2D` despawn.

Social companion and combat pet share it. The "Pleasurable Company" companion (frame 447) and the "Deathless
Acolyte" pet (640) in the same capture both run BCC ability → channel → `FA015807` → `PetIIR` mode-0 (len 41) →
stats, and both are commanded via `QueuePetCommand`. The companion is a social pet, so its summon has no extra
packets; the combat pet additionally gets an `ApplySpell` (its combat buff/aura) + a `MoveTask` (combat movement).

## What the server does

Pets spawn from the creature type, not a placement — `NpcSpawns` are world placements, wrong for a
summon-on-demand pet:

- `Npc.StatCore` (the `NpcStatCore` side table, incl. `.Level`) is the type-level captured stat core, so a creature
  can be built standalone; `NpcSpawnBuilder.TryBuildFromType(Npc, handle, pos, rot)` builds the `FA015807` from the
  type + a caller transform.
- `PetService.SummonPetForPlayer` (`Faolan/GameServer/Combat/PetService.cs`, reached from the cast path through
  `ISpellEffectOutbound`) resolves the creature type (`IDatabaseRepository.GetNpcByName`) and spawns it via
  `NpcBroadcaster.SpawnPetForPlayer`, falling back to the placement path only for a world-placed creature whose stat
  core is not on its type. The pet follows (`NpcBroadcaster.SetNpcFollow`).
- `PetCodec` builds the owner-assign/remove; the inbound `QueuePetCommand` honours all three dismissal codes. The
  stance commands are decoded, logged and inert: a pet picking and engaging a target does not exist (the simulation's
  attack path runs NPC → player, and a pet is an NPC that must attack another NPC).
- `CapturePetImporter` + `PetsModule` (`--only pets`) lift each pet's captured `FA015807` onto its `NpcTemplate`:
  Arcanist (tpl 11733) + Magus (tpl 11732), the two combat necro pets (lvl 80); Pleasurable Company (tpl 17756),
  the social companion (lvl 5).

"Deathless Acolyte" is the spell/UI name; the summoned creatures are named `Arcanist` / `Magus`. The mapping lives
in the `SpellEffects` table as `SummonPet` rows keyed by the summon spell id ([spell-effects.md](spell-effects.md)).

## Where the roster comes from

The client's own catalog states which spells summon, whether the pet fights, and which creature it is. A summon spell
performs no stat change; its `[2][9]` GameAction region carries one `StartServerSideScryScriptWithParams` (53261)
element whose `ScriptParams` string is the summon, spelled out, in two spellings:

```
spawnHash=YU86;pointCost=2;petBuff=1366140;spellID=385781     a COMBAT pet
Type=Pet;Tag=3681                                             the social COMPANION
```

| fact | where it comes from |
|---|---|
| which spells summon | `Spell.SummonSpawnHash != null` — the params, over all 21,502 spell records (319 summon spells) |
| whether it fights | which spelling the params use (`Spell.SummonIsCompanion`) |
| which creature | the spell's own name, after the first `": "` — `Undead Minion: Corruptor`, `Pet: King Cobra`, `Companion: Pleasurable Company` |

53261 is a general "run this server script" action, so the action does not mark a summon — the params do.

`spawnHash` is a 4-character ACG token and resolves to nothing in the client: it addressed retail's server-side
content, and the client neither reads nor compares it. It is carried as evidence, not as a lookup — spells sharing a
pet share a hash (`GGDI` on all three Mutilator summons, `NSAO` on both Corruptor ones), which makes a wrong naming
rule visible. `petBuff` is lifted onto `Spell` and read by nothing.

A summon is written as a row only when its pet name matches an `NpcTemplates` row, which is also the only thing the
runtime can spawn — 30 of the 319. The rest are counted and reported by the module rather than silently dropped;
they grow with `NpcTemplates`, i.e. with the corpus.

## The `Nemedian Deathstalker` 50 Hz stream is a retail defect

`Nemedian Deathstalker` (template 14321) takes 26,832 of the corpus' 37,965 `BccAction` records, where the median
actor takes one. Measured per actor, with the record's own args field 3 as the check:

| capture | actor | records | span | rate | counter | anim |
|---|---|---:|---:|---:|---|---|
| `..._casting-combat` | `0x0004f022` | 4,192 | 84.8 s | **49.4/s** | 1 … 4,192, step +1 with no gaps | 41413 |
| `..._casting-combat` | `0x0004f028` | 3,479 | 70.7 s | **49.2/s** | 42 … 3,520, step +1 | 41413 |
| `..._actions-described-in-chat` | `0x0003d310` | 3,724 | 75.2 s | **49.5/s** | 1 … 3,724, step +1 | 41413 |
| `..._mount_move_dismount` | `0x000286d5` | 1,190 | 22.4 s | **53.1/s** | 1,918 … 3,107, step +1 | 41413 |

It is a metronome, not a burst: an unbroken ~50 Hz stream from the frame the pet's `FA015807` spawn arrives to the
end of the capture. Every record is byte-identical bar the counter — the same fixed idle animation `41413` every
other actor gets once — and it is uncorrelated with movement, combat, casting or any c2s pet command. The
step-`+1`-with-no-gaps counter shows nothing is dropped.

- **The rate is every other retail server frame.** Retail's frame loop runs at a 99.34 Hz ceiling, measured from the
  4,243 captured `GameServerStats` readouts ([`../protocol/02-rpc.md`](../protocol/02-rpc.md)), and ~49.5 Hz is half
  of it within sampling error: the entity's brain runs on a two-frame cadence and puts its decision on the wire each
  time.
- **A controlled entity re-issues its default command every tick.** The counter is a per-entity command-queue index
  that starts at 1 when the entity is created (`0x0004f028` first appears at 42, ~0.84 s of ticks before its spawn
  reached the client; the mount capture's is at 1,918, summoned ~38 s before the window).
- **It is not the capturing player's pet.** The only `PetIIR` in that capture names a different handle as the
  player's pet, and three of the four measured actors are in captures with no ownership record at all. What is
  provable is the template: all four are 14321, and nothing else in the corpus behaves this way.
- **Do not reproduce it.** A record that says nothing new 50 times a second is a defect — the same class of thing as
  the server heap the spawn scaffold leaks ([`../re/retail-server-heap-leak.md`](../re/retail-server-heap-leak.md)).
  We send a pet's idle assert once, which is what the other 7,968 captured actors get.

## Not modelled

Pet-side combat AI (the stance commands), pet persistence, and despawn on the owner's death or logout. Open work
tracked in Gitea #307 and #823.
