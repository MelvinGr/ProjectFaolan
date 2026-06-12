# In-client test checklist

Things changed this session that I couldn't verify (no client on my side). Work through these in-client and
note what happens. For anything that fails, the **client log** is the most useful thing to grab:
`/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.log`
(look for `stream bad`, `Dispatcher call ... failed`, `invalid camera path`, `ResourceManager ... does not exist`).

Reminder: you revert `faolan.db` between tests. After pulling these changes, the DB needs the new schema —
either keep the committed `faolan.db` (it already has the migrations + reimported data) or run
`dotnet run --project Faolan.Importer` once. New tables this session: `CharacterMaxStats`, plus the
conversation key change.

---

I PUT THE LOGS IN ./logs-test

---

## 0. Backpack inventory — DONE (2026-06-12)

The player's real `CharacterInventory` bag items now render in-client, and re-arrangements persist. See
`docs/backpack-inventory-handoff.md` (RESOLVED) and memory `protocol_containeradditem`.

- [x] DB backpack delivered via a **post-load** `0x737A6DF9` snapshot (after the post-ready burst); world-entry
      snapshot stays verbatim (chat-safe). The blocker was the IIR prefix root-length field — fixed by
      `AdjustSnapshotPrefixRootLength`. Default on (`FAOLAN_POSTLOAD_INVENTORY=0` to disable).
- [x] Bag **moves / drops / stack splits persist** (`PersistInventoryMove`/`PersistDropItem`/`PersistSplitStack`).
- [x] Beach snapshot **prefix + tails built from scratch** (server-owned, no captured packet); byte-exact golden
      tests + 0 IIR errors live. Default on (`FAOLAN_SCRATCH_BEACH_SNAPSHOT=0` to disable).
- In-client checks still wanted (headless harness here dies on RDB thrash, not a code issue): bag shows the seeded
      items; move/drop/split survive relog; chat stays connected.

Next progression candidates scoped (memory + below): **quests** (content/VM/packets all exist — integration only,
unblocked) vs **skill tree/feats** (train-feat packet undecoded — needs a capture first).

---

## 1. Vitals packet `0x96B8DC59`  (commit: combat loop)
The decoded vitals/HP packet, wired to GM commands.
- [works] `.hurt 200` — your health bar should drop by ~200.
- [work up to 247, even though max says 3668] `.heal 200` — health bar should rise (clamped at max).
- [ ] `.vitals` (no args) — should set the bar to your character's MaxStats (class/level defaults).
- [ ] `.vitals 1000 800 500` — sets health/stamina/mana to those values.
- [ ] `.setstat 36 25` — sets Level to 25 on the sheet (statId 0x36 = Level).
**Expected:** the health/stamina/mana bars actually move. If nothing happens, grab the client log.bbbbbbb

## 2. Mounts  (commit: combat loop / mount summon)
Real mount spell decoded as `0x3BAD7D` (Fading Black Horse summon).
- [does nothing, shows nothing] `.mount` — should trigger the mount summon cast (and ideally put you on the horse).
- [same] If nothing mounts, try `.mount 3BAD7D` explicitly, and tell me what (if anything) happens
      (cast bar? horse spawns? you ride?). Full mounting may still need the mount-entity spawn — your
      observation tells me what's missing.

## 3. Combat (cast → damage → vitals)  (commit: grant starting abilities on login)
### FIX 2026-06-09: the player is now GRANTED the full retail starting ability kit on login.
On login the server grants the **universal starting abilities** via the retail 0x2052 ability-grant packet,
rebuilt DB-driven from the `Abilities` rows (Slot + identity quad; byte-for-byte vs the strickenbla capture,
AbilityGrantTests). The kit is harvested from the CREATE-flow captures by `CaptureAbilityImporter` and flagged
`IsStartingAbility` (8 abilities: 3 Normal Attack directions 769094/769095/769099, Toggle Combat, Switch Weapon,
Rest, + 2 utility). Sent right after the world-burst replay. (`Abilities` also now catalogs 26 abilities total -
the higher-level/class ones from gameplay captures - but only the starting kit is granted.)
- [ ] After a beach login, the Normal Attack abilities should appear (drag to the hotbar from the abilities/feats
      window if not auto-placed).
- [ ] Target an NPC/mob and use an attack. The target's health bar should drop, and on 0 HP the server logs a
      kill (despawn/XP not implemented yet).
**Expected:** target HP visibly decreases. Damage is a provisional formula for now. If NO abilities show up at
all, grab the client log — the grant may need to fire at a different point in the world-entry sequence.

