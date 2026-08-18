# Necro pets / minions — capture decode + MVP (2026-07-05)

From `NecroL80_mage-necromancer_login_summon_pet.pcapng` (necro lvl80 login in a town, player instance
`0x0802E5D4`). Goal: get minions working.

## What the capture shows (CORRECTED 2026-07-05 — an earlier pass wrongly called this a town-only login)

The scene IS a town (nearby entities decode to `"Guard"` `0x0001A2C8`–`0x0001A2D2`, `"Trader"`, `"Noblewoman"`).
The guard cluster receives `ApplySpell 0x3BAD7D` + `GameActionList` + BCC @frame 221 — **purpose UNCONFIRMED**
(likely ambient NPC state/buff + AI, NOT necessarily player combat; do not assume combat). **The confirmed
signal is the real pet summon later in the session:**

- **Frame 1197, C→S `BehaviorControlCenterIIR` (0x642CD3D6, 72 B):** the player activates the **summon ability**
  — a channelled BCC action. The body carries the player instance as a protobuf VARINT `d4 cb 8b 40`
  (= 0x0802E5D4) plus the ability/cast fields (`08 c0e80a … 52 09 08 d0 86 03 10 d4cb8b40 …`).
- Server streams the cast (BCC animation updates, `0x4517AA81` position, `0x2009` ack) frames 1198–1217.
- **Frame 1219, S→C `FA015807` spawn, handle `0x0001A3DB` = "Tamed Puma" (473 B, minimal creature):** the PET.
- Followed by the pet's stats (`0xF98E10B3` StatIIR ×3, `0x96B8DC59` StatUpdate) frame 1222.

So the retail necro-pet flow is: **BCC summon-ability activation → channel → `FA015807` pet spawn + stats.** The
pet spawn is the SAME `FA015807` creature spawn we already build; the owner binding is NOT a plain field in the
473-byte spawn (no `pet_owner` varint) — it is established by the BCC summon action / a follow-up, still to pin.
Player→pet commands reuse the 16-byte target RPC `[C350][player][C350][target]`.

## MVP shipped this session — `.pet <npc name>`

