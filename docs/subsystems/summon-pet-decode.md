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

**The command set is CLOSED at five.** The bar is five buttons — the abilities `Pet Attack` / `Defend` / `Follow`
/ `Assist` / `Dismiss`, slots 261–265 — and sweeping every capture for `QueuePetCommand` yields exactly five
distinct codes, `0x1F4`–`0x1F8`, across 18 sends. So nothing further is coming; the only open question is which
name goes on which code. [V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-3.tshark.json]

What makes the annotations readable is the **lag direction**, and `casting-with-chat-notes` settles it: there the
player narrates *before* acting, twice, ~3 s apart.

| code | name | evidence |
|---|---|---|
| `0x1F4` | **assist** | `chat-annotated-4`: press at 20:11:08, note "set app pets to assist" at 20:11:10. The weakest of the three — a single note, and *after* its press rather than before. |
| `0x1F5` | attack **or** follow | — |
| `0x1F6` | **defend** | `casting-with-chat-notes` "net magus to defend" 05:59:38 → press 05:59:41; `chat-annotated-4` "set app pets to defend" 20:11:00 → press 20:11:01, and "defend again" 20:11:18 → press 20:11:19. Three independent notes. |
| `0x1F7` | the other of attack / follow | — |
| `0x1F8` | **dismiss** | `casting-with-chat-notes` "dismissing arcanist" 05:59:13 → press 05:59:16, and confirmed by BEHAVIOUR: the server answers with `ApplySpell` + `PetIIR` mode-1 + `n3ToClientQuit 0x04B82B2D`. |

⚠️ `0x1F5` and `0x1F7` are carried as one named pair (`PetCommand.AttackOrFollowA/B`) rather than as two hopeful
names. There is no arithmetic shortcut to break the tie — the bar's ability slots have no monotone relation to the
codes (Assist 264 → `0x1F4` while Defend 262 → `0x1F6`) — and the client sends **no** `PerformSpecialAction`
alongside a pet command, so the bar press cannot be cross-referenced to the ability it came from either. One
annotated capture that says "attack" or "follow" settles it.

(A const-scan of `AgeOfConan.exe` for `0x1F4`–`0x1F8` is a dead end and a trap worth naming: it lands on the
client's STAT-name registration table, where `0x1F8` is `Stamina` and `0x1FA` is `Mana` — the same numbers in an
unrelated namespace.)

**Owner-binding — `PetIIR_c` (0x1F4CB0F7)** (opcode already named; reader in docs/re/iir-readers-conversation-misc.md
= `[u32 mode], mode∈{0,1}→[u32×2], mode==0→[u32×3]`). This is the packet that tells the client "this creature is
YOUR pet". Sent right after the pet spawn: carrier `[sub][C350][ownerHandle=player][u8 env]` then the mode
payload carrying the **pet handle** (+ extras for mode 0). Observed: mode-0 (len 41) on each summon
(frames 470/679/919 for the 3 pets), mode-1 (len 29) on dismiss (frame 1167). So ownership is a SERVER signal,
not client inference and not a spawn field.

**FULLY DECODED loop** (nothing left to RE on the protocol):
1. **Summon**: C→S BCC `0x642CD3D6` ability → channel → S→C `FA015807` pet spawn + `StatIIR` + `ApplySpell`
   (pet self-buff) + `StatUpdate` + `MoveTask` + **`PetIIR` 0x1F4CB0F7 mode-0 owner-assign** `[player][pet]`.
2. **Command**: C→S `QueuePetCommand` 0x205b `[C350][pet][cmd]` — the five bar codes above.
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
spell id (seeded by the importer's `spell-effects` module): `0x5E2F5`→Arcanist, `0x5E2F0`→Magus,
`0x4105E9`→Pleasurable Company (all `CastSpell` 0x4, spell id in the cast trailer).

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