### DIAGNOSED 2026-06-11 (user: "attack executes once, then random, sometimes hits self"). NEEDS IN-CLIENT RE.
NormalAttack (`0x642CD3D6` Interact, actionType `0xf`) is a **BehaviorControlCenter behaviour FSM**, not a one-shot.
Traced the strickenbla combat capture: the client sends a swing per press (args field2 = a swing counter that
increments 1,3,4,9,…; the attack DIRECTION ability id 769094/95/99 cycles in the request tail; the **target is in
args.SpellTarget.field2** and parses correctly — so "self" is NOT a target-parse bug). The retail server's BccAck
(`0x642CD3D6` download) is far richer than ours (`SendNormalAttackResponse`):
- it **inserts a field3 phase** (`18 0X`) into the echoed args and bumps argsLen (+2); ours echoes verbatim.
- its **actor word varies** — retail also acks the **NPC's own** behaviour (actor = target handle), driving the
  mob's counter-attack/animation; ours always uses the player instance → likely the "random / self" animation.
- result-block constant is `0x06 000000CB`; ours writes `0x000000CD` (wrong).
- ack 3 carries **multiple** result entries (one per accumulated target in the tail); ours sends one fixed block.
So our simplified ack desyncs the behaviour FSM after the first swing → the symptom.

**FIXED 2026-06-11 (high-confidence pieces; needs in-client re-test):**
- **THE bug — empty args.** `argsLen` is a u32 at rawPayload offset 22; the code read it as a u16 at 22 (= the
  zero high half), so the ack echoed NO args. Fixed (read u32@22, args@26). This alone likely fixes the desync.
- **field3 phase** now spliced into the echoed args (monotonic per-ack counter). `InsertBehaviorPhaseField`
  reproduces the captured swing-1 BccAck args BYTE-FOR-BYTE (BehaviorPhaseFieldTests).