A minion **works now** via a GM trigger, built on existing infra:
- `WorldBroadcaster.SpawnNpcForPlayer` — mid-session spawn that renders a creature next to the player
  (the #23 mid-session-spawn path).
- `WorldBroadcaster.SetNpcFollow` — companion-follow (#262).

`.pet <name>` spawns a creature (the SAME `FA015807` spawn retail uses for the pet) and makes it follow → a
rendered, following minion. This is structurally the retail flow minus the class-summon trigger + owner binding.

## FULL pet protocol — from the chat-annotated capture (2026-07-05)

`NecroL80_..._casting-with-chat-notes.pcapng` — a local-say note was typed before each action, so every packet
is labelled. The two **Deathless Acolyte** casts (Arcanist, Magnus) are the real necro pets ("Pleasurable
Company" is a generic companion; "sit on bench" was clicking a bench world-object to sit on it — a sittable
Interact, not a pet action and not an emote).

**Summon** (necro Deathless Acolyte): C→S **`BehaviorControlCenterIIR` 0x642CD3D6** (72 B) ability activation
(the summon-ability channel; body carries the caster instance as varint `d4 cb 8b 40` + the ability fields
`08 c0e80a … 52 09 08 d0860310 d4cb8b40 60 …`). Server channels (BCC anim updates + `0x2009` ack), then:
**S→C `FA015807` pet spawn** (`0x00023EDC` Arcanist / `0x00023EDF` Magus, ~471 B) + `StatIIR` + `ApplySpell`
(the pet's buff) + `StatUpdate` + `MoveTask`.

**Command — `QueuePetCommand` (0x205b)** (opcode already named in `Opcodes.cs`):
`[u32 type=0xC350][u32 petHandle][u32 command]`.

**The command set is the client's own TWELVE, and it was recovered from the client rather than the corpus.**

The client carries the names as one contiguous string block, and that block's order is the enum's order:

```
Assist  Follow  Defend  AttackTag  CeaseFire
AllAssist  AllFollow  AllDefend  AllAttackTag
DismissFollower  Dismiss  AllDismiss
```

The BASE is pinned by the corpus: require the three capture-attributed codes to land on semantically matching
names, and **exactly one base in the plausible range does it** — `0x1EF`.
[V1: docs/re/aoc-client-rpc-methods.json] [V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-3.tshark.json]

| code | name | evidence |
|---|---|---|
| `0x1EF`–`0x1F3` | Assist / Follow / Defend / AttackTag / CeaseFire | per-pet; **never used in any capture** |
| `0x1F4` | **AllAssist** | `chat-annotated-4`: press 20:11:08, note "set **app** pets to assist" 20:11:10 |
| `0x1F5` | **AllFollow** | the alignment, plus the ability-slot ladder below |
| `0x1F6` | **AllDefend** | "net magus to defend" 05:59:38 → press 05:59:41; "set app pets to defend" → press; "defend again" → press. Three independent notes |
| `0x1F7` | **AllAttackTag** | the alignment, plus the ability-slot ladder below |
| `0x1F8` | **DismissFollower** | "dismissing arcanist" 05:59:13 → press 05:59:16, confirmed by BEHAVIOUR: `ApplySpell` + `PetIIR` mode-1 + `n3ToClientQuit 0x04B82B2D` |
| `0x1F9`, `0x1FA` | Dismiss, AllDismiss | the per-pet and all-pets spellings of the same act |

The alignment is a derivation, not a fit: no other offset puts an assist-like, a defend-like **and** a dismiss-like
name on those three codes. The nearest rival (`0x1F4` as `Assist`) matches assist and defend but puts `CeaseFire`
where a pet was captured being despawned. Executable as
`PetCommandTests.ExactlyOneBase_SatisfiesAllThreeAnchors`.

**A second, independent derivation agrees on exactly the pair that was open.** The bar's ability slots map to the
all-pets codes in DESCENDING order — Assist 264 → `0x1F4`, Follow 263 → `0x1F5`, Defend 262 → `0x1F6`, Attack 261
→ `0x1F7`.

### Two earlier conclusions died here

⚠️ **"The command set is CLOSED at five" was wrong.** There are twelve. The corpus carries five because the
captured character was a necromancer commanding a GROUP, so every press went through the **all-pets** bar — which
is also why the annotations say "set *app* pets to…". The per-pet codes were simply never pressed on the wire.

⚠️ **"There is no arithmetic relation to the ability slots" was wrong**, and instructive about how: the relation
DESCENDS, and only an ascending one had been looked for. The original note reads "Assist 264 → `0x1F4` while
Defend 262 → `0x1F6`" as evidence of no relation; it is the relation, seen backwards.

(A const-scan of `AgeOfConan.exe` for `0x1F4`–`0x1F8` is still a dead end and a trap worth naming: it lands on the
client's STAT-name registration table, where `0x1F8` is `Stamina` and `0x1FA` is `Mana` — the same numbers in an
unrelated namespace. The string block above is the right artifact.)

**Owner-binding — `PetIIR_c` (0x1F4CB0F7)** (opcode already named; reader in docs/re/iir-readers-conversation-misc.md
= `[u32 mode], mode∈{0,1}→[u32×2], mode==0→[u32×3]`). This is the packet that tells the client "this creature is
YOUR pet". Sent right after the pet spawn: carrier `[sub][C350][ownerHandle=player][u8 env]` then the mode
payload carrying the **pet handle** (+ extras for mode 0). Observed: mode-0 (len 41) on each summon
(frames 470/679/919 for the 3 pets), mode-1 (len 29) on dismiss (frame 1167). So ownership is a SERVER signal,
not client inference and not a spawn field.

**FULLY DECODED loop** (nothing left to RE on the protocol):
1. **Summon**: C→S BCC `0x642CD3D6` ability → channel → S→C `FA015807` pet spawn + `StatIIR` + `ApplySpell`
   (pet self-buff) + `StatUpdate` + `MoveTask` + **`PetIIR` 0x1F4CB0F7 mode-0 owner-assign** `[player][pet]`.
2. **Command**: C→S `QueuePetCommand` 0x205b `[C350][pet][cmd]` — the twelve codes above (five captured).
3. **Dismiss**: 0x205b 0x1F8 → S→C `ApplySpell` + `PetIIR` mode-1 + `n3ToClientQuit 0x04B82B2D` despawn.

The only non-protocol unknown is **which BCC summon-ability id maps to which pet template** (Deathless Acolyte
Arcanist vs Magnus — a data mapping, harvestable from the BCC payload / RDB, not a wire decode).

### Generic companion vs class (necro) pet — SAME subsystem

Comparing the "Pleasurable Company" companion (frame 447) and the "Deathless Acolyte" necro pet (640) in the
same capture: **both use the identical OWNERSHIP + COMMAND wire subsystem** — C→S BCC ability → channel →
`FA015807` spawn → `PetIIR` mode-0 owner-assign (len 41) → stats, both commanded via `QueuePetCommand 0x205b`.
The difference is the **combat layer**: "Pleasurable Company" is a **SOCIAL pet** (no combat abilities), so its
summon has NO extra packets. The necro "Deathless Acolyte" is a **COMBAT pet**, so right after the spawn it also
gets an `ApplySpell` (the pet's combat buff/aura) + a `MoveTask` (combat movement/AI). So: one shared
spawn/own/command subsystem for ALL pets; combat pets stack the buff + combat-AI on top. Implementing `PetIIR`
+ `QueuePetCommand` once gives you both — social pets for free, combat pets by adding the buff + follow/attack AI.

## Implementation (2026-07-05) — pets spawn from the creature TYPE (the right way, #244)

Wired end-to-end. Design steer: `NpcSpawns` are world PLACEMENTS — wrong for a summon-on-demand pet — so
pets spawn from the creature **TYPE**, not a placement (the #244 stat-core lift):
- `Npc.StatCore` (the `NpcStatCore` side table, incl. `.Level`): the type-level captured stat core, so a creature can be built standalone (the `Npc.Data` blob is retired — the body builds from typed columns).
- `NpcSpawnBuilder.TryBuildFromType(Npc, handle, pos, rot)`: builds the `FA015807` from the type + a
  caller transform — no `NpcSpawn` instance.
- `WorldBroadcaster.SpawnPetForPlayer` + `IDatabaseRepository.GetNpcByName`: the spawn-from-type path.
- `SummonPetForPlayer` (shared by `.pet` + the CastSpell hook) resolves the creature TYPE; only falls back
  to the placement path for the `.pet` GM test.
- `PetCodec` (0x1F4CB0F7) owner-assign/remove + inbound `QueuePetCommand` (0x205B) dismiss.
- `CapturePetImporter` + `PetsModule` (`--only pets`): lifts each pet's captured `FA015807` onto its `NpcTemplate`
  type. Seeded from the necro capture: **Arcanist** (tpl 11733) + **Magus** (tpl 11732) — the two combat
  necro pets (lvl 80); **Pleasurable Company** (tpl 17756) — the social companion (lvl 5).

**Naming gotcha:** "Deathless Acolyte" is the SPELL/UI name; the summoned pet CREATURES are named
`Arcanist` / `Magus`. The mapping lives in the `SpellEffects` table as `SummonPet` rows keyed by the summon
spell id (written by the importer's `spell-effects` module).

## Where the roster comes from (#640)

The twelve pets above were a hand-typed `(spellId, template, combat)` table lifted out of the one necro capture.
**The client's own catalog states all three facts**, so it is read rather than listed — and the difference is not
cosmetic: the RDB names **319** summon spells, and three of the misses (`Summon Horde: Corruptor` /
`Blighted One` / `Mutilator`) are combat pets of the very class that capture was of.

A summon spell performs no stat change. Its `[2][9]` GameAction region carries one
`StartServerSideScryScriptWithParams` (53261) element whose `ScriptParams` string is the summon, spelled out —
and there are **two spellings**:

```
spawnHash=YU86;pointCost=2;petBuff=1366140;spellID=385781     a COMBAT pet
Type=Pet;Tag=3681                                             the social COMPANION
```

| fact | where it comes from |
|---|---|
| **which** spells summon | `Spell.SummonSpawnHash != null` — the params, over all 21,502 spell records |
| **whether it fights** | which spelling the params use (`Spell.SummonIsCompanion`) |
| **which creature** | the spell's own NAME, after the first `": "` — `Undead Minion: Corruptor`, `Pet: King Cobra`, `Companion: Pleasurable Company` |

53261 is a general "run this server script" action, so the ACTION does not mark a summon — the params do.

⚠️ **`spawnHash` is a 4-character ACG token and resolves to nothing in the client.** It addressed retail's
server-side content; the client neither reads nor compares it (the same finding that closed #547 for the quest
tokens). It is carried as *evidence*, not as a lookup — and it is what would make a wrong naming rule visible,
because spells sharing a pet share a hash (`GGDI` on all three Mutilator summons, `NSAO` on both Corruptor ones).

A summon is only written as a row when its pet name matches an `NpcTemplates` row, which is also the only thing
the runtime can spawn — **30 of the 319** today. The rest are counted and reported by the module rather than
silently dropped; they grow with `NpcTemplates`, i.e. with the corpus.

### ⭐⭐ `pointCost` GOES ON THE WIRE — it is `f2` of the assign's kind triple (#823)

The symptom was that **every necromancer pet showed as costing 2** in the client's pet-point bar, whatever it
was: four `Dread Archmage: Blood Arcanist` (cost 4 each) read as 8 rather than 16.

The accounting is entirely client-side — stat `0x1C8 PetPoints` appears in **zero** updates across all 59
necromancer captures — so whatever the client counts, it counts from something already in front of it. Two
readings were tried and refuted: the pet's own spawn body carries no field equal to its cost (29 varying numeric
fields, none of them the cost), and the only field that *determines* the cost is `npc_stats.TemplateId`, which
determines it merely by identifying the creature — and **our TemplateIds already match retail's exactly**
(11736 Blood Arcanist, 11735 Archmagus, 11733 Arcanist, …), so the client was being handed the same key.

⭐ It is in the one message we build ourselves. `PetCodec` carried the kind triple as two constants —
`CombatKind = (0, 2, 2)` and `SocialKind = (5, 0, 1)` — generalised from the two pets in the decode capture. The
middle slot is the cost, measured over the corpus by `--report pet-cost`:

| triple | assigns | costs | creatures |
|---|---:|---|---|
| `(0,1,1)` | 14 | cost 1 ×14 | Blighted One, Corruptor, Harvester, Life-stealer, Mutilator, Necrotic Bomb |
| `(0,2,2)` | 12 | cost 2 ×12 | Arcanist, Magus, Reaper |
| `(0,4,2)` | 36 | cost 4 ×35, cost 1 ×1 | Archmagus, Blood Arcanist, *Demon Avenger* |

**`f2` equals the summoning spell's `SummonPointCost` on 61 of 62 resolved assigns.** So the server was sending
a literal 2 for every pet in the game, and that is the whole bug.

⚠️ The single exception is our own join, not a counterexample: the report maps creature→cost by NAME, and several
spells can name one creature, so `Demon Avenger` picks up the cost-1 spell while retail assigns 4. The server
does not use that join — `SpellEffectService` reads `SummonPointCost` off **the spell it actually cast**.

⚠️ `f3` is the fitted half and is written as fitted: 1 on the cost-1 pets, 2 on both the cost-2 and the cost-4
ones, so over the three cost classes the corpus holds it reads as a coarse tier rather than a second copy of the
cost. A cost-3 pet would decide it; the corpus has none. A social pet carries 1 there too, which fits the tier
reading.

⚠️ A pet with no spell behind it — the `.pet` GM command — summons at **0**, which is the value retail's own
social pets carry rather than an invented one.

`petBuff` is still lifted onto `Spell` and read by nothing.

## A live pet costs retail 50 packets a second, forever — and it is a retail DEFECT (#777)

`Nemedian Deathstalker` (template 14321) takes **26,832 of the corpus' 37,965 `BccAction` records**, where the
median actor takes **one**. #225 read that as a world heartbeat and #777 as a burst. It is neither. Measured per
actor, with the record's own args field 3 as the check:

| capture | actor | records | span | rate | counter | anim |
|---|---|---:|---:|---:|---|---|
| `..._casting-combat` | `0x0004f022` | 4,192 | 84.8 s | **49.4/s** | 1 … 4,192, **step +1 with no gaps** | 41413 |
| `..._casting-combat` | `0x0004f028` | 3,479 | 70.7 s | **49.2/s** | 42 … 3,520, step +1 | 41413 |
| `..._actions-described-in-chat` | `0x0003d310` | 3,724 | 75.2 s | **49.5/s** | 1 … 3,724, step +1 | 41413 |
| `..._mount_move_dismount` | `0x000286d5` | 1,190 | 22.4 s | **53.1/s** | 1,918 … 3,107, step +1 | 41413 |

So it is a **metronome, not a burst**: an unbroken ~50 Hz stream that starts at the frame the pet's
`FA015807` spawn arrives and does not stop until the capture ends. Every record is byte-identical bar the
counter — the same fixed idle animation `41413` every other actor gets *once* — and it is uncorrelated with
movement, combat, casting or any c2s pet command. The step-`+1`-with-no-gaps counter across thousands of
records also proves nothing is being dropped: this is the whole stream, not a sample of a faster one.

Three things follow:

- **The rate is EVERY OTHER retail server frame.** Retail's frame loop is independently measured at a
  99.34 Hz ceiling from the 4,243 captured `GameServerStats` readouts
  ([`../protocol/02-rpc.md`](../protocol/02-rpc.md)), and ~49.5 Hz is half of it to within the sampling error.
  So this is not a throttle or a timer — it is the pet's brain running on a two-frame cadence and putting its
  decision on the wire every time.
- **A controlled entity re-issues its default command every tick.** The counter is a per-entity command-queue
  index that starts at 1 when the entity is created (`0x0004f028` first appears at 42, i.e. ~0.84 s of ticks
  before its spawn record reached the client; the mount capture's pet is at 1,918, having been summoned ~38 s
  before the window). An ambient NPC's brain script asserts once and stops; a *summoned* one never stops.
- ⚠️ **It is not the capturing player's pet**, though both tickets assumed so — which is why this sits under a
  "not modelled" heading rather than in the pet lifecycle. The only `PetIIR` in that capture names a *different*
  handle as the player's pet, and three of the four measured actors are in captures with no ownership record at
  all. What is provable is the TEMPLATE: all four are 14321, and nothing else in the corpus behaves this way.
- ⚠️ **Do not reproduce it.** A record that says nothing new 50 times a second, per pet, is a defect — the same
  class of thing as the server heap the spawn scaffold leaks ([`../re/retail-server-heap-leak.md`](../re/retail-server-heap-leak.md)).
  We send a pet's idle assert once, which is what the other 7,968 captured actors get, and that is the right
  behaviour rather than a gap.

## Follow-up (#307) to make it retail-accurate

Items 1-3 landed with the MVP above: the summon SPELL fires the spawn (`SpellEffects` rows of kind `SummonPet`
keyed by the summon spell id, shared with `.pet` through `SummonPetForPlayer`), the pet builds from the creature
TYPE, and owner binding is `PetIIR` mode-0. What is left:

- **Pet-side combat AI.** This is the one real blocker on the command bar. `Dismiss` acts; the four stance
  commands are decoded, logged and inert, because a pet picking and engaging a target does not exist - the
  simulation's attack path runs NPC -> player, and a pet is an NPC that must attack another NPC. A summoned minion
  only follows (`SetNpcFollow`).
- **The attack/follow tie-break** - see the command table above. One annotated capture settles it.
- **Persistence + despawn on death/logout.**

Open work tracked in Gitea #307.
