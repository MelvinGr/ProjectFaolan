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

`NecroL80_..._casting-with-chat-notes.pcapng` — Melvin typed a local-say note before each action, so every packet
is labelled. The two **Deathless Acolyte** casts (Arcanist, Magnus) are the real necro pets ("Pleasurable
Company" is a generic companion; "sit on bench" was clicking a bench world-object to sit on it — a sittable
Interact, not a pet action and not an emote).

**Summon** (necro Deathless Acolyte): C→S **`BehaviorControlCenterIIR` 0x642CD3D6** (72 B) ability activation
(the summon-ability channel; body carries the caster instance as varint `d4 cb 8b 40` + the ability fields
`08 c0e80a … 52 09 08 d0860310 d4cb8b40 60 …`). Server channels (BCC anim updates + `0x2009` ack), then:
**S→C `FA015807` pet spawn** (`0x00023EDC` Arcanist / `0x00023EDF` Magus, ~471 B) + `StatIIR` + `ApplySpell`
(the pet's buff) + `StatUpdate` + `MoveTask`.

**Command — `QueuePetCommand` (0x205b)** (opcode already named in `Opcodes.cs`):
`[u32 type=0xC350][u32 petHandle][u32 command]`. Confirmed command codes (labelled by the notes):
- `0x1F6` = **defend** ("net magus to defend")
- `0x1F8` = **dismiss** → server replies with `ApplySpell` + `n3ToClientQuit 0x04B82B2D` despawn of the pet.
- (attack/follow/stay/aggressive/passive are the neighbouring `0x1Fx` codes — capture those next.)

So the whole loop is now decoded: **summon (BCC ability → FA015807 spawn) · command (QueuePetCommand 0x205b) ·
dismiss (0x205b 0x1F8 → despawn)**. Implementing `QueuePetCommand` handling (dismiss/defend) + the BCC summon
trigger is now unblocked — no new RE needed for the command side.

## Follow-up (#307) to make it retail-accurate

The capture DOES contain the flow (frames 1197→1222, above), so the remaining work is:
1. **Trigger:** wire the necro summon ability — a `BehaviorControlCenterIIR` (0x642CD3D6) channelled activation
   (frame 1197) — to fire the pet spawn, instead of the GM `.pet` command.
2. **Spawn:** on channel-complete, spawn the pet as an `FA015807` creature (we already build these) — e.g. the
   "Tamed Puma" template — next to the caster.
3. **Owner binding:** pin how the client learns the pet is the caster's (NOT a plain field in the 473-B spawn —
   check the BCC summon action payload / the follow-up stats, or a dedicated pet-assign packet).
4. **Command bar + AI:** reuse the 16-byte `[player][target]` RPC for attack/follow/stay; follow AI = `SetNpcFollow`.
5. Pet persistence + despawn on death/logout.