- **0xCD → 0xCB** result constant (matches the capture).
With these, the ack args match the retail capture byte-for-byte. **Still NOT reproduced (next, in-client):**
the SECOND ack retail sends for the NPC's OWN behaviour (actor = target handle, drives the mob's counter-attack)
and the multi-result block — so the mob may not visibly fight back yet. Also the phase counter starts at 1
(retail's capture started mid-session at 3; monotonic is what matters, exact start may need tuning in-client).

**FIXED 2026-06-11 (separate, secondary bug): NPC combat HP.** Was a flat placeholder (`DefaultNpcMaxHealth=100`);
now seeded from the NPC's real spawn `CharacterStats.health` (e.g. 6300/100 = 63 for beach mobs). The burst replay
records each spawned NPC's max health into `Session.SpawnedNpcMaxHealthByHandle` (alongside the name), and combat
seeds the target's vitals from it (`GetOrAddNpcVitals`), so the bar moves relative to the HP the client rendered.
Falls back to the placeholder for NPCs not spawned via the burst. (Does NOT fix the FSM desync above — orthogonal.)

**Follow-up — SPELLS are blocked on data (investigated 2026-06-09):** only *abilities* are granted. Casting
*spells* needs the spell-grant packet, which **is not in any capture** — even a caster (`hoxxling`) only ever
receives the same 8 abilities via 0x2052; none of the granted ids are in the `Spells` table, `SpellBookIIR`
(0x6BA10936) is just a tiny notification, and the RE'd `DispatchLearnSpell(i_nSpellID)` method is never exercised.
So wiring real spells needs a FRESH capture of a caster actually learning/having a spell (e.g. class 29's 165241).
The C++ `addSpell` (0x09) is the dead old scheme.

## 4. Teleport GM command  (commit: fix teleport)
Rewritten to reuse the world-entry render path.
- [Does move player & apply buff, but maps does not change] `.teleport 2010` (also try `2040`, `3000`) — these maps have a stored world burst AND a real spawn
      position, so they're the best targets. You should load into the new map with NPCs.
- [same] `.teleport 4010` (Tortage Beach) should also work.
**Expected:** map changes and renders. Note: retail teleport reconnects to a new world server; ours does a
soft map-change on the same connection, so it may differ.

## 5. Character vitals from DB  (commit: CharacterMaxStats)
- [ ] Create a character, pick a class, finish naming, then check the character sheet vitals in-world —
      Health/Mana/Stamina should reflect that class (e.g. a mage has more mana, a soldier more health),
      not the level-1 rogue default.

## 6. Conversations — right NPC, right dialog  (commit: route conversations by NPC name)
### FIX 2026-06-09: root cause = session-volatile NPC handles; now routed by NPC NAME.
The beach burst spawns Kalanthes=530599/Casilda=530694, but the conversation nodes were captured in a different
session (handles 319114/319209) — so the old handle key never matched and the fallback served the wrong NPC.
Now: ConversationNode carries NpcName (resolved at import from the capture's spawn); the burst replay records live
handle→name; clicks route by name → right NPC or nothing, never wrong. RE-TEST a beach login:
- Casilda should give HER dialog (or nothing if her quest-mode isn't captured), NOT Kalanthes's.
- Kalanthes should give his own lines.
(Coverage caveat: if the live player's quest mode for an NPC isn't in the captures, no dialog shows — that's a
data gap, not mis-routing now.)

On a **beach login** (existing character), not the create flow:
- [first dialog is kalanthes, sfter selecting first option it switches to second dialog of casilda, advancing to third step locks the chat (unable to exit with esc)] Talk to **Casilda** — she should give HER dialogue, not Kalanthes's regular dialog.
- [ Dialos opens normally, and camera focuses on him. But no dialog lines to select, can exit conversation with esc] Talk to **Kalanthes** — his dialogue.
**Known limit:** in the *create-world* flow specifically, Casilda may still fall back to the scripted intro —
her create-world conversation isn't in the captures the analyzer can extract yet (needs a fresh capture).

### UPDATE 2026-06-11: the chat-LOCK is fixed; the "no lines / wrong NPC" is a CONTENT-COVERAGE gap.
Diagnosed: conversation replay is coverage-bound. The `(Mode, Field3)` the client sends is QUEST-STATE-dependent,
and our captures cover only some states per NPC — e.g. the dikketest capture clicks Casilda `mode=1 field3=0` then
`field3=2`, but the DB has Casilda `field3=0/1`, NOT `2`. So a live click on an un-captured branch found no node.
The Knubot opcodes are now named: `0x045B0B0F` OpenChatWindow, `0x4A3D899A` AppendText, `0xF164A4A5` **AnswerList**
(the selectable lines → "no lines to select" = an empty/absent AnswerList for that node), `0xC4AF39EB` CloseChatWindow.
- **FIXED (needs re-test): the chat-lock.** On a no-match beach login the server sent NOTHING, so the client hung
  (can't ESC). It now sends `KnubotCloseChatWindow` (graceful "nothing more to say" close). RE-TEST: advancing to an
  un-captured step should now CLOSE the window cleanly instead of locking.
- **FIXED (needs re-test): "all stages played at once".** Picking an option is a KnubotAnswer (0x4D7D9123) upload,
  not a TalkToNpc — the extractor only split at TalkToNpc, so it bundled every stage into one node and replayed the
  whole tree on the first click. Now the node is segmented into stages (each ends at an answer-list/close-window);
  TalkToNpc serves stage 0, each KnubotAnswer serves the next stage. RE-TEST: dialogue should advance greeting →
  option → reply → next option, one step per click. (Linear replay of the captured path; it ignores WHICH option you
  pick, so branching still follows the capture — full branching needs RDB dialogue trees.)
- **Still expected (data gap, not a bug): "no lines" / wrong-looking dialog** when the live quest-state branch isn't
  in any capture. The proper fix is RDB-driven dialogue (works for ANY quest state), not capture replay.
  **⛔ PINNED/DEFERRED 2026-06-11:** scoped the RDB — dialogue branching is Lua (LuaArchive 1010223) keyed on
  quest-state TAGS (`player:HasTag/AddTag`), text in TextStrings(TDC2); there's no static conversation-tree resource.
  So it needs a Lua + tag/quest VM — a quest-engine-sized effort entangled with quests. Defer until a quest/tag
  system exists. Full scoping in `docs/rdb-extraction-plan.md` + `protocol_conversation_routing` memory. The shipped
  capture-replay (stage machine + graceful close) is the interim.

## 7. Create-world Kalanthes intro  (commit: answer create-world spawn polls per-entity) ⭐ highest priority
The auto-cutscene where you wash ashore and Kalanthes walks up + starts the chat.

### ⭐ LATEST 2026-06-09 (end of session) — PINNED. Two-port done; cutscene bug is in the EDITOR phase.
DONE & confirmed in-client: (a) **two-port GameServer** — `WorldGameServerListener` on realm.Port+1 (7015), handoff
advertises it, client does a real `redir=1` reconnect (commits 6bb3cdc + 2513fc2)
[SUPERSEDED 2026-06-11: `WorldGameServerListener` REMOVED — the 0x2002 handoff carries the address explicitly, so we
now advertise the SAME GameServer port and serve both connections on one listener]; (b) **gametweaks** apply (19)
(commit 40d4bc0); (c) **create-world burst rebuilt clean** from the hoxxling capture via constructive PacketStream
(commit e8be7ba, generator `Captures/gen_createworld_burst.py`).
**The cutscene STILL fails identically** (invalid camera paths, `ThisIsKalanthes`=123456789, broken geometry,
disconnect). **DECISIVE: two totally different world bursts (dikketest vs hoxxling) → byte-identical failure ⇒ the
world-burst content is irrelevant.** Ruled out: client is fine (same client plays the intro on RETAIL — user
confirmed); RDB-miss + sex/race errors are benign; transition mechanics match retail (plain 0x206E→0x2002; retail
beach is also a ~300ms Pf-Proxy load — the earlier "loading screen / same-port" theory below is therefore a RED
HERRING for the cutscene). The editor burst was the CONSTANT across both world-burst tests ⇒ **the bug is in the
EDITOR (galley) phase** (the drowning cutscene should play on the galley right after PlayOrSkip but never does).
**NEXT (pinned):** capture our create-world loopback pcap (7014/7015), `--dump` it, and diff the EDITOR stream
packet-by-packet vs the hoxxling editor `:7018` to find the first divergence that arms the cutscene. Full diagnosis
in the auto-memory `createworld_cutscene_diagnosis`. Everything below is earlier (partly superseded) analysis.

### PINNED 2026-06-09 (logs-test/AgeOfConan.log + logs.txt + screenshot)
**The intro now TRIGGERS — real progress.** Server sends MapChange→4000 (Slave Galley) + 16 SpawnObject +
6 FA015807 entity spawns + answers the spawn-polls with SpawnChecks. Client receives the `PlayOrSkip` then
`ThisIsKalanthes` scry messages and *attempts* the full drowning→Kalanthes cinematic. So the world-build and
script-trigger sequence is essentially working.

### DECODED 2026-06-09: the retail ScryScript cutscene sequence (incl. the resource manifest)
The cutscene is driven by `ScryScriptIIR` (IIR sub-opcode **0xA36D3B74**, protobuf `ProtoScryScriptIIR`), now
fully decodable — run `Captures/decode_scryscript.py` on the dikketest create capture. The retail server->client
sequence is:
1. **PreloadAssets** — token `playorskip(72)|onclientconnected(1)|NEW_character_creation Slave Galley (server-side)`,
   **+ a big RDB resource manifest** in string-param-2: `1010004:793713; 1070003:4000; 1000097:4000; 1010030:156211;
   1000059:4000; 1010300:4000; 1000049:4000; 1066611:4000; 1010506:4000; 1000058:4000; ...` (the Slave Galley 4000
   playfield + cutscene assets the client must load).
2. **ScriptMessage "PlayOrSkip"** → `StartScriptGroupOnClient(35820)` → `ScriptMessage NicknameOk/<name>` →
   `ScriptMessage CharCreationFinished` → `StartScriptGroupOnClient(48126)`.
3. **PlayOrSkipQuestion** — token `...character_creation Tortage Beach (server-side)` + the Beach (4010) resource
   manifest (`1010003:3147889; ...`) → `StartScriptGroupOnClient(385910)` → **ScriptMessage `ThisIsKalanthes`** →
   `StartScriptGroupOnClient` → `ScriptMessage WelcomeTutorial`.
**Checked against our server (2026-06-09): we ALREADY send the Slave Galley `PreloadAssets` with the full manifest
verbatim** (`RetailCreateEditorCreationScriptBurst.cs:94-117`, the `playorskip(72)` packet + the entire
`1010004:793713;...` resource list) followed by `PlayOrSkip`. So a missing/empty `PreloadAssets` is NOT the gap.
Moreover the failing ids in the client log (`1000519:4000`, `1000103:4000`, `1066604:4010`) are **not in the
manifest at all** — they are `class Playfield requested CHILD resource` lookups, i.e. resources the playfield pulls
from the client's LOCAL RDB, not server-sent. **This reinforces the existing client-side RDB / project-config
diagnosis below** (the server is feeding the right script + manifest; the client can't resolve the playfield's own
child resources).
**✅ CHECKED 2026-06-09 — the beach scry stage IS replayed correctly (no server-side gap).** Verified the SECOND
scry stage — Tortage Beach (4010) `PlayOrSkipQuestion` + manifest (`1010003:3147889;...`) →
`StartScriptGroupOnClient(385910)` → `ThisIsKalanthes` → `WelcomeTutorial` — is present in
`RetailCreateWorldEntryBurst`: the beach `PlayOrSkipQuestion`+manifest is in chunk 0 (SCENE, line 344),
`ThisIsKalanthes` in chunk 1 (case 0, line 366) — early, NOT truncated at the `ApproachCasilda` finale.
**Crucially, the connection split matches retail exactly:** splitting retail's 12-step scry by source port shows
#1–#5 (galley: PreloadAssets/PlayOrSkip/CharCreationFinished) on the **creator** conn (:7016) and #6–#12 (beach
`PlayOrSkipQuestion`, `ThisIsKalanthes`, `WelcomeTutorial`) on the **world** conn (:7012) — which is exactly our
`RetailCreateEditorCreationScriptBurst` (creator) vs `RetailCreateWorldEntryBurst` (world) split. So the scry
sequence is server-side-correct on the right connections; this reinforces the pinned diagnosis that the bug is
NOT a missing/misplaced/truncated scry — it's in the EDITOR (galley) phase. (Split via `decode_scryscript.py` +
the per-step `srcPort`.)

### ⭐ DECISIVE 2026-06-09: retail-vs-faolan client-log DIFF — the old "client RDB" read below is WRONG.
Diffed `logs-test/retail-client.log` (working retail create-world) against `logs-test/faolan-client.log` (ours):

| marker | retail | faolan | meaning |
|---|---|---|---|
| `No project defined` | 1 | 1 | **BENIGN** (both) — NOT the cause |
| `does not exist in any RDB` | **182** | 128 | **BENIGN** — retail has MORE missing-RDB and still works |
| `invalid camera path` | **0** | 7 | ours-only — the real symptom |
| `OverrideGameTweaks` stream-bad/disconnect | 0 | ~~1~~ 0 | **FIXED** (40d4bc0 + de-magicked into `GameTweaks` catalog) |
| `ApplyDynamicGameTweaks: Applying N gametweaks` | **19** | ~~0~~ 19 | **FIXED** — ours now applies all 19 too |
| `ThisIsKalanthes` object | (resolves) | `123456789:123456789` | ours: Kalanthes dynel unbound |

So the RDB-missing + No-project errors are **benign** (retail has them, more even) — the prior client-RDB/
project-config diagnosis (below) is **disproven**. The failure is **server-side**, in our create→world entry.

**Root cause (user-observed + confirmed): retail does `click enter-world → LOADING SCREEN → cinematic`; ours
SKIPS the loading screen.** Evidence:
- **Same-port handoff.** Both our creator (`PlayerAgent.SendGameServer`, Packets.cs:119) AND our enter-world
  handoff (`0x2002`, `CreateWorldHandshake.SendWorldHandoff`:181) advertise the SAME `realm.IpAddress:realm.Port`
  (`127.0.0.1:7014`). Retail advertises a DIFFERENT world port (this capture: `:7027`; dikketest: creator `:7016`
  → world `:7012`). The client only does the full "new world server" load (loading screen → cinematic resources)
  when the world is a different endpoint → ours skips it. (User's hypothesis, matches the code.)
- **Timing confirms it:** retail `CharCreationFinished` 17:17:04 → **12s gap (enter-world + loading)** → beach
  playfield loads 17:17:16 → cinematic OK. Ours `CharCreationFinished` 16:19:49 → **1s** → beach loads 16:19:50 →
  cinematic fails 16:19:52. No enter-world/loading gap in ours.
- **Beach playfield Subgroup differs:** retail `Subgroup=35021789` (character-tied), ours `Subgroup=33554432`
  (= 0x02000000, the CreationInstanceBase placeholder) — our beach world-instance is set up with a placeholder.
- **Gametweaks not applied:** the malformed `OverrideGameTweaks` (route `=> PlayerInterface` disconnect) means our
  client applies 0 of the 19 gametweaks retail applies — those may gate cinematic/loading behavior.

**NEXT (server-side, ranked):**
1. **Advertise a DIFFERENT world port** in the `0x2002` enter-world handoff (and run a 2nd GameServer listener on
   it) so the client does a real new-world connect → loading screen → cinematic-resource load. Most likely fix.
2. **Fix the malformed `OverrideGameTweaks`** so the 19 gametweaks apply (the packet's `overrides` collection /
   nested `GameTweak<H/I/M/N/_N>` encoding — see docs/opcode-re-plan.md). Removes the PlayerInterface disconnect.
3. **Use a real beach Subgroup/instance** instead of 0x02000000 in the world-entry.
Compare against the full retail pcap: `logs-test/retail`.

---
**(SUPERSEDED — kept for history) It breaks on MISSING CLIENT PLAYFIELD RESOURCES, not a server packet defect.** Client log:
- `ResourceManager - class Playfield requested child resource 1000519:4000 / 1000103:4000 / 1000519:4010 /
  1066604:4010 does not exist in any RDB sources`
- `GameData.Playfield - PlayfieldStaticPoints_c::ReadBlob: Error reading the static ai points`
- → EVERY cinematic camera path is invalid (`CC_Drowning_Above/TiltDown/TrackUpFromBeneath`, `Awake`,
  `Kalanthes`, `GetUp`) → camera stuck → the grey, untextured scene in the screenshot.
- `ThisIsKalanthes` carries object `123456789:123456789` = the client's "unresolved object" sentinel (the
  CLIENT-SIDE intro script couldn't bind Kalanthes) — consistent with the scene/AI data not loading.

These `... does not exist in any RDB sources` lookups are the client's LOCAL RDB; the server doesn't serve RDB
resources. **Retail plays this cutscene on this same client, and the 27 GB rdbdata is intact (`Found 974.5 MB
Unused space in all rdbdata files`) — so the data is NOT absent.** Corrected read (2026-06-09):
- NOT a server packet defect, NOT data-absence. The leading suspect is the client-side
  `ERROR: ResourceManager - No project defined, ... running dreamworld control center and selecting a project
  should fix it` (log line 3). The client recovers enough to load base geometry/names, but **project-scoped**
  resources — exactly the intro cinematic camera paths + static-AI-points — don't resolve, so EVERY camera path
  fails uniformly. Geometry loads; cinematic doesn't. That's a client project-config issue, not our server.
- Our `le.idx` (importer index) is a CURATED SUBSET (195 types, lacks playfield type 51100 entirely) — it CANNOT
  confirm/deny these resources. Definitive check needs parsing the client's real `RDBHashIndex.bin` (RDHI v6).
- NEXT: (a) CLIENT — check/repair the Dreamworld project config (the `No project defined` error); cheap, possibly
  decisive. (b) TOOLING — parse `RDBHashIndex.bin` to confirm the cinematic resources for pf 4000/4010 exist (we
  have the dumps), proving the data's present and the issue is resolution/config, not absence.

### AWAITING: retail client log (user will capture at home) — DIFF against logs-test/AgeOfConan.log
The decisive test. Capture an `AgeOfConan.log` from a retail/working run of the SAME create-world intro, then
compare line-for-line with ours. What each outcome tells us:
1. **`ResourceManager - No project defined`** — present in retail too? → benign startup noise, NOT the cause.
   Absent in retail? → our client's project config is the culprit (client-side, not our server).
2. **`class Playfield requested child resource 1000519:4000 ... does not exist`** + static-AI-point errors —
   present in retail too (yet cutscene works)? → benign; real failure is elsewhere. Absent in retail? → retail
   resolves those resources; ours doesn't (config/resolution, or we put the client in a different playfield state).
3. **`CinematicCamera_c::InitPath ... invalid camera path "CC_Drowning_*/Awake/Kalanthes/GetUp"`** — gone in
   retail? → confirms the camera paths DO resolve normally; our failure is downstream of the missing pf resources.
4. **`ThisIsKalanthes` scry msg** — retail's `msgobject.name` should be a REAL dynel id, not our `123456789:
   123456789` sentinel. Confirms whether the Kalanthes binding (server-supplied handle) differs.
5. Bonus: compare the `Playfield.Loading - Loading stats for Pf Proxy` lines (Model/RunningPlayfield/Subgroup)
   to see if retail loads pf 4000/4010 with different Subgroup/instance than ours.
Put the retail log in `logs-test/` (e.g. `AgeOfConan-retail.log`) so it sits next to ours for the diff.

**Secondary server-side cleanups (won't alone fix the cutscene, but worth doing):**
- `rpc - ... OverrideGameTweaks() ... stream bad after 'overrides'!` → `Dispatcher call ... (OverrideGameTweaks)
  failed. Disconnecting ( Route ... => PlayerInterface)`. Known overrides bug, but here it DISCONNECTS a route
  mid-create-world. Confirm it isn't severing anything the intro needs (the intro did proceed, so likely tolerated).
- `GameCode.Chars - GameCodeDynelFullUpdateIIR::Activate Ignoring IIR for already existing Dynel 50000 <inst>`
  (341446/341448/341468/341469) — server RE-sends beach entities the client already has; client ignores the dupes.
  Indicates a double-spawn (initial create-world entry + world-burst replay). Not fatal; de-dupe it.

## 8. ⚠️ Float endianness on raw-body packets  (commit: float endianness)
I changed `MapChange`, `ReadyForPlayScreen`, the Move echo, and the `/setpos`-style GM packet to write raw
position floats **big-endian** (they were wrongly little-endian). This touches the **login path**, so it's the
most likely to regress something.
- [Works] Normal login — do you still spawn at the correct position/orientation (not at 0,0,0 or somewhere weird)?
- [Works] Movement still works normally?
**If a character spawns in the wrong place or login breaks, this change is the first suspect.**

## 9. Map rendering after instance-detection fix  (commit: fix instance detection)
- [The out of range ones need to be supported as well i assume, if they are in the dumps that must mean they exist. Wrong parse?] Maps that have a world burst (2000, 2010, 2040, 3000, 4000, 4010) should render on login/teleport.
      Maps 1000 and 11040 were intentionally dropped (their captured instance was out of range) — they'll
      fall back to the no-NPC hand-built path.

## 10. World burst + player spawn — RESTRUCTURED & server-generated  (2026-06-10 session, 30 commits)
Big architectural pass. Full detail in **`docs/player-spawn-generation-plan.md`** + the `world_entry_from_db` memory.

**DONE & harness-verified (16/16, 124 unit tests, roundtrip-confirmed over all 24 dumps):**
- **World burst is now STRUCTURED IIR data**, not opaque packet blobs. `IirRecordCodec` decomposes each burst packet
  into a `MapWorldBurstEntity` row {Opcode, IirType, Const, Handle, FieldA, Header, Body} and rebuilds it byte-exact
  (proven lossless on 68,264 captured IIR records). The `MapWorldBurstPacket` raw table + **`strie-login-worldburst.bin`
  are gone**.
- **Globals + player-state are SERVER-GENERATED, not stored:** MapChange + GameTime (built from the live map/instance),
  the player's Vitals (from MaxStats), and StatIIR dropped as redundant. NPC vitals/stats (handle != player) are kept.
  `IirMessageTypes.IsServerGenerated(type,handle,playerInstance)` is the rule; the importer drops them on import.
- **The player's own spawn reflects the live character from the DB:** name + race + sex + level + vitals + EQUIPMENT
  (`PlayerSpawnReader` decodes the CharacterIIR, `PlayerSpawnEquipment` builds the worn items, `SetCharacterMessage`
  re-encodes). New tables: `CharacterInventory`, `CharacterAttribute`, `CharacterSkill`.
- **`spawn-player-template.bin` removed** — first relocated to the `PlayerSpawnTemplate` C# constant, which the
  second session then DELETED outright (the scaffold frame is generated from DB rows; see the block below).
- The original "under-populated vs retail" gap (beach 0x2076 / post-poll spawns) is folded in: the beach burst is a
  committed DB seed with the full window; OTHER maps still truncate at the first `0x2020` poll in
  `MapWorldBurstExtractor` (lower priority; asset-stripped client won't render the extra NPCs anyway).

**DONE 2026-06-10 (second session): scaffold generation + template retirement.** The spawn scaffold is now
GENERATED from DB rows (`PlayerSpawnScaffoldCodec` + `PlayerSpawnScaffoldRows`; new/reshaped tables
CharacterAttribute/CharacterSkill/CharacterScaffoldPair/CharacterSpawnFrame, migration `SpawnScaffoldRows`);
fresh characters are seeded with Dikketest-derived defaults; `PlayerSpawnTemplate` deleted (Strie bytes are a
test golden only). Harness 16/16; the generated scaffold confirmed on the wire via pcap.
- [ ] RE-TEST a normal beach login in-client: world renders, your character looks/loads normally. The player
      spawn now carries a FRESH scaffold (Dikketest's defaults: 6 attributes, 7 morph entries, 20 feat ids)
      instead of Strie's progressed one — if anything about the character sheet/feats/abilities looks off after
      login, grab the client log.

**OPTIONAL remaining:**
- Wire the appearance: the editor morph sliders ARE the float-valued scaffold entries (discovered 2026-06-10;
  Dikketest's 7 entries match his slider multiset) — crack the exact slider→float mapping, then generate the
  morph entries from `CharacterBody` instead of the Dikketest defaults.
- ~~`MapWorldBurstExtractor` full-window fix for non-beach maps.~~ **DONE 2026-06-11.** The extractor's window no
  longer ends at the first `0x2020` poll: it keeps the window open and also captures the single post-poll `0x2076`
  spawn manifest (entity handle→slot table) the client needs — the one structural record the hand-seeded beach
  carried that extractor-derived maps lacked. Post-poll noise (0x2008 SpawnCheck, StatIir, 0x2024 ReadyForPlayScreen,
  gameplay) is still excluded; only `0x2076` is taken. Verified across all 29 captures: maps 1000/2000/2010/2040/4022
  each gained exactly +1 manifest, beach (4010) untouched (proven committed seed, still skipped on import), maps
  whose winning capture lacks a clean manifest (3000/4000/4020/11040) correctly gained nothing. faolan.db regenerated;
  187 unit tests pass (3 new extractor tests). Also confirmed #9: out-of-range maps 1000/11040 ARE imported now.

---

## Notes / known non-blockers (no action needed unless curious)
- `OverrideGameTweaks` (`0x2085...`) — client logs `stream bad after 'overrides'` every session and drops that
  one route; harmless, pre-existing.
- Analyzer tail-drop on ~3 GS streams (Strie-create/login, faolanx-charcreation) — a few KB short vs ground
  truth; doesn't affect gameplay, logged in README.

---

## Session 2026-06-11 (part 2) — live world simulation + RDB/CDN extraction

DB schema added this session (migrations auto-apply on server start; or re-run `Faolan.Importer`):
`AddRespawnPoints`, `AddCharacterQuests`, `AddQuestGoalTarget`. faolan.db is committed with these + reimported
map names (242) and resurrection points (351). See `docs/server-architecture-roadmap.md`.

All of the below are harness-verified (unit tests) but **need in-client check** — most are 2-client or live-combat:

### Combat loop (both directions)
- [ ] Kill an NPC → it despawns, you gain XP (bar moves), loot bag may drop at the corpse. On enough XP you
      **level up** (level + full vitals push). Real NPC HP now comes from the spawn (not the flat placeholder).
- [ ] Stand near a hostile NPC → it **aggros and hits you** every ~2s (your HP bar drops via `0x96B8DC59`).
- [ ] Let an NPC kill you → after ~10s you **respawn** at the closest known resurrection point (full HP).
- [ ] Walk near an undiscovered resurrection point → "Resurrection point discovered" system message; you can
      then respawn there.

### Loot bag (open/fade — item grant still gated)
- [ ] Click a dropped loot bag → a loot **window opens** (NewCurrentLooter + SimpleDynelVisualChange "Open"),
      bag stays. Close it (or it expires ~120s) → it **fades** (`n3ToClientFade`). NOTE: taking looted items into
      the bag is **client-side** (there is NO server add-item packet — confirmed; `0x79F8C7A1`/`0x63268258` are
      quest packets, not ContainerAddItem). Server-side loot→inventory persistence would need a `CharacterInventory`
      write on the take + a fresh post-load snapshot.

### Quests
- [ ] `.quest add 13479 5 Scavenger Lackey` → quest appears in journal; kill 5 → goal advances + "complete".
      `.quest list` / `.quest remove <id>`. Quests persist across relog. (Accept-from-dialogue is VM-gated.)

### Cross-player (needs TWO clients)
- [ ] Two players on the beach should **see each other** spawn, move, and despawn (on logout / out of range).
      The despawn token is `0x004fe428` for the beach — open question is whether the client enforces the token.

### Map names
- [ ] `.maps` / teleport labels should now show real names (e.g. 4010 = "Tortage Island", 2040 = "Castle of
      King Conan") — imported from the client RDB.

---

## Architecture / refactoring (2026-06-11, part 3) — GameServer decomposition

**DONE this session (mechanical, behaviour-preserving; 261 tests green throughout):**
- DB cleanup: dropped obsolete `CharacterEquipment.Bag/Quest/Resources`; dropped dead legacy `Npc`/`NpcBody`/
  `NpcEquipment` tables; removed 1051 orphaned character-child rows (FK integrity now clean).
- `GameServerListener` god-class decomposition — extracted three composed collaborators out of the listener
  (was one type across 26 partials / ~15.3k lines, now ~14.2k):
  - `World/WorldSimulation` — world model + 10 Hz tick + regen/death/respawn/AI/discovery, behind an
    `IWorldOutbound` port the listener implements.
  - `World/WorldBroadcaster` (+ `World/WorldFraming`) — world-event packet construction.
  - `Combat/CombatService` (+ `World/VitalsPacket`) — combat domain logic + combat wire packets.
- Removed `WorldGameServerListener` — the 0x2002 handoff advertises the world address explicitly, so we
  advertise the SAME GameServer port and serve both connections on one listener.
- Renamed `Packets/0x737A6DF9_Test/` → `Packets/FullCharSnapshot/` (it's production code, not tests).
- Removed dead char-preview block in `PlayerAgentListener`.

**Remaining architecture follow-ups (in priority order):**
- [ ] **#1 — embedded capture data out of source code (HIGH VALUE).** `CreateFlow/` is ~8.8k lines, 80%+ raw
      hex byte-literals (`RetailCreateWorldEntryBurst.cs` alone = 6,957 lines). This is data-as-code. Move it to
      a DB table + importer (mirror `MapWorldBurst`/`IirRecordCodec`), with a **byte-equality test (DB-loaded ==
      embedded arrays)** as the safety net, then delete the embedded file. Catch: the burst weaves the live
      instance id into ~324 packets via `AppendVarint`, so it needs a template-with-substitution capture, not a
      flat blob — a real design effort. Verifiable WITHOUT the client (the byte-equality oracle). Scope first.
- [ ] **Fold `Packets.cs` sender-bytes duplication into `World/WorldFraming`** — it still has its own copy of the
      world-conversation sender literal (`ExistingCharacterWorldEntryPromptSender` etc. — verify which are truly
      the same conversation vs. semantically distinct before merging). Small, safe.
- [ ] **Rename `Packets/FullCharSnapshot/BigboyTailsFile.cs`** — unprofessional name; pick something descriptive
      for the class + file. Trivial.

**Surveyed, no action needed:** the other four servers (UniverseAgent / PlayerAgent / AgentServer /
CSPlayerAgent) are clean, appropriately-sized switch dispatchers — no god-class, correct request/response framing.

---

## ⭐ Chat channels broken in-client (REGRESSION) — dedicated follow-up

**Symptom:** in-world, only the local "say" tab exists — Global/LFG/Trial/etc. channels are missing. The client
logs `Connecting to CommunityServer` → `Authenticating` → ~20s timeout → `HandleDisconnect redir=0`. The
**AgentServer (chat, 7012) gets ZERO TCP accepts** — the client connects to every other server but never the chat
server. No connection → no channel subscriptions → no channels.

**Root cause (bisected):** the **world-entry construct-don't-replay rewrite**. Chat works on raw-blob-replay
commits (`3d20d34`), broken on every construct commit (`e58b06c`→HEAD). The client opens the chat connection only
after it finishes the initial world build; the construct path dropped/changed world-build packets so it never
reaches "loaded". Exact commit is in the stuck-loading era (untestable for chat), but the rewrite is the culprit.

**Ruled out:** character/id (`0x02000000` works at 3d20d34, fails at HEAD), the AgentServer advertisement
(byte-identical, correct host:port), the post-ready burst (chat connects *before* ReadyForPlayScreen), the NPC
death-loop (separate, fixed). NOT environment/hosts/LocalConfig.

**Tried, none fixed it:** restored the dropped `CoreClaimIIR 0x06EC1255` (committed 4ebce3f — correct anyway);
removed 2 spurious `N3ToClientQuit` despawns; dropped the `0x2076` manifest. The single gate is still
unidentified — likely a content/ordering diff invisible at the opcode level.

- [ ] **Fix:** loopback-capture a working raw-blob login (`3d20d34`) vs HEAD and **byte-diff the world-build
      stream** (post-0x201c handshake → spawn-check stream), OR re-extract the 4010 burst **without over-filtering**
      (suspect: `ea84a38` "reject gameplay-polluted windows" + `13bd715` "drop transient rows") and byte-compare to
      the raw blob. Don't diff opcode counts — too noisy.

**Test method:** server from `Faolan/bin/Debug` (`dotnet Faolan.dll`); drive client headless with
`Faolan.ClientLauncher --console --universeaddr 127.0.0.1:7010 -u faolan -p any -c <char>`; chat connected iff the
server log shows `AgentServerListener[0] Client connected`. Test on HEAD (aggro gated, no death-loop).
